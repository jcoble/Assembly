use std::collections::{HashMap, HashSet, VecDeque};
use std::env;
use std::ffi::OsString;
use std::io::{self, BufRead, BufReader, Read, Write};
use std::os::unix::fs::PermissionsExt;
use std::os::unix::process::CommandExt;
use std::path::{Path, PathBuf};
use std::process::{Child, Command, Stdio};
use std::sync::atomic::{AtomicBool, AtomicI64, AtomicU32, AtomicU64, Ordering};
use std::sync::{mpsc, Arc, Condvar, Mutex, MutexGuard, OnceLock};
use std::thread;
use std::time::{Duration, Instant};

use axum::extract::ws::{Message as WebSocketMessage, WebSocket, WebSocketUpgrade};
use axum::extract::{Query, State};
use axum::http::StatusCode;
use axum::response::{IntoResponse, Response};
use axum::routing::get;
use axum::Router;
use futures_util::{SinkExt, StreamExt};
use serde::{Deserialize, Serialize};
use serde_json::{json, Value};
use tokio::io::{AsyncBufRead, AsyncBufReadExt, AsyncReadExt, AsyncWrite, AsyncWriteExt};
use tokio::net::TcpListener;
use tokio::sync::{broadcast, oneshot};

const LSP_REQUEST_TIMEOUT: Duration = Duration::from_secs(6);
const LSP_DIAGNOSTICS_TIMEOUT: Duration = Duration::from_millis(1200);
const LSP_SERVER_CANCEL_RETRY_DELAY: Duration = Duration::from_millis(180);
const MAX_LSP_HEADER_BYTES: usize = 8 * 1024;
/// Most symbols one document-symbol answer will carry. The editor draws at most a
/// hundred-odd margin counts per file, so this leaves plenty of room while keeping a
/// generated file with tens of thousands of symbols from crossing the bridge.
const MAX_LSP_DOCUMENT_SYMBOLS: usize = 500;
/// How big a file may be before the app declines to hand it to a language server for a
/// symbol list. Reading it would cost more memory than the answer is worth.
const MAX_LSP_DOCUMENT_SYMBOL_BYTES: u64 = 4 * 1024 * 1024;
/// How many lines of a language server's own error output are kept, so a reader can see
/// what it complained about without the app growing without bound.
const MAX_LSP_LOG_LINES: usize = 200;
/// The active Supercharged workspace owns the one process for each language.
const MAX_LSP_WORKSPACES_PER_LANGUAGE: usize = 1;
/// Project activation only needs enough filesystem discovery to know which
/// language server to preload. Keep this deliberately shallow and bounded: it
/// is not a source scan and it never reads file contents.
const MAX_LSP_PRELOAD_DISCOVERY_DEPTH: usize = 4;
const MAX_LSP_PRELOAD_DISCOVERY_ENTRIES: usize = 20_000;
const MAX_NATIVE_CSHARP_MESSAGE_BYTES: usize = 32 * 1024 * 1024;
/// How long a language server gets to act on the goodbye it was just sent
/// before its process group is stopped. Long enough for a server that means to
/// leave, short enough that turning full mode off feels immediate.
const LSP_SHUTDOWN_GRACE: Duration = Duration::from_millis(1500);
/// How often the goodbye wait checks whether the server has gone.
const LSP_SHUTDOWN_POLL: Duration = Duration::from_millis(25);
/// The same grace for Roslyn behind the native C# bridge.
const NATIVE_CSHARP_SHUTDOWN_GRACE: Duration = Duration::from_millis(1500);
const SOURCE_LSP_READINESS_LANGUAGES: &[&str] = &[
    "csharp",
    "typescript",
    "tsx",
    "javascript",
    "jsx",
    "rust",
    "svelte",
];

/// Whether each language server may run. The map is process-local, while its
/// compact snapshot is owned by the existing SQLite app-settings row.
///
/// It is the most expensive thing this app starts — around 800MB of memory once
/// it has loaded a large solution — and a reader who is not writing C# right now
/// has no use for it. Switching it off costs precision, not features: the counts
/// in the margin and the plain-text search over the project both read the files
/// themselves and keep working, while the squiggles under mistakes and the
/// "which of these three `Send` methods did I click" accuracy go away.
///
/// Process-wide rather than kept on the registry because the readiness report
/// ([`read_source_lsp_status_sync`]) has no registry to ask, and a report that
/// said "ready" for a server the reader has switched off would be a lie.
static LANGUAGE_SERVER_SETTINGS: OnceLock<Mutex<HashMap<String, bool>>> = OnceLock::new();

fn language_server_settings() -> &'static Mutex<HashMap<String, bool>> {
    LANGUAGE_SERVER_SETTINGS.get_or_init(|| {
        Mutex::new(HashMap::from([
            ("csharp".to_string(), true),
            ("typescript".to_string(), true),
            ("rust".to_string(), true),
        ]))
    })
}

fn language_server_key(language_id: &str) -> Option<&'static str> {
    match language_id.trim().to_ascii_lowercase().as_str() {
        "csharp" | "c#" => Some("csharp"),
        "typescript" | "tsx" | "javascript" | "jsx" => Some("typescript"),
        "rust" => Some("rust"),
        _ => None,
    }
}

pub(crate) fn language_server_enabled(language_id: &str) -> bool {
    language_server_key(language_id)
        .and_then(|key| locked(language_server_settings()).get(key).copied())
        .unwrap_or(true)
}

pub(crate) fn set_language_server_enabled(
    language_id: &str,
    enabled: bool,
) -> Result<bool, String> {
    let key = language_server_key(language_id)
        .ok_or_else(|| format!("Unsupported language server: {language_id}"))?;
    let mut settings = locked(language_server_settings());
    Ok(settings.insert(key.to_string(), enabled).unwrap_or(true) != enabled)
}

pub(crate) fn restore_language_server_settings(value: &str) -> Result<(), String> {
    let parsed: Value = serde_json::from_str(value)
        .map_err(|error| format!("Language server settings are invalid: {error}"))?;
    let Some(object) = parsed.as_object() else {
        return Err("Language server settings must be an object".to_string());
    };
    if let Some(enabled) = object.get("enabled").and_then(Value::as_bool) {
        set_language_servers_enabled(enabled);
    }
    if let Some(servers) = object.get("servers").and_then(Value::as_object) {
        for (language, value) in servers {
            if let Some(enabled) = value.as_bool() {
                let _ = set_language_server_enabled(language, enabled);
            }
        }
    }
    Ok(())
}

pub(crate) fn language_server_settings_snapshot() -> Value {
    let servers = locked(language_server_settings()).clone();
    json!({
        "enabled": language_servers_enabled(),
        "servers": servers,
    })
}

/// Is the C# language server allowed to run?
pub(crate) fn csharp_language_server_enabled() -> bool {
    language_server_enabled("csharp")
}

/// Whether ANY language server may run. One switch over all of them, in
/// Settings — a per-project switch in each editor header turned out to be a
/// switch that could be found to turn on and not to turn off.
static LANGUAGE_SERVERS_ENABLED: AtomicBool = AtomicBool::new(true);

/// Are language servers allowed at all?
pub(crate) fn language_servers_enabled() -> bool {
    LANGUAGE_SERVERS_ENABLED.load(Ordering::Relaxed)
}

/// Allow or forbid every language server. Returns whether this changed
/// anything. Forbidding does not stop what is running — see
/// [`SourceLspRegistry::stop_all_servers`] — for the same reason as the C#
/// switch: the flag can be set at startup before a registry exists.
pub(crate) fn set_language_servers_enabled(enabled: bool) -> bool {
    LANGUAGE_SERVERS_ENABLED.swap(enabled, Ordering::Relaxed) != enabled
}

/// Allow or forbid the C# language server. Returns whether this changed
/// anything, so the caller can tell the reader what actually happened.
///
/// Forbidding it does NOT stop a server that is already running — see
/// [`SourceLspRegistry::stop_servers_for_language`] for that half. They are
/// separate so the flag can be set before the registry exists (at startup, from
/// the reader's saved setting) without needing one.
pub(crate) fn set_csharp_language_server_enabled(enabled: bool) -> bool {
    let changed = set_language_server_enabled("csharp", enabled).unwrap_or(false);
    if changed {
        let (state, detail) = if enabled {
            (
                LanguageServerState::NotRunning,
                "The C# language server is back on. It starts the next time you open a C# file.",
            )
        } else {
            (
                LanguageServerState::Disabled,
                "The C# language server is switched off in Settings.",
            )
        };
        let roots = read_language_server_roots("csharp");
        if roots.is_empty() {
            record_language_server_state("csharp", "", state, Some(detail.to_string()));
        } else {
            for root in roots {
                record_language_server_state("csharp", &root, state, Some(detail.to_string()));
            }
        }
    }
    changed
}

/// Which workspaces the reader has turned language intelligence on for.
///
/// Read mode is the default and the whole point: opening a file — from a diff
/// hunk, the file tree, or anywhere else — colours it and stops there. Nothing
/// starts a language server until the reader turns full mode on for that
/// workspace, and turning it off again stops the process it started.
///
/// This is plain data with no processes in it, so the start/stop rules can be
/// read and tested on their own. [`SourceLspRegistry`] does the stopping.
#[derive(Debug, Default)]
pub(crate) struct LanguageIntelligenceModes {
    full_mode_roots: std::collections::HashSet<String>,
}

/// What a request to change one workspace's mode actually did.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub(crate) enum LanguageIntelligenceChange {
    AlreadyOff,
    AlreadyOn,
    TurnedOn,
    TurnedOff,
}

impl LanguageIntelligenceChange {
    /// Did this move the workspace from one mode to the other?
    pub(crate) fn changed(self) -> bool {
        matches!(self, Self::TurnedOn | Self::TurnedOff)
    }

    /// Must the caller stop this workspace's servers now?
    #[cfg(test)]
    pub(crate) fn must_stop_servers(self) -> bool {
        matches!(self, Self::TurnedOff)
    }

    /// May a server for this workspace be started after this change?
    pub(crate) fn may_start_servers(self) -> bool {
        matches!(self, Self::TurnedOn | Self::AlreadyOn)
    }
}

impl LanguageIntelligenceModes {
    /// Is full mode on for this workspace? Unknown workspaces are in read mode.
    pub(crate) fn is_on(&self, root: &str) -> bool {
        self.full_mode_roots.contains(root)
    }

    /// Turn full mode on or off for one workspace, and say what that did.
    pub(crate) fn set(&mut self, root: &str, enabled: bool) -> LanguageIntelligenceChange {
        let was_on = self.is_on(root);
        if enabled {
            if was_on {
                return LanguageIntelligenceChange::AlreadyOn;
            }
            self.full_mode_roots.insert(root.to_string());
            return LanguageIntelligenceChange::TurnedOn;
        }
        if !was_on {
            return LanguageIntelligenceChange::AlreadyOff;
        }
        self.full_mode_roots.remove(root);
        LanguageIntelligenceChange::TurnedOff
    }
}

fn language_intelligence_modes() -> &'static Mutex<LanguageIntelligenceModes> {
    static MODES: OnceLock<Mutex<LanguageIntelligenceModes>> = OnceLock::new();
    MODES.get_or_init(|| Mutex::new(LanguageIntelligenceModes::default()))
}

/// One spelling per workspace, so the same folder written two ways is one
/// entry. A folder that is not on disk keeps the text it was given: a saved
/// choice must survive a workspace that is temporarily unmounted or renamed.
pub(crate) fn language_intelligence_key(root: &str) -> String {
    normalized_lsp_root(root).unwrap_or_else(|| root.trim_end_matches('/').to_string())
}

/// Is full mode on for this workspace?
pub(crate) fn language_intelligence_on(root: &str) -> bool {
    locked(language_intelligence_modes()).is_on(&language_intelligence_key(root))
}

/// Turn full mode on or off for one workspace. Turning it ON starts nothing by
/// itself — the next file opened in that workspace does that — which is what
/// lets a saved choice be restored at launch without waking any servers.
pub(crate) fn set_language_intelligence(root: &str, enabled: bool) -> LanguageIntelligenceChange {
    let key = language_intelligence_key(root);
    let change = locked(language_intelligence_modes()).set(&key, enabled);
    if change.changed() && !enabled {
        record_language_server_state(
            "csharp",
            &key,
            LanguageServerState::Disabled,
            Some(READ_MODE_DETAIL.to_string()),
        );
    }
    change
}

/// What the editor says when a workspace is in read mode.
const READ_MODE_DETAIL: &str =
    "Language intelligence is off for this workspace. Files open with colouring only.";

/// What asking for one workspace's language server did.
///
/// The switch asks the moment it is turned on, and most of these answers are
/// the ones where nothing started. Each has to be something a reader can be
/// shown: silence is what made the switch look broken.
#[derive(Debug, Clone, PartialEq, Eq)]
pub(crate) enum LanguageServerStart {
    /// A server for this language is running for this workspace.
    Running { server_name: &'static str },
    /// This app has no language server for that language.
    NoServerForLanguage,
    /// There is one, but its program is not installed on this machine.
    NotInstalled {
        server_name: &'static str,
        command: &'static str,
    },
    /// C# starts through the desktop app's own language client, not here.
    NativeCsharpClient,
    /// Every language server is switched off in Settings.
    SwitchedOff,
    /// This language server is switched off in Settings.
    ServerSwitchedOff,
    /// C# is switched off in Settings.
    CsharpSwitchedOff,
    /// Nothing under this workspace is a C# project.
    NoCsharpProject,
    /// The workspace is in read mode, so nothing may start.
    ReadMode,
}

/// May a server for this language be started or reused right now?
fn language_server_allowed(language_id: &str) -> bool {
    language_servers_enabled() && language_server_enabled(language_id)
}

/// C# is owned by the VS Code-compatible Monaco language client in the desktop app.
///
/// Keep the original stdio router compiled for now, but do not let ordinary source-
/// intelligence requests or workspace warming start a second Roslyn process beside
/// the native client. Other languages still use the legacy registry unchanged.
fn legacy_language_server_allowed(language_id: &str) -> bool {
    language_id != "csharp" && language_server_allowed(language_id)
}

/// Did the reader start the app asking to be told how long things take?
///
/// Set `MCB_TIMING=1` in the environment. Off by default, and read once: this is
/// checked on every language-server question and every counting pass.
pub(crate) fn timing_enabled() -> bool {
    static ASKED_FOR_TIMINGS: OnceLock<bool> = OnceLock::new();
    *ASKED_FOR_TIMINGS.get_or_init(|| env::var("MCB_TIMING").is_ok_and(|value| value == "1"))
}

/// Take a lock, and keep working even if some earlier thread panicked while holding it.
///
/// Every lock in this file guards a plain map or a list — there is no half-finished
/// state that a panic could leave behind and no invariant to protect. Refusing to
/// answer for the rest of the app's life because one thread died is the worse outcome.
fn locked<T>(mutex: &Mutex<T>) -> MutexGuard<'_, T> {
    mutex
        .lock()
        .unwrap_or_else(|poisoned| poisoned.into_inner())
}

/// What a language server is doing right now, in words the editor can put on screen.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub(crate) enum LanguageServerState {
    /// No server for this language is running.
    NotRunning,
    /// The process has been started but has not finished its opening exchange.
    Starting,
    /// The server is running but busy reading the project, so its answers are
    /// incomplete until it finishes.
    Indexing,
    /// The server has read the project and is answering questions.
    Ready,
    /// The reader has switched this server off in Settings.
    Disabled,
}

impl LanguageServerState {
    /// The exact word the editor receives. These five strings are a promise to the
    /// frontend — do not reword them without changing it too.
    fn as_str(self) -> &'static str {
        match self {
            LanguageServerState::NotRunning => "not-running",
            LanguageServerState::Starting => "starting",
            LanguageServerState::Indexing => "indexing",
            LanguageServerState::Ready => "ready",
            LanguageServerState::Disabled => "disabled",
        }
    }
}

/// Sent to the editor every time a language server changes what it is doing, so the
/// editor never has to ask again on a timer.
#[derive(Debug, Clone, serde::Serialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub(crate) struct SourceLspStatusChange {
    /// The project folder the server is pointed at.
    pub(crate) root: String,
    /// The server's own language name — `csharp`, `rust`, `typescript`, `svelte` —
    /// which is the `languageID` field of a status reading, not the file's own
    /// language. One TypeScript server serves `.ts`, `.tsx`, `.js` and `.jsx` alike.
    pub(crate) language: String,
    pub(crate) state: String,
    pub(crate) detail: Option<String>,
}

/// Which server a connection is speaking for, so a message arriving on its pipe can be
/// turned into something the editor can show.
#[derive(Debug, Clone)]
struct LanguageServerIdentity {
    language_id: String,
    server_name: String,
    root: String,
}

/// The last thing each workspace-scoped language server was known to be doing.
///
/// This must have the same `(language, root)` identity as `SourceLspRegistry`.
/// Keying only by language lets one workspace's Roslyn progress overwrite another
/// workspace's readiness, which makes the editor hold or release the wrong counts.
type LanguageServerActivityKey = (String, String);
type LanguageServerActivity = (LanguageServerState, Option<String>);

fn language_server_activity(
) -> &'static Mutex<HashMap<LanguageServerActivityKey, LanguageServerActivity>> {
    static ACTIVITY: OnceLock<Mutex<HashMap<LanguageServerActivityKey, LanguageServerActivity>>> =
        OnceLock::new();
    ACTIVITY.get_or_init(|| Mutex::new(HashMap::new()))
}

fn language_server_activity_key(language_id: &str, root: &str) -> LanguageServerActivityKey {
    (
        language_id.to_string(),
        normalized_lsp_root(root).unwrap_or_else(|| root.to_string()),
    )
}

type SourceLspStatusListener = Arc<dyn Fn(SourceLspStatusChange) + Send + Sync>;

fn status_listener_slot() -> &'static Mutex<Option<SourceLspStatusListener>> {
    static LISTENER: OnceLock<Mutex<Option<SourceLspStatusListener>>> = OnceLock::new();
    LISTENER.get_or_init(|| Mutex::new(None))
}

/// Ask to be told every time a language server changes what it is doing. The app sets
/// this once at startup so the change can be forwarded to the editor.
pub(crate) fn set_source_lsp_status_listener(listener: SourceLspStatusListener) {
    *locked(status_listener_slot()) = Some(listener);
}

#[cfg(test)]
fn clear_source_lsp_status_listener() {
    *locked(status_listener_slot()) = None;
}

/// Write down what a language server is now doing, and tell the editor — but only when
/// something actually changed, so nothing is announced twice.
fn record_language_server_state(
    language_id: &str,
    root: &str,
    state: LanguageServerState,
    detail: Option<String>,
) {
    let root = language_server_activity_key(language_id, root).1;
    let change = {
        let mut activity = locked(language_server_activity());
        let entry = activity
            .entry((language_id.to_string(), root.clone()))
            .or_insert((LanguageServerState::NotRunning, None));
        if entry.0 == state && entry.1 == detail {
            None
        } else {
            *entry = (state, detail.clone());
            Some(SourceLspStatusChange {
                root: root.clone(),
                language: language_id.to_string(),
                state: state.as_str().to_string(),
                detail,
            })
        }
    };

    // Outside the lock on purpose: the listener hands the change to the editor, and
    // nothing in this file should be blocked behind that.
    if let Some(change) = change {
        let listener = locked(status_listener_slot()).clone();
        if let Some(listener) = listener {
            listener(change);
        }
    }
}

/// What this language's server at this exact workspace was last known to be doing.
/// `None` when that workspace slot has never been started.
fn read_language_server_activity(
    language_id: &str,
    root: &Path,
) -> Option<(LanguageServerState, Option<String>)> {
    let key = language_server_activity_key(language_id, &root.display().to_string());
    locked(language_server_activity())
        .get(&key)
        .map(|(state, detail)| (*state, detail.clone()))
}

fn read_language_server_roots(language_id: &str) -> Vec<String> {
    locked(language_server_activity())
        .keys()
        .filter(|(language, _)| language == language_id)
        .map(|(_, root)| root.clone())
        .collect()
}

#[derive(Debug, Clone, serde::Deserialize, serde::Serialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct SourceLspPreview {
    path: String,
    relative_path: String,
    file_name: String,
    language: String,
    byte_count: u64,
    content: String,
    line_count: usize,
}

#[derive(Debug, Clone, serde::Deserialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct SourceLspLookupRequest {
    root: String,
    line: usize,
    column: usize,
    limit: Option<usize>,
}

#[derive(Debug, Clone, serde::Deserialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct SourceLspRenameRequest {
    root: String,
    line: usize,
    column: usize,
    new_name: String,
}

#[derive(Debug, Clone, serde::Deserialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct SourceLspWorkspaceSymbolRequest {
    root: String,
    query: String,
    limit: Option<usize>,
}

#[derive(Debug, Clone, serde::Deserialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct SourceLspCodeActionDiagnostic {
    severity: String,
    message: String,
    start_line: usize,
    start_column: usize,
    end_line: usize,
    end_column: usize,
    source: Option<String>,
}

#[derive(Debug, Clone, serde::Deserialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct SourceLspCodeActionRequest {
    root: String,
    start_line: usize,
    start_column: usize,
    end_line: usize,
    end_column: usize,
    diagnostics: Vec<SourceLspCodeActionDiagnostic>,
    limit: Option<usize>,
}

#[derive(Debug, Clone, serde::Serialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub(crate) struct SourceLspStatus {
    language: String,
    #[serde(rename = "languageID")]
    language_id: String,
    available: bool,
    server_name: String,
    command: String,
    args: Vec<String>,
    reason: Option<String>,
    /// What this server is doing right now: `not-running`, `starting`, `indexing`,
    /// `ready` or `disabled`. The editor shows this beside the file name.
    state: String,
    /// One plain sentence expanding on `state`, or nothing when there is no more to say.
    detail: Option<String>,
}

#[derive(Debug, Clone, serde::Serialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub(crate) struct SourceLspHover {
    contents: Vec<String>,
}

#[derive(Debug, Clone, serde::Serialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub(crate) struct SourceLspSignatureParameter {
    label: String,
    documentation: String,
}

#[derive(Debug, Clone, serde::Serialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub(crate) struct SourceLspSignature {
    label: String,
    documentation: String,
    parameters: Vec<SourceLspSignatureParameter>,
}

#[derive(Debug, Clone, serde::Serialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub(crate) struct SourceLspSignatureHelp {
    signatures: Vec<SourceLspSignature>,
    active_signature: usize,
    active_parameter: usize,
}

#[derive(Debug, Clone, serde::Serialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub(crate) struct SourceLspInlayHint {
    label: String,
    tooltip: String,
    kind: String,
    line: usize,
    column: usize,
    padding_left: bool,
    padding_right: bool,
}

#[derive(Debug, Clone, serde::Serialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub(crate) struct SourceLspSemanticToken {
    token_type: String,
    line: usize,
    start_column: usize,
    length: usize,
}

#[derive(Debug, Clone, serde::Serialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub(crate) struct SourceLspDiagnostic {
    pub(crate) severity: String,
    pub(crate) message: String,
    pub(crate) line: usize,
    pub(crate) column: usize,
    pub(crate) source: Option<String>,
    /// Which file the language server was complaining about. The per-file read already
    /// knows the answer, but a whole-project list has to carry it on every row.
    pub(crate) path: Option<String>,
}

impl SourceLspDiagnostic {
    #[cfg(test)]
    pub(crate) fn for_test(
        severity: &str,
        message: &str,
        line: usize,
        column: usize,
        path: Option<&str>,
    ) -> Self {
        SourceLspDiagnostic {
            severity: severity.to_string(),
            message: message.to_string(),
            line,
            column,
            source: None,
            path: path.map(str::to_string),
        }
    }
}

#[derive(Debug, Clone, serde::Serialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub(crate) struct SourceLspSymbol {
    name: String,
    kind: String,
    line: usize,
    column: usize,
    detail: String,
}

#[derive(Debug, Clone, serde::Serialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub(crate) struct SourceLspWorkspaceSymbol {
    path: String,
    relative_path: String,
    file_name: String,
    language: String,
    byte_count: u64,
    symbol_name: String,
    kind: String,
    line: usize,
    column: usize,
    detail: String,
    container_name: Option<String>,
}

#[derive(Debug, Clone, serde::Serialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub(crate) struct SourceLspDocumentHighlight {
    start_line: usize,
    start_column: usize,
    end_line: usize,
    end_column: usize,
    kind: String,
}

#[derive(Debug, Clone, serde::Serialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub(crate) struct SourceLspCompletionItem {
    label: String,
    kind: String,
    detail: String,
    insert_text: String,
}

#[derive(Debug, Clone, serde::Serialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub(crate) struct SourceLspTextEdit {
    start_line: usize,
    start_column: usize,
    end_line: usize,
    end_column: usize,
    new_text: String,
}

#[derive(Debug, Clone, serde::Serialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub(crate) struct SourceLspWorkspaceEditFile {
    path: String,
    relative_path: String,
    edits: Vec<SourceLspTextEdit>,
}

#[derive(Debug, Clone, serde::Serialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub(crate) struct SourceLspRenameResult {
    files: Vec<SourceLspWorkspaceEditFile>,
}

#[derive(Debug, Clone, serde::Serialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub(crate) struct SourceLspCodeAction {
    title: String,
    kind: String,
    is_preferred: bool,
    disabled_reason: Option<String>,
    files: Vec<SourceLspWorkspaceEditFile>,
}

#[derive(Debug, Clone, serde::Serialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub(crate) struct SourceLspDefinitionTarget {
    path: String,
    relative_path: String,
    file_name: String,
    language: String,
    byte_count: u64,
    symbol_name: String,
    kind: String,
    line: usize,
    column: usize,
    detail: String,
}

#[derive(Debug, Clone, serde::Serialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub(crate) struct SourceLspReferenceTarget {
    path: String,
    relative_path: String,
    file_name: String,
    language: String,
    byte_count: u64,
    symbol_name: String,
    line: usize,
    column: usize,
    excerpt: String,
}

#[derive(Debug, Clone, Copy)]
struct LspServerSpec {
    server_name: &'static str,
    language_id: &'static str,
    command: &'static str,
    args: &'static [&'static str],
}

#[derive(Debug, Clone)]
struct ResolvedLspServer {
    spec: LspServerSpec,
    command: String,
}

#[derive(Debug)]
struct LspLocation {
    path: PathBuf,
    line: usize,
    column: usize,
}

/// One symbol in a file, as the editor's margin counts want it: no nesting, and both
/// numbers counted from zero, which is how the editor counts.
#[derive(Debug, Clone, serde::Serialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub(crate) struct SourceLspDocumentSymbol {
    name: String,
    kind: String,
    line: usize,
    character: usize,
}

/// A job the server has told us it is busy with.
struct RunningJob {
    token: String,
    title: String,
    message: Option<String>,
    percentage: Option<u64>,
}

impl RunningJob {
    /// One plain sentence a reader can understand, built out of whatever the server
    /// chose to say about the job.
    fn sentence(&self) -> String {
        let mut sentence = if self.title.is_empty() {
            "Reading the project".to_string()
        } else {
            self.title.clone()
        };
        if let Some(message) = &self.message {
            sentence.push_str(": ");
            sentence.push_str(message);
        }
        if let Some(percentage) = self.percentage {
            sentence.push_str(&format!(" ({percentage}% done)"));
        }
        sentence
    }
}

/// What the server has said about mistakes in each file, and how many times it has said
/// it. The count is what lets a fresh reading wait for a fresh answer instead of handing
/// back what the server said before the file was edited.
#[derive(Default)]
struct PublishedDiagnostics {
    by_uri: HashMap<String, Vec<SourceLspDiagnostic>>,
    times_published: HashMap<String, u64>,
}

/// The half of a language-server connection that everything shares: the pipe out to the
/// server, the list of questions still waiting for an answer, and everything the server
/// has told us without being asked.
///
/// One background thread reads the server's pipe and hands each answer to whichever
/// waiter asked the question, which is what lets several questions be outstanding at
/// once. Before this, one question at a time held the whole connection — and a question
/// may take six seconds.
struct LspRouter {
    identity: LanguageServerIdentity,
    /// Locked only long enough to put one message on the pipe.
    pipe_to_server: Mutex<Box<dyn Write + Send>>,
    /// Who is waiting for which answer, by the question's number.
    waiting: Mutex<HashMap<i64, mpsc::Sender<Value>>>,
    diagnostics: Mutex<PublishedDiagnostics>,
    /// Woken every time the server publishes a fresh list of mistakes.
    fresh_diagnostics: Condvar,
    /// Set when the server's pipe went quiet, with the plain-English reason.
    stopped: Mutex<Option<String>>,
    running_jobs: Mutex<Vec<RunningJob>>,
    /// The last few hundred lines the server printed to its own error output.
    recent_log: Mutex<VecDeque<String>>,
    /// Turned off when this connection is being retired, so a replacement that has
    /// already reported itself ready is not immediately contradicted by the old one
    /// reporting that it stopped.
    reporting: AtomicBool,
}

impl LspRouter {
    fn write_message(&self, message: &Value) -> Result<(), String> {
        let mut pipe = locked(&self.pipe_to_server);
        write_lsp_message(&mut *pipe, message)
    }

    /// Reserve a slot for the answer to question `id` before the question is sent, so an
    /// answer that comes back immediately is never missed.
    fn expect_answer(&self, id: i64) -> mpsc::Receiver<Value> {
        let (sender, receiver) = mpsc::channel();
        locked(&self.waiting).insert(id, sender);
        receiver
    }

    fn stop_waiting_for(&self, id: i64) {
        locked(&self.waiting).remove(&id);
    }

    fn stop_reason(&self) -> Option<String> {
        locked(&self.stopped).clone()
    }

    fn report(&self, state: LanguageServerState, detail: Option<String>) {
        if !self.reporting.load(Ordering::Relaxed) {
            return;
        }
        record_language_server_state(
            &self.identity.language_id,
            &self.identity.root,
            state,
            detail,
        );
    }

    /// Stop reporting what this connection is doing, because it is being replaced.
    fn stop_reporting(&self) {
        self.reporting.store(false, Ordering::Relaxed);
    }

    /// The server's pipe went quiet. Wake every waiter — dropping their slots is what
    /// tells them nobody is going to answer — and say so.
    fn note_the_server_went_quiet(&self, reason: String) {
        *locked(&self.stopped) = Some(reason);
        locked(&self.waiting).clear();
        locked(&self.running_jobs).clear();
        self.fresh_diagnostics.notify_all();
        self.report(
            LanguageServerState::NotRunning,
            Some("The language server stopped running.".to_string()),
        );
    }

    /// Hand one message from the server to whoever it is for.
    fn deliver(&self, message: Value) {
        let Some(method) = message.get("method").and_then(Value::as_str) else {
            // No method means this is an answer to something we asked.
            let Some(id) = message.get("id").and_then(Value::as_i64) else {
                return;
            };
            let waiter = locked(&self.waiting).remove(&id);
            if let Some(waiter) = waiter {
                let _ = waiter.send(message);
            }
            return;
        };
        let method = method.to_string();

        if let Some(id) = message.get("id").cloned() {
            // A method AND a number means the server is asking US something. We do none
            // of the things it can ask for, but a question left hanging makes some
            // servers wait forever, so every one gets an answer.
            self.answer_the_servers_own_question(id, &method, message.get("params"));
            return;
        }

        match method.as_str() {
            "textDocument/publishDiagnostics" => self.record_published_diagnostics(&message),
            "$/progress" => self.record_progress(&message),
            _ => {}
        }
    }

    fn answer_the_servers_own_question(&self, id: Value, method: &str, params: Option<&Value>) {
        // The one shape that is not simply "nothing": a request for settings expects one
        // answer per setting asked about, and a bare null makes some servers give up.
        let result = if method == "workspace/configuration" {
            workspace_configuration_result(params)
        } else {
            Value::Null
        };
        let _ = self.write_message(&json!({
            "jsonrpc": "2.0",
            "id": id,
            "result": result
        }));
    }

    fn record_published_diagnostics(&self, message: &Value) {
        let Some(uri) = diagnostic_uri_from_message(message) else {
            return;
        };
        let items = diagnostics_from_message(message, &uri);
        {
            let mut diagnostics = locked(&self.diagnostics);
            diagnostics.by_uri.insert(uri.clone(), items);
            *diagnostics.times_published.entry(uri).or_insert(0) += 1;
        }
        self.fresh_diagnostics.notify_all();
    }

    fn record_progress(&self, message: &Value) {
        let Some(params) = message.get("params") else {
            return;
        };
        let Some(token) = progress_token(params) else {
            return;
        };
        let Some(value) = params.get("value") else {
            return;
        };
        let kind = value
            .get("kind")
            .and_then(Value::as_str)
            .unwrap_or_default();

        let still_running = {
            let mut jobs = locked(&self.running_jobs);
            match kind {
                "begin" | "report" => {
                    let title = progress_text(value, "title");
                    let message = progress_text(value, "message");
                    let percentage = value.get("percentage").and_then(Value::as_u64);
                    match jobs.iter_mut().find(|job| job.token == token) {
                        Some(job) => {
                            if let Some(title) = title {
                                job.title = title;
                            }
                            if message.is_some() {
                                job.message = message;
                            }
                            if percentage.is_some() {
                                job.percentage = percentage;
                            }
                        }
                        None => jobs.push(RunningJob {
                            token,
                            title: title.unwrap_or_default(),
                            message,
                            percentage,
                        }),
                    }
                }
                "end" => jobs.retain(|job| job.token != token),
                _ => return,
            }
            jobs.first().map(RunningJob::sentence)
        };

        match still_running {
            Some(sentence) => self.report(LanguageServerState::Indexing, Some(sentence)),
            None => self.report(LanguageServerState::Ready, None),
        }
    }

    fn record_log_line(&self, line: String) {
        let mut log = locked(&self.recent_log);
        if log.len() >= MAX_LSP_LOG_LINES {
            log.pop_front();
        }
        log.push_back(line);
    }
}

/// Answer one configuration value for every item the server asked about.
///
/// Roslyn names its CodeLens settings under `csharp|code_lens`. Reference rows
/// stay enabled because Monaco renders those answers in the editor. Roslyn's
/// test lenses stay off because Assembly supplies Build and Test rows
/// that start real, workspace-owned terminal sessions instead of editor-only
/// commands the LSP bridge cannot represent.
fn workspace_configuration_result(params: Option<&Value>) -> Value {
    let values = params
        .and_then(|params| params.get("items"))
        .and_then(Value::as_array)
        .map(|items| {
            items
                .iter()
                .map(|item| {
                    let section = item
                        .get("section")
                        .and_then(Value::as_str)
                        .unwrap_or_default();
                    match section {
                        "csharp|background_analysis.dotnet_analyzer_diagnostics_scope"
                        | "csharp|background_analysis.dotnet_compiler_diagnostics_scope" => {
                            Value::String("openFiles".to_string())
                        }
                        "csharp|code_lens.dotnet_enable_references_code_lens" => Value::Bool(true),
                        "csharp|code_lens.dotnet_enable_tests_code_lens" => Value::Bool(false),
                        _ => Value::Null,
                    }
                })
                .collect::<Vec<_>>()
        })
        .unwrap_or_default();

    Value::Array(values)
}

/// A live connection to one language server: everything needed to ask it a question and
/// get the answer back, with several questions allowed to be outstanding at once.
struct LspConnection {
    router: Arc<LspRouter>,
    /// The number the next question will carry. One is spent on the opening exchange.
    next_id: AtomicI64,
    /// Which files the server has been shown, including the exact text in its current
    /// snapshot. Repeating a lookup against unchanged text must not manufacture a new
    /// `didChange` notification.
    documents: Mutex<HashMap<String, OpenDocument>>,
    /// Roslyn and some other servers request incremental document changes. A full-text
    /// replacement is still one valid incremental edit, but it must carry the range of
    /// the text being replaced.
    uses_incremental_document_sync: bool,
    semantic_token_types: Vec<String>,
    /// Some servers expose more than one pull-diagnostics source and identify the
    /// document source they advertised. Echoing it back selects the complete report.
    diagnostic_identifier: Option<String>,
    /// The server's own process, when there is one. Tests wire a connection to a pipe
    /// with no process behind it.
    child: Mutex<Option<Child>>,
}

struct OpenDocument {
    version: i32,
    content: String,
}

impl LspConnection {
    /// Wire up a connection over an already-open pair of pipes and play the opening
    /// exchange, so the caller gets back something ready to be asked questions.
    fn connect(
        from_server: Box<dyn Read + Send>,
        to_server: Box<dyn Write + Send>,
        identity: LanguageServerIdentity,
    ) -> Result<Arc<LspConnection>, String> {
        let router = Arc::new(LspRouter {
            identity,
            pipe_to_server: Mutex::new(to_server),
            waiting: Mutex::new(HashMap::new()),
            diagnostics: Mutex::new(PublishedDiagnostics::default()),
            fresh_diagnostics: Condvar::new(),
            stopped: Mutex::new(None),
            running_jobs: Mutex::new(Vec::new()),
            recent_log: Mutex::new(VecDeque::new()),
            reporting: AtomicBool::new(true),
        });
        router.report(
            LanguageServerState::Starting,
            Some(format!("Starting {}.", router.identity.server_name)),
        );

        {
            let router = Arc::clone(&router);
            let mut from_server = from_server;
            thread::spawn(move || loop {
                match read_lsp_message(&mut from_server) {
                    Ok(message) => router.deliver(message),
                    Err(error) => {
                        router.note_the_server_went_quiet(format!(
                            "The language server stopped sending messages: {error}"
                        ));
                        break;
                    }
                }
            });
        }

        let root = Path::new(&router.identity.root);
        let root_uri = path_to_file_uri(root);
        let workspace_name = root
            .file_name()
            .and_then(|name| name.to_str())
            .filter(|name| !name.is_empty())
            .unwrap_or("workspace");
        let opening = ask_the_server(
            &router,
            1,
            "initialize",
            json!({
                "processId": std::process::id(),
                "clientInfo": {
                    "name": crate::product_identity::LSP_CLIENT_NAME,
                    "version": env!("CARGO_PKG_VERSION")
                },
                "rootPath": router.identity.root,
                "rootUri": root_uri.clone(),
                "workspaceFolders": [{
                    "uri": root_uri,
                    "name": workspace_name
                }],
                "capabilities": lsp_client_capabilities()
            }),
            LSP_REQUEST_TIMEOUT,
            false,
        );
        let opening = match opening {
            Ok(opening) => opening,
            Err(error) => {
                router.report(LanguageServerState::NotRunning, Some(error.clone()));
                return Err(error);
            }
        };
        if let Some(error) = opening.get("error") {
            let error = format!("Language server initialize failed: {error}");
            router.report(LanguageServerState::NotRunning, Some(error.clone()));
            return Err(error);
        }
        let semantic_token_types = lsp_semantic_token_types_from_initialize(&opening);
        let uses_incremental_document_sync =
            lsp_text_document_sync_kind_from_initialize(&opening) == 2;
        let diagnostic_identifier = lsp_diagnostic_identifier_from_initialize(&opening);
        router.write_message(&json!({
            "jsonrpc": "2.0",
            "method": "initialized",
            "params": {}
        }))?;
        router.report(LanguageServerState::Ready, None);

        Ok(Arc::new(LspConnection {
            router,
            next_id: AtomicI64::new(2),
            documents: Mutex::new(HashMap::new()),
            uses_incremental_document_sync,
            semantic_token_types,
            diagnostic_identifier,
            child: Mutex::new(None),
        }))
    }

    /// Ask the server one question and wait for its answer. Several of these may be
    /// running at once on the same connection.
    fn send_request(
        &self,
        method: &str,
        params: Value,
        timeout: Duration,
        withdraw_if_late: bool,
    ) -> Result<Option<Value>, String> {
        for attempt in 0..2 {
            let id = self.next_id.fetch_add(1, Ordering::Relaxed);
            let answer = ask_the_server(
                &self.router,
                id,
                method,
                params.clone(),
                timeout,
                withdraw_if_late,
            )?;
            if let Some(error) = answer.get("error") {
                // Roslyn cancels otherwise-valid questions while its project snapshot is
                // being replaced during the first load. One short retry turns that
                // implementation detail into the same eventual answer other servers give.
                if attempt == 0 && error.get("code").and_then(Value::as_i64) == Some(-32800) {
                    thread::sleep(LSP_SERVER_CANCEL_RETRY_DELAY);
                    continue;
                }
                return Err(format!("Language server request failed: {error}"));
            }
            return Ok(answer.get("result").cloned());
        }

        Err("Language server cancelled the request twice".to_string())
    }

    fn request(
        &self,
        preview: &SourceLspPreview,
        request: &SourceLspLookupRequest,
        method: &str,
    ) -> Result<Option<Value>, String> {
        let file_uri = self.ensure_document_open(preview)?;
        let params = match method {
            "textDocument/documentSymbol" => json!({
                "textDocument": { "uri": file_uri }
            }),
            "textDocument/references" => json!({
                "textDocument": { "uri": file_uri },
                "position": lsp_position(request),
                "context": { "includeDeclaration": true }
            }),
            "textDocument/formatting" => json!({
                "textDocument": { "uri": file_uri },
                "options": {
                    "tabSize": 4,
                    "insertSpaces": true,
                    "trimTrailingWhitespace": true,
                    "insertFinalNewline": true,
                    "trimFinalNewlines": true
                }
            }),
            "textDocument/inlayHint" => json!({
                "textDocument": { "uri": file_uri },
                "range": lsp_full_document_range(preview)
            }),
            _ => json!({
                "textDocument": { "uri": file_uri },
                "position": lsp_position(request)
            }),
        };

        self.send_request(method, params, LSP_REQUEST_TIMEOUT, true)
    }

    fn request_rename(
        &self,
        preview: &SourceLspPreview,
        request: &SourceLspRenameRequest,
    ) -> Result<Option<Value>, String> {
        let file_uri = self.ensure_document_open(preview)?;
        self.send_request(
            "textDocument/rename",
            json!({
                "textDocument": { "uri": file_uri },
                "position": {
                    "line": request.line.saturating_sub(1),
                    "character": request.column.saturating_sub(1)
                },
                "newName": request.new_name.clone()
            }),
            LSP_REQUEST_TIMEOUT,
            true,
        )
    }

    fn request_code_actions(
        &self,
        preview: &SourceLspPreview,
        request: &SourceLspCodeActionRequest,
    ) -> Result<Option<Value>, String> {
        let file_uri = self.ensure_document_open(preview)?;
        self.send_request(
            "textDocument/codeAction",
            json!({
                "textDocument": { "uri": file_uri },
                "range": lsp_code_action_range(request),
                "context": {
                    "diagnostics": request
                        .diagnostics
                        .iter()
                        .map(lsp_code_action_diagnostic)
                        .collect::<Vec<_>>()
                }
            }),
            LSP_REQUEST_TIMEOUT,
            true,
        )
    }

    fn request_semantic_tokens(&self, preview: &SourceLspPreview) -> Result<Option<Value>, String> {
        let file_uri = self.ensure_document_open(preview)?;
        self.send_request(
            "textDocument/semanticTokens/full",
            json!({
                "textDocument": { "uri": file_uri }
            }),
            LSP_REQUEST_TIMEOUT,
            true,
        )
    }

    fn request_workspace_symbols(
        &self,
        preview: &SourceLspPreview,
        query: &str,
    ) -> Result<Option<Value>, String> {
        self.ensure_document_open(preview)?;
        self.send_request(
            "workspace/symbol",
            json!({ "query": query }),
            LSP_REQUEST_TIMEOUT,
            true,
        )
    }

    fn read_diagnostics(
        &self,
        preview: &SourceLspPreview,
    ) -> Result<Vec<SourceLspDiagnostic>, String> {
        let already_said =
            self.times_diagnostics_published(&path_to_file_uri(Path::new(&preview.path)));
        let file_uri = self.ensure_document_open(preview)?;
        // A fresh answer if the server gives one in time; failing that, the last thing it
        // said about this file, which is all there ever was before.
        let published = self
            .wait_for_fresh_diagnostics(&file_uri, already_said)
            .unwrap_or_else(|| self.last_published_diagnostics(&file_uri));
        if !published.is_empty() {
            return Ok(published);
        }

        Ok(self.request_pull_diagnostics(&file_uri).unwrap_or_default())
    }

    fn times_diagnostics_published(&self, file_uri: &str) -> u64 {
        locked(&self.router.diagnostics)
            .times_published
            .get(file_uri)
            .copied()
            .unwrap_or(0)
    }

    fn last_published_diagnostics(&self, file_uri: &str) -> Vec<SourceLspDiagnostic> {
        locked(&self.router.diagnostics)
            .by_uri
            .get(file_uri)
            .cloned()
            .unwrap_or_default()
    }

    /// Wait, up to the diagnostics deadline, for the server to say something NEW about
    /// this file — `None` if it says nothing new in that time.
    ///
    /// This is why the app can answer the moment the server speaks instead of always
    /// sitting out the full wait: the count of how many times the server has spoken about
    /// a file is what tells a fresh answer apart from the one it gave before the edit.
    fn wait_for_fresh_diagnostics(
        &self,
        file_uri: &str,
        already_said: u64,
    ) -> Option<Vec<SourceLspDiagnostic>> {
        let deadline = Instant::now() + LSP_DIAGNOSTICS_TIMEOUT;
        let mut diagnostics = locked(&self.router.diagnostics);
        loop {
            let times_said = diagnostics
                .times_published
                .get(file_uri)
                .copied()
                .unwrap_or(0);
            if times_said > already_said {
                return Some(
                    diagnostics
                        .by_uri
                        .get(file_uri)
                        .cloned()
                        .unwrap_or_default(),
                );
            }
            let now = Instant::now();
            if now >= deadline {
                return None;
            }
            let (guard, _) = self
                .router
                .fresh_diagnostics
                .wait_timeout(diagnostics, deadline.saturating_duration_since(now))
                .unwrap_or_else(|poisoned| poisoned.into_inner());
            diagnostics = guard;
        }
    }

    fn request_pull_diagnostics(&self, file_uri: &str) -> Option<Vec<SourceLspDiagnostic>> {
        let mut params = json!({ "textDocument": { "uri": file_uri } });
        if let Some(identifier) = &self.diagnostic_identifier {
            params["identifier"] = Value::String(identifier.clone());
        }
        let result = self
            .send_request(
                "textDocument/diagnostic",
                params,
                LSP_DIAGNOSTICS_TIMEOUT,
                true,
            )
            .ok()??;
        let diagnostics = diagnostics_from_pull_result(&result, file_uri);
        if !diagnostics.is_empty() {
            let mut published = locked(&self.router.diagnostics);
            published
                .by_uri
                .insert(file_uri.to_string(), diagnostics.clone());
            *published
                .times_published
                .entry(file_uri.to_string())
                .or_insert(0) += 1;
        }
        Some(diagnostics)
    }

    /// Show the server this file, or tell it the file changed. The lock is held across
    /// the write on purpose: two questions about the same file arriving at once must not
    /// both decide they are the first to open it.
    fn ensure_document_open(&self, preview: &SourceLspPreview) -> Result<String, String> {
        let file_uri = path_to_file_uri(Path::new(&preview.path));
        let mut documents = locked(&self.documents);
        let Some(open_document) = documents.get_mut(&file_uri) else {
            let version = 1;
            self.router.write_message(&json!({
                "jsonrpc": "2.0",
                "method": "textDocument/didOpen",
                "params": {
                    "textDocument": {
                        "uri": file_uri,
                        "languageId": self.router.identity.language_id,
                        "version": version,
                        "text": preview.content
                    }
                }
            }))?;
            documents.insert(
                file_uri.clone(),
                OpenDocument {
                    version,
                    content: preview.content.clone(),
                },
            );
            return Ok(file_uri);
        };

        if open_document.content == preview.content {
            return Ok(file_uri);
        }

        let next_version = open_document.version + 1;
        let content_change = if self.uses_incremental_document_sync {
            json!({
                "range": {
                    "start": { "line": 0, "character": 0 },
                    "end": lsp_document_end_position(&open_document.content)
                },
                "text": preview.content
            })
        } else {
            json!({ "text": preview.content })
        };
        self.router.write_message(&json!({
            "jsonrpc": "2.0",
            "method": "textDocument/didChange",
            "params": {
                "textDocument": {
                    "uri": file_uri,
                    "version": next_version
                },
                "contentChanges": [content_change]
            }
        }))?;
        open_document.version = next_version;
        open_document.content.clone_from(&preview.content);
        Ok(file_uri)
    }

    fn published_diagnostics(&self) -> HashMap<String, Vec<SourceLspDiagnostic>> {
        locked(&self.router.diagnostics).by_uri.clone()
    }

    fn recent_log_lines(&self) -> Vec<String> {
        locked(&self.router.recent_log).iter().cloned().collect()
    }

    fn is_alive(&self) -> bool {
        if self.router.stop_reason().is_some() {
            return false;
        }
        match locked(&self.child).as_mut() {
            Some(child) => matches!(child.try_wait(), Ok(None)),
            None => true,
        }
    }

    fn attach_child(&self, child: Child) {
        *locked(&self.child) = Some(child);
    }

    /// Say goodbye properly, then make sure the process is gone.
    ///
    /// Reporting is switched off first: this connection is on its way out, and a
    /// replacement may already have announced itself as ready.
    fn shut_down(&self) {
        self.router.stop_reporting();
        let id = self.next_id.fetch_add(1, Ordering::Relaxed);
        let _ = self.router.write_message(&json!({
            "jsonrpc": "2.0",
            "id": id,
            "method": "shutdown"
        }));
        let _ = self.router.write_message(&json!({
            "jsonrpc": "2.0",
            "method": "exit"
        }));
        if let Some(child) = locked(&self.child).as_mut() {
            // A server that takes its own goodbye leaves cleanly, flushing
            // whatever it was holding. Only one that ignores it gets stopped,
            // and only ever this app's own child.
            wait_for_lsp_exit(child, LSP_SHUTDOWN_GRACE);
            stop_lsp_process_tree(child);
        }
    }
}

/// Send one question and wait for its answer, withdrawing the question if we give up.
///
/// Withdrawing matters: giving up on our side does nothing to the server, which will
/// happily keep working on a question whose answer nobody will ever read.
fn ask_the_server(
    router: &Arc<LspRouter>,
    id: i64,
    method: &str,
    params: Value,
    timeout: Duration,
    withdraw_if_late: bool,
) -> Result<Value, String> {
    let started = Instant::now();
    let waiting_for_answer = router.expect_answer(id);
    if let Err(error) = router.write_message(&json!({
        "jsonrpc": "2.0",
        "id": id,
        "method": method,
        "params": params
    })) {
        router.stop_waiting_for(id);
        return Err(error);
    }

    match waiting_for_answer.recv_timeout(timeout) {
        Ok(answer) => {
            router.stop_waiting_for(id);
            log_lsp_timing(router, method, started, "was answered");
            Ok(answer)
        }
        Err(mpsc::RecvTimeoutError::Timeout) => {
            router.stop_waiting_for(id);
            if withdraw_if_late {
                let _ = router.write_message(&json!({
                    "jsonrpc": "2.0",
                    "method": "$/cancelRequest",
                    "params": { "id": id }
                }));
                log_lsp_timing(router, method, started, "took too long and was withdrawn");
            } else {
                log_lsp_timing(router, method, started, "took too long");
            }
            Err("Language server timed out".to_string())
        }
        Err(mpsc::RecvTimeoutError::Disconnected) => {
            router.stop_waiting_for(id);
            log_lsp_timing(
                router,
                method,
                started,
                "went unanswered because the server stopped",
            );
            Err(router
                .stop_reason()
                .unwrap_or_else(|| "Language server exited before responding".to_string()))
        }
    }
}

/// One line on the terminal saying how long a question took — only when the reader
/// started the app with `MCB_TIMING=1`.
fn log_lsp_timing(router: &Arc<LspRouter>, method: &str, started: Instant, outcome: &str) {
    if !timing_enabled() {
        return;
    }
    crate::debug_log::stderr_log!(
        "Timing: the question {method} to {} {outcome} after {} ms.",
        router.identity.server_name,
        started.elapsed().as_millis()
    );
}

/// A progress job's name, whatever shape the server chose to send it in.
fn progress_token(params: &Value) -> Option<String> {
    match params.get("token")? {
        Value::String(token) => Some(token.clone()),
        Value::Number(token) => Some(token.to_string()),
        _ => None,
    }
}

fn progress_text(value: &Value, field: &str) -> Option<String> {
    value
        .get(field)
        .and_then(Value::as_str)
        .map(str::trim)
        .filter(|text| !text.is_empty())
        .map(str::to_string)
}

#[derive(Clone, Default)]
pub(crate) struct SourceLspRegistry {
    sessions: Arc<Mutex<HashMap<SourceLspSessionKey, Arc<Mutex<SourceLspSession>>>>>,
    native_csharp_sessions: Arc<Mutex<HashMap<String, Arc<NativeCsharpSession>>>>,
    active_root: Arc<Mutex<Option<String>>>,
    warm_languages: Arc<Mutex<HashSet<String>>>,
    preload_languages_by_root: Arc<Mutex<HashMap<String, Vec<String>>>>,
    next_use: Arc<AtomicU64>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct NativeCsharpEndpoint {
    ws_url: String,
    root: String,
}

struct NativeCsharpSession {
    endpoint: NativeCsharpEndpoint,
    last_used: AtomicU64,
    shutdown: Mutex<Option<oneshot::Sender<()>>>,
    stop_clients: broadcast::Sender<()>,
    /// The same counter the bridge writes Roslyn's pid into. 0 means no server.
    roslyn_pid: Arc<AtomicU32>,
}

impl Drop for NativeCsharpSession {
    fn drop(&mut self) {
        let _ = self.stop_clients.send(());
        if let Some(shutdown) = locked(&self.shutdown).take() {
            let _ = shutdown.send(());
        }
    }
}

#[derive(Clone)]
struct NativeCsharpBridgeState {
    token: Arc<str>,
    root: Arc<PathBuf>,
    server: ResolvedLspServer,
    connected: Arc<AtomicBool>,
    stop_clients: broadcast::Sender<()>,
    /// The Roslyn process serving this workspace, or 0 while none is running.
    /// Shared with the registry so the resource view can charge its CPU and
    /// memory to this workspace instead of leaving it as an unnamed app helper.
    roslyn_pid: Arc<AtomicU32>,
}

#[derive(Deserialize)]
struct NativeCsharpAuth {
    token: String,
}

/// One running language-server process and the workspace it serves.
#[derive(Debug, Clone)]
pub(crate) struct LanguageServerProcess {
    pub(crate) pid: u32,
    pub(crate) language_id: String,
    pub(crate) root: String,
    pub(crate) server_name: String,
}

/// A language-server slot belongs to both a language and the active workspace root.
#[derive(Debug, Clone, PartialEq, Eq, Hash)]
struct SourceLspSessionKey {
    language: String,
    root: String,
}

struct SourceLspSession {
    root: PathBuf,
    connection: Arc<LspConnection>,
    last_used: u64,
}

fn lsp_session_key_to_evict(
    entries: &[(SourceLspSessionKey, u64)],
    incoming: &SourceLspSessionKey,
) -> Option<SourceLspSessionKey> {
    if entries.iter().any(|(key, _)| key == incoming)
        || entries.len() < MAX_LSP_WORKSPACES_PER_LANGUAGE
    {
        return None;
    }

    entries
        .iter()
        .min_by_key(|(_, last_used)| *last_used)
        .map(|(key, _)| key.clone())
}

impl SourceLspRegistry {
    /// Make one workspace the sole owner of language-server processes.
    pub(crate) fn set_active_root(&self, root: &str, enabled: bool) -> Result<usize, String> {
        let root = if enabled {
            normalized_lsp_root(root)
                .ok_or_else(|| "Project root is not a directory".to_string())?
        } else {
            language_intelligence_key(root)
        };
        let mut active_root = self
            .active_root
            .lock()
            .map_err(|_| "Active language-server root lock poisoned".to_string())?;
        if !enabled {
            let Some(old_root) = active_root.take() else {
                return Ok(0);
            };
            drop(active_root);
            return self.stop_servers_for_root(&old_root);
        }
        if active_root.as_deref() == Some(&root) {
            return Ok(0);
        }
        let next_root = Some(root);
        *active_root = next_root.clone();

        let stopped = {
            let mut sessions = self
                .sessions
                .lock()
                .map_err(|_| "Language server registry lock poisoned".to_string())?;
            let keys = sessions
                .keys()
                .filter(|key| next_root.as_deref() != Some(key.root.as_str()))
                .cloned()
                .collect::<Vec<_>>();
            keys.into_iter()
                .filter_map(|key| sessions.remove(&key).map(|session| (key, session)))
                .collect::<Vec<_>>()
        };
        let stopped_native = {
            let mut sessions = self
                .native_csharp_sessions
                .lock()
                .map_err(|_| "Native C# registry lock poisoned".to_string())?;
            let roots = sessions
                .keys()
                .filter(|existing| next_root.as_deref() != Some(existing.as_str()))
                .cloned()
                .collect::<Vec<_>>();
            roots
                .into_iter()
                .filter_map(|old_root| {
                    sessions
                        .remove(&old_root)
                        .map(|session| (old_root, session))
                })
                .collect::<Vec<_>>()
        };
        let stopped_count = stopped.len() + stopped_native.len();
        locked(&self.warm_languages).extend(
            stopped
                .iter()
                .map(|(key, _)| key.language.clone())
                .filter(|language| language != "csharp"),
        );
        let mut stopped_roots = stopped
            .iter()
            .map(|(key, _)| (key.language.clone(), key.root.clone()))
            .collect::<Vec<_>>();
        stopped_roots.extend(
            stopped_native
                .iter()
                .map(|(root, _)| ("csharp".to_string(), root.clone())),
        );
        drop(stopped);
        drop(stopped_native);
        for (language, root) in stopped_roots {
            record_language_server_state(
                &language,
                &root,
                LanguageServerState::NotRunning,
                Some("The active Supercharged project changed.".to_string()),
            );
        }
        Ok(stopped_count)
    }

    /// Return the one authenticated native C# language-client endpoint for a
    /// canonical workspace root. The registry lock covers lookup and insertion,
    /// so concurrent frontend and Rust warm calls cannot create duplicate slots.
    pub(crate) fn ensure_native_csharp_endpoint(
        &self,
        root: &str,
    ) -> Result<NativeCsharpEndpoint, String> {
        if !language_servers_enabled() {
            return Err("Language servers are switched off in Settings.".to_string());
        }
        if !csharp_language_server_enabled() {
            return Err("The C# language server is switched off in Settings.".to_string());
        }
        let root = normalized_lsp_root(root)
            .ok_or_else(|| "Project root is not a directory".to_string())?;
        // The one place a C# server can be started from. Read mode stops here,
        // so no amount of clicking in a workspace the reader has not switched on
        // can bring Roslyn up behind their back.
        if !language_intelligence_on(&root) {
            return Err(READ_MODE_DETAIL.to_string());
        }
        let active_root = self
            .active_root
            .lock()
            .map_err(|_| "Active language-server root lock poisoned".to_string())?;
        if active_root.as_deref() != Some(&root) {
            return Err("This workspace is no longer the active Supercharged project.".to_string());
        }
        let use_tick = self.next_use.fetch_add(1, Ordering::Relaxed) + 1;

        let mut sessions = self
            .native_csharp_sessions
            .lock()
            .map_err(|_| "Native C# registry lock poisoned".to_string())?;
        if let Some(session) = sessions.get(&root) {
            session.last_used.store(use_tick, Ordering::Relaxed);
            return Ok(session.endpoint.clone());
        }

        let server = resolve_server_for_language("csharp")
            .ok_or_else(|| "roslyn-language-server is not installed or not on PATH".to_string())?;
        let listener = std::net::TcpListener::bind(("127.0.0.1", 0))
            .map_err(|error| format!("Could not bind native C# bridge: {error}"))?;
        listener
            .set_nonblocking(true)
            .map_err(|error| format!("Could not configure native C# bridge: {error}"))?;
        let address = listener
            .local_addr()
            .map_err(|error| format!("Could not read native C# bridge address: {error}"))?;
        let token = uuid::Uuid::new_v4().simple().to_string();
        let endpoint = NativeCsharpEndpoint {
            ws_url: format!("ws://{address}/lsp?token={token}"),
            root: root.clone(),
        };
        let (shutdown_sender, shutdown_receiver) = oneshot::channel();
        let (stop_clients, _) = broadcast::channel(1);
        let roslyn_pid = Arc::new(AtomicU32::new(0));
        let state = NativeCsharpBridgeState {
            token: Arc::<str>::from(token),
            root: Arc::new(PathBuf::from(&root)),
            server,
            connected: Arc::new(AtomicBool::new(false)),
            stop_clients: stop_clients.clone(),
            roslyn_pid: Arc::clone(&roslyn_pid),
        };
        let listener = TcpListener::from_std(listener)
            .map_err(|error| format!("Could not start native C# bridge: {error}"))?;
        tauri::async_runtime::spawn(async move {
            let router = Router::new()
                .route("/lsp", get(upgrade_native_csharp_lsp))
                .with_state(state);
            let server = axum::serve(listener, router).with_graceful_shutdown(async move {
                let _ = shutdown_receiver.await;
            });
            if let Err(error) = server.await {
                crate::debug_log::stderr_log!("[native-csharp-bridge] {error}");
            }
        });

        let session = Arc::new(NativeCsharpSession {
            endpoint: endpoint.clone(),
            last_used: AtomicU64::new(use_tick),
            shutdown: Mutex::new(Some(shutdown_sender)),
            stop_clients,
            roslyn_pid,
        });
        let evicted = if sessions.len() >= MAX_LSP_WORKSPACES_PER_LANGUAGE {
            sessions
                .iter()
                .min_by_key(|(_, session)| session.last_used.load(Ordering::Relaxed))
                .map(|(root, _)| root.clone())
                .and_then(|root| sessions.remove(&root))
        } else {
            None
        };
        sessions.insert(root.clone(), session);
        drop(sessions);
        drop(evicted);
        record_language_server_state(
            "csharp",
            &root,
            LanguageServerState::Starting,
            Some("The native C# client endpoint is ready.".to_string()),
        );
        Ok(endpoint)
    }

    pub(crate) fn mark_native_csharp_client_ready(&self, root: &str) -> Result<(), String> {
        let root = normalized_lsp_root(root)
            .ok_or_else(|| "Project root is not a directory".to_string())?;
        let active_root = self
            .active_root
            .lock()
            .map_err(|_| "Active language-server root lock poisoned".to_string())?;
        if active_root.as_deref() != Some(&root) {
            return Err("This workspace is no longer the active Supercharged project.".to_string());
        }
        let sessions = self
            .native_csharp_sessions
            .lock()
            .map_err(|_| "Native C# registry lock poisoned".to_string())?;
        if !sessions.contains_key(&root) {
            return Err("The native C# language client is not running for this root.".to_string());
        }
        drop(sessions);
        record_language_server_state("csharp", &root, LanguageServerState::Ready, None);
        Ok(())
    }

    pub(crate) fn find_definitions(
        &self,
        preview: SourceLspPreview,
        request: SourceLspLookupRequest,
    ) -> Result<Vec<SourceLspDefinitionTarget>, String> {
        let Some(result) = self.request(&preview, &request, "textDocument/definition")? else {
            return Ok(Vec::new());
        };

        let symbol_name = symbol_at_position(&preview.content, request.line, request.column)
            .unwrap_or_else(|| preview.file_name.clone());
        let mut targets = Vec::new();
        for location in lsp_locations_from_result(&result) {
            targets.push(definition_target_from_location(
                &preview,
                &request,
                &location,
                &symbol_name,
            ));
            if targets.len() >= request.limit.unwrap_or(20) {
                break;
            }
        }
        Ok(targets)
    }

    pub(crate) fn find_implementations(
        &self,
        preview: SourceLspPreview,
        request: SourceLspLookupRequest,
    ) -> Result<Vec<SourceLspDefinitionTarget>, String> {
        let Some(result) = self.request(&preview, &request, "textDocument/implementation")? else {
            return Ok(Vec::new());
        };

        let symbol_name = symbol_at_position(&preview.content, request.line, request.column)
            .unwrap_or_else(|| preview.file_name.clone());
        let mut targets = Vec::new();
        for location in lsp_locations_from_result(&result) {
            targets.push(definition_target_from_location(
                &preview,
                &request,
                &location,
                &symbol_name,
            ));
            if targets.len() >= request.limit.unwrap_or(20) {
                break;
            }
        }
        Ok(targets)
    }

    pub(crate) fn find_type_definitions(
        &self,
        preview: SourceLspPreview,
        request: SourceLspLookupRequest,
    ) -> Result<Vec<SourceLspDefinitionTarget>, String> {
        let Some(result) = self.request(&preview, &request, "textDocument/typeDefinition")? else {
            return Ok(Vec::new());
        };

        let symbol_name = symbol_at_position(&preview.content, request.line, request.column)
            .unwrap_or_else(|| preview.file_name.clone());
        let mut targets = Vec::new();
        for location in lsp_locations_from_result(&result) {
            targets.push(definition_target_from_location(
                &preview,
                &request,
                &location,
                &symbol_name,
            ));
            if targets.len() >= request.limit.unwrap_or(20) {
                break;
            }
        }
        Ok(targets)
    }

    pub(crate) fn find_document_highlights(
        &self,
        preview: SourceLspPreview,
        request: SourceLspLookupRequest,
    ) -> Result<Vec<SourceLspDocumentHighlight>, String> {
        let Some(result) = self.request(&preview, &request, "textDocument/documentHighlight")?
        else {
            return Ok(Vec::new());
        };

        Ok(lsp_document_highlights_from_result(&result))
    }

    pub(crate) fn find_references(
        &self,
        preview: SourceLspPreview,
        request: SourceLspLookupRequest,
    ) -> Result<Vec<SourceLspReferenceTarget>, String> {
        let Some(result) = self.request(&preview, &request, "textDocument/references")? else {
            return Ok(Vec::new());
        };

        let symbol_name = symbol_at_position(&preview.content, request.line, request.column)
            .unwrap_or_else(|| preview.file_name.clone());
        let mut targets = Vec::new();
        for location in lsp_locations_from_result(&result) {
            targets.push(reference_target_from_location(
                &preview,
                &request,
                &location,
                &symbol_name,
            ));
            if targets.len() >= request.limit.unwrap_or(50) {
                break;
            }
        }
        Ok(targets)
    }

    pub(crate) fn find_hover(
        &self,
        preview: SourceLspPreview,
        request: SourceLspLookupRequest,
    ) -> Result<Option<SourceLspHover>, String> {
        let Some(result) = self.request(&preview, &request, "textDocument/hover")? else {
            return Ok(None);
        };

        let contents = hover_contents_from_result(&result);
        if contents.is_empty() {
            return Ok(None);
        }
        Ok(Some(SourceLspHover { contents }))
    }

    pub(crate) fn find_signature_help(
        &self,
        preview: SourceLspPreview,
        request: SourceLspLookupRequest,
    ) -> Result<Option<SourceLspSignatureHelp>, String> {
        let Some(result) = self.request(&preview, &request, "textDocument/signatureHelp")? else {
            return Ok(None);
        };

        Ok(lsp_signature_help_from_result(&result))
    }

    pub(crate) fn find_inlay_hints(
        &self,
        preview: SourceLspPreview,
        request: SourceLspLookupRequest,
    ) -> Result<Vec<SourceLspInlayHint>, String> {
        let limit = request.limit.unwrap_or(200);
        let Some(result) = self.request(&preview, &request, "textDocument/inlayHint")? else {
            return Ok(Vec::new());
        };

        Ok(lsp_inlay_hints_from_result(&result, limit))
    }

    pub(crate) fn find_semantic_tokens(
        &self,
        preview: SourceLspPreview,
        request: SourceLspLookupRequest,
    ) -> Result<Vec<SourceLspSemanticToken>, String> {
        let limit = request.limit.unwrap_or(5_000);
        let tokens = self.run_on_server(&preview, &request, |connection| {
            let Some(result) = connection.request_semantic_tokens(&preview)? else {
                return Ok(Vec::new());
            };
            Ok(lsp_semantic_tokens_from_result(
                &result,
                &connection.semantic_token_types,
                limit,
            ))
        })?;
        Ok(tokens.unwrap_or_default())
    }

    /// Every symbol in one file, flattened and counted from zero, for the counts the
    /// editor draws in the margin.
    ///
    /// Unlike the other lookups this one is handed only a path: it reads the file from
    /// disk itself, so what it reports is the file as saved. The editor asks for it when
    /// a file is opened, which is exactly when those two agree.
    pub(crate) fn find_document_symbols(
        &self,
        root: String,
        language: String,
        path: String,
    ) -> Result<Vec<SourceLspDocumentSymbol>, String> {
        let preview = read_preview_for_symbols(Path::new(&root), Path::new(&path), &language)?;
        let request = SourceLspLookupRequest {
            root,
            line: 1,
            column: 1,
            limit: None,
        };

        let symbols = self.run_on_server(&preview, &request, |connection| {
            let Some(result) =
                connection.request(&preview, &request, "textDocument/documentSymbol")?
            else {
                return Ok(Vec::new());
            };
            Ok(lsp_document_symbols_from_result(
                &result,
                MAX_LSP_DOCUMENT_SYMBOLS,
            ))
        })?;
        Ok(symbols.unwrap_or_default())
    }

    /// The recent output from every warm workspace server for this language.
    pub(crate) fn read_server_log(&self, language: &str) -> Result<Vec<String>, String> {
        let Some(spec) = server_spec_for_language(language) else {
            return Ok(Vec::new());
        };
        let sessions = {
            let sessions = locked(&self.sessions);
            sessions
                .iter()
                .filter(|(key, _)| key.language == spec.language_id)
                .map(|(_, session)| Arc::clone(session))
                .collect::<Vec<_>>()
        };
        let mut lines = Vec::new();
        for session in sessions {
            lines.extend(lock_lsp_session(&session)?.connection.recent_log_lines());
        }
        Ok(lines)
    }

    pub(crate) fn find_workspace_symbols(
        &self,
        preview: SourceLspPreview,
        request: SourceLspWorkspaceSymbolRequest,
    ) -> Result<Vec<SourceLspWorkspaceSymbol>, String> {
        let query = request.query.trim().to_string();
        if query.is_empty() {
            return Ok(Vec::new());
        }

        let lookup_request = SourceLspLookupRequest {
            root: request.root.clone(),
            line: 1,
            column: 1,
            limit: request.limit,
        };
        let limit = lookup_request.limit.unwrap_or(50);
        let symbols = self.run_on_server(&preview, &lookup_request, |connection| {
            let Some(result) = connection.request_workspace_symbols(&preview, &query)? else {
                return Ok(Vec::new());
            };
            Ok(lsp_workspace_symbols_from_result(
                &result,
                Path::new(&request.root),
                &preview.language,
                limit,
            ))
        })?;
        Ok(symbols.unwrap_or_default())
    }

    pub(crate) fn find_symbols(
        &self,
        preview: SourceLspPreview,
        request: SourceLspLookupRequest,
    ) -> Result<Vec<SourceLspSymbol>, String> {
        let Some(result) = self.request(&preview, &request, "textDocument/documentSymbol")? else {
            return Ok(Vec::new());
        };

        Ok(lsp_symbols_from_result(
            &result,
            request.limit.unwrap_or(100),
        ))
    }

    pub(crate) fn find_completions(
        &self,
        preview: SourceLspPreview,
        request: SourceLspLookupRequest,
    ) -> Result<Vec<SourceLspCompletionItem>, String> {
        let Some(result) = self.request(&preview, &request, "textDocument/completion")? else {
            return Ok(Vec::new());
        };

        Ok(lsp_completion_items_from_result(
            &result,
            request.limit.unwrap_or(50),
        ))
    }

    pub(crate) fn format_document(
        &self,
        preview: SourceLspPreview,
        request: SourceLspLookupRequest,
    ) -> Result<Vec<SourceLspTextEdit>, String> {
        let Some(result) = self.request(&preview, &request, "textDocument/formatting")? else {
            return Ok(Vec::new());
        };

        Ok(lsp_text_edits_from_result(&result))
    }

    pub(crate) fn rename(
        &self,
        preview: SourceLspPreview,
        request: SourceLspRenameRequest,
    ) -> Result<SourceLspRenameResult, String> {
        let lookup_request = SourceLspLookupRequest {
            root: request.root.clone(),
            line: request.line,
            column: request.column,
            limit: None,
        };

        let files = self.run_on_server(&preview, &lookup_request, |connection| {
            let Some(result) = connection.request_rename(&preview, &request)? else {
                return Ok(Vec::new());
            };
            Ok(lsp_workspace_edit_files_from_result(
                &result,
                Path::new(&request.root),
            ))
        })?;
        Ok(SourceLspRenameResult {
            files: files.unwrap_or_default(),
        })
    }

    pub(crate) fn find_code_actions(
        &self,
        preview: SourceLspPreview,
        request: SourceLspCodeActionRequest,
    ) -> Result<Vec<SourceLspCodeAction>, String> {
        let lookup_request = SourceLspLookupRequest {
            root: request.root.clone(),
            line: request.start_line,
            column: request.start_column,
            limit: request.limit,
        };

        let limit = request.limit.unwrap_or(50);
        let actions = self.run_on_server(&preview, &lookup_request, |connection| {
            let Some(result) = connection.request_code_actions(&preview, &request)? else {
                return Ok(Vec::new());
            };
            Ok(lsp_code_actions_from_result(
                &result,
                Path::new(&request.root),
                limit,
            ))
        })?;
        Ok(actions.unwrap_or_default())
    }

    pub(crate) fn read_diagnostics(
        &self,
        preview: SourceLspPreview,
        request: SourceLspLookupRequest,
    ) -> Result<Vec<SourceLspDiagnostic>, String> {
        let diagnostics = self.run_on_server(&preview, &request, |connection| {
            connection.read_diagnostics(&preview)
        })?;
        Ok(diagnostics.unwrap_or_default())
    }

    fn request(
        &self,
        preview: &SourceLspPreview,
        request: &SourceLspLookupRequest,
        method: &str,
    ) -> Result<Option<Value>, String> {
        Ok(self
            .run_on_server(preview, request, |connection| {
                connection.request(preview, request, method)
            })?
            .flatten())
    }

    /// Run one piece of work against this language's server, starting it if needed.
    ///
    /// `Ok(None)` means there is no server to ask — no server is configured for this
    /// language, or the reader has switched it off — which every caller turns into an
    /// empty answer rather than an error. A server that turns out to have died is
    /// dropped and the work is tried once more on a fresh one.
    ///
    /// The registry is not held while the work runs, and neither is the session: the
    /// connection carries several questions at once, which is the whole point.
    fn run_on_server<T>(
        &self,
        preview: &SourceLspPreview,
        request: &SourceLspLookupRequest,
        mut work: impl FnMut(&LspConnection) -> Result<T, String>,
    ) -> Result<Option<T>, String> {
        for attempt in 0..2 {
            let Some(connection) = self.connection_for(preview, request)? else {
                return Ok(None);
            };

            match work(&connection) {
                Ok(value) => return Ok(Some(value)),
                Err(_) if attempt == 0 && !connection.is_alive() => {
                    self.remove_session(preview, request)?;
                    continue;
                }
                Err(error) => return Err(error),
            }
        }

        Ok(None)
    }

    fn connection_for(
        &self,
        preview: &SourceLspPreview,
        request: &SourceLspLookupRequest,
    ) -> Result<Option<Arc<LspConnection>>, String> {
        let Some(session) = self.session_for(preview, request)? else {
            return Ok(None);
        };
        let connection = Arc::clone(&lock_lsp_session(&session)?.connection);
        Ok(Some(connection))
    }

    fn session_for(
        &self,
        preview: &SourceLspPreview,
        request: &SourceLspLookupRequest,
    ) -> Result<Option<Arc<Mutex<SourceLspSession>>>, String> {
        let Some(key) = SourceLspSessionKey::from_preview(preview, request) else {
            return Ok(None);
        };
        // A language whose server the reader has switched off answers nothing,
        // which sends every lookup down to the plain-text tier instead of
        // starting the server behind their back.
        if !legacy_language_server_allowed(&key.language) {
            return Ok(None);
        }
        // Read mode is the default, and it is the reason a file can be opened in
        // every project at once: a workspace nobody has turned full mode on for
        // never starts a server, however many lookups arrive for it.
        if !language_intelligence_on(&key.root) {
            return Ok(None);
        }

        let Some(server) = resolve_server_for_language(&preview.language) else {
            return Ok(None);
        };
        self.ensure_session(key, server).map(Some)
    }

    fn ensure_session(
        &self,
        key: SourceLspSessionKey,
        server: ResolvedLspServer,
    ) -> Result<Arc<Mutex<SourceLspSession>>, String> {
        let active_root = self
            .active_root
            .lock()
            .map_err(|_| "Active language-server root lock poisoned".to_string())?;
        if active_root.as_deref() != Some(&key.root) {
            return Err("This workspace is no longer the active Supercharged project.".to_string());
        }
        let use_tick = self.next_use.fetch_add(1, Ordering::Relaxed) + 1;

        let mut sessions = self
            .sessions
            .lock()
            .map_err(|_| "Language server registry lock poisoned".to_string())?;
        if let Some(session) = sessions.get(&key).cloned() {
            let usable = {
                let mut guard = lock_lsp_session(&session)?;
                if guard.connection.is_alive() {
                    guard.last_used = use_tick;
                    true
                } else {
                    false
                }
            };
            if usable {
                return Ok(session);
            }
            sessions.remove(&key);
        }

        let session = SourceLspSession::start(PathBuf::from(&key.root), server, use_tick)?;
        let session = Arc::new(Mutex::new(session));

        let same_language = sessions
            .iter()
            .filter(|(existing_key, _)| existing_key.language == key.language)
            .map(|(existing_key, existing_session)| {
                Ok((
                    existing_key.clone(),
                    lock_lsp_session(existing_session)?.last_used,
                ))
            })
            .collect::<Result<Vec<_>, String>>()?;
        let evicted = lsp_session_key_to_evict(&same_language, &key)
            .and_then(|oldest| sessions.remove(&oldest).map(|session| (oldest, session)));
        sessions.insert(key, Arc::clone(&session));
        drop(sessions);
        // Dropping outside the registry lock performs the shutdown without
        // blocking unrelated lookups from reading the pool.
        if let Some((evicted_key, evicted_session)) = evicted {
            drop(evicted_session);
            record_language_server_state(
                &evicted_key.language,
                &evicted_key.root,
                LanguageServerState::NotRunning,
                Some(
                    "This workspace's language server was released to keep the warm-workspace pool bounded."
                        .to_string(),
                ),
            );
        }
        Ok(session)
    }

    /// Shut down the running server for one language, if there is one.
    ///
    /// Dropping the registry's last handle on a session is what stops the
    /// process: [`SourceLspSession`]'s destructor sends the language-server
    /// shutdown handshake and then reaps the child. The handles are taken out
    /// from under the registry lock and dropped after it is released, because
    /// that handshake waits on the child and holding the lock across it would
    /// stall lookups for every other language.
    ///
    /// Returns how many servers were stopped, so the caller can say so plainly.
    /// Stop every language server, whatever the language and workspace.
    pub(crate) fn stop_all_servers(&self) -> Result<usize, String> {
        let mut languages = {
            let sessions = self
                .sessions
                .lock()
                .map_err(|_| "Language server registry lock poisoned".to_string())?;
            sessions
                .keys()
                .map(|key| key.language.clone())
                .collect::<Vec<_>>()
        };
        languages.push("csharp".to_string());
        languages.sort();
        languages.dedup();
        let mut stopped = 0;
        for language in languages {
            stopped += self.stop_servers_for_language(&language)?;
        }
        Ok(stopped)
    }

    pub(crate) fn stop_servers_for_language(&self, language_id: &str) -> Result<usize, String> {
        let stopped = {
            let mut sessions = self
                .sessions
                .lock()
                .map_err(|_| "Language server registry lock poisoned".to_string())?;
            let keys = sessions
                .keys()
                .filter(|key| key.language == language_id)
                .cloned()
                .collect::<Vec<_>>();
            keys.into_iter()
                .filter_map(|key| sessions.remove(&key).map(|session| (key, session)))
                .collect::<Vec<_>>()
        };
        let stopped_native = if language_id == "csharp" {
            let mut sessions = self
                .native_csharp_sessions
                .lock()
                .map_err(|_| "Native C# registry lock poisoned".to_string())?;
            sessions.drain().collect::<Vec<_>>()
        } else {
            Vec::new()
        };

        if !stopped.is_empty() || !stopped_native.is_empty() {
            let mut roots = stopped
                .iter()
                .map(|(key, _)| key.root.clone())
                .collect::<Vec<_>>();
            roots.extend(stopped_native.iter().map(|(root, _)| root.clone()));
            roots.sort();
            roots.dedup();
            let stopped_count = stopped.len() + stopped_native.len();
            drop(stopped);
            drop(stopped_native);
            // A server stopped because the reader switched it off must say so, not just
            // that it is not running — the two look identical on screen otherwise, and
            // only one of them is something the reader chose.
            let (state, detail) = if language_server_allowed(language_id) {
                (
                    LanguageServerState::NotRunning,
                    "The language server has been stopped.",
                )
            } else {
                (
                    LanguageServerState::Disabled,
                    "The C# language server is switched off in Settings.",
                )
            };
            for root in roots {
                record_language_server_state(language_id, &root, state, Some(detail.to_string()));
            }
            return Ok(stopped_count);
        }

        Ok(0)
    }

    /// Stop every language server this workspace has running, whichever
    /// language it belongs to. This is what turning full mode off does.
    ///
    /// Same two halves as [`Self::stop_servers_for_language`]: the handles come
    /// out from under the registry lock and are dropped after it is released,
    /// because the goodbye handshake waits on the child.
    ///
    /// Servers are per workspace, so this stops the one process that was
    /// serving every session and every editor view on that workspace — and
    /// leaves other workspaces' servers alone.
    pub(crate) fn stop_servers_for_root(&self, root: &str) -> Result<usize, String> {
        let root = language_intelligence_key(root);
        let stopped = {
            let mut sessions = self
                .sessions
                .lock()
                .map_err(|_| "Language server registry lock poisoned".to_string())?;
            let keys = sessions
                .keys()
                .filter(|key| key.root == root)
                .cloned()
                .collect::<Vec<_>>();
            keys.into_iter()
                .filter_map(|key| sessions.remove(&key).map(|session| (key, session)))
                .collect::<Vec<_>>()
        };
        let stopped_native = {
            let mut sessions = self
                .native_csharp_sessions
                .lock()
                .map_err(|_| "Native C# registry lock poisoned".to_string())?;
            sessions
                .remove(&root)
                .map(|session| (root.clone(), session))
        };

        let mut languages = stopped
            .iter()
            .map(|(key, _)| key.language.clone())
            .collect::<Vec<_>>();
        if stopped_native.is_some() {
            languages.push("csharp".to_string());
        }
        languages.sort();
        languages.dedup();

        let stopped_count = stopped.len() + usize::from(stopped_native.is_some());
        drop(stopped);
        drop(stopped_native);

        for language in languages {
            record_language_server_state(
                &language,
                &root,
                LanguageServerState::Disabled,
                Some(READ_MODE_DETAIL.to_string()),
            );
        }
        Ok(stopped_count)
    }

    /// Every language-server process running right now, with the workspace it
    /// serves. The resource view uses this to charge a server's CPU and memory
    /// to its project instead of leaving it as an unnamed app helper.
    ///
    /// Never blocks: a session busy answering a question is skipped for this
    /// sample rather than made to wait. It will be in the next one.
    pub(crate) fn running_language_server_processes(&self) -> Vec<LanguageServerProcess> {
        let mut running = Vec::new();

        let entries = match self.sessions.lock() {
            Ok(sessions) => sessions
                .iter()
                .map(|(key, session)| (key.clone(), Arc::clone(session)))
                .collect::<Vec<_>>(),
            Err(_) => Vec::new(),
        };
        for (key, session) in entries {
            let Ok(guard) = session.try_lock() else {
                continue;
            };
            let Ok(child) = guard.connection.child.try_lock() else {
                continue;
            };
            let Some(pid) = child.as_ref().map(|child| child.id()) else {
                continue;
            };
            running.push(LanguageServerProcess {
                pid,
                language_id: key.language.clone(),
                root: key.root.clone(),
                server_name: server_spec_for_language(&key.language)
                    .map(|spec| spec.server_name.to_string())
                    .unwrap_or_else(|| key.language.clone()),
            });
        }

        if let Ok(sessions) = self.native_csharp_sessions.lock() {
            for (root, session) in sessions.iter() {
                let pid = session.roslyn_pid.load(Ordering::Acquire);
                if pid == 0 {
                    continue;
                }
                running.push(LanguageServerProcess {
                    pid,
                    language_id: "csharp".to_string(),
                    root: root.clone(),
                    server_name: "roslyn-language-server".to_string(),
                });
            }
        }

        running.sort_by(|left, right| {
            left.root
                .cmp(&right.root)
                .then_with(|| left.language_id.cmp(&right.language_id))
        });
        running
    }

    fn remove_session(
        &self,
        preview: &SourceLspPreview,
        request: &SourceLspLookupRequest,
    ) -> Result<(), String> {
        let Some(key) = SourceLspSessionKey::from_preview(preview, request) else {
            return Ok(());
        };
        let mut sessions = self
            .sessions
            .lock()
            .map_err(|_| "Language server registry lock poisoned".to_string())?;
        sessions.remove(&key);
        Ok(())
    }

    /// Proactively warm every known language at the active `root`.
    ///
    /// The frontend calls this on a project switch, before the active file is
    /// read. Existing server languages follow the reader between roots, while a
    /// cheap cached project-marker probe lets a C# workspace start Roslyn before
    /// its first file is opened.
    pub(crate) fn warm_running_servers_for_root(&self, root: &str) -> Result<usize, String> {
        let Some(root) = normalized_lsp_root(root) else {
            return Ok(0);
        };
        // Warming is a start in disguise. A workspace in read mode warms nothing.
        if !language_intelligence_on(&root) {
            return Ok(0);
        }
        let mut languages = {
            let sessions = self
                .sessions
                .lock()
                .map_err(|_| "Language server registry lock poisoned".to_string())?;
            let mut languages = locked(&self.warm_languages)
                .iter()
                .cloned()
                .chain(sessions.keys().map(|key| key.language.clone()))
                .collect::<Vec<_>>();
            languages.sort();
            languages.dedup();
            languages
        };

        // The native Monaco client owns C#. Never carry an old C# session into
        // another workspace or preload another Roslyn through this legacy pool.
        languages.retain(|language| language != "csharp");
        let preload_languages = self.preload_languages_for_root(&root)?;
        for language in preload_languages {
            if legacy_language_server_allowed(&language) && !languages.contains(&language) {
                languages.push(language);
            }
        }

        let mut warmed = 0;
        for language in languages {
            if !legacy_language_server_allowed(&language) {
                continue;
            }
            let Some(server) = resolve_server_for_language(&language) else {
                continue;
            };
            self.ensure_session(
                SourceLspSessionKey {
                    language,
                    root: root.clone(),
                },
                server,
            )?;
            warmed += 1;
        }

        Ok(warmed)
    }

    /// Start this workspace's server for one language now, and say what that did.
    ///
    /// This is the lazy start the first hover performs, asked for on purpose
    /// instead of by accident: the same [`Self::ensure_session`] slot, so a
    /// server that is already warm is reused rather than started twice. Every
    /// reason a server cannot start comes back as a plain answer rather than an
    /// error, because "there is no server for this language" is an ordinary
    /// thing for a workspace to be, not a fault.
    pub(crate) fn start_server_for_language(
        &self,
        root: &str,
        language: &str,
    ) -> Result<LanguageServerStart, String> {
        if !language_servers_enabled() {
            return Ok(LanguageServerStart::SwitchedOff);
        }
        if !language_intelligence_on(root) {
            return Ok(LanguageServerStart::ReadMode);
        }
        let Some(spec) = server_spec_for_language(language) else {
            return Ok(LanguageServerStart::NoServerForLanguage);
        };
        if !language_server_enabled(spec.language_id) {
            return Ok(LanguageServerStart::ServerSwitchedOff);
        }
        if spec.language_id == "csharp" {
            // C# belongs to the native language client, and starting a second
            // Roslyn here beside it is exactly what the legacy pool must not do.
            // Its own gates decide, and the reader is told which one held.
            if !csharp_language_server_enabled() {
                return Ok(LanguageServerStart::CsharpSwitchedOff);
            }
            if !workspace_has_csharp_project_marker(root) {
                return Ok(LanguageServerStart::NoCsharpProject);
            }
            return Ok(LanguageServerStart::NativeCsharpClient);
        }
        let Some(command) = resolve_command(spec.command) else {
            return Ok(LanguageServerStart::NotInstalled {
                server_name: spec.server_name,
                command: spec.command,
            });
        };
        let Some(root) = normalized_lsp_root(root) else {
            return Err("This project folder could not be found on this machine.".to_string());
        };
        self.ensure_session(
            SourceLspSessionKey {
                language: spec.language_id.to_string(),
                root,
            },
            ResolvedLspServer { spec, command },
        )?;
        Ok(LanguageServerStart::Running {
            server_name: spec.server_name,
        })
    }

    fn preload_languages_for_root(&self, root: &str) -> Result<Vec<String>, String> {
        if let Some(languages) = self
            .preload_languages_by_root
            .lock()
            .map_err(|_| "Language server preload cache lock poisoned".to_string())?
            .get(root)
            .cloned()
        {
            return Ok(languages);
        }

        let languages = if workspace_contains_csharp_project(Path::new(root)) {
            vec!["csharp".to_string()]
        } else {
            Vec::new()
        };
        self.preload_languages_by_root
            .lock()
            .map_err(|_| "Language server preload cache lock poisoned".to_string())?
            .insert(root.to_string(), languages.clone());
        Ok(languages)
    }

    /// Every diagnostic the running language servers currently hold for files under
    /// `root`. Nothing is requested from a server here — this reads what has already
    /// been published, so a project with no warm server simply reports nothing.
    pub(crate) fn list_diagnostics_for_root(
        &self,
        root: PathBuf,
    ) -> Result<Vec<SourceLspDiagnostic>, String> {
        let snapshot: Vec<Arc<Mutex<SourceLspSession>>> = {
            let sessions = self
                .sessions
                .lock()
                .map_err(|_| "Language server registry lock poisoned".to_string())?;
            sessions.values().map(Arc::clone).collect()
        };

        let mut per_session = Vec::with_capacity(snapshot.len());
        for session in snapshot {
            let guard = lock_lsp_session(&session)?;
            per_session.push((guard.root.clone(), guard.connection.published_diagnostics()));
        }

        Ok(collect_diagnostics_for_root(&per_session, &root))
    }

    /// Number of live language-server sessions in the registry (test introspection only).
    #[cfg(test)]
    fn session_count(&self) -> Result<usize, String> {
        let sessions = self
            .sessions
            .lock()
            .map_err(|_| "Language server registry lock poisoned".to_string())?;
        Ok(sessions.len())
    }

    /// OS pid of the warm server for `language` and `root`, if present.
    #[cfg(test)]
    fn session_pid_for(&self, language: &str, root: &Path) -> Option<u32> {
        let key = SourceLspSessionKey {
            language: server_spec_for_language(language)?.language_id.to_string(),
            root: normalized_lsp_root(&root.display().to_string())?,
        };
        let session = self.sessions.lock().ok()?.get(&key).cloned()?;
        let pid = lock_lsp_session(&session)
            .ok()?
            .connection
            .child
            .lock()
            .ok()?
            .as_ref()?
            .id();
        Some(pid)
    }
}

async fn upgrade_native_csharp_lsp(
    ws: WebSocketUpgrade,
    Query(auth): Query<NativeCsharpAuth>,
    State(state): State<NativeCsharpBridgeState>,
) -> Response {
    if auth.token.as_str() != state.token.as_ref() {
        return StatusCode::UNAUTHORIZED.into_response();
    }
    if state.connected.swap(true, Ordering::AcqRel) {
        return StatusCode::CONFLICT.into_response();
    }
    let connected = Arc::clone(&state.connected);
    ws.max_message_size(MAX_NATIVE_CSHARP_MESSAGE_BYTES)
        .on_upgrade(move |socket| async move {
            if let Err(error) = proxy_native_csharp_lsp(socket, state).await {
                crate::debug_log::stderr_log!("[native-csharp-bridge] {error}");
            }
            connected.store(false, Ordering::Release);
        })
        .into_response()
}

async fn proxy_native_csharp_lsp(
    socket: WebSocket,
    state: NativeCsharpBridgeState,
) -> Result<(), String> {
    let mut command = tokio::process::Command::new(&state.server.command);
    command
        .args(state.server.spec.args)
        .current_dir(state.root.as_ref())
        .stdin(Stdio::piped())
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .kill_on_drop(true);
    if let Some(search_path) = lsp_search_path_env() {
        command.env("PATH", search_path);
    }
    configure_lsp_process_group(command.as_std_mut());
    let mut child = command
        .spawn()
        .map_err(|error| format!("Could not start Roslyn for native C# client: {error}"))?;
    state
        .roslyn_pid
        .store(child.id().unwrap_or(0), Ordering::Release);
    let mut roslyn_stdin = child
        .stdin
        .take()
        .ok_or_else(|| "Roslyn stdin unavailable".to_string())?;
    let roslyn_stdout = child
        .stdout
        .take()
        .ok_or_else(|| "Roslyn stdout unavailable".to_string())?;
    if let Some(stderr) = child.stderr.take() {
        tauri::async_runtime::spawn(async move {
            let mut lines = tokio::io::BufReader::new(stderr).lines();
            while let Ok(Some(line)) = lines.next_line().await {
                crate::debug_log::stderr_log!("[native-csharp-roslyn] {line}");
            }
        });
    }

    record_language_server_state(
        "csharp",
        &state.root.display().to_string(),
        LanguageServerState::Starting,
        Some("The native C# client is connecting to Roslyn.".to_string()),
    );
    let (mut websocket_sender, mut websocket_receiver) = socket.split();
    let mut roslyn_stdout = tokio::io::BufReader::new(roslyn_stdout);
    let mut stop_clients = state.stop_clients.subscribe();

    let browser_to_roslyn = async {
        while let Some(frame) = websocket_receiver.next().await {
            match frame.map_err(|error| error.to_string())? {
                WebSocketMessage::Text(text) => {
                    write_native_lsp_message(&mut roslyn_stdin, text.as_bytes()).await?;
                }
                WebSocketMessage::Binary(bytes) => {
                    write_native_lsp_message(&mut roslyn_stdin, bytes.as_ref()).await?;
                }
                WebSocketMessage::Close(_) => break,
                WebSocketMessage::Ping(_) | WebSocketMessage::Pong(_) => {}
            }
        }
        Ok::<(), String>(())
    };
    let roslyn_to_browser = async {
        while let Some(body) = read_native_lsp_message(&mut roslyn_stdout).await? {
            let json = String::from_utf8(body)
                .map_err(|error| format!("Roslyn returned invalid UTF-8: {error}"))?;
            websocket_sender
                .send(WebSocketMessage::Text(json.into()))
                .await
                .map_err(|error| error.to_string())?;
        }
        Ok::<(), String>(())
    };

    tokio::select! {
        result = browser_to_roslyn => result?,
        result = roslyn_to_browser => result?,
        _ = stop_clients.recv() => {
            // Turning full mode off closes the language client first, which
            // sends Roslyn the shutdown request. Give it that moment to leave
            // on its own before the group is stopped; the kill below is the
            // backstop for a server that ignores its own goodbye.
            let _ = tokio::time::timeout(NATIVE_CSHARP_SHUTDOWN_GRACE, child.wait()).await;
        },
        status = child.wait() => {
            state.roslyn_pid.store(0, Ordering::Release);
            let status = status.map_err(|error| format!("Could not wait for Roslyn: {error}"))?;
            return Err(format!("Roslyn exited with {status}"));
        }
    }
    if let Some(pid) = child.id() {
        // The process was placed in its own group before spawn, so this reaches
        // Roslyn and its BuildHost children without touching unrelated processes.
        unsafe { libc::kill(-(pid as i32), libc::SIGKILL) };
    }
    let _ = child.wait().await;
    state.roslyn_pid.store(0, Ordering::Release);
    record_language_server_state(
        "csharp",
        &state.root.display().to_string(),
        LanguageServerState::NotRunning,
        Some("The native C# client disconnected.".to_string()),
    );
    Ok(())
}

async fn write_native_lsp_message<W>(writer: &mut W, body: &[u8]) -> Result<(), String>
where
    W: AsyncWrite + Unpin,
{
    if body.len() > MAX_NATIVE_CSHARP_MESSAGE_BYTES {
        return Err("Native C# LSP message exceeds size limit".to_string());
    }
    writer
        .write_all(format!("Content-Length: {}\r\n\r\n", body.len()).as_bytes())
        .await
        .map_err(|error| error.to_string())?;
    writer
        .write_all(body)
        .await
        .map_err(|error| error.to_string())?;
    writer.flush().await.map_err(|error| error.to_string())
}

async fn read_native_lsp_message<R>(reader: &mut R) -> Result<Option<Vec<u8>>, String>
where
    R: AsyncBufRead + Unpin,
{
    let mut content_length = None;
    let mut header_bytes = 0usize;
    loop {
        let mut line = String::new();
        let bytes_read = reader
            .read_line(&mut line)
            .await
            .map_err(|error| error.to_string())?;
        if bytes_read == 0 {
            return if content_length.is_none() {
                Ok(None)
            } else {
                Err("EOF while reading native C# LSP headers".to_string())
            };
        }
        header_bytes += bytes_read;
        if header_bytes > MAX_LSP_HEADER_BYTES {
            return Err("Native C# LSP header exceeded size limit".to_string());
        }
        if line == "\r\n" || line == "\n" {
            break;
        }
        if let Some((name, value)) = line.split_once(':') {
            if name.eq_ignore_ascii_case("content-length") {
                content_length = Some(
                    value
                        .trim()
                        .parse::<usize>()
                        .map_err(|error| error.to_string())?,
                );
            }
        }
    }
    let length = content_length.ok_or_else(|| "Missing Content-Length".to_string())?;
    if length > MAX_NATIVE_CSHARP_MESSAGE_BYTES {
        return Err("Native C# LSP message exceeds size limit".to_string());
    }
    let mut body = vec![0; length];
    reader
        .read_exact(&mut body)
        .await
        .map_err(|error| error.to_string())?;
    Ok(Some(body))
}

impl SourceLspSessionKey {
    fn from_preview(
        preview: &SourceLspPreview,
        request: &SourceLspLookupRequest,
    ) -> Option<SourceLspSessionKey> {
        let spec = server_spec_for_language(&preview.language)?;
        Some(SourceLspSessionKey {
            language: spec.language_id.to_string(),
            root: normalized_lsp_root(&request.root)?,
        })
    }
}

impl SourceLspSession {
    fn start(
        root: PathBuf,
        server: ResolvedLspServer,
        last_used: u64,
    ) -> Result<SourceLspSession, String> {
        let connection = start_lsp_server(&server, &root)?;
        Ok(SourceLspSession {
            root,
            connection,
            last_used,
        })
    }
}

impl Drop for SourceLspSession {
    fn drop(&mut self) {
        self.connection.shut_down();
    }
}

/// Give each language server and every helper it starts one process-group
/// boundary owned by this connection. Roslyn starts MSBuild BuildHost children;
/// killing only the top-level server leaves those children behind after a
/// workspace switch or app shutdown.
fn configure_lsp_process_group(command: &mut Command) {
    command.process_group(0);
}

/// Wait up to `grace` for a server to exit by itself. Returns whether it did.
fn wait_for_lsp_exit(child: &mut Child, grace: Duration) -> bool {
    let deadline = Instant::now() + grace;
    loop {
        if matches!(child.try_wait(), Ok(Some(_))) {
            return true;
        }
        if Instant::now() >= deadline {
            return false;
        }
        thread::sleep(LSP_SHUTDOWN_POLL);
    }
}

/// Stop the complete language-server process tree and reap its direct child.
fn stop_lsp_process_tree(child: &mut Child) {
    if matches!(child.try_wait(), Ok(Some(_))) {
        return;
    }

    let process_group = -(child.id() as i32);
    // SAFETY: `configure_lsp_process_group` makes the spawned child's PID its
    // process-group id. A negative kill target therefore reaches only that
    // owned language-server group, including Roslyn's BuildHost helpers.
    let group_stopped = unsafe { libc::kill(process_group, libc::SIGKILL) } == 0;
    if !group_stopped {
        let _ = child.kill();
    }
    let _ = child.wait();
}

/// Start a language server under `root` and get a connection to it that several
/// questions can share.
fn start_lsp_server(server: &ResolvedLspServer, root: &Path) -> Result<Arc<LspConnection>, String> {
    if !root.is_dir() {
        return Err("Project root is not a directory".to_string());
    }

    let identity = LanguageServerIdentity {
        language_id: server.spec.language_id.to_string(),
        server_name: server.spec.server_name.to_string(),
        root: root.display().to_string(),
    };

    let mut command = Command::new(&server.command);
    command
        .args(server.spec.args)
        .current_dir(root)
        .stdin(Stdio::piped())
        .stdout(Stdio::piped())
        // Kept rather than thrown away: when a server refuses to work, what it printed
        // here is usually the only explanation there is. It is drained continuously into
        // a small in-memory list, so the pipe can never fill up and stall the server.
        .stderr(Stdio::piped());
    // Hand the server the same enriched PATH used to discover it, so servers that shell
    // out at runtime (the `rust-analyzer` rustup proxy → toolchain; Roslyn → dotnet)
    // resolve their tools even when the app was Finder/Dock-launched with a minimal PATH.
    if let Some(search_path) = lsp_search_path_env() {
        command.env("PATH", search_path);
    }
    configure_lsp_process_group(&mut command);
    let mut child = command.spawn().map_err(|error| {
        let reason = format!("Could not start {}: {error}", server.spec.server_name);
        record_language_server_state(
            &identity.language_id,
            &identity.root,
            LanguageServerState::NotRunning,
            Some(reason.clone()),
        );
        reason
    })?;

    let stdin = child
        .stdin
        .take()
        .ok_or_else(|| "Language server stdin unavailable".to_string())?;
    let stdout = child
        .stdout
        .take()
        .ok_or_else(|| "Language server stdout unavailable".to_string())?;
    let stderr = child.stderr.take();

    let connection = match LspConnection::connect(Box::new(stdout), Box::new(stdin), identity) {
        Ok(connection) => connection,
        Err(error) => {
            // The process is already running even though it never got as far as saying
            // hello. Nothing will ever tidy it up but us — dropping the handle does not
            // stop a process — and a stranded language server holds real memory.
            stop_lsp_process_tree(&mut child);
            return Err(error);
        }
    };
    connection.attach_child(child);

    if let Some(stderr) = stderr {
        let router = Arc::clone(&connection.router);
        thread::spawn(move || {
            for line in BufReader::new(stderr).lines() {
                match line {
                    Ok(line) => router.record_log_line(line),
                    Err(_) => break,
                }
            }
        });
    }

    Ok(connection)
}

/// Read a file from disk and describe it the way the language-server client expects.
///
/// Used only by the document-symbol lookup, which is handed a path rather than the
/// editor's copy of the text.
fn read_preview_for_symbols(
    root: &Path,
    path: &Path,
    language: &str,
) -> Result<SourceLspPreview, String> {
    let metadata = std::fs::metadata(path)
        .map_err(|error| format!("Could not read {}: {error}", path.display()))?;
    if metadata.len() > MAX_LSP_DOCUMENT_SYMBOL_BYTES {
        return Err(format!(
            "{} is too big to hand to a language server for a symbol list.",
            file_name_for(path)
        ));
    }
    let content = std::fs::read_to_string(path)
        .map_err(|error| format!("Could not read {}: {error}", path.display()))?;

    Ok(SourceLspPreview {
        path: path.display().to_string(),
        relative_path: relative_path_for(path, root),
        file_name: file_name_for(path),
        language: language.to_string(),
        byte_count: metadata.len(),
        content: content.clone(),
        line_count: content.lines().count().max(1),
    })
}

fn lock_lsp_session(
    session: &Arc<Mutex<SourceLspSession>>,
) -> Result<std::sync::MutexGuard<'_, SourceLspSession>, String> {
    session
        .lock()
        .map_err(|_| "Language server session lock poisoned".to_string())
}

pub(crate) fn read_source_lsp_status_sync(
    root: PathBuf,
    language: String,
) -> Result<SourceLspStatus, String> {
    let Some(spec) = server_spec_for_language(&language) else {
        return Ok(SourceLspStatus {
            language,
            language_id: "plaintext".to_string(),
            available: false,
            server_name: "none".to_string(),
            command: String::new(),
            args: Vec::new(),
            reason: Some("No language server configured for this file type".to_string()),
            state: LanguageServerState::NotRunning.as_str().to_string(),
            detail: Some(
                "No language server understands this kind of file, so there is nothing to run."
                    .to_string(),
            ),
        });
    };

    let root_exists = root.is_dir();
    let switched_off = !language_server_allowed(spec.language_id);
    let resolved = resolve_command(spec.command);
    let available = root_exists && resolved.is_some() && !switched_off;
    let command = resolved.unwrap_or_else(|| spec.command.to_string());
    let (state, detail) =
        describe_language_server_activity(spec, &root, switched_off, root_exists, available);
    Ok(SourceLspStatus {
        language,
        language_id: spec.language_id.to_string(),
        available,
        server_name: spec.server_name.to_string(),
        command,
        args: spec.args.iter().map(|arg| (*arg).to_string()).collect(),
        reason: if switched_off {
            Some(format!(
                "The {} language server is switched off in Settings. Reference counts and project search still work; mistake squiggles and precise go-to-definition do not.",
                spec.server_name
            ))
        } else if !root_exists {
            Some("Project root is not a directory".to_string())
        } else if !available {
            Some(format!("{} is not installed or not on PATH", spec.command))
        } else {
            None
        },
        state: state.as_str().to_string(),
        detail,
    })
}

/// What to tell the reader this server is doing, and one plain sentence about it.
///
/// Each warm workspace has its own slot. Activity from another root is irrelevant to
/// this reading and must never make this workspace look stopped or ready.
fn describe_language_server_activity(
    spec: LspServerSpec,
    root: &Path,
    switched_off: bool,
    root_exists: bool,
    available: bool,
) -> (LanguageServerState, Option<String>) {
    if switched_off {
        return (
            LanguageServerState::Disabled,
            Some(format!(
                "The {} language server is switched off in Settings.",
                spec.server_name
            )),
        );
    }
    if !root_exists {
        return (
            LanguageServerState::NotRunning,
            Some("The folder this was asked about is not a folder on disk.".to_string()),
        );
    }
    if !available {
        return (
            LanguageServerState::NotRunning,
            Some(format!(
                "{} is not installed, or it is not on the list of places this app looks for programs.",
                spec.command
            )),
        );
    }
    // Read mode is a choice, not a failure, so it says so in those words rather
    // than leaving the chip looking like a server that would not start. It is
    // read after the checks above because a missing program is a fact about the
    // machine, true in either mode, and more use to the reader than the mode is.
    if !language_intelligence_on(&root.display().to_string()) {
        return (
            LanguageServerState::Disabled,
            Some(READ_MODE_DETAIL.to_string()),
        );
    }

    match read_language_server_activity(spec.language_id, root) {
        Some((state, detail)) => {
            let detail = detail.or_else(|| default_language_server_detail(spec, state));
            (state, detail)
        }
        None => (
            LanguageServerState::NotRunning,
            Some(format!(
                "{} has not been started yet. It starts the first time you open a file it understands.",
                spec.server_name
            )),
        ),
    }
}

fn default_language_server_detail(
    spec: LspServerSpec,
    state: LanguageServerState,
) -> Option<String> {
    match state {
        LanguageServerState::Ready => Some(format!(
            "{} has read the project and is answering questions.",
            spec.server_name
        )),
        LanguageServerState::Starting => Some(format!("Starting {}.", spec.server_name)),
        LanguageServerState::Indexing => Some(format!(
            "{} is still reading the project, so its answers are incomplete.",
            spec.server_name
        )),
        LanguageServerState::NotRunning => Some(format!(
            "{} is not running. It starts the first time you open a file it understands.",
            spec.server_name
        )),
        LanguageServerState::Disabled => None,
    }
}

pub(crate) fn list_source_lsp_statuses_sync(root: PathBuf) -> Vec<SourceLspStatus> {
    SOURCE_LSP_READINESS_LANGUAGES
        .iter()
        .map(|language| {
            read_source_lsp_status_sync(root.clone(), (*language).to_string()).unwrap_or_else(
                |error| SourceLspStatus {
                    language: (*language).to_string(),
                    language_id: (*language).to_string(),
                    available: false,
                    server_name: "unknown".to_string(),
                    command: String::new(),
                    args: Vec::new(),
                    reason: Some(error.clone()),
                    state: LanguageServerState::NotRunning.as_str().to_string(),
                    detail: Some(error),
                },
            )
        })
        .collect()
}

fn write_lsp_message<W: Write>(writer: &mut W, message: &Value) -> Result<(), String> {
    let body = serde_json::to_vec(message)
        .map_err(|error| format!("Could not encode LSP message: {error}"))?;
    write!(writer, "Content-Length: {}\r\n\r\n", body.len())
        .map_err(|error| format!("Could not write LSP header: {error}"))?;
    writer
        .write_all(&body)
        .map_err(|error| format!("Could not write LSP body: {error}"))?;
    writer
        .flush()
        .map_err(|error| format!("Could not flush LSP message: {error}"))
}

fn read_lsp_message<R: Read>(reader: &mut R) -> io::Result<Value> {
    let mut header = Vec::new();
    let mut byte = [0_u8; 1];
    while !header.ends_with(b"\r\n\r\n") {
        reader.read_exact(&mut byte)?;
        header.push(byte[0]);
        if header.len() > MAX_LSP_HEADER_BYTES {
            return Err(io::Error::new(
                io::ErrorKind::InvalidData,
                "LSP header exceeded size limit",
            ));
        }
    }

    let header_text = String::from_utf8(header)
        .map_err(|error| io::Error::new(io::ErrorKind::InvalidData, error))?;
    let content_length = header_text
        .lines()
        .find_map(|line| {
            let (name, value) = line.split_once(':')?;
            if name.eq_ignore_ascii_case("content-length") {
                value.trim().parse::<usize>().ok()
            } else {
                None
            }
        })
        .ok_or_else(|| io::Error::new(io::ErrorKind::InvalidData, "Missing Content-Length"))?;

    let mut body = vec![0_u8; content_length];
    reader.read_exact(&mut body)?;
    serde_json::from_slice(&body).map_err(|error| io::Error::new(io::ErrorKind::InvalidData, error))
}

fn lsp_position(request: &SourceLspLookupRequest) -> Value {
    json!({
        "line": request.line.saturating_sub(1),
        "character": request.column.saturating_sub(1)
    })
}

fn lsp_code_action_range(request: &SourceLspCodeActionRequest) -> Value {
    json!({
        "start": {
            "line": request.start_line.saturating_sub(1),
            "character": request.start_column.saturating_sub(1)
        },
        "end": {
            "line": request.end_line.saturating_sub(1),
            "character": request.end_column.saturating_sub(1)
        }
    })
}

fn lsp_full_document_range(preview: &SourceLspPreview) -> Value {
    let line_count = preview.line_count.max(1);
    json!({
        "start": {
            "line": 0,
            "character": 0
        },
        "end": {
            "line": line_count.saturating_sub(1),
            "character": last_line_utf16_len(&preview.content)
        }
    })
}

fn last_line_utf16_len(content: &str) -> usize {
    content
        .rsplit_once('\n')
        .map(|(_, last_line)| last_line)
        .unwrap_or(content)
        .encode_utf16()
        .count()
}

fn lsp_code_action_diagnostic(diagnostic: &SourceLspCodeActionDiagnostic) -> Value {
    json!({
        "range": {
            "start": {
                "line": diagnostic.start_line.saturating_sub(1),
                "character": diagnostic.start_column.saturating_sub(1)
            },
            "end": {
                "line": diagnostic.end_line.saturating_sub(1),
                "character": diagnostic.end_column.saturating_sub(1)
            }
        },
        "severity": lsp_diagnostic_severity_code(&diagnostic.severity),
        "message": diagnostic.message,
        "source": diagnostic.source
    })
}

fn lsp_diagnostic_severity_code(severity: &str) -> u8 {
    match severity {
        "error" => 1,
        "warning" => 2,
        "info" => 3,
        "hint" => 4,
        _ => 3,
    }
}

fn lsp_semantic_token_types_from_initialize(response: &Value) -> Vec<String> {
    response
        .get("result")
        .and_then(|result| result.get("capabilities"))
        .and_then(|capabilities| capabilities.get("semanticTokensProvider"))
        .and_then(|provider| provider.get("legend"))
        .and_then(|legend| legend.get("tokenTypes"))
        .and_then(Value::as_array)
        .map(|token_types| {
            token_types
                .iter()
                .filter_map(Value::as_str)
                .map(str::to_string)
                .collect()
        })
        .unwrap_or_default()
}

/// The LSP `TextDocumentSyncKind` advertised by the server: 0 none, 1 full,
/// 2 incremental. Servers may send either the bare number or an options object.
fn lsp_text_document_sync_kind_from_initialize(response: &Value) -> u64 {
    let sync = response
        .get("result")
        .and_then(|result| result.get("capabilities"))
        .and_then(|capabilities| capabilities.get("textDocumentSync"));

    sync.and_then(Value::as_u64)
        .or_else(|| {
            sync.and_then(|sync| sync.get("change"))
                .and_then(Value::as_u64)
        })
        .unwrap_or(0)
}

fn lsp_diagnostic_identifier_from_initialize(response: &Value) -> Option<String> {
    response
        .get("result")
        .and_then(|result| result.get("capabilities"))
        .and_then(|capabilities| capabilities.get("diagnosticProvider"))
        .and_then(|provider| provider.get("identifier"))
        .and_then(Value::as_str)
        .map(str::to_string)
}

/// End of a document in LSP coordinates. Characters are UTF-16 code units, not
/// Rust bytes or Unicode scalar values.
fn lsp_document_end_position(content: &str) -> Value {
    let mut lines = content.split('\n');
    let mut line = 0_u64;
    let mut last_line = lines.next().unwrap_or_default();
    for next in lines {
        line += 1;
        last_line = next;
    }
    json!({
        "line": line,
        "character": last_line.encode_utf16().count()
    })
}

fn lsp_client_capabilities() -> Value {
    json!({
        "workspace": {
            "configuration": true,
            "workspaceFolders": true
        },
        "window": {
            "workDoneProgress": true
        },
        "textDocument": {
            "codeAction": {
                "codeActionLiteralSupport": {
                    "codeActionKind": {
                        "valueSet": [
                            "",
                            "quickfix",
                            "refactor",
                            "refactor.extract",
                            "refactor.inline",
                            "refactor.rewrite",
                            "source",
                            "source.organizeImports",
                            "source.fixAll"
                        ]
                    }
                },
                "isPreferredSupport": true
            },
            "completion": {
                "completionItem": {
                    "snippetSupport": false
                }
            },
            "signatureHelp": {
                "signatureInformation": {
                    "documentationFormat": ["markdown", "plaintext"],
                    "parameterInformation": {
                        "labelOffsetSupport": true
                    }
                }
            },
            "inlayHint": {
                "dynamicRegistration": false,
                "resolveSupport": {
                    "properties": [
                        "tooltip",
                        "textEdits",
                        "label.tooltip",
                        "label.location",
                        "label.command"
                    ]
                }
            },
            "semanticTokens": {
                "dynamicRegistration": false,
                "requests": {
                    "range": false,
                    "full": true
                },
                "tokenTypes": [
                    "namespace",
                    "type",
                    "class",
                    "enum",
                    "interface",
                    "struct",
                    "typeParameter",
                    "parameter",
                    "variable",
                    "property",
                    "enumMember",
                    "event",
                    "function",
                    "method",
                    "macro",
                    "keyword",
                    "modifier",
                    "comment",
                    "string",
                    "number",
                    "regexp",
                    "operator"
                ],
                "tokenModifiers": [],
                "formats": ["relative"],
                "overlappingTokenSupport": false,
                "multilineTokenSupport": false
            },
            "definition": {
                "linkSupport": true
            },
            "diagnostic": {
                "dynamicRegistration": false,
                "relatedDocumentSupport": false
            },
            "implementation": {
                "linkSupport": true
            },
            "typeDefinition": {
                "linkSupport": true
            },
            "publishDiagnostics": {
                "relatedInformation": true,
                "tagSupport": {
                    "valueSet": [1, 2]
                }
            }
        }
    })
}

fn lsp_locations_from_result(result: &Value) -> Vec<LspLocation> {
    match result {
        Value::Array(values) => values.iter().filter_map(lsp_location_from_value).collect(),
        Value::Object(_) => lsp_location_from_value(result).into_iter().collect(),
        _ => Vec::new(),
    }
}

fn lsp_location_from_value(value: &Value) -> Option<LspLocation> {
    let uri = value
        .get("uri")
        .or_else(|| value.get("targetUri"))
        .and_then(Value::as_str)?;
    let range = value
        .get("range")
        .or_else(|| value.get("targetSelectionRange"))
        .or_else(|| value.get("targetRange"))?;
    let start = range.get("start")?;
    let line = start.get("line")?.as_u64()? as usize + 1;
    let column = start.get("character")?.as_u64()? as usize + 1;
    Some(LspLocation {
        path: file_uri_to_path(uri)?,
        line,
        column,
    })
}

fn hover_contents_from_result(result: &Value) -> Vec<String> {
    result
        .get("contents")
        .map(hover_contents_from_value)
        .unwrap_or_default()
}

fn hover_contents_from_value(value: &Value) -> Vec<String> {
    match value {
        Value::String(text) if !text.trim().is_empty() => vec![text.clone()],
        Value::Array(values) => values
            .iter()
            .flat_map(hover_contents_from_value)
            .filter(|text| !text.trim().is_empty())
            .collect(),
        Value::Object(map) => {
            if let Some(text) = map.get("value").and_then(Value::as_str) {
                if let Some(language) = map.get("language").and_then(Value::as_str) {
                    return vec![format!("```{language}\n{text}\n```")];
                }
                return vec![text.to_string()];
            }
            Vec::new()
        }
        _ => Vec::new(),
    }
}

fn lsp_signature_help_from_result(result: &Value) -> Option<SourceLspSignatureHelp> {
    let signatures = result
        .get("signatures")
        .and_then(Value::as_array)?
        .iter()
        .filter_map(lsp_signature_from_value)
        .collect::<Vec<_>>();
    if signatures.is_empty() {
        return None;
    }

    Some(SourceLspSignatureHelp {
        active_signature: result
            .get("activeSignature")
            .and_then(Value::as_u64)
            .unwrap_or(0) as usize,
        active_parameter: result
            .get("activeParameter")
            .and_then(Value::as_u64)
            .unwrap_or(0) as usize,
        signatures,
    })
}

fn lsp_inlay_hints_from_result(result: &Value, limit: usize) -> Vec<SourceLspInlayHint> {
    let Some(items) = result.as_array() else {
        return Vec::new();
    };

    items
        .iter()
        .filter_map(lsp_inlay_hint_from_value)
        .take(limit)
        .collect()
}

fn lsp_inlay_hint_from_value(value: &Value) -> Option<SourceLspInlayHint> {
    let position = value.get("position")?;
    let label = lsp_inlay_hint_label(value.get("label")?);
    if label.trim().is_empty() {
        return None;
    }
    let mut tooltip = lsp_documentation_from_value(value.get("tooltip"));
    if tooltip.is_empty() {
        tooltip = lsp_inlay_hint_label_tooltip(value.get("label"));
    }

    Some(SourceLspInlayHint {
        label,
        tooltip,
        kind: lsp_inlay_hint_kind(value.get("kind").and_then(Value::as_u64)).to_string(),
        line: position.get("line")?.as_u64()? as usize + 1,
        column: position.get("character")?.as_u64()? as usize + 1,
        padding_left: value
            .get("paddingLeft")
            .and_then(Value::as_bool)
            .unwrap_or(false),
        padding_right: value
            .get("paddingRight")
            .and_then(Value::as_bool)
            .unwrap_or(false),
    })
}

fn lsp_inlay_hint_label(value: &Value) -> String {
    if let Some(label) = value.as_str() {
        return label.to_string();
    }

    value
        .as_array()
        .map(|parts| {
            parts
                .iter()
                .filter_map(|part| part.get("value").and_then(Value::as_str))
                .collect::<String>()
        })
        .unwrap_or_default()
}

fn lsp_inlay_hint_label_tooltip(value: Option<&Value>) -> String {
    let Some(Value::Array(parts)) = value else {
        return String::new();
    };

    parts
        .iter()
        .find_map(|part| {
            let tooltip = lsp_documentation_from_value(part.get("tooltip"));
            if tooltip.is_empty() {
                None
            } else {
                Some(tooltip)
            }
        })
        .unwrap_or_default()
}

fn lsp_inlay_hint_kind(kind: Option<u64>) -> &'static str {
    match kind {
        Some(1) => "type",
        Some(2) => "parameter",
        _ => "other",
    }
}

fn lsp_semantic_tokens_from_result(
    result: &Value,
    legend: &[String],
    limit: usize,
) -> Vec<SourceLspSemanticToken> {
    let Some(data) = result.get("data").and_then(Value::as_array) else {
        return Vec::new();
    };

    let mut tokens = Vec::new();
    let mut current_line = 0_usize;
    let mut current_start = 0_usize;

    for chunk in data.chunks(5) {
        if chunk.len() != 5 || tokens.len() >= limit {
            break;
        }

        let delta_line = chunk[0].as_u64().unwrap_or(0) as usize;
        let delta_start = chunk[1].as_u64().unwrap_or(0) as usize;
        let length = chunk[2].as_u64().unwrap_or(0) as usize;
        let token_type_index = chunk[3].as_u64().unwrap_or(u64::MAX) as usize;

        current_line += delta_line;
        current_start = if delta_line == 0 {
            current_start + delta_start
        } else {
            delta_start
        };

        if length == 0 {
            continue;
        }
        let Some(raw_token_type) = legend.get(token_type_index) else {
            continue;
        };
        let Some(token_type) = normalized_semantic_token_type(raw_token_type) else {
            continue;
        };

        tokens.push(SourceLspSemanticToken {
            token_type: token_type.to_string(),
            line: current_line + 1,
            start_column: current_start + 1,
            length,
        });
    }

    tokens
}

fn normalized_semantic_token_type(raw: &str) -> Option<&'static str> {
    match raw {
        "namespace" => Some("namespace"),
        "class" | "struct" => Some("class"),
        "interface" => Some("interface"),
        "type" => Some("type"),
        "enum" => Some("enum"),
        "function" => Some("function"),
        "method" | "constructor" => Some("method"),
        "property" => Some("property"),
        "variable" | "local" => Some("variable"),
        "parameter" => Some("parameter"),
        "enumMember" => Some("enumMember"),
        "typeParameter" => Some("typeParameter"),
        "keyword" | "modifier" => Some("keyword"),
        "string" => Some("string"),
        "number" => Some("number"),
        "operator" => Some("operator"),
        "comment" => Some("comment"),
        _ => None,
    }
}

fn lsp_signature_from_value(value: &Value) -> Option<SourceLspSignature> {
    let label = value.get("label")?.as_str()?.trim().to_string();
    if label.is_empty() {
        return None;
    }

    let parameters = value
        .get("parameters")
        .and_then(Value::as_array)
        .map(|values| {
            values
                .iter()
                .filter_map(|parameter| lsp_signature_parameter_from_value(parameter, &label))
                .collect()
        })
        .unwrap_or_default();

    Some(SourceLspSignature {
        documentation: lsp_documentation_from_value(value.get("documentation")),
        label,
        parameters,
    })
}

fn lsp_signature_parameter_from_value(
    value: &Value,
    signature_label: &str,
) -> Option<SourceLspSignatureParameter> {
    let label = lsp_signature_parameter_label(value.get("label")?, signature_label)?;
    if label.trim().is_empty() {
        return None;
    }

    Some(SourceLspSignatureParameter {
        label,
        documentation: lsp_documentation_from_value(value.get("documentation")),
    })
}

fn lsp_signature_parameter_label(value: &Value, signature_label: &str) -> Option<String> {
    if let Some(label) = value.as_str() {
        return Some(label.to_string());
    }

    let range = value.as_array()?;
    if range.len() != 2 {
        return None;
    }
    let start = range[0].as_u64()? as usize;
    let end = range[1].as_u64()? as usize;
    if start >= end {
        return None;
    }

    signature_label.get(start..end).map(str::to_string)
}

fn lsp_documentation_from_value(value: Option<&Value>) -> String {
    let Some(value) = value else {
        return String::new();
    };

    match value {
        Value::String(text) => text.trim().to_string(),
        Value::Object(map) => map
            .get("value")
            .and_then(Value::as_str)
            .unwrap_or_default()
            .trim()
            .to_string(),
        _ => String::new(),
    }
}

pub(crate) fn diagnostics_from_message(
    message: &Value,
    file_uri: &str,
) -> Vec<SourceLspDiagnostic> {
    if message.get("method").and_then(Value::as_str) != Some("textDocument/publishDiagnostics") {
        return Vec::new();
    }

    let Some(params) = message.get("params") else {
        return Vec::new();
    };
    if params.get("uri").and_then(Value::as_str) != Some(file_uri) {
        return Vec::new();
    }

    params
        .get("diagnostics")
        .and_then(Value::as_array)
        .map(|diagnostics| {
            diagnostics
                .iter()
                .filter_map(|value| lsp_diagnostic_from_value(value, file_uri))
                .collect()
        })
        .unwrap_or_default()
}

fn diagnostics_from_pull_result(result: &Value, file_uri: &str) -> Vec<SourceLspDiagnostic> {
    result
        .get("items")
        .and_then(Value::as_array)
        .map(|diagnostics| {
            diagnostics
                .iter()
                .filter_map(|value| lsp_diagnostic_from_value(value, file_uri))
                .collect()
        })
        .unwrap_or_default()
}

/// Every file with diagnostics under `root`, flattened into one list ordered by file and
/// then by position in the file, so a problems list can render it straight through.
/// A file whose location cannot be read from its URI falls back to the root the language
/// server for it is pointed at.
pub(crate) fn collect_diagnostics_for_root(
    sessions: &[(PathBuf, HashMap<String, Vec<SourceLspDiagnostic>>)],
    root: &Path,
) -> Vec<SourceLspDiagnostic> {
    let mut collected = Vec::new();
    for (session_root, diagnostics_by_uri) in sessions {
        for (file_uri, diagnostics) in diagnostics_by_uri {
            let file_path = file_uri_to_path(file_uri);
            let belongs = match file_path.as_deref() {
                Some(path) => lsp_path_is_within(path, root),
                None => lsp_path_is_within(session_root, root),
            };
            if !belongs {
                continue;
            }

            for diagnostic in diagnostics {
                let mut diagnostic = diagnostic.clone();
                if diagnostic.path.is_none() {
                    diagnostic.path = file_path.as_deref().map(|path| path.display().to_string());
                }
                collected.push(diagnostic);
            }
        }
    }

    collected.sort_by(|left, right| {
        left.path
            .cmp(&right.path)
            .then(left.line.cmp(&right.line))
            .then(left.column.cmp(&right.column))
            .then(left.message.cmp(&right.message))
    });
    collected
}

/// True when `path` is `root` itself or sits inside it. Compares whole path segments, so
/// `/repo-backup` is never mistaken for something inside `/repo`.
fn lsp_path_is_within(path: &Path, root: &Path) -> bool {
    path == root || path.starts_with(root)
}

fn diagnostic_uri_from_message(message: &Value) -> Option<String> {
    if message.get("method").and_then(Value::as_str) != Some("textDocument/publishDiagnostics") {
        return None;
    }
    message
        .get("params")?
        .get("uri")?
        .as_str()
        .map(|uri| uri.to_string())
}

fn lsp_diagnostic_from_value(value: &Value, file_uri: &str) -> Option<SourceLspDiagnostic> {
    let range = value.get("range")?;
    let start = range.get("start")?;
    let line = start.get("line")?.as_u64()? as usize + 1;
    let column = start.get("character")?.as_u64()? as usize + 1;
    let message = value.get("message")?.as_str()?.trim().to_string();
    if message.is_empty() {
        return None;
    }

    Some(SourceLspDiagnostic {
        severity: lsp_diagnostic_severity(value.get("severity").and_then(Value::as_u64)),
        message,
        line,
        column,
        source: value
            .get("source")
            .and_then(Value::as_str)
            .map(|source| source.to_string()),
        path: file_uri_to_path(file_uri).map(|path| path.display().to_string()),
    })
}

fn lsp_diagnostic_severity(severity: Option<u64>) -> String {
    match severity {
        Some(1) => "error",
        Some(2) => "warning",
        Some(4) => "hint",
        _ => "info",
    }
    .to_string()
}

fn lsp_workspace_edit_files_from_result(
    result: &Value,
    root: &Path,
) -> Vec<SourceLspWorkspaceEditFile> {
    let mut files = Vec::new();

    if let Some(document_changes) = result.get("documentChanges").and_then(Value::as_array) {
        for document_change in document_changes {
            let Some(uri) = document_change
                .get("textDocument")
                .and_then(|text_document| text_document.get("uri"))
                .and_then(Value::as_str)
            else {
                continue;
            };
            let Some(edits) = document_change.get("edits").and_then(Value::as_array) else {
                continue;
            };
            push_lsp_workspace_edit_file(&mut files, uri, edits, root);
        }
    }

    if let Some(changes) = result.get("changes").and_then(Value::as_object) {
        for (uri, edits) in changes {
            let Some(edits) = edits.as_array() else {
                continue;
            };
            push_lsp_workspace_edit_file(&mut files, uri, edits, root);
        }
    }

    files
}

fn push_lsp_workspace_edit_file(
    files: &mut Vec<SourceLspWorkspaceEditFile>,
    uri: &str,
    edits: &[Value],
    root: &Path,
) {
    let Some(path) = file_uri_to_path(uri) else {
        return;
    };
    let parsed_edits = edits
        .iter()
        .filter_map(lsp_text_edit_from_value)
        .collect::<Vec<_>>();
    if parsed_edits.is_empty() {
        return;
    }

    files.push(SourceLspWorkspaceEditFile {
        path: path.display().to_string(),
        relative_path: relative_path_for(&path, root),
        edits: parsed_edits,
    });
}

fn lsp_code_actions_from_result(
    result: &Value,
    root: &Path,
    limit: usize,
) -> Vec<SourceLspCodeAction> {
    let Some(actions) = result.as_array() else {
        return Vec::new();
    };

    actions
        .iter()
        .filter_map(|action| lsp_code_action_from_value(action, root))
        .take(limit)
        .collect()
}

fn lsp_code_action_from_value(value: &Value, root: &Path) -> Option<SourceLspCodeAction> {
    let title = value.get("title").and_then(Value::as_str)?.to_string();
    let kind = value
        .get("kind")
        .and_then(Value::as_str)
        .unwrap_or("command")
        .to_string();
    let is_preferred = value
        .get("isPreferred")
        .and_then(Value::as_bool)
        .unwrap_or(false);
    let files = value
        .get("edit")
        .map(|edit| lsp_workspace_edit_files_from_result(edit, root))
        .unwrap_or_default();
    let disabled_reason = value
        .get("disabled")
        .and_then(|disabled| disabled.get("reason"))
        .and_then(Value::as_str)
        .map(str::to_string)
        .or_else(|| {
            if files.is_empty() && value.get("command").is_some() {
                Some("Command-only code action is not supported yet".to_string())
            } else {
                None
            }
        });

    Some(SourceLspCodeAction {
        title,
        kind,
        is_preferred,
        disabled_reason,
        files,
    })
}

fn lsp_document_highlights_from_result(result: &Value) -> Vec<SourceLspDocumentHighlight> {
    let Some(items) = result.as_array() else {
        return Vec::new();
    };

    items
        .iter()
        .filter_map(lsp_document_highlight_from_value)
        .collect()
}

fn lsp_document_highlight_from_value(value: &Value) -> Option<SourceLspDocumentHighlight> {
    let range = value.get("range")?;
    let start = range.get("start")?;
    let end = range.get("end")?;

    Some(SourceLspDocumentHighlight {
        start_line: start.get("line")?.as_u64()? as usize + 1,
        start_column: start.get("character")?.as_u64()? as usize + 1,
        end_line: end.get("line")?.as_u64()? as usize + 1,
        end_column: end.get("character")?.as_u64()? as usize + 1,
        kind: lsp_document_highlight_kind(value.get("kind").and_then(Value::as_u64)).to_string(),
    })
}

fn lsp_document_highlight_kind(kind: Option<u64>) -> &'static str {
    match kind {
        Some(2) => "read",
        Some(3) => "write",
        _ => "text",
    }
}

fn lsp_symbols_from_result(result: &Value, limit: usize) -> Vec<SourceLspSymbol> {
    let mut symbols = Vec::new();
    if let Value::Array(items) = result {
        for item in items {
            collect_lsp_symbol(item, &mut symbols, limit);
            if symbols.len() >= limit {
                break;
            }
        }
    }
    symbols
}

/// Every symbol in a document-symbol answer, nesting removed and both numbers counted
/// from zero — the shape the editor's margin counts want.
fn lsp_document_symbols_from_result(result: &Value, limit: usize) -> Vec<SourceLspDocumentSymbol> {
    lsp_symbols_from_result(result, limit)
        .into_iter()
        .map(|symbol| SourceLspDocumentSymbol {
            name: symbol.name,
            kind: symbol.kind,
            line: symbol.line.saturating_sub(1),
            character: symbol.column.saturating_sub(1),
        })
        .collect()
}

fn lsp_workspace_symbols_from_result(
    result: &Value,
    root: &Path,
    fallback_language: &str,
    limit: usize,
) -> Vec<SourceLspWorkspaceSymbol> {
    let Some(items) = result.as_array() else {
        return Vec::new();
    };

    items
        .iter()
        .filter_map(|value| lsp_workspace_symbol_from_value(value, root, fallback_language))
        .take(limit)
        .collect()
}

fn lsp_workspace_symbol_from_value(
    value: &Value,
    root: &Path,
    fallback_language: &str,
) -> Option<SourceLspWorkspaceSymbol> {
    let symbol_name = value.get("name")?.as_str()?.trim().to_string();
    if symbol_name.is_empty() {
        return None;
    }

    let location = value.get("location")?;
    let uri = location
        .get("uri")
        .or_else(|| location.get("targetUri"))?
        .as_str()?;
    let path = file_uri_to_path(uri)?;
    let range = location
        .get("range")
        .or_else(|| location.get("targetSelectionRange"))
        .or_else(|| value.get("range"))?;
    let start = range.get("start")?;
    let line = start.get("line")?.as_u64()? as usize + 1;
    let column = start.get("character")?.as_u64()? as usize + 1;
    let relative_path = relative_path_for(&path, root);
    let file_name = file_name_for(&path);
    let container_name = value
        .get("containerName")
        .and_then(Value::as_str)
        .map(str::trim)
        .filter(|container| !container.is_empty())
        .map(str::to_string);
    let location_detail = format!("{relative_path}:{line}:{column}");
    let detail = container_name
        .as_ref()
        .map(|container| format!("{container} - {location_detail}"))
        .unwrap_or_else(|| location_detail.clone());
    let metadata = std::fs::metadata(&path).ok();

    Some(SourceLspWorkspaceSymbol {
        path: path.display().to_string(),
        relative_path,
        file_name,
        language: language_for_path(&path).unwrap_or_else(|| fallback_language.to_string()),
        byte_count: metadata.map(|metadata| metadata.len()).unwrap_or(0),
        symbol_name,
        kind: lsp_symbol_kind(value.get("kind").and_then(Value::as_u64)),
        line,
        column,
        detail,
        container_name,
    })
}

fn lsp_completion_items_from_result(result: &Value, limit: usize) -> Vec<SourceLspCompletionItem> {
    let items = result
        .as_array()
        .or_else(|| result.get("items").and_then(Value::as_array));
    let Some(items) = items else {
        return Vec::new();
    };

    items
        .iter()
        .filter_map(lsp_completion_item_from_value)
        .take(limit)
        .collect()
}

fn lsp_text_edits_from_result(result: &Value) -> Vec<SourceLspTextEdit> {
    let Some(items) = result.as_array() else {
        return Vec::new();
    };

    items.iter().filter_map(lsp_text_edit_from_value).collect()
}

fn lsp_text_edit_from_value(value: &Value) -> Option<SourceLspTextEdit> {
    let range = value.get("range")?;
    let start = range.get("start")?;
    let end = range.get("end")?;

    Some(SourceLspTextEdit {
        start_line: start.get("line")?.as_u64()? as usize + 1,
        start_column: start.get("character")?.as_u64()? as usize + 1,
        end_line: end.get("line")?.as_u64()? as usize + 1,
        end_column: end.get("character")?.as_u64()? as usize + 1,
        new_text: value
            .get("newText")
            .and_then(Value::as_str)
            .unwrap_or_default()
            .to_string(),
    })
}

fn lsp_completion_item_from_value(value: &Value) -> Option<SourceLspCompletionItem> {
    let label = value.get("label")?.as_str()?.trim().to_string();
    if label.is_empty() {
        return None;
    }

    let insert_text = value
        .get("insertText")
        .and_then(Value::as_str)
        .unwrap_or(&label)
        .trim()
        .to_string();

    Some(SourceLspCompletionItem {
        label,
        kind: lsp_completion_item_kind(value.get("kind").and_then(Value::as_u64)),
        detail: value
            .get("detail")
            .or_else(|| value.get("documentation").and_then(|doc| doc.get("value")))
            .and_then(Value::as_str)
            .unwrap_or_default()
            .trim()
            .to_string(),
        insert_text,
    })
}

fn collect_lsp_symbol(value: &Value, symbols: &mut Vec<SourceLspSymbol>, limit: usize) {
    if symbols.len() >= limit {
        return;
    }

    if let Some(symbol) = lsp_symbol_from_value(value) {
        symbols.push(symbol);
    }

    if let Some(children) = value.get("children").and_then(Value::as_array) {
        for child in children {
            collect_lsp_symbol(child, symbols, limit);
            if symbols.len() >= limit {
                break;
            }
        }
    }
}

fn lsp_symbol_from_value(value: &Value) -> Option<SourceLspSymbol> {
    let name = value.get("name")?.as_str()?.trim().to_string();
    if name.is_empty() {
        return None;
    }

    let kind = lsp_symbol_kind(value.get("kind").and_then(Value::as_u64));
    let detail = value
        .get("detail")
        .or_else(|| value.get("containerName"))
        .and_then(Value::as_str)
        .unwrap_or_default()
        .trim()
        .to_string();
    let start = value
        .get("selectionRange")
        .or_else(|| value.get("range"))
        .or_else(|| {
            value
                .get("location")
                .and_then(|location| location.get("range"))
        })?
        .get("start")?;
    let line = start.get("line")?.as_u64()? as usize + 1;
    let column = start.get("character")?.as_u64()? as usize + 1;

    Some(SourceLspSymbol {
        name,
        kind,
        line,
        column,
        detail,
    })
}

fn lsp_symbol_kind(kind: Option<u64>) -> String {
    match kind {
        Some(1) => "file",
        Some(2) => "module",
        Some(3) => "namespace",
        Some(4) => "package",
        Some(5) => "class",
        Some(6) => "method",
        Some(7) => "property",
        Some(8) => "field",
        Some(9) => "constructor",
        Some(10) => "enum",
        Some(11) => "interface",
        Some(12) => "function",
        Some(13) => "variable",
        Some(14) => "constant",
        Some(15) => "string",
        Some(16) => "number",
        Some(17) => "boolean",
        Some(18) => "array",
        Some(19) => "object",
        Some(20) => "key",
        Some(21) => "null",
        Some(22) => "enumMember",
        Some(23) => "struct",
        Some(24) => "event",
        Some(25) => "operator",
        Some(26) => "typeParameter",
        _ => "symbol",
    }
    .to_string()
}

fn lsp_completion_item_kind(kind: Option<u64>) -> String {
    match kind {
        Some(1) => "text",
        Some(2) => "method",
        Some(3) => "function",
        Some(4) => "constructor",
        Some(5) => "field",
        Some(6) => "variable",
        Some(7) => "class",
        Some(8) => "interface",
        Some(9) => "module",
        Some(10) => "property",
        Some(11) => "unit",
        Some(12) => "value",
        Some(13) => "enum",
        Some(14) => "keyword",
        Some(15) => "snippet",
        Some(16) => "color",
        Some(17) => "file",
        Some(18) => "reference",
        Some(19) => "folder",
        Some(20) => "enumMember",
        Some(21) => "constant",
        Some(22) => "struct",
        Some(23) => "event",
        Some(24) => "operator",
        Some(25) => "typeParameter",
        _ => "value",
    }
    .to_string()
}

fn definition_target_from_location(
    preview: &SourceLspPreview,
    request: &SourceLspLookupRequest,
    location: &LspLocation,
    symbol_name: &str,
) -> SourceLspDefinitionTarget {
    let metadata = std::fs::metadata(&location.path).ok();
    let relative_path = relative_path_for(&location.path, Path::new(&request.root));
    SourceLspDefinitionTarget {
        path: location.path.display().to_string(),
        file_name: file_name_for(&location.path),
        language: language_for_path(&location.path).unwrap_or_else(|| preview.language.clone()),
        byte_count: metadata.map(|metadata| metadata.len()).unwrap_or(0),
        relative_path: relative_path.clone(),
        symbol_name: symbol_name.to_string(),
        kind: "lsp".to_string(),
        line: location.line,
        column: location.column,
        detail: format!("{relative_path}:{}:{}", location.line, location.column),
    }
}

fn reference_target_from_location(
    preview: &SourceLspPreview,
    request: &SourceLspLookupRequest,
    location: &LspLocation,
    symbol_name: &str,
) -> SourceLspReferenceTarget {
    let metadata = std::fs::metadata(&location.path).ok();
    SourceLspReferenceTarget {
        path: location.path.display().to_string(),
        relative_path: relative_path_for(&location.path, Path::new(&request.root)),
        file_name: file_name_for(&location.path),
        language: language_for_path(&location.path).unwrap_or_else(|| preview.language.clone()),
        byte_count: metadata.map(|metadata| metadata.len()).unwrap_or(0),
        symbol_name: symbol_name.to_string(),
        line: location.line,
        column: location.column,
        excerpt: source_line_excerpt(preview, location),
    }
}

fn source_line_excerpt(preview: &SourceLspPreview, location: &LspLocation) -> String {
    let content = if Path::new(&preview.path) == location.path {
        preview.content.clone()
    } else {
        std::fs::read_to_string(&location.path).unwrap_or_default()
    };
    content
        .lines()
        .nth(location.line.saturating_sub(1))
        .map(compact_line)
        .unwrap_or_default()
}

fn relative_path_for(path: &Path, root: &Path) -> String {
    path.strip_prefix(root)
        .unwrap_or(path)
        .display()
        .to_string()
}

fn file_name_for(path: &Path) -> String {
    path.file_name()
        .and_then(|name| name.to_str())
        .unwrap_or("source")
        .to_string()
}

fn language_for_path(path: &Path) -> Option<String> {
    match path.extension().and_then(|extension| extension.to_str())? {
        "cs" => Some("csharp".to_string()),
        "ts" => Some("typescript".to_string()),
        "tsx" => Some("typescript".to_string()),
        "js" => Some("javascript".to_string()),
        "jsx" => Some("javascript".to_string()),
        "rs" => Some("rust".to_string()),
        "svelte" => Some("svelte".to_string()),
        _ => None,
    }
}

fn symbol_at_position(content: &str, line: usize, column: usize) -> Option<String> {
    let line_text = content.lines().nth(line.saturating_sub(1))?;
    let chars: Vec<char> = line_text.chars().collect();
    if chars.is_empty() {
        return None;
    }

    let mut index = column.saturating_sub(1).min(chars.len().saturating_sub(1));
    if !is_symbol_char(chars[index]) && index > 0 {
        index -= 1;
    }
    if !is_symbol_char(chars[index]) {
        return None;
    }

    let mut start = index;
    while start > 0 && is_symbol_char(chars[start - 1]) {
        start -= 1;
    }
    let mut end = index + 1;
    while end < chars.len() && is_symbol_char(chars[end]) {
        end += 1;
    }

    Some(chars[start..end].iter().collect())
}

fn is_symbol_char(character: char) -> bool {
    character == '_' || character.is_ascii_alphanumeric()
}

fn compact_line(line: &str) -> String {
    let compacted = line.split_whitespace().collect::<Vec<_>>().join(" ");
    if compacted.len() > 140 {
        format!("{}...", &compacted[..137])
    } else {
        compacted
    }
}

fn resolve_server_for_language(language: &str) -> Option<ResolvedLspServer> {
    let spec = server_spec_for_language(language)?;
    let command = resolve_command(spec.command)?;
    Some(ResolvedLspServer { spec, command })
}

fn server_spec_for_language(language: &str) -> Option<LspServerSpec> {
    match language.trim().to_lowercase().as_str() {
        "csharp" | "c#" | "cs" => Some(LspServerSpec {
            server_name: "Roslyn",
            language_id: "csharp",
            command: "roslyn-language-server",
            args: &["--stdio", "--autoLoadProjects", "--telemetryLevel", "off"],
        }),
        "typescript" | "ts" | "tsx" => Some(LspServerSpec {
            server_name: "typescript-language-server",
            language_id: "typescript",
            command: "typescript-language-server",
            args: &["--stdio"],
        }),
        "javascript" | "js" | "jsx" => Some(LspServerSpec {
            server_name: "typescript-language-server",
            language_id: "javascript",
            command: "typescript-language-server",
            args: &["--stdio"],
        }),
        "rust" | "rs" => Some(LspServerSpec {
            server_name: "rust-analyzer",
            language_id: "rust",
            command: "rust-analyzer",
            args: &[],
        }),
        "svelte" => Some(LspServerSpec {
            server_name: "svelte-language-server",
            language_id: "svelte",
            command: "svelteserver",
            args: &["--stdio"],
        }),
        _ => None,
    }
}

fn resolve_command(command: &str) -> Option<String> {
    if command.contains('/') {
        return executable_path(PathBuf::from(command));
    }

    command_search_paths()
        .into_iter()
        .map(|path| path.join(command))
        .find_map(executable_path)
}

fn normalized_lsp_root(root: &str) -> Option<String> {
    let path = PathBuf::from(root);
    if !path.is_dir() {
        return None;
    }

    Some(
        std::fs::canonicalize(&path)
            .unwrap_or(path)
            .display()
            .to_string(),
    )
}

fn workspace_contains_csharp_project(root: &Path) -> bool {
    let mut pending = VecDeque::from([(root.to_path_buf(), 0usize)]);
    let mut visited_entries = 0usize;

    while let Some((directory, depth)) = pending.pop_front() {
        let Ok(entries) = std::fs::read_dir(directory) else {
            continue;
        };
        for entry in entries.flatten() {
            visited_entries += 1;
            if visited_entries > MAX_LSP_PRELOAD_DISCOVERY_ENTRIES {
                return false;
            }

            let path = entry.path();
            let Ok(file_type) = entry.file_type() else {
                continue;
            };
            if file_type.is_file()
                && path
                    .extension()
                    .and_then(|extension| extension.to_str())
                    .is_some_and(|extension| {
                        extension.eq_ignore_ascii_case("sln")
                            || extension.eq_ignore_ascii_case("slnx")
                            || extension.eq_ignore_ascii_case("csproj")
                    })
            {
                return true;
            }

            if !file_type.is_dir() || depth >= MAX_LSP_PRELOAD_DISCOVERY_DEPTH {
                continue;
            }
            let name = entry.file_name();
            let name = name.to_string_lossy();
            if matches!(
                name.as_ref(),
                ".git"
                    | ".idea"
                    | ".svelte-kit"
                    | ".vscode"
                    | "bin"
                    | "build"
                    | "coverage"
                    | "dist"
                    | "node_modules"
                    | "obj"
                    | "output"
                    | "target"
            ) {
                continue;
            }
            pending.push_back((path, depth + 1));
        }
    }

    false
}

pub(crate) fn workspace_has_csharp_project_marker(root: &str) -> bool {
    normalized_lsp_root(root)
        .is_some_and(|root| workspace_contains_csharp_project(Path::new(&root)))
}

fn executable_path(path: PathBuf) -> Option<String> {
    let metadata = std::fs::metadata(&path).ok()?;
    if !metadata.is_file() || metadata.permissions().mode() & 0o111 == 0 {
        return None;
    }
    Some(path.display().to_string())
}

fn command_search_paths() -> Vec<PathBuf> {
    // In development, language servers are pinned application dependencies.
    // Resolve those before system locations so the editor does not silently use
    // an incompatible global version. Packaged builds can replace this location
    // with a bundled resource/sidecar without changing the LSP registry.
    let mut paths = vec![PathBuf::from(env!("CARGO_MANIFEST_DIR"))
        .join("..")
        .join("node_modules")
        .join(".bin")];
    // Start from the user's *login-shell* PATH (the same environment the embedded
    // terminal loads with `-l`), not just the PATH this process inherited. A
    // Finder/Dock-launched .app inherits only the minimal launchd PATH
    // (/usr/bin:/bin:/usr/sbin:/sbin), so login-only toolchain dirs — `~/.cargo/bin`
    // (rust-analyzer), nvm node bins, `~/.dotnet/tools` — would be invisible and a
    // real, installed language server would be misreported as "not installed,"
    // forcing the regex index fallback. See spec §5.5 / §9 (login-shell `-l` fix).
    paths.extend(
        login_shell_path()
            .map(|path| split_paths(OsString::from(path)))
            .unwrap_or_default(),
    );
    if let Some(inherited) = env::var_os("PATH") {
        paths.extend(split_paths(inherited));
    }
    if let Some(home) = env::var_os("HOME").map(PathBuf::from) {
        // Explicit, well-known toolchain dirs as a belt-and-suspenders fallback so the
        // common servers still resolve even if the login-shell probe is unavailable.
        paths.push(home.join(".cargo/bin"));
        paths.push(home.join(".dotnet/tools"));
        paths.push(home.join(".local/bin"));
        paths.extend(nvm_bin_paths(&home));
    }
    paths.extend([
        PathBuf::from("/opt/homebrew/bin"),
        PathBuf::from("/usr/local/bin"),
        PathBuf::from("/usr/bin"),
        PathBuf::from("/bin"),
    ]);
    dedupe_paths(paths)
}

/// The combined LSP search path (login-shell PATH + inherited + toolchain dirs) as a
/// single PATH-formatted string, suitable for the spawned server child's `PATH` env.
fn lsp_search_path_env() -> Option<OsString> {
    env::join_paths(command_search_paths()).ok()
}

/// The user's full PATH as a login shell would compute it, captured once.
///
/// Cached because the probe spawns a shell (~30ms) and the value is stable for the
/// life of the process. `None` when no usable shell exists or the probe fails — callers
/// fall back to the inherited PATH plus the hardcoded toolchain dirs above.
fn login_shell_path() -> Option<String> {
    static LOGIN_SHELL_PATH: OnceLock<Option<String>> = OnceLock::new();
    LOGIN_SHELL_PATH.get_or_init(probe_login_shell_path).clone()
}

/// Spawn the user's login shell (`$SHELL -lc 'printf %s $PATH'`) and capture its PATH.
///
/// Mirrors `terminal.rs`'s `-l` login-shell spawn so LSP binary detection sees exactly
/// the same toolchain dirs the embedded terminal does. Only POSIX login shells are
/// probed; anything unexpected (or a non-zero/garbled result) yields `None`.
fn probe_login_shell_path() -> Option<String> {
    let shell = env::var("SHELL")
        .ok()
        .filter(|value| !value.trim().is_empty())?;
    let shell_name = Path::new(&shell)
        .file_name()
        .and_then(|name| name.to_str())?;
    if !matches!(shell_name, "zsh" | "bash" | "sh" | "dash" | "ksh" | "fish") {
        return None;
    }

    let output = Command::new(&shell)
        .arg("-l")
        .arg("-c")
        .arg("printf '%s' \"$PATH\"")
        .stdin(Stdio::null())
        .stderr(Stdio::null())
        .output()
        .ok()?;
    if !output.status.success() {
        return None;
    }
    let path = String::from_utf8_lossy(&output.stdout).trim().to_string();
    if path.is_empty() {
        None
    } else {
        Some(path)
    }
}

fn split_paths(paths: OsString) -> Vec<PathBuf> {
    env::split_paths(&paths).collect()
}

fn nvm_bin_paths(home: &Path) -> Vec<PathBuf> {
    let versions = home.join(".nvm/versions/node");
    let Ok(entries) = std::fs::read_dir(versions) else {
        return Vec::new();
    };
    entries
        .flatten()
        .map(|entry| entry.path().join("bin"))
        .filter(|path| path.is_dir())
        .collect()
}

fn dedupe_paths(paths: Vec<PathBuf>) -> Vec<PathBuf> {
    let mut deduped = Vec::new();
    for path in paths {
        if !deduped.iter().any(|candidate: &PathBuf| candidate == &path) {
            deduped.push(path);
        }
    }
    deduped
}

fn path_to_file_uri(path: &Path) -> String {
    let absolute = if path.is_absolute() {
        path.to_path_buf()
    } else {
        env::current_dir()
            .map(|cwd| cwd.join(path))
            .unwrap_or_else(|_| path.to_path_buf())
    };
    format!(
        "file://{}",
        percent_encode_path(&absolute.display().to_string())
    )
}

fn file_uri_to_path(uri: &str) -> Option<PathBuf> {
    let raw = uri.strip_prefix("file://")?;
    percent_decode_path(raw).map(PathBuf::from)
}

fn percent_encode_path(path: &str) -> String {
    let mut encoded = String::new();
    for byte in path.bytes() {
        if byte.is_ascii_alphanumeric() || matches!(byte, b'/' | b'-' | b'_' | b'.' | b'~') {
            encoded.push(byte as char);
        } else {
            encoded.push_str(&format!("%{byte:02X}"));
        }
    }
    encoded
}

fn percent_decode_path(path: &str) -> Option<String> {
    let bytes = path.as_bytes();
    let mut decoded = Vec::with_capacity(bytes.len());
    let mut index = 0;
    while index < bytes.len() {
        if bytes[index] == b'%' {
            let hex = std::str::from_utf8(bytes.get(index + 1..index + 3)?).ok()?;
            decoded.push(u8::from_str_radix(hex, 16).ok()?);
            index += 3;
        } else {
            decoded.push(bytes[index]);
            index += 1;
        }
    }
    String::from_utf8(decoded).ok()
}

#[cfg(test)]
mod tests {
    use super::*;

    fn supercharged_test_lock() -> MutexGuard<'static, ()> {
        static LOCK: OnceLock<Mutex<()>> = OnceLock::new();
        locked(LOCK.get_or_init(|| Mutex::new(())))
    }

    #[test]
    fn product_identity_lsp_client_name_is_assembly() {
        assert_eq!(crate::product_identity::LSP_CLIENT_NAME, "Assembly");
    }

    /// Read mode is the default, and it is per workspace. A workspace nobody
    /// has switched on must never look like one that has been.
    #[test]
    fn language_intelligence_starts_off_for_every_workspace() {
        let modes = LanguageIntelligenceModes::default();
        assert!(!modes.is_on("/projects/one"));
        assert!(!modes.is_on("/projects/two"));
    }

    #[test]
    fn turning_language_intelligence_on_and_off_says_what_it_did() {
        let mut modes = LanguageIntelligenceModes::default();

        let turned_on = modes.set("/projects/one", true);
        assert_eq!(turned_on, LanguageIntelligenceChange::TurnedOn);
        assert!(turned_on.changed());
        assert!(turned_on.may_start_servers());
        assert!(!turned_on.must_stop_servers());
        assert!(modes.is_on("/projects/one"));

        let again = modes.set("/projects/one", true);
        assert_eq!(again, LanguageIntelligenceChange::AlreadyOn);
        assert!(!again.changed(), "asking twice must not restart anything");
        assert!(again.may_start_servers());
        assert!(!again.must_stop_servers());

        let turned_off = modes.set("/projects/one", false);
        assert_eq!(turned_off, LanguageIntelligenceChange::TurnedOff);
        assert!(turned_off.changed());
        assert!(
            turned_off.must_stop_servers(),
            "turning full mode off is what stops the workspace's server"
        );
        assert!(!turned_off.may_start_servers());
        assert!(!modes.is_on("/projects/one"));

        let already_off = modes.set("/projects/one", false);
        assert_eq!(already_off, LanguageIntelligenceChange::AlreadyOff);
        assert!(
            !already_off.must_stop_servers(),
            "a workspace that was already in read mode has nothing to stop"
        );
    }

    /// One workspace's choice is its own. Turning the editor on for one project
    /// must not start anything for the project beside it.
    #[test]
    fn each_workspace_keeps_its_own_mode() {
        let mut modes = LanguageIntelligenceModes::default();
        modes.set("/projects/one", true);
        assert!(modes.is_on("/projects/one"));
        assert!(!modes.is_on("/projects/two"));

        modes.set("/projects/two", true);
        modes.set("/projects/one", false);
        assert!(!modes.is_on("/projects/one"));
        assert!(
            modes.is_on("/projects/two"),
            "stopping one workspace's server must leave the other's alone"
        );
    }

    /// The same folder written two ways is one workspace, so a server started
    /// under one spelling is reused — never duplicated — under the other.
    #[test]
    fn workspace_keys_are_one_spelling_per_folder() {
        assert_eq!(
            language_intelligence_key("/projects/one/"),
            language_intelligence_key("/projects/one")
        );
    }

    /// The promise of read mode, tested against the registry rather than the
    /// state machine: a workspace in read mode starts nothing, however it is
    /// asked, and says why in words rather than looking broken.
    #[test]
    fn a_read_mode_workspace_starts_no_language_server() {
        let _test_lock = supercharged_test_lock();
        let root = unique_lsp_temp_root("mcb-lsp-read-mode");
        std::fs::write(root.join("App.csproj"), "<Project />").unwrap();
        let root_text = root.display().to_string();
        set_language_intelligence(&root_text, false);
        let registry = SourceLspRegistry::default();
        assert_eq!(
            registry.warm_running_servers_for_root(&root_text).unwrap(),
            0,
            "warming a read-mode workspace must start nothing"
        );
        assert!(
            registry.ensure_native_csharp_endpoint(&root_text).is_err(),
            "the C# endpoint is the one way Roslyn starts, and read mode closes it"
        );
        assert_eq!(registry.session_count().unwrap(), 0);
        assert!(registry.running_language_server_processes().is_empty());

        let status = read_source_lsp_status_sync(root.clone(), "typescript".to_string()).unwrap();
        assert_eq!(status.state, LanguageServerState::Disabled.as_str());
        assert_eq!(status.detail.as_deref(), Some(READ_MODE_DETAIL));

        // Turning full mode off again when nothing is running stops nothing.
        assert_eq!(registry.stop_servers_for_root(&root_text).unwrap(), 0);

        std::fs::remove_dir_all(root).unwrap();
    }

    /// The switch's own start path. A language this app has no server for must
    /// come back saying that, because the switch is on and the reader is owed a
    /// reason rather than a control that appears to do nothing.
    #[test]
    fn starting_a_language_with_no_server_says_so() {
        let _test_lock = supercharged_test_lock();
        let root = unique_lsp_temp_root("mcb-lsp-no-server");
        let root_text = root.display().to_string();
        let registry = SourceLspRegistry::default();
        assert_eq!(
            registry
                .start_server_for_language(&root_text, "python")
                .unwrap(),
            LanguageServerStart::NoServerForLanguage
        );
        assert_eq!(registry.session_count().unwrap(), 0);
        assert!(registry.running_language_server_processes().is_empty());

        std::fs::remove_dir_all(root).unwrap();
    }

    /// C# has its own client and its own gates. The switch must report which
    /// gate held rather than starting a second Roslyn through the legacy pool.
    #[test]
    fn starting_csharp_reports_its_gates_instead_of_starting_a_second_roslyn() {
        let _test_lock = supercharged_test_lock();
        let root = unique_lsp_temp_root("mcb-lsp-csharp-gate");
        let root_text = root.display().to_string();
        let registry = SourceLspRegistry::default();
        assert_eq!(
            registry
                .start_server_for_language(&root_text, "csharp")
                .unwrap(),
            LanguageServerStart::NoCsharpProject,
            "a workspace with no C# project in it says so"
        );

        std::fs::write(root.join("App.csproj"), "<Project />").unwrap();
        assert_eq!(
            registry
                .start_server_for_language(&root_text, "csharp")
                .unwrap(),
            LanguageServerStart::NativeCsharpClient,
            "C# starts through the native client, never through this pool"
        );
        assert_eq!(registry.session_count().unwrap(), 0);

        std::fs::remove_dir_all(root).unwrap();
    }

    /// Read mode still wins. Asking the switch's start path for a workspace
    /// nobody switched on starts nothing, whatever the language.
    #[test]
    fn a_read_mode_workspace_starts_nothing_when_asked_directly() {
        let _test_lock = supercharged_test_lock();
        let root = unique_lsp_temp_root("mcb-lsp-start-read-mode");
        let root_text = root.display().to_string();
        set_language_intelligence(&root_text, false);
        let registry = SourceLspRegistry::default();
        assert_eq!(
            registry
                .start_server_for_language(&root_text, "typescript")
                .unwrap(),
            LanguageServerStart::ReadMode
        );
        assert_eq!(registry.session_count().unwrap(), 0);
        assert!(registry.running_language_server_processes().is_empty());

        std::fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn supercharged_off_allows_no_server() {
        let _test_lock = supercharged_test_lock();
        let root = unique_lsp_temp_root("mcb-lsp-supercharged-off");
        let root_text = root.display().to_string();
        set_language_servers_enabled(false);

        let registry = SourceLspRegistry::default();
        assert_eq!(
            registry
                .start_server_for_language(&root_text, "rust")
                .unwrap(),
            LanguageServerStart::SwitchedOff
        );
        assert_eq!(
            registry.warm_running_servers_for_root(&root_text).unwrap(),
            0
        );
        assert_eq!(registry.session_count().unwrap(), 0);
        assert!(registry.running_language_server_processes().is_empty());

        set_language_servers_enabled(true);
        set_language_intelligence(&root_text, false);
        std::fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn supercharged_on_respects_per_server_off() {
        let _test_lock = supercharged_test_lock();
        let root = unique_lsp_temp_root("mcb-lsp-rust-off");
        let root_text = root.display().to_string();
        set_language_servers_enabled(true);
        set_language_server_enabled("rust", false).unwrap();
        set_language_intelligence(&root_text, true);

        let registry = SourceLspRegistry::default();
        assert_eq!(
            registry
                .start_server_for_language(&root_text, "rust")
                .unwrap(),
            LanguageServerStart::ServerSwitchedOff
        );
        assert_eq!(registry.session_count().unwrap(), 0);
        assert!(registry.running_language_server_processes().is_empty());

        set_language_server_enabled("rust", true).unwrap();
        set_language_intelligence(&root_text, false);
        std::fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn turning_supercharged_off_stops_running_servers() {
        let _test_lock = supercharged_test_lock();
        let root = unique_lsp_temp_root("mcb-lsp-supercharged-stop");
        let root_text = root.display().to_string();
        let root_key = normalized_lsp_root(&root_text).expect("canonical temp root");
        set_language_servers_enabled(true);
        set_language_server_enabled("rust", true).unwrap();
        assert!(language_intelligence_on(&root_text));

        let registry = SourceLspRegistry::default();
        registry
            .set_active_root(&root_text, true)
            .expect("active root should be accepted");
        let (connection, _server) = connect_to_a_fake_language_server("rust");
        locked(&registry.sessions).insert(
            SourceLspSessionKey {
                language: "rust".to_string(),
                root: root_key,
            },
            Arc::new(Mutex::new(SourceLspSession {
                root: root.clone(),
                connection,
                last_used: 1,
            })),
        );
        assert_eq!(registry.session_count().unwrap(), 1);

        let changed = set_language_intelligence(&root_text, false);
        assert_eq!(changed, LanguageIntelligenceChange::TurnedOff);
        assert!(changed.must_stop_servers());
        assert_eq!(registry.set_active_root(&root_text, false).unwrap(), 1);
        assert_eq!(registry.session_count().unwrap(), 0);

        std::fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn command_search_paths_include_cargo_bin_so_rust_analyzer_resolves() {
        // Regression: rust-analyzer commonly lives ONLY at ~/.cargo/bin (a rustup proxy),
        // which is on the login PATH but NOT the launchd PATH a Finder-launched .app
        // inherits. Omitting it made an installed rust-analyzer report as "not installed"
        // and forced the regex index fallback. Spec §5.5.
        let Some(home) = env::var_os("HOME").map(PathBuf::from) else {
            return;
        };
        let paths = command_search_paths();
        assert!(
            paths.contains(&home.join(".cargo/bin")),
            "~/.cargo/bin must be searched for language-server binaries (rust-analyzer)"
        );
        assert!(
            paths.contains(&home.join(".dotnet/tools")),
            "~/.dotnet/tools must be searched (Roslyn language server)"
        );
    }

    #[test]
    fn command_search_paths_prefer_pinned_application_language_servers() {
        let expected = PathBuf::from(env!("CARGO_MANIFEST_DIR"))
            .join("..")
            .join("node_modules")
            .join(".bin");
        assert_eq!(command_search_paths().first(), Some(&expected));
        assert!(expected.join("svelteserver").is_file());
        assert!(expected.join("typescript-language-server").is_file());
    }

    #[test]
    fn command_search_paths_are_deduped_and_joinable_into_a_path_env() {
        // The same combined set is handed to spawned servers as their PATH, so it must
        // contain no duplicates and round-trip through env::join_paths.
        let paths = command_search_paths();
        let deduped = dedupe_paths(paths.clone());
        assert_eq!(
            paths.len(),
            deduped.len(),
            "search paths must already be deduped"
        );
        assert!(
            lsp_search_path_env().is_some(),
            "combined search path must join into a valid PATH env value"
        );
    }

    #[test]
    fn language_server_runs_in_an_owned_process_group() {
        let mut command = Command::new("sh");
        command
            .arg("-c")
            .arg("sleep 30 & wait")
            .stdin(Stdio::null())
            .stdout(Stdio::null())
            .stderr(Stdio::null());
        configure_lsp_process_group(&mut command);

        let mut child = command.spawn().expect("spawn grouped fixture");
        let pid = child.id() as i32;
        assert_eq!(unsafe { libc::getpgid(pid) }, pid);

        stop_lsp_process_tree(&mut child);
        assert!(child
            .try_wait()
            .expect("read grouped fixture state")
            .is_some());
    }

    #[test]
    fn resolves_language_server_specs() {
        let csharp = server_spec_for_language("csharp").expect("csharp spec");
        assert_eq!(csharp.server_name, "Roslyn");
        assert_eq!(csharp.command, "roslyn-language-server");
        assert_eq!(csharp.language_id, "csharp");
        assert_eq!(
            csharp.args,
            &["--stdio", "--autoLoadProjects", "--telemetryLevel", "off"]
        );

        let typescript = server_spec_for_language("typescript").expect("typescript spec");
        assert_eq!(typescript.command, "typescript-language-server");
        assert_eq!(typescript.args, &["--stdio"]);

        let rust = server_spec_for_language("rust").expect("rust spec");
        assert_eq!(rust.command, "rust-analyzer");

        let svelte = server_spec_for_language("svelte").expect("svelte spec");
        assert_eq!(svelte.command, "svelteserver");
        assert_eq!(svelte.args, &["--stdio"]);
    }

    #[test]
    fn maps_primary_native_lsp_languages_to_commands_and_lsp_ids() {
        let cases: &[(&str, &str, &str, &str, &[&str])] = &[
            (
                "csharp",
                "Roslyn",
                "csharp",
                "roslyn-language-server",
                &["--stdio", "--autoLoadProjects", "--telemetryLevel", "off"],
            ),
            (
                "cs",
                "Roslyn",
                "csharp",
                "roslyn-language-server",
                &["--stdio", "--autoLoadProjects", "--telemetryLevel", "off"],
            ),
            (
                "svelte",
                "svelte-language-server",
                "svelte",
                "svelteserver",
                &["--stdio"],
            ),
            (
                "typescript",
                "typescript-language-server",
                "typescript",
                "typescript-language-server",
                &["--stdio"],
            ),
            (
                "ts",
                "typescript-language-server",
                "typescript",
                "typescript-language-server",
                &["--stdio"],
            ),
            (
                "tsx",
                "typescript-language-server",
                "typescript",
                "typescript-language-server",
                &["--stdio"],
            ),
            (
                "javascript",
                "typescript-language-server",
                "javascript",
                "typescript-language-server",
                &["--stdio"],
            ),
            (
                "js",
                "typescript-language-server",
                "javascript",
                "typescript-language-server",
                &["--stdio"],
            ),
            (
                "jsx",
                "typescript-language-server",
                "javascript",
                "typescript-language-server",
                &["--stdio"],
            ),
            ("rust", "rust-analyzer", "rust", "rust-analyzer", &[]),
            ("rs", "rust-analyzer", "rust", "rust-analyzer", &[]),
        ];

        for (language, server_name, language_id, command, args) in cases {
            let spec = server_spec_for_language(language).expect("target LSP spec");
            assert_eq!(spec.server_name, *server_name);
            assert_eq!(spec.language_id, *language_id);
            assert_eq!(spec.command, *command);
            assert_eq!(spec.args, *args);
        }
    }

    #[test]
    fn lsp_status_accepts_native_lsp_server_spec_aliases() {
        let root = unique_lsp_temp_root("mcb-lsp-status-mapping");
        let cases: &[(&str, &str, &str, &str, &[&str])] = &[
            (
                "csharp",
                "Roslyn",
                "csharp",
                "roslyn-language-server",
                &["--stdio", "--autoLoadProjects", "--telemetryLevel", "off"],
            ),
            (
                "cs",
                "Roslyn",
                "csharp",
                "roslyn-language-server",
                &["--stdio", "--autoLoadProjects", "--telemetryLevel", "off"],
            ),
            (
                "svelte",
                "svelte-language-server",
                "svelte",
                "svelteserver",
                &["--stdio"],
            ),
            (
                "typescript",
                "typescript-language-server",
                "typescript",
                "typescript-language-server",
                &["--stdio"],
            ),
            (
                "ts",
                "typescript-language-server",
                "typescript",
                "typescript-language-server",
                &["--stdio"],
            ),
            (
                "tsx",
                "typescript-language-server",
                "typescript",
                "typescript-language-server",
                &["--stdio"],
            ),
            (
                "javascript",
                "typescript-language-server",
                "javascript",
                "typescript-language-server",
                &["--stdio"],
            ),
            (
                "js",
                "typescript-language-server",
                "javascript",
                "typescript-language-server",
                &["--stdio"],
            ),
            (
                "jsx",
                "typescript-language-server",
                "javascript",
                "typescript-language-server",
                &["--stdio"],
            ),
            ("rust", "rust-analyzer", "rust", "rust-analyzer", &[]),
            ("rs", "rust-analyzer", "rust", "rust-analyzer", &[]),
        ];
        for (language, server_name, language_id, command, args) in cases {
            let status = read_source_lsp_status_sync(root.clone(), (*language).to_string())
                .expect("target LSP status");
            let reported_command = Path::new(&status.command)
                .file_name()
                .and_then(|value| value.to_str())
                .unwrap_or(status.command.as_str());

            assert_eq!(status.language, *language);
            assert_eq!(status.language_id, *language_id);
            assert_eq!(status.server_name, *server_name);
            assert_eq!(reported_command, *command);
            assert_eq!(
                status.args,
                args.iter()
                    .map(|arg| (*arg).to_string())
                    .collect::<Vec<_>>()
            );
        }

        std::fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn python_and_go_lsp_adapters_are_deferred() {
        for language in ["python", "py", "go"] {
            assert!(
                server_spec_for_language(language).is_none(),
                "{language} should not advertise a native LSP adapter yet"
            );
            let status = read_source_lsp_status_sync(PathBuf::from("."), language.to_string())
                .expect("unsupported language status");
            assert_eq!(status.language, language);
            assert_eq!(status.language_id, "plaintext");
            assert!(!status.available);
            assert_eq!(status.server_name, "none");
            assert!(status.command.is_empty());
            assert_eq!(
                status.reason.as_deref(),
                Some("No language server configured for this file type")
            );
        }
    }

    #[test]
    fn lsp_readiness_lists_file_variants_without_shorthand_alias_noise() {
        let root = unique_lsp_temp_root("mcb-lsp-readiness");
        std::fs::create_dir_all(&root).unwrap();

        let statuses = list_source_lsp_statuses_sync(root.clone());
        let languages = statuses
            .iter()
            .map(|status| status.language.as_str())
            .collect::<Vec<_>>();

        assert_eq!(
            languages,
            vec![
                "csharp",
                "typescript",
                "tsx",
                "javascript",
                "jsx",
                "rust",
                "svelte"
            ]
        );
        assert_eq!(
            statuses
                .iter()
                .find(|status| status.language == "tsx")
                .map(|status| status.language_id.as_str()),
            Some("typescript")
        );
        assert_eq!(
            statuses
                .iter()
                .find(|status| status.language == "jsx")
                .map(|status| status.language_id.as_str()),
            Some("javascript")
        );
        for shorthand_alias in ["cs", "ts", "js", "rs"] {
            assert!(
                !languages.contains(&shorthand_alias),
                "{shorthand_alias} should be supported by per-file status but hidden from readiness"
            );
        }
        assert!(!languages.contains(&"python"));
        assert!(!languages.contains(&"go"));
        assert!(statuses.iter().all(|status| !status.server_name.is_empty()));
        assert!(statuses.iter().all(|status| !status.command.is_empty()));

        std::fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn infers_languages_for_native_lsp_result_paths() {
        assert_eq!(
            language_for_path(Path::new("Widget.cs")).as_deref(),
            Some("csharp")
        );
        assert_eq!(
            language_for_path(Path::new("App.js")).as_deref(),
            Some("javascript")
        );
        assert_eq!(
            language_for_path(Path::new("App.jsx")).as_deref(),
            Some("javascript")
        );
        assert_eq!(
            language_for_path(Path::new("src/App.ts")).as_deref(),
            Some("typescript")
        );
        assert_eq!(
            language_for_path(Path::new("src/App.tsx")).as_deref(),
            Some("typescript")
        );
        assert_eq!(
            language_for_path(Path::new("src/lib.rs")).as_deref(),
            Some("rust")
        );
        assert_eq!(
            language_for_path(Path::new("src/App.svelte")).as_deref(),
            Some("svelte")
        );
    }

    #[test]
    fn diagnostics_timeout_stays_bounded() {
        assert!(LSP_DIAGNOSTICS_TIMEOUT < LSP_REQUEST_TIMEOUT);
        assert!(LSP_DIAGNOSTICS_TIMEOUT <= Duration::from_millis(1500));
    }

    #[test]
    fn lsp_client_capabilities_enable_editor_diagnostics() {
        let capabilities = lsp_client_capabilities();
        assert_eq!(
            capabilities
                .get("workspace")
                .and_then(|workspace| workspace.get("configuration"))
                .and_then(Value::as_bool),
            Some(true)
        );
        assert_eq!(
            capabilities
                .get("workspace")
                .and_then(|workspace| workspace.get("workspaceFolders"))
                .and_then(Value::as_bool),
            Some(true)
        );
        assert_eq!(
            capabilities
                .get("textDocument")
                .and_then(|text_document| text_document.get("publishDiagnostics"))
                .and_then(|publish_diagnostics| publish_diagnostics.get("relatedInformation"))
                .and_then(Value::as_bool),
            Some(true)
        );
        assert_eq!(
            capabilities
                .get("textDocument")
                .and_then(|text_document| text_document.get("definition"))
                .and_then(|definition| definition.get("linkSupport"))
                .and_then(Value::as_bool),
            Some(true)
        );
        assert_eq!(
            capabilities
                .get("textDocument")
                .and_then(|text_document| text_document.get("diagnostic"))
                .and_then(|diagnostic| diagnostic.get("dynamicRegistration"))
                .and_then(Value::as_bool),
            Some(false)
        );
        assert_eq!(
            capabilities
                .get("textDocument")
                .and_then(|text_document| text_document.get("implementation"))
                .and_then(|implementation| implementation.get("linkSupport"))
                .and_then(Value::as_bool),
            Some(true)
        );
        assert_eq!(
            capabilities
                .get("textDocument")
                .and_then(|text_document| text_document.get("typeDefinition"))
                .and_then(|type_definition| type_definition.get("linkSupport"))
                .and_then(Value::as_bool),
            Some(true)
        );
        assert_eq!(
            capabilities
                .get("textDocument")
                .and_then(|text_document| text_document.get("completion"))
                .and_then(|completion| completion.get("completionItem"))
                .and_then(|completion_item| completion_item.get("snippetSupport"))
                .and_then(Value::as_bool),
            Some(false)
        );
        assert_eq!(
            capabilities
                .get("textDocument")
                .and_then(|text_document| text_document.get("codeAction"))
                .and_then(|code_action| code_action.get("isPreferredSupport"))
                .and_then(Value::as_bool),
            Some(true)
        );
        assert_eq!(
            capabilities
                .get("textDocument")
                .and_then(|text_document| text_document.get("signatureHelp"))
                .and_then(|signature_help| signature_help.get("signatureInformation"))
                .and_then(|signature_information| signature_information.get("parameterInformation"))
                .and_then(|parameter_information| parameter_information.get("labelOffsetSupport"))
                .and_then(Value::as_bool),
            Some(true)
        );
        assert_eq!(
            capabilities
                .get("textDocument")
                .and_then(|text_document| text_document.get("inlayHint"))
                .and_then(|inlay_hint| inlay_hint.get("dynamicRegistration"))
                .and_then(Value::as_bool),
            Some(false)
        );
        assert_eq!(
            capabilities
                .get("textDocument")
                .and_then(|text_document| text_document.get("semanticTokens"))
                .and_then(|semantic_tokens| semantic_tokens.get("requests"))
                .and_then(|requests| requests.get("full"))
                .and_then(Value::as_bool),
            Some(true)
        );
        assert_eq!(
            capabilities
                .get("textDocument")
                .and_then(|text_document| text_document.get("semanticTokens"))
                .and_then(|semantic_tokens| semantic_tokens.get("formats"))
                .and_then(Value::as_array)
                .and_then(|formats| formats.first())
                .and_then(Value::as_str),
            Some("relative")
        );
    }

    #[test]
    fn reads_full_and_incremental_document_sync_capabilities() {
        assert_eq!(
            lsp_text_document_sync_kind_from_initialize(&json!({
                "result": { "capabilities": { "textDocumentSync": 1 } }
            })),
            1
        );
        assert_eq!(
            lsp_text_document_sync_kind_from_initialize(&json!({
                "result": {
                    "capabilities": {
                        "textDocumentSync": { "openClose": true, "change": 2 }
                    }
                }
            })),
            2
        );
        assert_eq!(
            lsp_diagnostic_identifier_from_initialize(&json!({
                "result": {
                    "capabilities": {
                        "diagnosticProvider": {
                            "identifier": "document",
                            "workspaceDiagnostics": true
                        }
                    }
                }
            }))
            .as_deref(),
            Some("document")
        );
    }

    #[test]
    fn document_end_positions_use_lines_and_utf16_characters() {
        assert_eq!(
            lsp_document_end_position("alpha\n😀x"),
            json!({
                "line": 1,
                "character": 3
            })
        );
        assert_eq!(
            lsp_document_end_position("alpha\n"),
            json!({
                "line": 1,
                "character": 0
            })
        );
    }

    #[test]
    fn roslyn_configuration_enables_reference_lenses_without_native_test_commands() {
        let answer = workspace_configuration_result(Some(&json!({
            "items": [
                { "section": "csharp|background_analysis.dotnet_compiler_diagnostics_scope" },
                { "section": "csharp|code_lens.dotnet_enable_references_code_lens" },
                { "section": "csharp|code_lens.dotnet_enable_tests_code_lens" },
                { "section": "unknown" }
            ]
        })));

        assert_eq!(answer, json!(["openFiles", true, false, null]));
    }

    #[test]
    fn extracts_text_edits_from_formatting_result() {
        let result = json!([
            {
                "range": {
                    "start": { "line": 1, "character": 2 },
                    "end": { "line": 1, "character": 8 }
                },
                "newText": "formatted"
            }
        ]);

        assert_eq!(
            lsp_text_edits_from_result(&result),
            vec![SourceLspTextEdit {
                start_line: 2,
                start_column: 3,
                end_line: 2,
                end_column: 9,
                new_text: "formatted".to_string(),
            }]
        );
    }

    #[test]
    fn extracts_workspace_edits_from_rename_result() {
        let root = env::temp_dir().join("mcb-lsp-rename-root");
        let source_path = root.join("src/App.ts");
        let other_path = root.join("src/App.test.ts");
        let result = json!({
            "documentChanges": [
                {
                    "textDocument": { "uri": path_to_file_uri(&source_path) },
                    "edits": [
                        {
                            "range": {
                                "start": { "line": 2, "character": 4 },
                                "end": { "line": 2, "character": 12 }
                            },
                            "newText": "nextName"
                        }
                    ]
                },
                {
                    "textDocument": { "uri": path_to_file_uri(&other_path) },
                    "edits": [
                        {
                            "range": {
                                "start": { "line": 4, "character": 6 },
                                "end": { "line": 4, "character": 14 }
                            },
                            "newText": "nextName"
                        }
                    ]
                }
            ]
        });

        assert_eq!(
            lsp_workspace_edit_files_from_result(&result, &root),
            vec![
                SourceLspWorkspaceEditFile {
                    path: source_path.display().to_string(),
                    relative_path: "src/App.ts".to_string(),
                    edits: vec![SourceLspTextEdit {
                        start_line: 3,
                        start_column: 5,
                        end_line: 3,
                        end_column: 13,
                        new_text: "nextName".to_string(),
                    }],
                },
                SourceLspWorkspaceEditFile {
                    path: other_path.display().to_string(),
                    relative_path: "src/App.test.ts".to_string(),
                    edits: vec![SourceLspTextEdit {
                        start_line: 5,
                        start_column: 7,
                        end_line: 5,
                        end_column: 15,
                        new_text: "nextName".to_string(),
                    }],
                },
            ]
        );
    }

    #[test]
    fn extracts_code_actions_with_workspace_edits() {
        let root = env::temp_dir().join("mcb-lsp-code-action-root");
        let source_path = root.join("src/App.ts");
        let result = json!([
            {
                "title": "Add missing import",
                "kind": "quickfix",
                "isPreferred": true,
                "edit": {
                    "changes": {
                        path_to_file_uri(&source_path): [
                            {
                                "range": {
                                    "start": { "line": 0, "character": 0 },
                                    "end": { "line": 0, "character": 0 }
                                },
                                "newText": "import { thing } from './thing';\n"
                            }
                        ]
                    }
                }
            },
            {
                "title": "Organize Imports",
                "kind": "source.organizeImports",
                "command": {
                    "title": "Organize Imports",
                    "command": "typescript.organizeImports"
                }
            }
        ]);

        assert_eq!(
            lsp_code_actions_from_result(&result, &root, 10),
            vec![
                SourceLspCodeAction {
                    title: "Add missing import".to_string(),
                    kind: "quickfix".to_string(),
                    is_preferred: true,
                    disabled_reason: None,
                    files: vec![SourceLspWorkspaceEditFile {
                        path: source_path.display().to_string(),
                        relative_path: "src/App.ts".to_string(),
                        edits: vec![SourceLspTextEdit {
                            start_line: 1,
                            start_column: 1,
                            end_line: 1,
                            end_column: 1,
                            new_text: "import { thing } from './thing';\n".to_string(),
                        }],
                    }],
                },
                SourceLspCodeAction {
                    title: "Organize Imports".to_string(),
                    kind: "source.organizeImports".to_string(),
                    is_preferred: false,
                    disabled_reason: Some(
                        "Command-only code action is not supported yet".to_string()
                    ),
                    files: Vec::new(),
                },
            ]
        );
    }

    #[test]
    fn extracts_document_highlights() {
        let result = json!([
            {
                "range": {
                    "start": { "line": 4, "character": 8 },
                    "end": { "line": 4, "character": 14 }
                },
                "kind": 2
            },
            {
                "range": {
                    "start": { "line": 8, "character": 2 },
                    "end": { "line": 8, "character": 8 }
                },
                "kind": 3
            }
        ]);

        assert_eq!(
            lsp_document_highlights_from_result(&result),
            vec![
                SourceLspDocumentHighlight {
                    start_line: 5,
                    start_column: 9,
                    end_line: 5,
                    end_column: 15,
                    kind: "read".to_string(),
                },
                SourceLspDocumentHighlight {
                    start_line: 9,
                    start_column: 3,
                    end_line: 9,
                    end_column: 9,
                    kind: "write".to_string(),
                },
            ]
        );
    }

    #[test]
    fn extracts_signature_help() {
        let result = json!({
            "activeSignature": 0,
            "activeParameter": 1,
            "signatures": [
                {
                    "label": "Format(value: string, uppercase: bool)",
                    "documentation": { "kind": "markdown", "value": "Formats a value." },
                    "parameters": [
                        { "label": "value: string", "documentation": "Input value." },
                        { "label": "uppercase: bool" }
                    ]
                }
            ]
        });

        assert_eq!(
            lsp_signature_help_from_result(&result),
            Some(SourceLspSignatureHelp {
                active_signature: 0,
                active_parameter: 1,
                signatures: vec![SourceLspSignature {
                    label: "Format(value: string, uppercase: bool)".to_string(),
                    documentation: "Formats a value.".to_string(),
                    parameters: vec![
                        SourceLspSignatureParameter {
                            label: "value: string".to_string(),
                            documentation: "Input value.".to_string(),
                        },
                        SourceLspSignatureParameter {
                            label: "uppercase: bool".to_string(),
                            documentation: String::new(),
                        },
                    ],
                }],
            })
        );
    }

    #[test]
    fn extracts_inlay_hints() {
        let result = json!([
            {
                "position": { "line": 8, "character": 16 },
                "label": ": string",
                "kind": 1,
                "tooltip": { "kind": "markdown", "value": "Inferred type" },
                "paddingLeft": true
            },
            {
                "position": { "line": 10, "character": 22 },
                "label": [
                    { "value": "value", "tooltip": "Parameter name" },
                    { "value": ": " }
                ],
                "kind": 2,
                "paddingRight": true
            }
        ]);

        assert_eq!(
            lsp_inlay_hints_from_result(&result, 10),
            vec![
                SourceLspInlayHint {
                    label: ": string".to_string(),
                    tooltip: "Inferred type".to_string(),
                    kind: "type".to_string(),
                    line: 9,
                    column: 17,
                    padding_left: true,
                    padding_right: false,
                },
                SourceLspInlayHint {
                    label: "value: ".to_string(),
                    tooltip: "Parameter name".to_string(),
                    kind: "parameter".to_string(),
                    line: 11,
                    column: 23,
                    padding_left: false,
                    padding_right: true,
                },
            ]
        );
    }

    #[test]
    fn extracts_semantic_token_legend_from_initialize() {
        let response = json!({
            "result": {
                "capabilities": {
                    "semanticTokensProvider": {
                        "legend": {
                            "tokenTypes": ["namespace", "class", "property", "modifier"],
                            "tokenModifiers": ["static"]
                        }
                    }
                }
            }
        });

        assert_eq!(
            lsp_semantic_token_types_from_initialize(&response),
            vec![
                "namespace".to_string(),
                "class".to_string(),
                "property".to_string(),
                "modifier".to_string(),
            ]
        );
    }

    #[test]
    fn extracts_semantic_tokens_from_relative_data() {
        let legend = vec![
            "namespace".to_string(),
            "class".to_string(),
            "property".to_string(),
            "method".to_string(),
            "modifier".to_string(),
            "struct".to_string(),
            "local".to_string(),
            "unmapped".to_string(),
        ];
        let result = json!({
            "data": [
                0, 0, 5, 0, 0,
                0, 10, 6, 1, 0,
                2, 4, 8, 2, 0,
                0, 12, 6, 3, 0,
                1, 2, 7, 4, 0,
                1, 1, 6, 5, 0,
                0, 8, 5, 6, 0,
                0, 8, 4, 99, 0,
                0, 12, 4, 7, 0
            ]
        });

        assert_eq!(
            lsp_semantic_tokens_from_result(&result, &legend, 10),
            vec![
                SourceLspSemanticToken {
                    token_type: "namespace".to_string(),
                    line: 1,
                    start_column: 1,
                    length: 5,
                },
                SourceLspSemanticToken {
                    token_type: "class".to_string(),
                    line: 1,
                    start_column: 11,
                    length: 6,
                },
                SourceLspSemanticToken {
                    token_type: "property".to_string(),
                    line: 3,
                    start_column: 5,
                    length: 8,
                },
                SourceLspSemanticToken {
                    token_type: "method".to_string(),
                    line: 3,
                    start_column: 17,
                    length: 6,
                },
                SourceLspSemanticToken {
                    token_type: "keyword".to_string(),
                    line: 4,
                    start_column: 3,
                    length: 7,
                },
                SourceLspSemanticToken {
                    token_type: "class".to_string(),
                    line: 5,
                    start_column: 2,
                    length: 6,
                },
                SourceLspSemanticToken {
                    token_type: "variable".to_string(),
                    line: 5,
                    start_column: 10,
                    length: 5,
                },
            ]
        );
    }

    #[test]
    fn limits_semantic_tokens_after_normalization() {
        let legend = vec!["class".to_string()];
        let result = json!({
            "data": [
                0, 0, 5, 0, 0,
                1, 2, 6, 0, 0,
                1, 2, 7, 0, 0
            ]
        });

        assert_eq!(
            lsp_semantic_tokens_from_result(&result, &legend, 2),
            vec![
                SourceLspSemanticToken {
                    token_type: "class".to_string(),
                    line: 1,
                    start_column: 1,
                    length: 5,
                },
                SourceLspSemanticToken {
                    token_type: "class".to_string(),
                    line: 2,
                    start_column: 3,
                    length: 6,
                },
            ]
        );
    }

    #[test]
    fn builds_session_key_from_language_and_root() {
        let root = env::temp_dir();
        let preview = SourceLspPreview {
            path: root.join("App.ts").display().to_string(),
            relative_path: "App.ts".to_string(),
            file_name: "App.ts".to_string(),
            language: "typescript".to_string(),
            byte_count: 16,
            content: "export {};".to_string(),
            line_count: 1,
        };
        let request = SourceLspLookupRequest {
            root: root.display().to_string(),
            line: 1,
            column: 1,
            limit: None,
        };

        let key = SourceLspSessionKey::from_preview(&preview, &request).expect("session key");
        assert_eq!(key.language, "typescript");
        assert_eq!(
            key.root,
            std::fs::canonicalize(root).unwrap().display().to_string()
        );
    }

    #[test]
    fn session_key_separates_workspace_roots_but_collapses_language_aliases() {
        let root_a = unique_lsp_temp_root("mcb-lsp-key-a");
        let root_b = unique_lsp_temp_root("mcb-lsp-key-b");
        let preview = SourceLspPreview {
            path: root_a.join("App.ts").display().to_string(),
            relative_path: "App.ts".to_string(),
            file_name: "App.ts".to_string(),
            language: "typescript".to_string(),
            byte_count: 16,
            content: "export {};".to_string(),
            line_count: 1,
        };
        let request_root_a = SourceLspLookupRequest {
            root: root_a.display().to_string(),
            line: 1,
            column: 1,
            limit: None,
        };
        let request_root_b = SourceLspLookupRequest {
            root: root_b.display().to_string(),
            line: 1,
            column: 1,
            limit: None,
        };

        let key_a =
            SourceLspSessionKey::from_preview(&preview, &request_root_a).expect("session key a");
        let key_b =
            SourceLspSessionKey::from_preview(&preview, &request_root_b).expect("session key b");
        assert_ne!(
            key_a, key_b,
            "different workspace roots need distinct persistent server slots"
        );

        // Aliases that resolve to the same language id collapse to one key too (ts/tsx).
        let mut tsx_preview = preview.clone();
        tsx_preview.language = "tsx".to_string();
        let key_tsx =
            SourceLspSessionKey::from_preview(&tsx_preview, &request_root_a).expect("tsx key");
        assert_eq!(
            key_a, key_tsx,
            "ts and tsx must share the typescript server"
        );

        std::fs::remove_dir_all(root_a).unwrap();
        std::fs::remove_dir_all(root_b).unwrap();
    }

    #[test]
    fn workspace_pool_replaces_its_single_slot_but_reuses_the_active_root() {
        let key = |root: &str| SourceLspSessionKey {
            language: "csharp".to_string(),
            root: root.to_string(),
        };
        let entries = vec![(key("/workspace/a"), 10)];

        assert_eq!(MAX_LSP_WORKSPACES_PER_LANGUAGE, 1);
        assert_eq!(
            lsp_session_key_to_evict(&entries, &key("/workspace/f")),
            Some(key("/workspace/a"))
        );
        assert_eq!(
            lsp_session_key_to_evict(&entries, &key("/workspace/a")),
            None,
            "reusing a warm workspace never evicts another slot"
        );
    }

    #[test]
    fn reads_lsp_message_with_content_length() {
        let body = r#"{"jsonrpc":"2.0","id":2,"result":null}"#;
        let input = format!("Content-Length: {}\r\n\r\n{}", body.len(), body).into_bytes();
        let message = read_lsp_message(&mut input.as_slice()).expect("message");
        assert_eq!(message.get("id").and_then(Value::as_i64), Some(2));
    }

    #[test]
    fn extracts_locations_from_location_link() {
        let result = json!([
            {
                "targetUri": "file:///Users/blackcolours/dev/work/EdiPlatform/App.cs",
                "targetSelectionRange": {
                    "start": { "line": 9, "character": 4 },
                    "end": { "line": 9, "character": 7 }
                }
            }
        ]);
        let locations = lsp_locations_from_result(&result);
        assert_eq!(locations.len(), 1);
        assert_eq!(locations[0].line, 10);
        assert_eq!(locations[0].column, 5);
        assert_eq!(
            locations[0].path,
            PathBuf::from("/Users/blackcolours/dev/work/EdiPlatform/App.cs")
        );
    }

    #[test]
    fn extracts_publish_diagnostics_for_current_file() {
        let message = json!({
            "jsonrpc": "2.0",
            "method": "textDocument/publishDiagnostics",
            "params": {
                "uri": "file:///tmp/App.ts",
                "diagnostics": [
                    {
                        "range": {
                            "start": { "line": 4, "character": 8 },
                            "end": { "line": 4, "character": 11 }
                        },
                        "severity": 1,
                        "source": "typescript",
                        "message": "Cannot find name 'foo'."
                    },
                    {
                        "range": {
                            "start": { "line": 7, "character": 2 },
                            "end": { "line": 7, "character": 8 }
                        },
                        "severity": 4,
                        "message": "Unnecessary assignment."
                    }
                ]
            }
        });

        let diagnostics = diagnostics_from_message(&message, "file:///tmp/App.ts");
        assert_eq!(
            diagnostics,
            vec![
                SourceLspDiagnostic {
                    severity: "error".to_string(),
                    message: "Cannot find name 'foo'.".to_string(),
                    line: 5,
                    column: 9,
                    source: Some("typescript".to_string()),
                    path: Some("/tmp/App.ts".to_string()),
                },
                SourceLspDiagnostic {
                    severity: "hint".to_string(),
                    message: "Unnecessary assignment.".to_string(),
                    line: 8,
                    column: 3,
                    source: None,
                    path: Some("/tmp/App.ts".to_string()),
                }
            ]
        );
        assert!(diagnostics_from_message(&message, "file:///tmp/Other.ts").is_empty());
    }

    #[test]
    fn extracts_pull_diagnostics() {
        let result = json!({
            "kind": "full",
            "items": [
                {
                    "range": {
                        "start": { "line": 2, "character": 12 },
                        "end": { "line": 2, "character": 20 }
                    },
                    "severity": 2,
                    "source": "typescript",
                    "message": "Type mismatch."
                }
            ]
        });

        assert_eq!(
            diagnostics_from_pull_result(&result, "file:///tmp/App.ts"),
            vec![SourceLspDiagnostic {
                severity: "warning".to_string(),
                message: "Type mismatch.".to_string(),
                line: 3,
                column: 13,
                source: Some("typescript".to_string()),
                path: Some("/tmp/App.ts".to_string()),
            }]
        );
    }

    #[test]
    fn extracts_nested_document_symbols() {
        let result = json!([
            {
                "name": "FormatResolver",
                "kind": 5,
                "detail": "class",
                "selectionRange": {
                    "start": { "line": 7, "character": 20 },
                    "end": { "line": 7, "character": 34 }
                },
                "range": {
                    "start": { "line": 7, "character": 0 },
                    "end": { "line": 40, "character": 1 }
                },
                "children": [
                    {
                        "name": "Resolve",
                        "kind": 6,
                        "selectionRange": {
                            "start": { "line": 16, "character": 22 },
                            "end": { "line": 16, "character": 29 }
                        },
                        "range": {
                            "start": { "line": 16, "character": 2 },
                            "end": { "line": 24, "character": 3 }
                        }
                    }
                ]
            }
        ]);

        let symbols = lsp_symbols_from_result(&result, 20);
        assert_eq!(
            symbols,
            vec![
                SourceLspSymbol {
                    name: "FormatResolver".to_string(),
                    kind: "class".to_string(),
                    line: 8,
                    column: 21,
                    detail: "class".to_string(),
                },
                SourceLspSymbol {
                    name: "Resolve".to_string(),
                    kind: "method".to_string(),
                    line: 17,
                    column: 23,
                    detail: String::new(),
                }
            ]
        );
    }

    #[test]
    fn extracts_workspace_symbols_from_locations() {
        let root = env::temp_dir().join("mcb-workspace-symbol-root");
        let source_path = root.join("src/FormatResolver.cs");
        let result = json!([
            {
                "name": "FormatResolver",
                "kind": 5,
                "containerName": "EdiPlatform.Core.Services",
                "location": {
                    "uri": path_to_file_uri(&source_path),
                    "range": {
                        "start": { "line": 7, "character": 20 },
                        "end": { "line": 7, "character": 34 }
                    }
                }
            }
        ]);

        assert_eq!(
            lsp_workspace_symbols_from_result(&result, &root, "csharp", 20),
            vec![SourceLspWorkspaceSymbol {
                path: source_path.display().to_string(),
                relative_path: "src/FormatResolver.cs".to_string(),
                file_name: "FormatResolver.cs".to_string(),
                language: "csharp".to_string(),
                byte_count: 0,
                symbol_name: "FormatResolver".to_string(),
                kind: "class".to_string(),
                line: 8,
                column: 21,
                detail: "EdiPlatform.Core.Services - src/FormatResolver.cs:8:21".to_string(),
                container_name: Some("EdiPlatform.Core.Services".to_string()),
            }]
        );
    }

    #[test]
    fn extracts_completion_items_from_list_and_array_results() {
        let list_result = json!({
            "isIncomplete": false,
            "items": [
                {
                    "label": "Format",
                    "kind": 2,
                    "detail": "string Format(string value)",
                    "insertText": "Format"
                },
                {
                    "label": "Widget",
                    "kind": 7
                }
            ]
        });
        assert_eq!(
            lsp_completion_items_from_result(&list_result, 20),
            vec![
                SourceLspCompletionItem {
                    label: "Format".to_string(),
                    kind: "method".to_string(),
                    detail: "string Format(string value)".to_string(),
                    insert_text: "Format".to_string(),
                },
                SourceLspCompletionItem {
                    label: "Widget".to_string(),
                    kind: "class".to_string(),
                    detail: String::new(),
                    insert_text: "Widget".to_string(),
                }
            ]
        );

        let array_result = json!([
            {
                "label": "Console",
                "kind": 7,
                "detail": "class Console"
            }
        ]);
        assert_eq!(
            lsp_completion_items_from_result(&array_result, 1),
            vec![SourceLspCompletionItem {
                label: "Console".to_string(),
                kind: "class".to_string(),
                detail: "class Console".to_string(),
                insert_text: "Console".to_string(),
            }]
        );
    }

    #[test]
    fn extracts_symbol_at_position() {
        let source = "public sealed class FormatResolver\n{\n}";
        assert_eq!(
            symbol_at_position(source, 1, 23).as_deref(),
            Some("FormatResolver")
        );
    }

    #[test]
    fn a_real_server_answers_several_questions_asked_at_once() {
        let _test_lock = supercharged_test_lock();
        // The pipe-level test above proves questions can overlap. This proves the whole
        // path does it against a server that really exists — one server started, four
        // questions in the air, four right answers — and covers the new symbol list the
        // margin counts are built on.
        if resolve_server_for_language("typescript").is_none() {
            crate::debug_log::stderr_log!("skipping: typescript-language-server is not installed");
            return;
        }
        let root = unique_lsp_temp_root("mcb-ts-lsp-at-once");
        std::fs::write(
            root.join("tsconfig.json"),
            r#"{"compilerOptions":{"strict":true,"target":"ES2022","module":"ESNext"}}"#,
        )
        .unwrap();
        let content = [
            "export function greet(name: string): string {",
            "  return `Hello ${name}`;",
            "}",
            "",
            "const value = greet(\"Mac\");",
        ]
        .join("\n");
        let file_path = root.join("App.ts");
        std::fs::write(&file_path, &content).unwrap();

        let preview = SourceLspPreview {
            path: file_path.display().to_string(),
            relative_path: "App.ts".to_string(),
            file_name: "App.ts".to_string(),
            language: "typescript".to_string(),
            byte_count: content.len() as u64,
            content,
            line_count: 5,
        };
        let request = SourceLspLookupRequest {
            root: root.display().to_string(),
            line: 5,
            column: 16,
            limit: Some(20),
        };
        let registry = SourceLspRegistry::default();
        set_language_intelligence(&request.root, true);
        registry
            .set_active_root(&request.root, true)
            .expect("activate TypeScript workspace");

        // Warm the server first so the four questions below race each other rather than
        // the one-off cost of starting a process.
        registry
            .find_symbols(preview.clone(), request.clone())
            .expect("the server should start and list this file's symbols");

        thread::scope(|scope| {
            let references =
                scope.spawn(|| registry.find_references(preview.clone(), request.clone()));
            let hover = scope.spawn(|| registry.find_hover(preview.clone(), request.clone()));
            let highlights =
                scope.spawn(|| registry.find_document_highlights(preview.clone(), request.clone()));
            let document_symbols = scope.spawn(|| {
                registry.find_document_symbols(
                    root.display().to_string(),
                    "typescript".to_string(),
                    file_path.display().to_string(),
                )
            });

            let references = references
                .join()
                .expect("references thread")
                .expect("references");
            assert!(
                references.iter().any(|target| target.line == 1),
                "the declaration of greet should be among its mentions; got {references:?}"
            );

            let hover = hover
                .join()
                .expect("hover thread")
                .expect("hover")
                .expect("hover text");
            assert!(hover.contents.join("\n").contains("greet"));

            let highlights = highlights
                .join()
                .expect("highlights thread")
                .expect("highlights");
            assert!(!highlights.is_empty());

            let document_symbols = document_symbols
                .join()
                .expect("document symbol thread")
                .expect("document symbols");
            let greet = document_symbols
                .iter()
                .find(|symbol| symbol.name == "greet")
                .unwrap_or_else(|| panic!("greet should be listed; got {document_symbols:?}"));
            assert_eq!(
                greet.line, 0,
                "greet is declared on the first line, which is line zero to the editor"
            );
        });

        assert_eq!(
            registry.session_count().expect("session count"),
            1,
            "four questions at once must share the one server, not start more"
        );
        std::fs::remove_dir_all(&root).ok();
    }

    #[test]
    fn typescript_language_server_smoke_reads_intelligence_actions() {
        let _test_lock = supercharged_test_lock();
        if resolve_server_for_language("typescript").is_none() {
            crate::debug_log::stderr_log!(
                "skipping TypeScript LSP smoke: typescript-language-server not found"
            );
            return;
        }
        let root = unique_lsp_temp_root("mcb-ts-lsp-smoke");
        std::fs::write(
            root.join("tsconfig.json"),
            r#"{"compilerOptions":{"strict":true,"target":"ES2022","module":"ESNext"}}"#,
        )
        .unwrap();
        let content = [
            "export function greet(name: string): string {",
            "  return `Hello ${name}`;",
            "}",
            "",
            "const value = greet(\"Mac\");",
            "const broken: number = \"oops\";",
        ]
        .join("\n");
        let file_path = root.join("App.ts");
        std::fs::write(&file_path, &content).unwrap();

        let preview = SourceLspPreview {
            path: file_path.display().to_string(),
            relative_path: "App.ts".to_string(),
            file_name: "App.ts".to_string(),
            language: "typescript".to_string(),
            byte_count: content.len() as u64,
            content,
            line_count: 6,
        };
        let request = SourceLspLookupRequest {
            root: root.display().to_string(),
            line: 5,
            column: 16,
            limit: Some(20),
        };
        let registry = SourceLspRegistry::default();
        set_language_intelligence(&request.root, true);
        registry
            .set_active_root(&request.root, true)
            .expect("activate TypeScript workspace");

        let symbols = registry
            .find_symbols(preview.clone(), request.clone())
            .expect("document symbols");
        assert!(
            symbols
                .iter()
                .any(|symbol| symbol.name == "greet" && symbol.kind == "function"),
            "expected TypeScript document symbols to include greet; got {symbols:?}"
        );

        let hover = registry
            .find_hover(preview.clone(), request.clone())
            .expect("hover")
            .expect("hover contents");
        assert!(
            hover.contents.join("\n").contains("greet"),
            "expected hover to describe greet; got {hover:?}"
        );

        let references = registry
            .find_references(preview.clone(), request.clone())
            .expect("references");
        assert!(
            references
                .iter()
                .any(|target| target.path == file_path.display().to_string() && target.line == 1),
            "expected references to include greet declaration; got {references:?}"
        );
        assert!(
            references
                .iter()
                .any(|target| target.path == file_path.display().to_string() && target.line == 5),
            "expected references to include greet call site; got {references:?}"
        );

        let definitions = registry
            .find_definitions(preview.clone(), request.clone())
            .expect("definitions");
        assert!(
            definitions
                .iter()
                .any(|target| target.path == file_path.display().to_string() && target.line == 1),
            "expected definition to resolve to App.ts line 1; got {definitions:?}"
        );

        let diagnostics = registry
            .read_diagnostics(preview, request)
            .expect("diagnostics");
        assert!(
            diagnostics.iter().any(|diagnostic| {
                diagnostic.severity == "error"
                    && diagnostic.message.contains("string")
                    && diagnostic.message.contains("number")
            }),
            "expected TypeScript diagnostics to include the broken number assignment; got {diagnostics:?}"
        );

        std::fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn switching_active_workspace_replaces_the_previous_server() {
        let _test_lock = supercharged_test_lock();
        if resolve_server_for_language("typescript").is_none() {
            crate::debug_log::stderr_log!(
                "skipping LSP dedupe smoke: typescript-language-server not found"
            );
            return;
        }
        let registry = SourceLspRegistry::default();
        let make_root = |label: &str,
                         symbol: &str|
         -> (PathBuf, SourceLspPreview, SourceLspLookupRequest) {
            let root = unique_lsp_temp_root(label);
            std::fs::write(
                root.join("tsconfig.json"),
                r#"{"compilerOptions":{"strict":true,"target":"ES2022","module":"ESNext"}}"#,
            )
            .unwrap();
            let content =
                format!("export function {symbol}(name: string): string {{\n  return name;\n}}\n");
            let file_path = root.join("App.ts");
            std::fs::write(&file_path, &content).unwrap();
            let preview = SourceLspPreview {
                path: file_path.display().to_string(),
                relative_path: "App.ts".to_string(),
                file_name: "App.ts".to_string(),
                language: "typescript".to_string(),
                byte_count: content.len() as u64,
                content,
                line_count: 3,
            };
            let request = SourceLspLookupRequest {
                root: root.display().to_string(),
                line: 1,
                column: 17,
                limit: Some(20),
            };
            (root, preview, request)
        };

        let (root_a, preview_a, request_a) = make_root("mcb-lsp-dedupe-a", "alpha");
        let (root_b, preview_b, request_b) = make_root("mcb-lsp-dedupe-b", "beta");

        // First root spins its warm server.
        set_language_intelligence(&request_a.root, true);
        set_language_intelligence(&request_b.root, true);
        registry
            .set_active_root(&request_a.root, true)
            .expect("activate root A");
        let symbols_a = registry
            .find_symbols(preview_a.clone(), request_a.clone())
            .expect("root A symbols");
        assert!(
            symbols_a.iter().any(|symbol| symbol.name == "alpha"),
            "expected root A document symbols to include alpha; got {symbols_a:?}"
        );
        assert_eq!(
            registry.session_count().unwrap(),
            1,
            "first root must create exactly one typescript session"
        );
        let pid_after_a = registry.session_pid_for("typescript", &root_a).unwrap();

        // Moving Supercharged mode to another root tears down the old process.
        assert_eq!(
            registry
                .set_active_root(&request_b.root, true)
                .expect("activate root B"),
            1
        );
        let symbols_b = registry
            .find_symbols(preview_b.clone(), request_b.clone())
            .expect("root B symbols");
        assert!(
            symbols_b.iter().any(|symbol| symbol.name == "beta"),
            "expected root B document symbols to include beta after re-point; got {symbols_b:?}"
        );
        assert_eq!(
            registry.session_count().unwrap(),
            1,
            "only the active workspace keeps a language-server process"
        );
        let pid_after_b = registry.session_pid_for("typescript", &root_b).unwrap();
        assert!(registry.session_pid_for("typescript", &root_a).is_none());

        // Returning to root A starts a fresh process for that workspace.
        registry
            .set_active_root(&request_a.root, true)
            .expect("reactivate root A");
        let symbols_a_again = registry
            .find_symbols(preview_a, request_a)
            .expect("root A symbols again");
        assert!(
            symbols_a_again.iter().any(|symbol| symbol.name == "alpha"),
            "expected re-point back to root A to serve alpha; got {symbols_a_again:?}"
        );
        assert_eq!(
            registry.session_count().unwrap(),
            1,
            "switching back still owns only one workspace process"
        );
        assert!(registry.session_pid_for("typescript", &root_b).is_none());
        assert_ne!(registry.session_pid_for("typescript", &root_a).unwrap(), pid_after_b);
        let _ = pid_after_a;

        std::fs::remove_dir_all(root_a).unwrap();
        std::fs::remove_dir_all(root_b).unwrap();
    }

    #[test]
    fn warm_with_no_running_server_is_a_noop() {
        // A workspace without a recognized project marker must not speculatively
        // start every language server the app happens to support.
        let registry = SourceLspRegistry::default();
        let root = unique_lsp_temp_root("mcb-lsp-warm-noop");
        let warmed = registry
            .warm_running_servers_for_root(&root.display().to_string())
            .expect("warm with empty registry");
        assert_eq!(warmed, 0, "warming an empty registry must warm nothing");
        assert_eq!(
            registry.session_count().unwrap(),
            0,
            "warming an unrecognized workspace must not spawn a server"
        );

        std::fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn csharp_project_markers_are_detected_without_reading_source_files() {
        let root = unique_lsp_temp_root("mcb-lsp-csharp-preload");
        let project = root.join("src").join("App");
        std::fs::create_dir_all(&project).unwrap();
        std::fs::write(project.join("App.csproj"), "<Project />").unwrap();

        assert!(workspace_contains_csharp_project(&root));

        std::fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn csharp_preload_discovery_is_cached_by_canonical_root() {
        let registry = SourceLspRegistry::default();
        let root = unique_lsp_temp_root("mcb-lsp-csharp-preload-cache");
        let marker = root.join("App.csproj");
        std::fs::write(&marker, "<Project />").unwrap();
        let canonical_root = normalized_lsp_root(&root.display().to_string()).unwrap();
        assert_eq!(
            registry
                .preload_languages_for_root(&canonical_root)
                .unwrap(),
            vec!["csharp"]
        );
        std::fs::remove_file(marker).unwrap();
        assert_eq!(
            registry
                .preload_languages_for_root(&canonical_root)
                .unwrap(),
            vec!["csharp"],
            "revisiting a workspace should use its marker result without scanning again"
        );

        std::fs::remove_dir_all(root).unwrap();
    }

    #[tokio::test(flavor = "multi_thread", worker_threads = 2)]
    async fn concurrent_native_csharp_ensure_calls_share_one_canonical_root_endpoint() {
        let _test_lock = supercharged_test_lock();
        if resolve_server_for_language("csharp").is_none() {
            crate::debug_log::stderr_log!(
                "skipping native C# endpoint coalescing: roslyn-language-server not found"
            );
            return;
        }
        let registry = SourceLspRegistry::default();
        let root = unique_lsp_temp_root("mcb-native-csharp-coalesce");
        std::fs::write(root.join("App.csproj"), "<Project />").unwrap();
        let root_text = root.display().to_string();
        set_language_intelligence(&root_text, true);
        registry
            .set_active_root(&root_text, true)
            .expect("activate C# workspace");
        let barrier = Arc::new(std::sync::Barrier::new(3));
        let mut calls = Vec::new();
        for _ in 0..2 {
            let registry = registry.clone();
            let root = root_text.clone();
            let barrier = Arc::clone(&barrier);
            calls.push(tokio::task::spawn_blocking(move || {
                barrier.wait();
                registry.ensure_native_csharp_endpoint(&root)
            }));
        }
        barrier.wait();
        let left = calls.remove(0).await.unwrap().unwrap();
        let right = calls.remove(0).await.unwrap().unwrap();
        assert_eq!(left.ws_url, right.ws_url);
        assert_eq!(left.root, right.root);
        assert_eq!(locked(&registry.native_csharp_sessions).len(), 1);
        std::fs::remove_dir_all(root).unwrap();
    }
    #[test]
    fn legacy_warm_does_not_start_a_second_roslyn_for_csharp_workspace() {
        let registry = SourceLspRegistry::default();
        let root = unique_lsp_temp_root("mcb-lsp-csharp-warm");
        std::fs::write(root.join("App.csproj"), "<Project />").unwrap();
        let warmed = registry
            .warm_running_servers_for_root(&root.display().to_string())
            .expect("legacy warm C# workspace");
        assert_eq!(warmed, 0, "the native Monaco client owns C# warming");
        assert_eq!(registry.session_count().unwrap(), 0);
        assert!(registry.session_pid_for("csharp", &root).is_none());

        std::fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn warm_moves_the_single_workspace_slot_to_the_active_root() {
        let _test_lock = supercharged_test_lock();
        if resolve_server_for_language("typescript").is_none() {
            crate::debug_log::stderr_log!(
                "skipping LSP warm smoke: typescript-language-server not found"
            );
            return;
        }
        let registry = SourceLspRegistry::default();
        let make_root = |label: &str,
                         symbol: &str|
         -> (PathBuf, SourceLspPreview, SourceLspLookupRequest) {
            let root = unique_lsp_temp_root(label);
            std::fs::write(
                root.join("tsconfig.json"),
                r#"{"compilerOptions":{"strict":true,"target":"ES2022","module":"ESNext"}}"#,
            )
            .unwrap();
            let content =
                format!("export function {symbol}(name: string): string {{\n  return name;\n}}\n");
            let file_path = root.join("App.ts");
            std::fs::write(&file_path, &content).unwrap();
            let preview = SourceLspPreview {
                path: file_path.display().to_string(),
                relative_path: "App.ts".to_string(),
                file_name: "App.ts".to_string(),
                language: "typescript".to_string(),
                byte_count: content.len() as u64,
                content,
                line_count: 3,
            };
            let request = SourceLspLookupRequest {
                root: root.display().to_string(),
                line: 1,
                column: 17,
                limit: Some(20),
            };
            (root, preview, request)
        };

        let (root_a, preview_a, request_a) = make_root("mcb-lsp-warm-a", "alpha");
        let (root_b, _preview_b, _request_b) = make_root("mcb-lsp-warm-b", "beta");

        // A real request spins the first warm workspace server.
        set_language_intelligence(&request_a.root, true);
        set_language_intelligence(&root_b.display().to_string(), true);
        registry
            .set_active_root(&request_a.root, true)
            .expect("activate root A");
        registry
            .find_symbols(preview_a, request_a)
            .expect("root A symbols");
        assert_eq!(registry.session_count().unwrap(), 1);
        let pid_at_a = registry.session_pid_for("typescript", &root_a).unwrap();

        // Warming the SAME root re-points to where it already is — a no-op, so the child
        // process must be unchanged (no needless re-index churn).
        let warmed_same = registry
            .warm_running_servers_for_root(&root_a.display().to_string())
            .expect("warm same root");
        assert_eq!(warmed_same, 1, "the running session is reported as warmed");
        assert_eq!(
            registry.session_pid_for("typescript", &root_a).unwrap(),
            pid_at_a,
            "re-warming the active root must not replace the child process"
        );

        // Warming a different active root replaces the previous workspace slot.
        registry
            .set_active_root(&root_b.display().to_string(), true)
            .expect("activate root B");
        let warmed_b = registry
            .warm_running_servers_for_root(&root_b.display().to_string())
            .expect("warm new root");
        assert_eq!(warmed_b, 1, "the known language is warmed under root B");
        assert_eq!(
            registry.session_count().unwrap(),
            1,
            "warming keeps only the active workspace slot"
        );
        let pid_at_b = registry.session_pid_for("typescript", &root_b).unwrap();
        assert_ne!(pid_at_a, pid_at_b);
        assert!(registry.session_pid_for("typescript", &root_a).is_none());

        std::fs::remove_dir_all(root_a).unwrap();
        std::fs::remove_dir_all(root_b).unwrap();
    }

    #[test]
    fn javascript_language_server_smoke_reads_intelligence_actions() {
        let _test_lock = supercharged_test_lock();
        if resolve_server_for_language("javascript").is_none() {
            crate::debug_log::stderr_log!(
                "skipping JavaScript LSP smoke: typescript-language-server not found"
            );
            return;
        }
        let root = unique_lsp_temp_root("mcb-js-lsp-smoke");
        std::fs::write(
            root.join("jsconfig.json"),
            r#"{"compilerOptions":{"target":"ES2022","module":"ESNext","checkJs":true}}"#,
        )
        .unwrap();
        let content = [
            "export function greet(name) {",
            "  return `Hello ${name}`;",
            "}",
            "",
            "const value = greet(\"Mac\");",
        ]
        .join("\n");
        let file_path = root.join("App.js");
        std::fs::write(&file_path, &content).unwrap();

        let preview = SourceLspPreview {
            path: file_path.display().to_string(),
            relative_path: "App.js".to_string(),
            file_name: "App.js".to_string(),
            language: "javascript".to_string(),
            byte_count: content.len() as u64,
            content,
            line_count: 5,
        };
        let request = SourceLspLookupRequest {
            root: root.display().to_string(),
            line: 5,
            column: 16,
            limit: Some(20),
        };
        let registry = SourceLspRegistry::default();
        set_language_intelligence(&request.root, true);
        registry
            .set_active_root(&request.root, true)
            .expect("activate JavaScript workspace");

        let symbols = registry
            .find_symbols(preview.clone(), request.clone())
            .expect("JavaScript document symbols");
        assert!(
            symbols
                .iter()
                .any(|symbol| symbol.name == "greet" && symbol.kind == "function"),
            "expected JavaScript document symbols to include greet; got {symbols:?}"
        );

        let hover = registry
            .find_hover(preview.clone(), request.clone())
            .expect("JavaScript hover")
            .expect("JavaScript hover contents");
        assert!(
            hover.contents.join("\n").contains("greet"),
            "expected JavaScript hover to describe greet; got {hover:?}"
        );

        let definitions = registry
            .find_definitions(preview.clone(), request.clone())
            .expect("JavaScript definitions");
        assert!(
            definitions
                .iter()
                .any(|target| target.path == file_path.display().to_string() && target.line == 1),
            "expected JavaScript definition to resolve to App.js line 1; got {definitions:?}"
        );

        let references = registry
            .find_references(preview, request)
            .expect("JavaScript references");
        assert!(
            references
                .iter()
                .any(|target| target.path == file_path.display().to_string() && target.line == 1),
            "expected JavaScript references to include greet declaration; got {references:?}"
        );
        assert!(
            references
                .iter()
                .any(|target| target.path == file_path.display().to_string() && target.line == 5),
            "expected JavaScript references to include greet call site; got {references:?}"
        );

        std::fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn rust_language_server_smoke_reads_intelligence_actions() {
        let _test_lock = supercharged_test_lock();
        if resolve_server_for_language("rust").is_none() {
            crate::debug_log::stderr_log!("skipping Rust LSP smoke: rust-analyzer not found");
            return;
        }
        if let Err(reason) = rust_analyzer_ready_for_smoke() {
            crate::debug_log::stderr_log!("skipping Rust LSP smoke: {reason}");
            return;
        }
        let root = unique_lsp_temp_root("mcb-rust-lsp-smoke");
        let source_dir = root.join("src");
        std::fs::create_dir_all(&source_dir).unwrap();
        std::fs::write(
            root.join("Cargo.toml"),
            [
                "[package]",
                r#"name = "mcb_lsp_smoke""#,
                r#"version = "0.1.0""#,
                r#"edition = "2021""#,
            ]
            .join("\n"),
        )
        .unwrap();
        let content = [
            "pub struct Widget {",
            "    pub name: String,",
            "}",
            "",
            "impl Widget {",
            "    pub fn label(&self) -> String {",
            "        self.name.clone()",
            "    }",
            "}",
            "",
            "pub fn run() -> String {",
            "    let widget = Widget { name: \"mac\".to_string() };",
            "    widget.label()",
            "}",
        ]
        .join("\n");
        let file_path = source_dir.join("lib.rs");
        std::fs::write(&file_path, &content).unwrap();

        let preview = SourceLspPreview {
            path: file_path.display().to_string(),
            relative_path: "src/lib.rs".to_string(),
            file_name: "lib.rs".to_string(),
            language: "rust".to_string(),
            byte_count: content.len() as u64,
            content,
            line_count: 14,
        };
        let request = SourceLspLookupRequest {
            root: root.display().to_string(),
            line: 12,
            column: 20,
            limit: Some(20),
        };
        let registry = SourceLspRegistry::default();
        set_language_intelligence(&request.root, true);
        registry
            .set_active_root(&request.root, true)
            .expect("activate Rust workspace");

        let symbols = registry
            .find_symbols(preview.clone(), request.clone())
            .expect("Rust document symbols");
        assert!(
            symbols
                .iter()
                .any(|symbol| symbol.name == "Widget" && symbol.kind == "struct"),
            "expected Rust document symbols to include Widget; got {symbols:?}"
        );

        let hover = registry
            .find_hover(preview.clone(), request.clone())
            .expect("Rust hover")
            .expect("Rust hover contents");
        assert!(
            hover.contents.join("\n").contains("Widget"),
            "expected Rust hover to describe Widget; got {hover:?}"
        );

        let definitions = registry
            .find_definitions(preview.clone(), request.clone())
            .expect("Rust definitions");
        assert!(
            definitions
                .iter()
                .any(|target| target.path == file_path.display().to_string() && target.line == 1),
            "expected Rust definition to resolve to lib.rs line 1; got {definitions:?}"
        );

        let references = registry
            .find_references(preview, request)
            .expect("Rust references");
        assert!(
            references
                .iter()
                .any(|target| target.path == file_path.display().to_string() && target.line == 12),
            "expected Rust references to include Widget use site; got {references:?}"
        );

        std::fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn svelte_language_server_smoke_reads_document_symbols_when_available() {
        let _test_lock = supercharged_test_lock();
        if resolve_server_for_language("svelte").is_none() {
            crate::debug_log::stderr_log!("skipping Svelte LSP smoke: svelteserver not found");
            return;
        }
        let root = unique_lsp_temp_root("mcb-svelte-lsp-smoke");
        let file_path = root.join("App.svelte");
        let content = "<script lang=\"ts\">\nfunction currentThemeKey(): string { return 'light'; }\nconst themeKey = currentThemeKey();\n</script>\n<p>{themeKey}</p>\n".to_string();
        std::fs::write(&file_path, &content).unwrap();
        let line_count = content.lines().count().max(1);
        let definition_line = content
            .lines()
            .position(|line| line.contains("function currentThemeKey"))
            .map(|line| line + 1)
            .expect("currentThemeKey definition");
        let use_line = content
            .lines()
            .position(|line| line.contains("const themeKey = currentThemeKey()"))
            .map(|line| line + 1)
            .expect("currentThemeKey use");
        let preview = SourceLspPreview {
            path: file_path.display().to_string(),
            relative_path: "App.svelte".to_string(),
            file_name: "App.svelte".to_string(),
            language: "svelte".to_string(),
            byte_count: content.len() as u64,
            content,
            line_count,
        };
        let request = SourceLspLookupRequest {
            root: root.display().to_string(),
            line: 1,
            column: 1,
            limit: Some(5_000),
        };
        let registry = SourceLspRegistry::default();
        registry
            .set_active_root(&request.root, true)
            .expect("activate Svelte workspace");

        match registry.find_symbols(preview.clone(), request) {
            Ok(symbols) => {
                assert!(
                symbols.iter().any(|symbol| symbol.name == "currentThemeKey"),
                "expected Svelte document symbols to include currentThemeKey; got {symbols:?}"
            )
            }
            Err(error) => {
                crate::debug_log::stderr_log!("skipping Svelte LSP smoke: {error}");
                return;
            }
        }

        let navigation_request = SourceLspLookupRequest {
            root: root.display().to_string(),
            line: use_line,
            column: 20,
            limit: Some(100),
        };
        let definitions = registry
            .find_definitions(preview.clone(), navigation_request.clone())
            .expect("Svelte definitions");
        assert!(
            definitions
                .iter()
                .any(|target| target.path == file_path.display().to_string()
                    && target.line == definition_line),
            "expected currentThemeKey definition at line {definition_line}; got {definitions:?}"
        );

        let references = registry
            .find_references(preview, navigation_request)
            .expect("Svelte references");
        assert!(
            references.iter().any(|target| {
                target.path == file_path.display().to_string() && target.line == use_line
            }),
            "expected currentThemeKey references to include line {use_line}; got {references:?}"
        );
        drop(registry);
        std::fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn csharp_language_server_smoke_reads_intelligence_actions() {
        let _test_lock = supercharged_test_lock();
        if env::var_os("MCB_RUN_CSHARP_LSP_SMOKE").is_none() {
            crate::debug_log::stderr_log!(
                "skipping C# LSP smoke: set MCB_RUN_CSHARP_LSP_SMOKE=1 to enable"
            );
            return;
        }
        if resolve_server_for_language("csharp").is_none() {
            crate::debug_log::stderr_log!(
                "skipping C# LSP smoke: roslyn-language-server not found"
            );
            return;
        }
        let root = unique_lsp_temp_root("mcb-cs-lsp-smoke");
        let outcome = run_csharp_language_server_smoke(&root);
        let _ = std::fs::remove_dir_all(root);
        outcome.expect("C# LSP smoke");
    }

    fn run_csharp_language_server_smoke(root: &Path) -> Result<(), String> {
        std::fs::write(
            root.join("Smoke.csproj"),
            [
                r#"<Project Sdk="Microsoft.NET.Sdk">"#,
                "  <PropertyGroup>",
                "    <TargetFramework>net9.0</TargetFramework>",
                "    <Nullable>enable</Nullable>",
                "    <ImplicitUsings>enable</ImplicitUsings>",
                "  </PropertyGroup>",
                "</Project>",
            ]
            .join("\n"),
        )
        .map_err(|error| format!("write csproj: {error}"))?;

        let content = [
            "namespace Smoke;",
            "",
            "public sealed class Widget",
            "{",
            "    public string Format(string value) => value.ToUpperInvariant();",
            "}",
            "",
            "public sealed class Runner",
            "{",
            "    public string Run()",
            "    {",
            "        var widget = new Widget();",
            "        int broken = \"oops\";",
            "        return widget.Format(\"mac\");",
            "    }",
            "}",
        ]
        .join("\n");
        let file_path = root.join("Widget.cs");
        std::fs::write(&file_path, &content).map_err(|error| format!("write source: {error}"))?;

        let restore = Command::new("dotnet")
            .arg("restore")
            .arg("--nologo")
            .current_dir(root)
            .output()
            .map_err(|error| format!("dotnet restore failed to start: {error}"))?;
        if !restore.status.success() {
            return Err(format!(
                "dotnet restore failed: {}",
                String::from_utf8_lossy(&restore.stderr)
            ));
        }

        let status = read_source_lsp_status_sync(root.to_path_buf(), "csharp".to_string())?;
        if !status.available {
            return Err(format!("C# LSP status unavailable: {:?}", status.reason));
        }

        let preview = SourceLspPreview {
            path: file_path.display().to_string(),
            relative_path: "Widget.cs".to_string(),
            file_name: "Widget.cs".to_string(),
            language: "csharp".to_string(),
            byte_count: content.len() as u64,
            content,
            line_count: 16,
        };
        let request = SourceLspLookupRequest {
            root: root.display().to_string(),
            line: 12,
            column: 28,
            limit: Some(20),
        };
        // C# in the app is owned by the authenticated CodeMirror-to-Roslyn bridge.
        // Exercise the same Roslyn process directly here without re-enabling the
        // retired registry route, which would let a second server start in production.
        let server = resolve_server_for_language("csharp")
            .ok_or_else(|| "roslyn-language-server not found".to_string())?;
        let session = SourceLspSession::start(root.to_path_buf(), server, 1)?;
        let connection = Arc::clone(&session.connection);

        let symbols_result = connection
            .request(&preview, &request, "textDocument/documentSymbol")?
            .ok_or_else(|| "expected C# document symbols".to_string())?;
        let symbols = lsp_symbols_from_result(&symbols_result, request.limit.unwrap_or(100));
        if !symbols.iter().any(|symbol| {
            symbol.name.rsplit('.').next() == Some("Widget") && symbol.kind == "class"
        }) {
            return Err(format!(
                "expected C# document symbols to include Widget; got {symbols:?}"
            ));
        }

        let hover_result = connection
            .request(&preview, &request, "textDocument/hover")?
            .ok_or_else(|| "expected C# hover contents".to_string())?;
        let hover = hover_contents_from_result(&hover_result);
        if !hover.join("\n").contains("Widget") {
            return Err(format!(
                "expected C# hover to describe Widget; got {hover:?}"
            ));
        }

        let definition_result = connection
            .request(&preview, &request, "textDocument/definition")?
            .ok_or_else(|| "expected C# definition".to_string())?;
        let definitions = lsp_locations_from_result(&definition_result);
        if !definitions
            .iter()
            .any(|target| target.path == file_path && target.line == 3)
        {
            return Err(format!(
                "expected C# definition to resolve to Widget.cs line 3; got {definitions:?}"
            ));
        }

        let references_result = connection
            .request(&preview, &request, "textDocument/references")?
            .ok_or_else(|| "expected C# references".to_string())?;
        let references = lsp_locations_from_result(&references_result);
        if !references
            .iter()
            .any(|target| target.path == file_path && target.line == 12)
        {
            return Err(format!(
                "expected C# references to include Widget call site; got {references:?}"
            ));
        }

        Ok(())
    }

    #[test]
    fn round_trips_file_uri_encoding() {
        let path = PathBuf::from("/tmp/source file #1.cs");
        let uri = path_to_file_uri(&path);
        assert_eq!(uri, "file:///tmp/source%20file%20%231.cs");
        assert_eq!(file_uri_to_path(&uri), Some(path));
    }

    /// A stand-in language server that speaks the real protocol down a pipe, so a test
    /// can decide exactly what it answers and when. No process is started: both ends of
    /// a socket pair live in this test, one held by the app's client and one held here.
    struct FakeLanguageServer {
        pipe: std::os::unix::net::UnixStream,
    }

    impl FakeLanguageServer {
        fn read_message(&mut self) -> Value {
            read_lsp_message(&mut self.pipe).expect("the app should have sent a message")
        }

        fn write_message(&mut self, message: &Value) {
            write_lsp_message(&mut self.pipe, message).expect("the fake server should be writable");
        }

        /// Play the opening exchange every real server plays: answer `initialize`, then
        /// wait for the app's `initialized` note.
        fn answer_the_opening_exchange(&mut self) {
            let opening = self.read_message();
            assert_eq!(
                opening.get("method").and_then(Value::as_str),
                Some("initialize")
            );
            let id = opening
                .get("id")
                .cloned()
                .expect("initialize carries a number");
            self.write_message(&json!({
                "jsonrpc": "2.0",
                "id": id,
                "result": { "capabilities": {} }
            }));
            let ready_note = self.read_message();
            assert_eq!(
                ready_note.get("method").and_then(Value::as_str),
                Some("initialized")
            );
        }
    }

    /// A client wired to a fake server instead of a real process, already past the
    /// opening exchange.
    fn connect_to_a_fake_language_server(
        language_id: &str,
    ) -> (Arc<LspConnection>, FakeLanguageServer) {
        let (app_end, server_end) =
            std::os::unix::net::UnixStream::pair().expect("a pair of connected pipes");
        let mut server = FakeLanguageServer { pipe: server_end };
        let answering = thread::spawn(move || {
            server.answer_the_opening_exchange();
            server
        });

        let identity = LanguageServerIdentity {
            language_id: language_id.to_string(),
            server_name: "a fake language server".to_string(),
            root: "/tmp/a-fake-project".to_string(),
        };
        let connection = LspConnection::connect(
            Box::new(app_end.try_clone().expect("a second handle on the pipe")),
            Box::new(app_end),
            identity,
        )
        .expect("the client should finish the opening exchange");

        let server = answering.join().expect("the fake server thread");
        (connection, server)
    }

    /// Give a background thread up to a second to make `check` true. Returns whether it did.
    fn eventually(mut check: impl FnMut() -> bool) -> bool {
        let deadline = Instant::now() + Duration::from_secs(1);
        while Instant::now() < deadline {
            if check() {
                return true;
            }
            thread::sleep(Duration::from_millis(5));
        }
        check()
    }

    #[test]
    fn two_questions_can_be_waiting_on_one_server_at_the_same_time() {
        // The old client held the whole session while a question was outstanding, so a
        // second question could not even be sent until the first was answered — up to six
        // seconds of nothing. Here both questions must reach the server before either is
        // answered, and the answers may come back in any order.
        let (connection, mut server) = connect_to_a_fake_language_server("fake-two-at-once");

        let asking_for_references = {
            let connection = Arc::clone(&connection);
            thread::spawn(move || {
                connection.send_request(
                    "textDocument/references",
                    json!({}),
                    Duration::from_secs(5),
                    true,
                )
            })
        };
        let asking_for_hover = {
            let connection = Arc::clone(&connection);
            thread::spawn(move || {
                connection.send_request(
                    "textDocument/hover",
                    json!({}),
                    Duration::from_secs(5),
                    true,
                )
            })
        };

        let mut numbers_by_method = HashMap::new();
        for _ in 0..2 {
            let question = server.read_message();
            let method = question
                .get("method")
                .and_then(Value::as_str)
                .expect("every question names a method")
                .to_string();
            let number = question
                .get("id")
                .and_then(Value::as_i64)
                .expect("every question carries a number");
            numbers_by_method.insert(method, number);
        }
        assert_eq!(
            numbers_by_method.len(),
            2,
            "both questions must be on the wire before either is answered"
        );

        // Answer them in the opposite order on purpose: each waiter must get its own answer.
        server.write_message(&json!({
            "jsonrpc": "2.0",
            "id": numbers_by_method["textDocument/hover"],
            "result": { "answerTo": "textDocument/hover" }
        }));
        server.write_message(&json!({
            "jsonrpc": "2.0",
            "id": numbers_by_method["textDocument/references"],
            "result": { "answerTo": "textDocument/references" }
        }));

        let references = asking_for_references
            .join()
            .expect("the references thread")
            .expect("references should be answered")
            .expect("references should carry a result");
        let hover = asking_for_hover
            .join()
            .expect("the hover thread")
            .expect("hover should be answered")
            .expect("hover should carry a result");

        assert_eq!(
            references.get("answerTo").and_then(Value::as_str),
            Some("textDocument/references")
        );
        assert_eq!(
            hover.get("answerTo").and_then(Value::as_str),
            Some("textDocument/hover")
        );
    }

    #[test]
    fn a_question_the_app_gave_up_on_tells_the_server_to_stop_working_on_it() {
        // Giving up on our side does not stop the server: without this it keeps grinding
        // on a question whose answer nobody will ever read, while everything else queues
        // behind it.
        let (connection, mut server) = connect_to_a_fake_language_server("fake-giving-up");

        let asking = {
            let connection = Arc::clone(&connection);
            thread::spawn(move || {
                connection.send_request(
                    "textDocument/references",
                    json!({}),
                    Duration::from_millis(120),
                    true,
                )
            })
        };

        let question = server.read_message();
        let number = question
            .get("id")
            .and_then(Value::as_i64)
            .expect("the question carries a number");

        let outcome = asking.join().expect("the asking thread");
        assert_eq!(outcome.unwrap_err(), "Language server timed out");

        let withdrawal = server.read_message();
        assert_eq!(
            withdrawal.get("method").and_then(Value::as_str),
            Some("$/cancelRequest"),
            "the app must withdraw the question it gave up on"
        );
        assert_eq!(
            withdrawal.pointer("/params/id").and_then(Value::as_i64),
            Some(number)
        );
    }

    #[test]
    fn a_busy_server_reports_indexing_and_says_so_again_when_it_finishes() {
        let language = "fake-busy-then-ready";
        let seen: Arc<Mutex<Vec<SourceLspStatusChange>>> = Arc::new(Mutex::new(Vec::new()));
        {
            let seen = Arc::clone(&seen);
            set_source_lsp_status_listener(Arc::new(move |change: SourceLspStatusChange| {
                if change.language == language {
                    seen.lock().unwrap().push(change);
                }
            }));
        }

        let (connection, mut server) = connect_to_a_fake_language_server(language);
        let root = Path::new("/tmp/a-fake-project");
        assert_eq!(
            read_language_server_activity(language, root).map(|activity| activity.0),
            Some(LanguageServerState::Ready),
            "a server that has finished the opening exchange is ready until it says otherwise"
        );

        server.write_message(&json!({
            "jsonrpc": "2.0",
            "method": "$/progress",
            "params": {
                "token": "loading-the-solution",
                "value": { "kind": "begin", "title": "Loading the EdiPlatform solution" }
            }
        }));
        assert!(
            eventually(|| read_language_server_activity(language, root)
                == Some((
                    LanguageServerState::Indexing,
                    Some("Loading the EdiPlatform solution".to_string())
                ))),
            "a server that says it started a job is indexing, and says which job"
        );

        server.write_message(&json!({
            "jsonrpc": "2.0",
            "method": "$/progress",
            "params": {
                "token": "loading-the-solution",
                "value": { "kind": "end" }
            }
        }));
        assert!(
            eventually(
                || read_language_server_activity(language, root).map(|activity| activity.0)
                    == Some(LanguageServerState::Ready)
            ),
            "a server with no jobs left is ready again"
        );

        let states = seen
            .lock()
            .unwrap()
            .iter()
            .map(|change| change.state.clone())
            .collect::<Vec<_>>();
        assert_eq!(
            states,
            vec![
                "starting".to_string(),
                "ready".to_string(),
                "indexing".to_string(),
                "ready".to_string()
            ],
            "every move must be announced once, in order"
        );

        clear_source_lsp_status_listener();
        drop(connection);
    }

    #[test]
    fn workspace_statuses_do_not_overwrite_each_other() {
        let language = "fake-workspace-scoped-status";
        let spec = LspServerSpec {
            server_name: "a pretend server",
            language_id: language,
            command: "a-program-that-is-not-installed",
            args: &[],
        };
        let root_a = unique_lsp_temp_root("mcb-lsp-status-root-a");
        let root_b = unique_lsp_temp_root("mcb-lsp-status-root-b");
        let root_a_text = root_a.display().to_string();
        let root_b_text = root_b.display().to_string();

        record_language_server_state(language, &root_a_text, LanguageServerState::Ready, None);
        record_language_server_state(
            language,
            &root_b_text,
            LanguageServerState::Indexing,
            Some("Reading the second project".to_string()),
        );

        assert_eq!(
            describe_language_server_activity(spec, &root_a, false, true, true).0,
            LanguageServerState::Ready,
            "the second workspace must not make a warm first workspace look stopped"
        );
        assert_eq!(
            describe_language_server_activity(spec, &root_b, false, true, true).0,
            LanguageServerState::Indexing,
            "each workspace must keep its own readiness state"
        );
    }

    #[test]
    fn a_status_reading_says_what_the_server_is_doing_in_a_full_sentence() {
        let spec = LspServerSpec {
            server_name: "a pretend server",
            language_id: "fake-status-wording",
            command: "a-program-that-is-not-installed",
            args: &[],
        };
        let root = env::temp_dir();
        // This test is about the wording for a server's own state, so the
        // workspace is in full mode. Read mode has its own sentence and its own
        // test (`a_read_mode_workspace_starts_no_language_server`).
        set_language_intelligence(&root.display().to_string(), true);

        let (state, detail) = describe_language_server_activity(spec, &root, true, true, false);
        assert_eq!(state, LanguageServerState::Disabled);
        assert_eq!(
            detail,
            Some("The a pretend server language server is switched off in Settings.".to_string())
        );

        let (state, detail) = describe_language_server_activity(spec, &root, false, true, false);
        assert_eq!(state, LanguageServerState::NotRunning);
        assert!(
            detail
                .as_deref()
                .is_some_and(|detail| detail.contains("a-program-that-is-not-installed")),
            "a reader must be told which program is missing, not just that something is"
        );

        let (state, detail) = describe_language_server_activity(spec, &root, false, true, true);
        assert_eq!(
            state,
            LanguageServerState::NotRunning,
            "installed but never started is still not running"
        );
        assert!(
            detail
                .as_deref()
                .is_some_and(|detail| detail.contains("has not been started yet")),
            "the sentence must say why nothing is happening yet, it was {detail:?}"
        );
    }

    #[test]
    fn document_symbols_are_flattened_and_counted_from_zero() {
        // The editor draws its margin counts from these, and the editor counts lines from
        // zero, so the answer is handed over already zero-based and already flattened — a
        // method inside a class gets a count of its own.
        let result = json!([
            {
                "name": "OrderService",
                "kind": 5,
                "range": { "start": { "line": 4, "character": 0 }, "end": { "line": 40, "character": 1 } },
                "selectionRange": { "start": { "line": 4, "character": 13 }, "end": { "line": 4, "character": 25 } },
                "children": [
                    {
                        "name": "Send",
                        "kind": 6,
                        "range": { "start": { "line": 9, "character": 4 }, "end": { "line": 20, "character": 5 } },
                        "selectionRange": { "start": { "line": 9, "character": 16 }, "end": { "line": 9, "character": 20 } }
                    }
                ]
            }
        ]);

        let symbols = lsp_document_symbols_from_result(&result, 100);

        assert_eq!(
            symbols,
            vec![
                SourceLspDocumentSymbol {
                    name: "OrderService".to_string(),
                    kind: "class".to_string(),
                    line: 4,
                    character: 13,
                },
                SourceLspDocumentSymbol {
                    name: "Send".to_string(),
                    kind: "method".to_string(),
                    line: 9,
                    character: 16,
                },
            ]
        );
    }

    /// A throwaway workspace for a test, in full mode.
    ///
    /// Full mode is set here rather than in each test because the app's default
    /// is read mode — no server, ever — and a test that means to watch a real
    /// server work has to be a workspace someone switched on. The tests that
    /// assert nothing starts still assert exactly that; they simply prove it
    /// against the harder case where the mode is not what is holding it back.
    fn unique_lsp_temp_root(prefix: &str) -> PathBuf {
        let mut root = env::temp_dir();
        let nonce = std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .unwrap()
            .as_nanos();
        root.push(format!("{prefix}-{nonce}"));
        std::fs::create_dir_all(&root).unwrap();
        set_language_intelligence(&root.display().to_string(), true);
        root
    }

    fn rust_analyzer_ready_for_smoke() -> Result<(), String> {
        let server = resolve_server_for_language("rust")
            .ok_or_else(|| "rust-analyzer not found".to_string())?;
        let output = Command::new(&server.command)
            .arg("--version")
            .output()
            .map_err(|error| format!("rust-analyzer --version failed to start: {error}"))?;
        if output.status.success() {
            return Ok(());
        }

        let stderr = String::from_utf8_lossy(&output.stderr).trim().to_string();
        let stdout = String::from_utf8_lossy(&output.stdout).trim().to_string();
        Err(if stderr.is_empty() { stdout } else { stderr })
    }
}
