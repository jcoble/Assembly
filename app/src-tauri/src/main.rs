//! Heavy Tauri commands are async because synchronous command bodies run on the UI thread.

use std::collections::{HashMap, HashSet};
use std::path::{Path, PathBuf};
use std::process::Command;
use std::sync::{Arc, Mutex};
use std::time::Duration;

use commands::agent_sessions::*;
use commands::application::*;
use files::{lsp, lsp_commands::*};
use workflow::orchestration_commands::*;
use commands::runtime::*;
use files::commands::*;
use git::commands::*;
use commands::usage::*;
use workflow::commands::*;
use workflow::orchestration::import_legacy_orchestration_events;
use tauri::{Emitter, Manager};
use workflow::WorkflowEngine;

mod agent_conversation;
mod bounded_process;
mod browser;
mod claude_quota;
mod commands;
mod debug_log;
mod evidence;
mod git;
mod helper;
mod files;
mod notion_oauth;
mod notion_tasks;
mod product_identity;
mod project_folders;
mod proof_mode;
mod projection_streams;
mod resources;
mod resources_disk;
mod terminal;
mod usage_current;
mod usage_db;
mod usage_history;
mod usage_indexer;
mod usage_remote;
mod usage_sources;
mod workflow;

const DEFAULT_GIT_HISTORY_LIMIT: usize = 24;

/// The most changed files the status sends to the panel. A folder with tens of
/// thousands of untracked files froze the app drawing one row each.
const MAX_GIT_STATUS_FILES: usize = 1000;

#[derive(Debug, serde::Serialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct ProjectGitStatus {
    branch: Option<String>,
    ahead: usize,
    behind: usize,
    has_upstream: bool,
    /// At most `MAX_GIT_STATUS_FILES`, in git's order (tracked before untracked).
    files: Vec<GitFileStatus>,
    /// Every changed file git reported, including those cut from `files`.
    total_files: usize,
    /// Lines added and removed in the working tree against HEAD (tracked files
    /// only); `None` when there is no HEAD to compare with.
    additions: Option<usize>,
    deletions: Option<usize>,
}

#[derive(Debug, serde::Serialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct GitActionResult {
    message: String,
    status: ProjectGitStatus,
}

#[derive(Debug, serde::Serialize)]
#[serde(rename_all = "camelCase")]
struct GitFileStatus {
    relative_path: String,
    index_status: String,
    worktree_status: String,
    status: String,
    badge: String,
}

#[derive(Debug, serde::Serialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct SourceGitDiff {
    relative_path: String,
    status: String,
    diff: String,
    is_binary: bool,
    original_content: Option<String>,
    modified_content: Option<String>,
}

/// Everything on the checked-out branch since it left the repository's default
/// branch, as one diff text; the Changes tab splits it per file.
#[derive(Debug, serde::Serialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct GitBranchDiff {
    /// The branch compared with, e.g. `origin/main`.
    base: String,
    diff: String,
}

/// One file touched by one commit. Same three fields the working-copy status list
/// shows for a file, so a commit's file list and the changed-files list render the
/// same way.
#[derive(Debug, Clone, serde::Serialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub(crate) struct GitCommitFileChange {
    relative_path: String,
    status: String,
    badge: String,
}

#[derive(Debug, Clone, serde::Serialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub(crate) struct ProjectWorktree {
    repo: String,
    path: String,
    branch: String,
    task_id: Option<String>,
    is_dirty: bool,
    has_unmerged_commits: bool,
    is_prunable: bool,
    prunable_reason: Option<String>,
    is_locked: bool,
    locked_reason: Option<String>,
    last_activity: Option<String>,
    delete_eligibility: String,
}

#[derive(Debug, serde::Serialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct ProjectWorktreeActionResult {
    message: String,
    worktrees: Vec<ProjectWorktree>,
}

#[derive(Debug, serde::Serialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct ProjectWorktreeArchiveResult {
    message: String,
    archive_path: String,
    worktrees: Vec<ProjectWorktree>,
}

#[derive(Debug, Clone, serde::Deserialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct RuntimeContextProject {
    id: String,
    name: String,
    path: String,
}

#[derive(Debug, serde::Serialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct RuntimeContext {
    pid: u32,
    command: String,
    port: u16,
    cwd: String,
    #[serde(rename = "projectID")]
    project_id: Option<String>,
    project_name: String,
    root_label: String,
}

#[derive(Debug, Clone, serde::Serialize)]
#[serde(rename_all = "camelCase")]
struct PlaywrightProcessInfo {
    pid: u32,
    pgid: u32,
    command: String,
    name: String,
    label: String,
    elapsed: String,
    args: String,
}

#[derive(Debug, Clone, serde::Serialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct PlaywrightSessionInfo {
    pgid: u32,
    label: String,
    pids: Vec<u32>,
    processes: Vec<PlaywrightProcessInfo>,
}

#[derive(Debug, serde::Serialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct PlaywrightCleanupResult {
    sessions: Vec<PlaywrightSessionInfo>,
    terminated_pgids: Vec<u32>,
    terminated_pids: Vec<u32>,
    failed_pgids: Vec<PlaywrightCleanupFailure>,
}

#[derive(Debug, serde::Serialize)]
#[serde(rename_all = "camelCase")]
struct PlaywrightCleanupFailure {
    pgid: u32,
    pid: Option<u32>,
    message: String,
}

/// What happened when the app was asked to stop one process.
///
/// `message` is a whole sentence for the reader, whichever way it went — asking
/// a process to stop can fail for reasons the reader can act on (it belongs to
/// another user, it already exited) and "failed" on its own tells them nothing.
#[derive(Debug, PartialEq, Eq, serde::Serialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct ProcessKillResult {
    ok: bool,
    message: String,
}

/// What happened when the C# language server was switched off or on.
#[derive(Debug, PartialEq, Eq, serde::Serialize)]
#[serde(rename_all = "camelCase")]
struct CsharpLanguageServerToggleResult {
    enabled: bool,
    stopped_servers: usize,
    message: String,
}

/// What happened when every language server was switched off or on.
#[derive(Debug, PartialEq, Eq, serde::Serialize)]
#[serde(rename_all = "camelCase")]
struct LanguageServersToggleResult {
    enabled: bool,
    stopped_servers: usize,
    message: String,
}

#[derive(Debug, PartialEq, Eq, serde::Serialize)]
#[serde(rename_all = "camelCase")]
struct LanguageServerToggleResult {
    language: String,
    enabled: bool,
    stopped_servers: usize,
    message: String,
}

/// Whether one workspace is in full mode, and what the editor should say.
#[derive(Debug, PartialEq, Eq, serde::Serialize)]
#[serde(rename_all = "camelCase")]
struct WorkspaceLanguageIntelligence {
    root: String,
    enabled: bool,
    /// Language-server processes running for this workspace right now.
    running_servers: usize,
    /// Their process ids, so a reader can find the same numbers in the
    /// resource view.
    server_pids: Vec<u32>,
    stopped_servers: usize,
    message: String,
}

#[derive(Debug, Clone, serde::Serialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub(crate) struct GitRepositorySummary {
    #[serde(rename = "projectID")]
    project_id: String,
    project_name: String,
    repo: String,
    path: String,
    root_label: String,
    branch: String,
    #[serde(rename = "taskID")]
    task_id: Option<String>,
    is_worktree: bool,
    is_dirty: bool,
    staged_count: usize,
    unstaged_count: usize,
    untracked_count: usize,
    dirty_count: usize,
    ahead: usize,
    behind: usize,
    has_upstream: bool,
    last_commit_sha: Option<String>,
    last_commit_subject: Option<String>,
    last_commit_at: Option<String>,
    dirty_since_epoch_ms: Option<u64>,
    dirty_status_fingerprint: String,
    error: Option<String>,
}

#[derive(Debug, Clone, serde::Serialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
struct GitCommitHistoryEntry {
    short_sha: String,
    sha: String,
    subject: String,
    author: String,
    committed_at: String,
    refs: String,
    parent_shas: Vec<String>,
    parent_count: usize,
    #[serde(rename = "taskID")]
    task_id: Option<String>,
    task_source: Option<String>,
}

#[derive(Debug, Clone, serde::Serialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct GitHistoryPage {
    root: String,
    relative_path: Option<String>,
    commits: Vec<GitCommitHistoryEntry>,
    next_cursor: Option<String>,
    complete: bool,
}

#[derive(Debug, Clone, PartialEq, Eq)]
struct ProcessListener {
    pid: u32,
    command: String,
    port: u16,
}

#[derive(Debug, Clone, PartialEq, Eq)]
struct RuntimeContextProjectMatch {
    project_id: Option<String>,
    project_name: String,
    root_label: String,
}

#[derive(Clone, Copy)]
enum SourceFileAction {
    Open,
    Reveal,
}

#[derive(Clone, Copy)]
enum PathAction {
    Open,
    Reveal,
}

#[derive(Debug, PartialEq, Eq)]
struct SourceFileActionCommand {
    program: String,
    args: Vec<String>,
}

const LANGUAGE_SERVER_SETTINGS_KEY: &str = "workbench.languageServers";

fn persist_language_server_settings(
    manager: &agent_conversation::manager::AgentRuntimeManager,
) -> Result<(), String> {
    manager.write_app_setting(
        LANGUAGE_SERVER_SETTINGS_KEY,
        &lsp::language_server_settings_snapshot().to_string(),
    )
}

/// The process ids of the language servers serving one workspace.
fn workspace_language_server_pids(registry: &lsp::SourceLspRegistry, root: &str) -> Vec<u32> {
    registry
        .running_language_server_processes()
        .into_iter()
        .filter(|process| process.root == root)
        .map(|process| process.pid)
        .collect()
}

fn describe_workspace_language_intelligence(
    enabled: bool,
    running_servers: usize,
    stopped_servers: usize,
) -> String {
    if enabled {
        return if running_servers > 0 {
            "Language intelligence is on for this project, and its language server is running."
                .to_string()
        } else {
            "Language intelligence is on for this project. Its language server starts with the next file you open, and takes a moment to read the project."
                .to_string()
        };
    }
    if stopped_servers > 0 {
        return "Language intelligence is off for this project and its language server has been stopped, freeing that memory. Files still open with colouring."
            .to_string();
    }
    "Language intelligence is off for this project. Files open with colouring only, and nothing is started in the background."
        .to_string()
}

/// What the switch says after it has asked for a language server.
///
/// Every branch here is a sentence a reader can act on. The ones where nothing
/// started matter most: those used to be silence, and silence next to a switch
/// that is on reads as broken.
fn describe_language_server_start(start: &Result<lsp::LanguageServerStart, String>) -> String {
    match start {
        Ok(lsp::LanguageServerStart::Running { server_name }) => {
            format!("Language intelligence is on for this project, and {server_name} is running. It takes a moment to read the project.")
        }
        Ok(lsp::LanguageServerStart::NoServerForLanguage) => {
            "Language intelligence is on for this project, but this app has no language server for the file you have open. It keeps colouring and the built-in index."
                .to_string()
        }
        Ok(lsp::LanguageServerStart::NotInstalled {
            server_name,
            command,
        }) => {
            format!("Language intelligence is on for this project, but {server_name} is not installed. Install `{command}` and switch this on again.")
        }
        Ok(lsp::LanguageServerStart::NativeCsharpClient) => {
            "Language intelligence is on for this project. The C# language server starts with the C# file you have open, and takes a minute to read the solution."
                .to_string()
        }
        Ok(lsp::LanguageServerStart::SwitchedOff) => {
            "Language servers are switched off in Settings, so nothing was started. Files open with colouring and the built-in index."
                .to_string()
        }
        Ok(lsp::LanguageServerStart::ServerSwitchedOff) => {
            "This language server is switched off in Settings, so the file stays in Editor-only mode."
                .to_string()
        }
        Ok(lsp::LanguageServerStart::CsharpSwitchedOff) => {
            "Language intelligence is on for this project, but the C# language server is switched off in Settings."
                .to_string()
        }
        Ok(lsp::LanguageServerStart::NoCsharpProject) => {
            "Language intelligence is on for this project, but there is no C# project or solution under it to read."
                .to_string()
        }
        Ok(lsp::LanguageServerStart::ReadMode) => {
            "Language intelligence is off for this project, so no language server was started."
                .to_string()
        }
        Err(error) => {
            format!("Language intelligence is on for this project, but its language server could not be started: {error}")
        }
    }
}

fn describe_language_servers_toggle(
    enabled: bool,
    changed: bool,
    stopped_servers: usize,
) -> String {
    if enabled {
        return if changed {
            "Language servers are back on. One starts the next time you open a file in a project that has language intelligence on."
                .to_string()
        } else {
            "Language servers were already on.".to_string()
        };
    }
    match stopped_servers {
        0 => "Language servers are off. None was running, so nothing had to be stopped. Files still open with colouring and the built-in index."
            .to_string(),
        1 => "Language servers are off and the running one has been stopped. Files still open with colouring and the built-in index."
            .to_string(),
        count => format!(
            "Language servers are off and the {count} running ones have been stopped. Files still open with colouring and the built-in index."
        ),
    }
}

fn describe_csharp_language_server_toggle(
    enabled: bool,
    changed: bool,
    stopped_servers: usize,
) -> String {
    if enabled {
        return if changed {
            "The C# language server is back on. It starts the next time you open a C# file, and takes a minute to read the solution."
                .to_string()
        } else {
            "The C# language server was already on.".to_string()
        };
    }

    if stopped_servers > 0 {
        "The C# language server is off and the running one has been stopped, freeing its memory. Reference counts and project search still work; mistake squiggles and precise go-to-definition do not."
            .to_string()
    } else {
        "The C# language server is off. It was not running, so nothing had to be stopped. Reference counts and project search still work; mistake squiggles and precise go-to-definition do not."
            .to_string()
    }
}

/// What this build of the backend can do, by name.
///
/// The frontend needs this because some additions are new ARGUMENTS on commands that
/// already existed. Tauri quietly drops a payload key a command does not declare, so an
/// older desktop build handed `force: true` runs the ordinary safe removal and reports
/// success — there is no unknown-command error to catch and nothing else to test. Asking
/// for this list first is the only way to know before offering the button.
///
/// Anything added here is a promise: check the name before offering the feature, and treat
/// this command being missing as "none of these are available".
const BACKEND_CAPABILITIES: [&str; 29] = [
    // `remove_project_worktree` accepts `force`.
    "worktreeForceRemove",
    // `kill_playwright_session` stops one process group.
    "playwrightSessionKill",
    // `list_source_lsp_diagnostics_for_root` reads a whole project's diagnostics.
    "lspDiagnosticsForRoot",
    // `start_terminal_session` accepts `command` and exits with its code.
    "terminalCommandSpawn",
    // `count_source_references` counts every requested symbol in one project pass.
    "referenceCounts",
    // `kill_process` stops one process the app is showing, by number.
    "processKill",
    // `remove_project_worktree` on a worktree whose folder is gone clears only that
    // one entry's records, instead of every entry whose folder is gone.
    "worktreePruneSingle",
    // `set_csharp_language_server_enabled` turns the C# language server off and on.
    "csharpLanguageServerToggle",
    // `set_language_servers_enabled` turns every language server off and on.
    "languageServersToggle",
    // `read_workspace_language_intelligence` and
    // `set_workspace_language_intelligence` read and set one project's editor
    // mode: read mode (colouring only, nothing started) or full mode.
    "workspaceLanguageIntelligence",
    // `find_source_lsp_document_symbols` lists a file's symbols using the language
    // server, which is what gives Rust and Svelte margin counts.
    "lspDocumentSymbols",
    // The app sends a `source-lsp-status-changed` event whenever a language server
    // changes what it is doing, so nothing has to ask on a timer.
    "lspStatusEvents",
    // `read_source_lsp_log` hands back what a running language server printed to its
    // own error output.
    "lspLog",
    // A10 resource and space inventory/action commands.
    "resourceSample",
    "resourceSnapshot",
    "resourceDiskScan",
    "resourceDiskCleanup",
    "resourceStopOwned",
    // Provider-authored quota and SQLite usage history queries.
    "providerUsageQuota",
    "usageHistory",
    "usageHistoryIncremental",
    "usageProviderSummary",
    "usageDailyTotals",
    // Source-control agent actions and the GitHub CLI PR lifecycle.
    "generate_commit_message",
    "read_pull_request_context",
    "generate_pull_request_details",
    "create_pull_request",
    "read_pull_request_status",
    // The conversation manager pushes ACP turn and item events while a turn is live.
    "acpLiveConversationEvents",
];

/// The event the app sends whenever a language server changes what it is doing.
const SOURCE_LSP_STATUS_CHANGED_EVENT: &str = "source-lsp-status-changed";
const SESSION_TITLE_CHANGED_EVENT: &str = "session-title-changed";

const AGENT_MESSAGES_CHANGED_EVENT: &str = "agent-messages-changed";

/// The event the app sends when one saved remote machine's transport connects,
/// starts attempting, or stops. The rail's remote rows read nothing else.
const REMOTE_CONNECTION_CHANGED_EVENT: &str = "remote-connection-changed";

/// A session that has just been given a better name than the one taken from
/// its first prompt, so the rail can show it without asking.
#[derive(Clone, serde::Serialize)]
#[serde(rename_all = "camelCase")]
struct SessionTitleChanged {
    owned_id: String,
    title: String,
}

pub(crate) fn backend_capabilities() -> Vec<String> {
    BACKEND_CAPABILITIES
        .iter()
        .map(|capability| capability.to_string())
        .collect()
}

fn run_source_file_action(path: PathBuf, action: SourceFileAction) -> Result<(), String> {
    let command = source_file_action_command(&path, action)?;
    let status = Command::new(&command.program)
        .args(&command.args)
        .status()
        .map_err(|error| format!("Could not run source file action: {error}"))?;

    if status.success() {
        Ok(())
    } else {
        Err(format!("Source file action exited with {status}"))
    }
}

fn run_path_action(path: PathBuf, action: PathAction) -> Result<(), String> {
    let command = path_action_command(&path, action)?;
    let status = Command::new(&command.program)
        .args(&command.args)
        .status()
        .map_err(|error| format!("Could not run path action: {error}"))?;

    if status.success() {
        Ok(())
    } else {
        Err(format!("Path action exited with {status}"))
    }
}

fn run_terminal_path_action(path: PathBuf, terminal: Option<String>) -> Result<(), String> {
    let command = terminal_path_action_command(&path, terminal)?;
    let status = Command::new(&command.program)
        .args(&command.args)
        .status()
        .map_err(|error| format!("Could not run terminal action: {error}"))?;

    if status.success() {
        Ok(())
    } else {
        Err(format!("Terminal action exited with {status}"))
    }
}

fn run_terminal_command_action(
    path: PathBuf,
    command: String,
    terminal: Option<String>,
) -> Result<(), String> {
    let command = terminal_command_action_command(&path, &command, terminal)?;
    let status = Command::new(&command.program)
        .args(&command.args)
        .status()
        .map_err(|error| format!("Could not run terminal command action: {error}"))?;

    if status.success() {
        Ok(())
    } else {
        Err(format!("Terminal command action exited with {status}"))
    }
}

fn source_file_action_command(
    path: &Path,
    action: SourceFileAction,
) -> Result<SourceFileActionCommand, String> {
    let metadata = std::fs::metadata(path)
        .map_err(|error| format!("Could not read {}: {error}", path.display()))?;
    if !metadata.is_file() {
        return Err("Source path is not a file".to_string());
    }

    let path_arg = path.display().to_string();
    let args = match action {
        SourceFileAction::Open => vec![path_arg],
        SourceFileAction::Reveal => vec!["-R".to_string(), path_arg],
    };

    Ok(SourceFileActionCommand {
        program: "open".to_string(),
        args,
    })
}

fn path_action_command(path: &Path, action: PathAction) -> Result<SourceFileActionCommand, String> {
    let metadata = std::fs::metadata(path)
        .map_err(|error| format!("Could not read path metadata: {error}"))?;
    if !metadata.is_file() && !metadata.is_dir() {
        return Err("Path is not a file or directory".to_string());
    }

    let path_arg = path.display().to_string();
    let args = match action {
        PathAction::Open => vec![path_arg],
        PathAction::Reveal => vec!["-R".to_string(), path_arg],
    };

    Ok(SourceFileActionCommand {
        program: "open".to_string(),
        args,
    })
}

fn terminal_path_action_command(
    path: &Path,
    terminal: Option<String>,
) -> Result<SourceFileActionCommand, String> {
    let metadata = std::fs::metadata(path)
        .map_err(|error| format!("Could not read terminal path metadata: {error}"))?;
    if !metadata.is_dir() {
        return Err("Terminal path is not a directory".to_string());
    }

    let terminal_app = normalize_terminal_app(terminal.as_deref())?;
    if terminal_app == "Warp" {
        return Ok(SourceFileActionCommand {
            program: "open".to_string(),
            args: vec![format!(
                "warp://action/new_tab?path={}",
                uri_query_encode(&path.display().to_string())
            )],
        });
    }

    Ok(SourceFileActionCommand {
        program: "open".to_string(),
        args: vec!["-a".to_string(), terminal_app, path.display().to_string()],
    })
}

fn terminal_command_action_command(
    path: &Path,
    command: &str,
    terminal: Option<String>,
) -> Result<SourceFileActionCommand, String> {
    let metadata = std::fs::metadata(path)
        .map_err(|error| format!("Could not read terminal path metadata: {error}"))?;
    if !metadata.is_dir() {
        return Err("Terminal path is not a directory".to_string());
    }

    let command = normalize_terminal_command(command)?;
    let terminal_app = normalize_terminal_app(terminal.as_deref())?;
    let shell_command = format!(
        "cd {} && {}",
        shell_quote(&path.display().to_string()),
        command
    );
    let escaped_shell_command = applescript_string_escape(&shell_command);
    let script = match terminal_app.as_str() {
        "Terminal" => format!(
            "tell application \"Terminal\"\nactivate\ndo script \"{}\"\nend tell",
            escaped_shell_command
        ),
        "iTerm" | "iTerm2" => format!(
            "tell application \"{}\"\nactivate\ncreate window with default profile\ntell current session of current window\nwrite text \"{}\"\nend tell\nend tell",
            terminal_app,
            escaped_shell_command
        ),
        _ => {
            return Err(format!(
                "Terminal command execution is not supported for {terminal_app}"
            ))
        }
    };

    Ok(SourceFileActionCommand {
        program: "osascript".to_string(),
        args: vec!["-e".to_string(), script],
    })
}

fn normalize_terminal_app(terminal: Option<&str>) -> Result<String, String> {
    let requested = terminal
        .map(str::trim)
        .filter(|value| !value.is_empty())
        .unwrap_or("Terminal");
    let allowlisted = [
        ("terminal", "Terminal"),
        ("terminal.app", "Terminal"),
        ("warp", "Warp"),
        ("warp.app", "Warp"),
        ("iterm", "iTerm"),
        ("iterm.app", "iTerm"),
        ("iterm2", "iTerm2"),
        ("iterm2.app", "iTerm2"),
        ("wezterm", "WezTerm"),
        ("wezterm.app", "WezTerm"),
        ("ghostty", "Ghostty"),
        ("ghostty.app", "Ghostty"),
        ("alacritty", "Alacritty"),
        ("alacritty.app", "Alacritty"),
    ];

    allowlisted
        .iter()
        .find(|(alias, _)| alias.eq_ignore_ascii_case(requested))
        .map(|(_, app)| (*app).to_string())
        .ok_or_else(|| format!("Unsupported terminal app: {requested}"))
}

fn normalize_terminal_command(command: &str) -> Result<String, String> {
    let command = command.trim();
    if command.is_empty() {
        return Err("Terminal command is empty".to_string());
    }
    if command.contains('\0') || command.contains('\n') || command.contains('\r') {
        return Err("Terminal command must be a single line".to_string());
    }

    Ok(command.to_string())
}

fn shell_quote(value: &str) -> String {
    if value.is_empty() {
        return "''".to_string();
    }
    format!("'{}'", value.replace('\'', "'\\''"))
}

fn uri_query_encode(value: &str) -> String {
    let mut encoded = String::new();
    for byte in value.bytes() {
        match byte {
            b'A'..=b'Z' | b'a'..=b'z' | b'0'..=b'9' | b'-' | b'_' | b'.' | b'~' | b'/' => {
                encoded.push(byte as char)
            }
            _ => encoded.push_str(&format!("%{byte:02X}")),
        }
    }
    encoded
}

fn applescript_string_escape(value: &str) -> String {
    value.replace('\\', "\\\\").replace('"', "\\\"")
}

pub(crate) fn project_git_status_sync(root: PathBuf) -> Result<ProjectGitStatus, String> {
    validate_git_root(&root)?;

    let root_arg = root.display().to_string();
    let output = bounded_process::output(
        Command::new("git").args([
            "-C",
            root_arg.as_str(),
            "status",
            "--porcelain=v1",
            "--branch",
            "--untracked-files=all",
        ]),
        "git status",
        bounded_process::LOCAL_COMMAND_TIMEOUT,
    )
    .map_err(|error| format!("Could not run git status: {error}"))?;

    if !output.status.success() {
        let stderr = String::from_utf8_lossy(&output.stderr).trim().to_string();
        return Err(if stderr.is_empty() {
            format!("git status exited with {}", output.status)
        } else {
            stderr
        });
    }

    let mut status = parse_project_git_status(&String::from_utf8_lossy(&output.stdout))?;

    // A repository with no commits has no HEAD; its counts stay absent.
    let numstat = bounded_process::output(
        Command::new("git").args(["-C", root_arg.as_str(), "diff", "--numstat", "HEAD"]),
        "git diff --numstat",
        bounded_process::LOCAL_COMMAND_TIMEOUT,
    );
    if let Some(numstat) = numstat.ok().filter(|numstat| numstat.status.success()) {
        let (additions, deletions) =
            parse_git_numstat_totals(&String::from_utf8_lossy(&numstat.stdout));
        status.additions = Some(additions);
        status.deletions = Some(deletions);
    }
    Ok(status)
}

/// Sums `git diff --numstat` lines. Binary files report `-` and are skipped.
fn parse_git_numstat_totals(output: &str) -> (usize, usize) {
    output.lines().fold((0, 0), |(added, removed), line| {
        let mut fields = line.split('\t');
        match (
            fields.next().and_then(|value| value.parse::<usize>().ok()),
            fields.next().and_then(|value| value.parse::<usize>().ok()),
        ) {
            (Some(plus), Some(minus)) => (added + plus, removed + minus),
            _ => (added, removed),
        }
    })
}

fn git_history_cursor_offset(cursor: Option<String>) -> Result<usize, String> {
    let Some(cursor) = cursor else {
        return Ok(0);
    };
    let trimmed = cursor.trim();
    if trimmed.is_empty() {
        return Ok(0);
    }
    trimmed
        .parse::<usize>()
        .map_err(|_| "Git history cursor was not recognized".to_string())
}

pub(crate) fn read_git_commit_history_page_sync(
    root: PathBuf,
    cursor: Option<String>,
    relative_path: Option<String>,
) -> Result<GitHistoryPage, String> {
    validate_git_root(&root)?;

    let page_root = root.to_string_lossy().into_owned();
    let page_relative_path = relative_path.clone();
    let offset = git_history_cursor_offset(cursor)?;
    let page_size = DEFAULT_GIT_HISTORY_LIMIT;
    let limit_arg = format!("-n{}", page_size + 1);
    let skip_arg = format!("--skip={offset}");
    let base_args = [
        "log",
        "--decorate=short",
        "--date=iso-strict",
        "--format=%h%x1f%H%x1f%s%x1f%an%x1f%cI%x1f%D%x1f%P",
        skip_arg.as_str(),
        limit_arg.as_str(),
    ];
    let history_output = if let Some(relative_path) = relative_path {
        let paths = validate_git_relative_paths(&[relative_path])?;
        let mut file_args = base_args.to_vec();
        file_args.push("--follow");
        run_git_with_paths(&root, &file_args, &paths)
    } else {
        run_git_text(&root, &base_args)
    };

    match history_output {
        Ok(output) => {
            let mut commits = parse_git_commit_history(&output)?;
            let complete = commits.len() <= page_size;
            if !complete {
                commits.truncate(page_size);
            }
            Ok(GitHistoryPage {
                root: page_root,
                relative_path: page_relative_path,
                commits,
                next_cursor: if complete {
                    None
                } else {
                    Some((offset + page_size).to_string())
                },
                complete,
            })
        }
        Err(error) if error.contains("does not have any commits") => Ok(GitHistoryPage {
            root: page_root,
            relative_path: page_relative_path,
            commits: Vec::new(),
            next_cursor: None,
            complete: true,
        }),
        Err(error) => Err(error),
    }
}

pub(crate) fn read_git_commit_files_sync(
    root: PathBuf,
    sha: String,
) -> Result<Vec<GitCommitFileChange>, String> {
    validate_git_root(&root)?;
    let sha = validate_git_commit_id(&sha)?;

    let output = run_git_text(&root, &["show", "--name-status", "--format=", sha.as_str()])?;
    Ok(parse_git_commit_file_changes(&output))
}

pub(crate) fn read_git_commit_file_diff_sync(
    root: PathBuf,
    sha: String,
    relative_path: String,
) -> Result<SourceGitDiff, String> {
    validate_git_root(&root)?;
    let relative_path = validate_git_relative_paths(&[relative_path])?
        .into_iter()
        .next()
        .expect("one validated path");
    let sha = validate_git_commit_id(&sha)?;

    let name_status = run_git_text(
        &root,
        &[
            "show",
            "--name-status",
            "--format=",
            sha.as_str(),
            "--",
            relative_path.as_str(),
        ],
    )?;
    let status = parse_git_commit_file_changes(&name_status)
        .into_iter()
        .next()
        .map(|change| change.status)
        .unwrap_or_default();

    // `--format=` drops the commit header, so what comes back is only the patch —
    // the same text shape the working-copy diff returns, readable by the same parser.
    let diff = run_git_text(
        &root,
        &[
            "show",
            "--no-ext-diff",
            "--format=",
            sha.as_str(),
            "--",
            relative_path.as_str(),
        ],
    )?;
    let is_binary = diff.contains("Binary files ") || diff.contains("GIT binary patch");
    let models = if is_binary {
        git::diff_models::GitDiffModels {
            original_content: None,
            modified_content: None,
        }
    } else {
        git::diff_models::commit_models(&root, &sha, &relative_path)?
    };
    let status = if status.is_empty() && !diff.is_empty() {
        "modified".to_string()
    } else if status.is_empty() {
        "clean".to_string()
    } else {
        status
    };

    Ok(SourceGitDiff {
        relative_path,
        status,
        diff,
        is_binary,
        original_content: models.original_content,
        modified_content: models.modified_content,
    })
}

/// `git show --name-status --format=` prints one tab-separated line per file:
/// a status letter (renames and copies add a similarity number, and a second path)
/// then the path. A merge commit prints nothing here, which reads as an empty list.
fn parse_git_commit_file_changes(output: &str) -> Vec<GitCommitFileChange> {
    output
        .lines()
        .filter(|line| !line.trim().is_empty())
        .filter_map(|line| {
            let mut fields = line.split('\t');
            let status_field = fields.next()?.trim();
            let status_code = status_field.chars().next()?;
            // For a rename or a copy git prints the old path then the new one; the
            // file lives at the last path, so that is the one we report.
            let relative_path = fields.last().map(str::trim).unwrap_or_default();
            if relative_path.is_empty() {
                return None;
            }

            Some(GitCommitFileChange {
                relative_path: normalize_git_status_path(relative_path),
                status: git_status_name(status_code).to_string(),
                badge: git_status_badge(status_code, ' ').to_string(),
            })
        })
        .collect()
}

/// Commit ids reach git as a bare argument, so anything that could be read as an
/// option (or as a shell-ish path) is refused instead of forwarded.
fn validate_git_commit_id(sha: &str) -> Result<String, String> {
    let trimmed = sha.trim();
    let is_safe = !trimmed.is_empty()
        && !trimmed.starts_with('-')
        && trimmed.len() <= 200
        && trimmed.chars().all(|character| {
            character.is_ascii_alphanumeric()
                || matches!(character, '_' | '-' | '.' | '/' | '^' | '~')
        });

    if is_safe {
        Ok(trimmed.to_string())
    } else {
        Err("Git commit id must be a plain commit id or ref name".to_string())
    }
}

fn validate_git_root(root: &Path) -> Result<(), String> {
    let metadata = std::fs::metadata(root)
        .map_err(|error| format!("Could not read Git root metadata: {error}"))?;
    if metadata.is_dir() {
        Ok(())
    } else {
        Err("Git root is not a directory".to_string())
    }
}

fn validate_git_relative_paths(paths: &[String]) -> Result<Vec<String>, String> {
    if paths.is_empty() {
        return Err("At least one relative repo path is required".to_string());
    }

    let mut validated_paths = Vec::with_capacity(paths.len());
    for path in paths {
        let trimmed = path.trim();
        let relative_path = Path::new(trimmed);
        let is_safe_relative_path = !trimmed.is_empty()
            && !trimmed.contains('\0')
            && !relative_path.is_absolute()
            && relative_path
                .components()
                .all(|component| matches!(component, std::path::Component::Normal(_)));

        if !is_safe_relative_path {
            return Err("Git actions only accept relative repo paths".to_string());
        }

        validated_paths.push(trimmed.to_string());
    }

    Ok(validated_paths)
}

fn run_git_with_paths(root: &Path, args: &[&str], paths: &[String]) -> Result<String, String> {
    let mut git_args = args.to_vec();
    git_args.push("--");
    for path in paths {
        git_args.push(path.as_str());
    }

    run_git_text(root, &git_args)
}

/// Committed and uncommitted tracked changes on this branch: one `git diff`
/// from the merge base of HEAD and `origin/HEAD` to the working tree. The app
/// does not record which branch a worktree was cut from, so the repository's
/// default branch is the base.
pub(crate) fn read_git_branch_diff_sync(root: PathBuf) -> Result<GitBranchDiff, String> {
    validate_git_root(&root)?;
    let base = run_git_text(&root, &["rev-parse", "--abbrev-ref", "origin/HEAD"])
        .map_err(|_| "This repository has no origin/HEAD, so there is no default branch to compare with.".to_string())?
        .trim()
        .to_string();
    let merge_base = run_git_text(&root, &["merge-base", "HEAD", base.as_str()])?
        .trim()
        .to_string();
    let diff = run_git_text(
        &root,
        &["-c", "core.quotepath=false", "diff", "--no-ext-diff", merge_base.as_str()],
    )?;
    Ok(GitBranchDiff { base, diff })
}

pub(crate) fn read_source_git_diff_sync(
    root: PathBuf,
    path: PathBuf,
) -> Result<SourceGitDiff, String> {
    validate_git_root(&root)?;

    let canonical_root = std::fs::canonicalize(&root)
        .map_err(|error| format!("Could not resolve Git root: {error}"))?;
    let (relative_path, worktree_path) = match std::fs::metadata(&path) {
        Ok(metadata) => {
            if !metadata.is_file() {
                return Err("Source path is not a file".to_string());
            }
            let canonical_path = std::fs::canonicalize(&path)
                .map_err(|error| format!("Could not resolve source path: {error}"))?;
            let root_string = normalized_path_string(&canonical_root);
            let path_string = normalized_path_string(&canonical_path);
            if !path_is_within(&path_string, &root_string) {
                return Err("Source path is outside Git root".to_string());
            }
            let relative_path = canonical_path
                .strip_prefix(&canonical_root)
                .map_err(|error| format!("Could not derive source relative path: {error}"))?
                .to_string_lossy()
                .replace('\\', "/");
            (relative_path, canonical_path)
        }
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => {
            let relative_path = path
                .strip_prefix(&root)
                .or_else(|_| path.strip_prefix(&canonical_root))
                .map_err(|_| "Source path is outside Git root".to_string())?
                .to_string_lossy()
                .replace('\\', "/");
            if !is_safe_git_relative_path(&relative_path) {
                return Err("Source path is outside Git root".to_string());
            }
            let worktree_path = canonical_root.join(&relative_path);
            (relative_path, worktree_path)
        }
        Err(error) => return Err(format!("Could not read source path metadata: {error}")),
    };
    let status = read_source_git_status(&canonical_root, &relative_path)?;
    let staged_diff = run_git_text(
        &canonical_root,
        &[
            "diff",
            "--no-ext-diff",
            "--cached",
            "--",
            relative_path.as_str(),
        ],
    )?;
    let working_diff = run_git_text(
        &canonical_root,
        &["diff", "--no-ext-diff", "--", relative_path.as_str()],
    )?;
    let diff = combine_source_git_diffs(&staged_diff, &working_diff);
    let is_binary = diff.contains("Binary files ") || diff.contains("GIT binary patch");
    let models = if is_binary {
        git::diff_models::GitDiffModels {
            original_content: None,
            modified_content: None,
        }
    } else {
        git::diff_models::working_tree_models(&canonical_root, &relative_path, &worktree_path)?
    };
    let status = if status.is_empty() && !diff.is_empty() {
        "modified".to_string()
    } else if status.is_empty() {
        "clean".to_string()
    } else {
        status
    };

    Ok(SourceGitDiff {
        relative_path,
        status,
        diff,
        is_binary,
        original_content: models.original_content,
        modified_content: models.modified_content,
    })
}

fn read_source_git_status(root: &Path, relative_path: &str) -> Result<String, String> {
    let output = run_git_text(
        root,
        &[
            "status",
            "--porcelain=v1",
            "--untracked-files=normal",
            "--",
            relative_path,
        ],
    )?;
    Ok(parse_project_git_status(&output)?
        .files
        .into_iter()
        .next()
        .map(|file| file.status)
        .unwrap_or_default())
}

fn run_git_text(root: &Path, args: &[&str]) -> Result<String, String> {
    let root_arg = root.display().to_string();
    let timeout = if args
        .first()
        .is_some_and(|arg| matches!(*arg, "fetch" | "pull" | "push" | "clone"))
    {
        bounded_process::NETWORK_COMMAND_TIMEOUT
    } else {
        bounded_process::LOCAL_COMMAND_TIMEOUT
    };
    let output = bounded_process::output(
        Command::new("git").arg("-C").arg(root_arg).args(args),
        &format!("git {}", args.join(" ")),
        timeout,
    )
    .map_err(|error| format!("Could not run git {}: {error}", args.join(" ")))?;

    if !output.status.success() {
        let stderr = String::from_utf8_lossy(&output.stderr).trim().to_string();
        return Err(if stderr.is_empty() {
            format!("git {} exited with {}", args.join(" "), output.status)
        } else {
            stderr
        });
    }

    Ok(String::from_utf8_lossy(&output.stdout).to_string())
}

fn combine_source_git_diffs(staged_diff: &str, working_diff: &str) -> String {
    let staged_diff = staged_diff.trim_end();
    let working_diff = working_diff.trim_end();

    match (staged_diff.is_empty(), working_diff.is_empty()) {
        (true, true) => String::new(),
        (false, true) => staged_diff.to_string(),
        (true, false) => working_diff.to_string(),
        (false, false) => {
            format!("## Staged\n{staged_diff}\n\n## Working tree\n{working_diff}")
        }
    }
}

pub(crate) fn list_project_worktrees_sync(root: PathBuf) -> Result<Vec<ProjectWorktree>, String> {
    let metadata = std::fs::metadata(&root)
        .map_err(|error| format!("Could not read worktree root metadata: {error}"))?;
    if !metadata.is_dir() {
        return Err("Worktree root is not a directory".to_string());
    }

    let root_arg = root.display().to_string();
    let output = Command::new("git")
        .args(["-C", root_arg.as_str(), "worktree", "list", "--porcelain"])
        .output()
        .map_err(|error| format!("Could not run git worktree list: {error}"))?;

    if !output.status.success() {
        let stderr = String::from_utf8_lossy(&output.stderr).trim().to_string();
        return Err(if stderr.is_empty() {
            format!("git worktree list exited with {}", output.status)
        } else {
            stderr
        });
    }

    let repo = path_last_segment(&root).unwrap_or_else(|| "repo".to_string());
    Ok(
        parse_project_worktree_porcelain(&String::from_utf8_lossy(&output.stdout))
            .into_iter()
            .map(|mut record| {
                record.repo = repo.clone();
                if !record.is_prunable {
                    record.is_dirty = project_worktree_is_dirty(&record.path);
                    record.has_unmerged_commits =
                        project_worktree_has_unmerged_commits(&record.path);
                    record.last_activity = project_worktree_last_activity(&record.path);
                }
                record.delete_eligibility = project_worktree_delete_eligibility(
                    record.is_dirty,
                    record.has_unmerged_commits,
                    record.is_prunable,
                    record.is_locked,
                );
                record
            })
            .collect(),
    )
}

/// `force = false` is the everyday remove: it refuses anything that would lose work.
/// `force = true` is the "I know, delete it anyway" remove: it unlocks the worktree if
/// it is locked, deletes it even when files are uncommitted or commits are unmerged, and
/// reports in the returned message exactly what went away. Neither mode will ever touch
/// the primary checkout.
pub(crate) fn remove_project_worktree_sync(
    root: PathBuf,
    path: PathBuf,
    force: bool,
) -> Result<ProjectWorktreeActionResult, String> {
    validate_git_root(&root)?;

    let root_metadata = std::fs::metadata(&root)
        .map_err(|error| format!("Could not read worktree root metadata: {error}"))?;
    if !root_metadata.is_dir() {
        return Err("Worktree root is not a directory".to_string());
    }

    let canonical_root = std::fs::canonicalize(&root)
        .map_err(|error| format!("Could not resolve Git root: {error}"))?;
    let canonical_path = std::fs::canonicalize(&path).ok();
    if canonical_path
        .as_ref()
        .map(|path| normalized_path_string(&canonical_root) == normalized_path_string(path))
        .unwrap_or(false)
    {
        return Err("Refusing to remove the primary checkout".to_string());
    }

    let worktrees = list_project_worktrees_sync(root.clone())?;
    let worktree = worktrees
        .iter()
        .find(|worktree| {
            project_worktree_path_matches(&worktree.path, &path, canonical_path.as_deref())
        })
        .ok_or_else(|| "Worktree path is not registered for this repository".to_string())?;

    // Comparing the path against the root that was passed only catches "remove the repo you
    // asked through". Ask through a linked worktree FOR the primary checkout and those two
    // paths differ, so that check waves it through. `git worktree list --porcelain` always
    // prints the main working tree first, so that is the answer that actually holds.
    if worktrees
        .first()
        .is_some_and(|main_working_tree| main_working_tree.path == worktree.path)
    {
        return Err("Refusing to remove the primary checkout".to_string());
    }

    if !force {
        if worktree.is_locked {
            return Err("Refusing to remove locked worktree".to_string());
        }
        if worktree.is_dirty {
            return Err("Refusing to remove dirty worktree".to_string());
        }
        if worktree.has_unmerged_commits {
            return Err("Refusing to remove worktree with unmerged commits".to_string());
        }
    }
    if worktree.is_prunable {
        // Only this row. `git worktree prune` would clear the records of EVERY
        // worktree whose folder is gone, and it did: a reader who clicked one
        // stale row watched two of them disappear. Git documents removing a
        // single `.git/worktrees/<name>` folder by hand as the supported way to
        // forget one worktree, which is exactly the promise the button makes.
        let branch = worktree.branch.clone();
        let cleared = clear_one_worktree_record(&root, &worktree.path)?;
        return Ok(ProjectWorktreeActionResult {
            message: if cleared {
                format!(
                    "Cleared git's records for {branch}. Its folder was already gone, and nothing on disk was touched."
                )
            } else {
                format!("Git had no records left for {branch}, so there was nothing to clear.")
            },
            worktrees: list_project_worktrees_sync(root)?,
        });
    }

    let path_metadata = std::fs::metadata(&path)
        .map_err(|error| format!("Could not read worktree path metadata: {error}"))?;
    if !path_metadata.is_dir() {
        return Err("Worktree path is not a directory".to_string());
    }

    let worktree_path = worktree.path.clone();

    if force {
        // Count what is about to be destroyed BEFORE destroying it, so the message can
        // name it. After the remove there is nothing left to look at.
        let dirty_file_count = project_worktree_dirty_file_count(&worktree_path);
        let was_dirty = worktree.is_dirty;
        let had_unmerged_commits = worktree.has_unmerged_commits;
        let unlocked_reason = worktree.is_locked.then(|| {
            worktree
                .locked_reason
                .clone()
                .unwrap_or_default()
                .trim()
                .to_string()
        });

        if unlocked_reason.is_some() {
            run_git_text(&root, &["worktree", "unlock", worktree_path.as_str()])?;
        }
        run_git_text(
            &root,
            &["worktree", "remove", "--force", worktree_path.as_str()],
        )?;
        run_git_text(&root, &["worktree", "prune"])?;

        return Ok(ProjectWorktreeActionResult {
            message: describe_forced_worktree_removal(
                &worktree.branch,
                dirty_file_count,
                was_dirty,
                had_unmerged_commits,
                unlocked_reason.as_deref(),
            ),
            worktrees: list_project_worktrees_sync(root)?,
        });
    }

    run_git_text(&root, &["worktree", "remove", worktree_path.as_str()])?;
    run_git_text(&root, &["worktree", "prune"])?;

    Ok(ProjectWorktreeActionResult {
        message: format!("Removed worktree {}", worktree.branch),
        worktrees: list_project_worktrees_sync(root)?,
    })
}

/// Forget ONE worktree, leaving every other worktree's records alone.
///
/// Git keeps a small folder of records per linked worktree under the
/// repository's `worktrees` directory. The folder is named after the worktree
/// but not reliably so — two worktrees whose folders share a name get suffixed
/// names — so this does not guess from the name. Each record folder holds a
/// `gitdir` file naming the worktree it belongs to, and that is what is matched.
///
/// Returns whether a record folder was actually found and removed.
fn clear_one_worktree_record(root: &Path, worktree_path: &str) -> Result<bool, String> {
    let records_directory = git_worktree_records_directory(root)?;
    let Ok(entries) = std::fs::read_dir(&records_directory) else {
        return Ok(false);
    };

    for entry in entries.flatten() {
        let record_directory = entry.path();
        if !record_directory.is_dir() {
            continue;
        }
        let Ok(gitdir) = std::fs::read_to_string(record_directory.join("gitdir")) else {
            continue;
        };
        if worktree_record_gitdir_names(gitdir.trim()) != Some(worktree_path.to_string()) {
            continue;
        }

        std::fs::remove_dir_all(&record_directory).map_err(|error| {
            format!(
                "Could not clear git's records for this worktree: {error} ({})",
                record_directory.display()
            )
        })?;
        return Ok(true);
    }

    Ok(false)
}

/// Where this repository keeps its per-worktree record folders.
///
/// Asked of git rather than assumed to be `<root>/.git/worktrees`: the answer
/// may be relative to the root, and in a repository that is itself a linked
/// worktree the records live in the primary checkout, not next door.
fn git_worktree_records_directory(root: &Path) -> Result<PathBuf, String> {
    let common_directory = run_git_text(root, &["rev-parse", "--git-common-dir"])?
        .trim()
        .to_string();
    if common_directory.is_empty() {
        return Err("Could not find where this repository keeps its records".to_string());
    }

    let common_directory = PathBuf::from(&common_directory);
    let common_directory = if common_directory.is_absolute() {
        common_directory
    } else {
        root.join(common_directory)
    };

    Ok(common_directory.join("worktrees"))
}

/// Which worktree a record folder's `gitdir` file points at.
///
/// The file names the worktree's own `.git` file — `/path/to/worktree/.git` —
/// so the worktree is its parent folder. The folder is normally gone by the
/// time this is asked, which is why this is pure text and never touches disk.
fn worktree_record_gitdir_names(gitdir: &str) -> Option<String> {
    let gitdir = gitdir.trim();
    if gitdir.is_empty() {
        return None;
    }
    Path::new(gitdir).parent().map(normalized_path_string)
}

pub(crate) fn archive_project_worktree_sync(
    root: PathBuf,
    path: PathBuf,
) -> Result<ProjectWorktreeArchiveResult, String> {
    validate_git_root(&root)?;

    let root_metadata = std::fs::metadata(&root)
        .map_err(|error| format!("Could not read worktree root metadata: {error}"))?;
    if !root_metadata.is_dir() {
        return Err("Worktree root is not a directory".to_string());
    }

    let path_metadata = std::fs::metadata(&path)
        .map_err(|error| format!("Could not read worktree path metadata: {error}"))?;
    if !path_metadata.is_dir() {
        return Err("Worktree path is not a directory".to_string());
    }

    let canonical_root = std::fs::canonicalize(&root)
        .map_err(|error| format!("Could not resolve Git root: {error}"))?;
    let canonical_path = std::fs::canonicalize(&path)
        .map_err(|error| format!("Could not resolve worktree path: {error}"))?;
    if normalized_path_string(&canonical_root) == normalized_path_string(&canonical_path) {
        return Err("Refusing to archive the primary checkout as a removable worktree".to_string());
    }

    let worktrees = list_project_worktrees_sync(root.clone())?;
    let worktree = worktrees
        .iter()
        .find(|worktree| {
            project_worktree_path_matches(&worktree.path, &path, Some(&canonical_path))
        })
        .ok_or_else(|| "Worktree path is not registered for this repository".to_string())?;
    let archive_path = project_worktree_archive_path(worktree)?;
    std::fs::create_dir_all(&archive_path)
        .map_err(|error| format!("Could not create worktree archive directory: {error}"))?;

    write_git_command_output(
        &canonical_path,
        &["status", "--short", "--branch"],
        &archive_path.join("status.txt"),
    )?;
    write_git_command_output(
        &canonical_path,
        &["log", "--oneline", "--decorate", "--max-count=40"],
        &archive_path.join("commits.txt"),
    )?;
    write_git_command_output(
        &canonical_path,
        &["diff", "--binary"],
        &archive_path.join("unstaged.patch"),
    )?;
    write_git_command_output(
        &canonical_path,
        &["diff", "--cached", "--binary"],
        &archive_path.join("staged.patch"),
    )?;

    let untracked_paths = git_untracked_paths(&canonical_path)?;
    std::fs::write(
        archive_path.join("untracked.txt"),
        format!("{}\n", untracked_paths.join("\n")),
    )
    .map_err(|error| format!("Could not write untracked file list: {error}"))?;
    copy_untracked_worktree_files(
        &canonical_path,
        &archive_path.join("untracked"),
        &untracked_paths,
    )?;

    let bundle_path = archive_path.join("head.bundle");
    run_git_text(
        &canonical_path,
        &[
            "bundle",
            "create",
            bundle_path.to_str().unwrap_or_default(),
            "HEAD",
        ],
    )?;

    Ok(ProjectWorktreeArchiveResult {
        message: format!("Archived worktree {}", worktree.branch),
        archive_path: normalized_path_string(&archive_path),
        worktrees: list_project_worktrees_sync(root)?,
    })
}

fn project_worktree_path_matches(
    record_path: &str,
    target_path: &Path,
    canonical_target: Option<&Path>,
) -> bool {
    let normalized_target = normalized_path_string(target_path);
    let normalized_record = normalized_path_string(Path::new(record_path));
    if normalized_record == normalized_target {
        return true;
    }

    let Some(canonical_target) = canonical_target else {
        return false;
    };
    let normalized_canonical_target = normalized_path_string(canonical_target);
    if normalized_record == normalized_canonical_target {
        return true;
    }

    std::fs::canonicalize(record_path)
        .ok()
        .map(|canonical_record| {
            normalized_path_string(&canonical_record) == normalized_canonical_target
        })
        .unwrap_or(false)
}

fn parse_project_worktree_porcelain(output: &str) -> Vec<ProjectWorktree> {
    output
        .split("\n\n")
        .filter_map(|block| {
            let mut path = None;
            let mut branch = None;
            let mut is_prunable = false;
            let mut prunable_reason = None;
            let mut is_locked = false;
            let mut locked_reason = None;

            for line in block.lines() {
                if let Some(value) = line.strip_prefix("worktree ") {
                    path = Some(value.to_string());
                } else if let Some(value) = line.strip_prefix("branch ") {
                    branch = Some(value.trim_start_matches("refs/heads/").to_string());
                } else if line == "detached" {
                    branch = Some("detached".to_string());
                } else if line == "prunable" {
                    is_prunable = true;
                } else if let Some(value) = line.strip_prefix("prunable ") {
                    is_prunable = true;
                    prunable_reason = Some(value.to_string());
                } else if line == "locked" {
                    is_locked = true;
                } else if let Some(value) = line.strip_prefix("locked ") {
                    is_locked = true;
                    locked_reason = Some(value.to_string());
                }
            }

            path.map(|path| {
                let branch = branch.unwrap_or_else(|| "unknown".to_string());
                let task_id = branch_task_id(&branch);
                ProjectWorktree {
                    repo: String::new(),
                    path,
                    branch,
                    task_id,
                    is_dirty: false,
                    has_unmerged_commits: false,
                    is_prunable,
                    prunable_reason,
                    is_locked,
                    locked_reason,
                    last_activity: None,
                    delete_eligibility: "unknown".to_string(),
                }
            })
        })
        .collect()
}

fn project_worktree_delete_eligibility(
    is_dirty: bool,
    has_unmerged_commits: bool,
    is_prunable: bool,
    is_locked: bool,
) -> String {
    if is_dirty {
        "blocked: dirty worktree".to_string()
    } else if has_unmerged_commits {
        "blocked: unmerged commits".to_string()
    } else if is_locked {
        "blocked: locked worktree".to_string()
    } else if is_prunable {
        "review: prunable missing worktree metadata".to_string()
    } else {
        "requires-confirmation".to_string()
    }
}

/// Plain sentences describing what a forced removal actually threw away, so the person
/// who clicked the button can read the consequence rather than decode a status code.
/// `dirty_file_count` is `None` when git could not be asked how much was there. That is NOT
/// the same as zero, and it must never be reported as "nothing was lost" — the removal has
/// already happened by the time this runs. `was_dirty` is what the worktree listing said a
/// moment earlier, and it is believed over a count of zero when the two disagree.
fn describe_forced_worktree_removal(
    branch: &str,
    dirty_file_count: Option<usize>,
    was_dirty: bool,
    had_unpushed_commits: bool,
    unlocked_reason: Option<&str>,
) -> String {
    let mut message = format!("Force removed worktree {branch}.");

    match dirty_file_count {
        Some(count) if count > 0 => {
            let noun = if count == 1 { "file" } else { "files" };
            message.push_str(&format!(
                " Deleted {count} {noun} with changes that were never committed."
            ));
        }
        // Counted zero, but the listing had already seen changes — the count is the one
        // that is wrong, so say what is known and admit the number is not.
        _ if was_dirty => {
            message.push_str(
                " Deleted files with changes that were never committed; they could not be counted before the removal.",
            );
        }
        Some(_) => {
            message.push_str(" It had no uncommitted changes.");
        }
        // Nothing could be read at all, so promising a clean worktree would be a guess.
        None => {
            message.push_str(
                " Could not check for uncommitted changes before the removal, so anything not committed is gone.",
            );
        }
    }

    // The check behind this is "does this branch have commits that are on no remote", so
    // the sentence says that and nothing wider.
    if had_unpushed_commits {
        message.push_str(" Deleted commits on this branch that were never pushed to a remote.");
    }

    if let Some(reason) = unlocked_reason {
        if reason.trim().is_empty() {
            message.push_str(" Unlocked it first; it was locked with no reason given.");
        } else {
            message.push_str(&format!(
                " Unlocked it first; it was locked because: {}.",
                reason.trim()
            ));
        }
    }

    message
}

/// How many files in the worktree have changes git has not been told to keep — the
/// number a forced removal is about to delete for good.
/// How many files in the worktree have changes that were never committed, or `None` when
/// git could not answer — a stale `.git` file, an index lock, a folder that moved. The
/// caller is about to destroy this worktree and then tell someone what was in it, so a
/// failed count must NOT come back looking like a confident zero.
fn project_worktree_dirty_file_count(path: &str) -> Option<usize> {
    let output = Command::new("git")
        .args([
            "-C",
            path,
            "status",
            "--porcelain=v1",
            "--untracked-files=all",
        ])
        .output();
    output
        .ok()
        .filter(|output| output.status.success())
        .map(|output| {
            String::from_utf8_lossy(&output.stdout)
                .lines()
                .filter(|line| !line.trim().is_empty())
                .count()
        })
}

fn project_worktree_is_dirty(path: &str) -> bool {
    let output = Command::new("git")
        .args(["-C", path, "status", "--short"])
        .output();
    output
        .ok()
        .filter(|output| output.status.success())
        .map(|output| !output.stdout.is_empty())
        .unwrap_or(false)
}

fn project_worktree_has_unmerged_commits(path: &str) -> bool {
    let output = Command::new("git")
        .args([
            "-C",
            path,
            "log",
            "--oneline",
            "-1",
            "HEAD",
            "--not",
            "--remotes",
        ])
        .output();
    output
        .ok()
        .filter(|output| output.status.success())
        .map(|output| !output.stdout.is_empty())
        .unwrap_or(false)
}

fn project_worktree_last_activity(path: &str) -> Option<String> {
    let output = Command::new("git")
        .args(["-C", path, "log", "-1", "--format=%cI"])
        .output()
        .ok()?;
    if !output.status.success() {
        return None;
    }

    let value = String::from_utf8_lossy(&output.stdout).trim().to_string();
    (!value.is_empty()).then_some(value)
}

fn project_worktree_archive_path(worktree: &ProjectWorktree) -> Result<PathBuf, String> {
    let archive_root = project_worktree_archive_root(Path::new(&worktree.path));
    let timestamp = std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map_err(|error| format!("Could not build archive timestamp: {error}"))?
        .as_millis();
    let repo = safe_archive_path_segment(&worktree.repo);
    let branch = safe_archive_path_segment(&worktree.branch);
    Ok(archive_root
        .join(repo)
        .join(format!("{branch}-{timestamp}")))
}

fn project_worktree_archive_root(path: &Path) -> PathBuf {
    let normalized_path = normalized_path_string(path);
    let marker = "/worktrees/";
    if let Some(index) = normalized_path.find(marker) {
        return PathBuf::from(format!("{}/worktree-archives", &normalized_path[..index]));
    }

    path.parent()
        .map(|parent| parent.join("worktree-archives"))
        .unwrap_or_else(|| PathBuf::from("worktree-archives"))
}

fn safe_archive_path_segment(value: &str) -> String {
    let mut segment = value
        .trim()
        .chars()
        .map(|character| {
            if character.is_ascii_alphanumeric() || matches!(character, '.' | '_' | '-') {
                character
            } else {
                '-'
            }
        })
        .collect::<String>();
    while segment.contains("--") {
        segment = segment.replace("--", "-");
    }
    let segment = segment.trim_matches('-').to_string();
    if segment.is_empty() {
        "worktree".to_string()
    } else {
        segment
    }
}

fn write_git_command_output(root: &Path, args: &[&str], destination: &Path) -> Result<(), String> {
    let output = Command::new("git")
        .arg("-C")
        .arg(root)
        .args(args)
        .output()
        .map_err(|error| format!("Could not run git {}: {error}", args.join(" ")))?;

    if !output.status.success() {
        let stderr = String::from_utf8_lossy(&output.stderr).trim().to_string();
        return Err(if stderr.is_empty() {
            format!("git {} exited with {}", args.join(" "), output.status)
        } else {
            stderr
        });
    }

    std::fs::write(destination, output.stdout)
        .map_err(|error| format!("Could not write {}: {error}", destination.display()))
}

fn git_untracked_paths(root: &Path) -> Result<Vec<String>, String> {
    let output = Command::new("git")
        .arg("-C")
        .arg(root)
        .args(["ls-files", "--others", "--exclude-standard", "-z"])
        .output()
        .map_err(|error| format!("Could not list untracked files: {error}"))?;

    if !output.status.success() {
        let stderr = String::from_utf8_lossy(&output.stderr).trim().to_string();
        return Err(if stderr.is_empty() {
            format!("git ls-files exited with {}", output.status)
        } else {
            stderr
        });
    }

    Ok(output
        .stdout
        .split(|byte| *byte == 0)
        .filter(|chunk| !chunk.is_empty())
        .map(|chunk| String::from_utf8_lossy(chunk).to_string())
        .filter(|path| is_safe_git_relative_path(path))
        .collect())
}

fn copy_untracked_worktree_files(
    root: &Path,
    destination_root: &Path,
    relative_paths: &[String],
) -> Result<(), String> {
    for relative_path in relative_paths {
        if !is_safe_git_relative_path(relative_path) {
            continue;
        }

        let source = root.join(relative_path);
        let Ok(metadata) = std::fs::metadata(&source) else {
            continue;
        };
        if !metadata.is_file() {
            continue;
        }

        let destination = destination_root.join(relative_path);
        if let Some(parent) = destination.parent() {
            std::fs::create_dir_all(parent).map_err(|error| {
                format!("Could not create untracked archive directory: {error}")
            })?;
        }
        std::fs::copy(&source, &destination).map_err(|error| {
            format!(
                "Could not copy untracked file {} to {}: {error}",
                source.display(),
                destination.display()
            )
        })?;
    }

    Ok(())
}

fn is_safe_git_relative_path(path: &str) -> bool {
    let trimmed = path.trim();
    let relative_path = Path::new(trimmed);
    !trimmed.is_empty()
        && !trimmed.contains('\0')
        && !relative_path.is_absolute()
        && relative_path
            .components()
            .all(|component| matches!(component, std::path::Component::Normal(_)))
}

pub(crate) fn list_git_repository_summaries_sync(
    projects: Vec<RuntimeContextProject>,
) -> Result<Vec<GitRepositorySummary>, String> {
    let mut seen_paths = HashSet::new();
    let mut summaries = Vec::new();

    for project in projects {
        for (path, is_worktree) in git_repository_paths_for_project(&project) {
            let normalized_path = normalized_path_string(&path);
            if !seen_paths.insert(normalized_path) {
                continue;
            }

            summaries.push(git_repository_summary_for_path(&project, path, is_worktree));
        }
    }

    summaries.sort_by(|left, right| {
        left.project_name
            .cmp(&right.project_name)
            .then(left.root_label.cmp(&right.root_label))
            .then(left.branch.cmp(&right.branch))
    });
    Ok(summaries)
}

fn git_repository_paths_for_project(project: &RuntimeContextProject) -> Vec<(PathBuf, bool)> {
    let project_path = PathBuf::from(&project.path);
    let project_path_string = normalized_path_string(&project_path);
    let mut seen = HashSet::from([project_path_string]);
    let mut paths = vec![(project_path.clone(), false)];

    if let Ok(worktrees) = list_project_worktree_paths(&project_path) {
        for worktree_path in worktrees {
            let normalized_path = normalized_path_string(&worktree_path);
            if seen.insert(normalized_path) {
                paths.push((worktree_path, true));
            }
        }
    }

    paths
}

fn list_project_worktree_paths(root: &Path) -> Result<Vec<PathBuf>, String> {
    let output = run_git_text(root, &["worktree", "list", "--porcelain"])?;
    Ok(parse_project_worktree_porcelain(&output)
        .into_iter()
        .map(|worktree| PathBuf::from(worktree.path))
        .collect())
}

fn git_repository_summary_for_path(
    project: &RuntimeContextProject,
    path: PathBuf,
    is_worktree: bool,
) -> GitRepositorySummary {
    git_repository_summary_for_path_result(project, &path, is_worktree).unwrap_or_else(|error| {
        empty_git_repository_summary(project, &path, is_worktree, Some(error))
    })
}

fn git_repository_summary_for_path_result(
    project: &RuntimeContextProject,
    path: &Path,
    is_worktree: bool,
) -> Result<GitRepositorySummary, String> {
    let metadata = std::fs::metadata(path)
        .map_err(|error| format!("Could not read repository metadata: {error}"))?;
    if !metadata.is_dir() {
        return Err("Repository path is not a directory".to_string());
    }

    let status = project_git_status_sync(path.to_path_buf())?;
    let mut counts = git_repository_status_counts(&status.files);
    // Files past the status cap are untracked (git lists tracked files first).
    counts.untracked_count += status.total_files - status.files.len();
    let (last_commit_sha, last_commit_subject, last_commit_at) = git_last_commit(path);
    let branch = status.branch.unwrap_or_else(|| "unknown".to_string());
    let task_id = branch_task_id(&branch)
        .or_else(|| last_commit_subject.as_deref().and_then(task_id_from_text));
    let dirty_count = counts.staged_count + counts.unstaged_count + counts.untracked_count;

    Ok(GitRepositorySummary {
        project_id: project.id.clone(),
        project_name: project.name.clone(),
        repo: path_last_segment(Path::new(&project.path)).unwrap_or_else(|| project.name.clone()),
        path: path.display().to_string(),
        root_label: runtime_context_root_label(path),
        branch: branch.clone(),
        task_id,
        is_worktree,
        is_dirty: dirty_count > 0,
        staged_count: counts.staged_count,
        unstaged_count: counts.unstaged_count,
        untracked_count: counts.untracked_count,
        dirty_count,
        ahead: status.ahead,
        behind: status.behind,
        has_upstream: status.has_upstream,
        last_commit_sha,
        last_commit_subject,
        last_commit_at,
        dirty_since_epoch_ms: git_dirty_since_epoch_ms(path, &status.files),
        dirty_status_fingerprint: git_dirty_status_fingerprint(&status.files),
        error: None,
    })
}

fn empty_git_repository_summary(
    project: &RuntimeContextProject,
    path: &Path,
    is_worktree: bool,
    error: Option<String>,
) -> GitRepositorySummary {
    GitRepositorySummary {
        project_id: project.id.clone(),
        project_name: project.name.clone(),
        repo: path_last_segment(Path::new(&project.path)).unwrap_or_else(|| project.name.clone()),
        path: path.display().to_string(),
        root_label: runtime_context_root_label(path),
        branch: "unknown".to_string(),
        task_id: None,
        is_worktree,
        is_dirty: false,
        staged_count: 0,
        unstaged_count: 0,
        untracked_count: 0,
        dirty_count: 0,
        ahead: 0,
        behind: 0,
        has_upstream: false,
        last_commit_sha: None,
        last_commit_subject: None,
        last_commit_at: None,
        dirty_since_epoch_ms: None,
        dirty_status_fingerprint: git_dirty_status_fingerprint(&[]),
        error,
    }
}

#[derive(Default)]
struct GitRepositoryStatusCounts {
    staged_count: usize,
    unstaged_count: usize,
    untracked_count: usize,
}

fn git_repository_status_counts(files: &[GitFileStatus]) -> GitRepositoryStatusCounts {
    let mut counts = GitRepositoryStatusCounts::default();

    for file in files {
        if file.status == "untracked" {
            counts.untracked_count += 1;
            continue;
        }

        if !file.index_status.is_empty() {
            counts.staged_count += 1;
        }
        if !file.worktree_status.is_empty() {
            counts.unstaged_count += 1;
        }
    }

    counts
}

fn git_last_commit(root: &Path) -> (Option<String>, Option<String>, Option<String>) {
    let Ok(output) = run_git_text(root, &["log", "-1", "--format=%h%x1f%s%x1f%cI"]) else {
        return (None, None, None);
    };

    let mut parts = output.trim().split('\x1f');
    let sha = parts
        .next()
        .map(str::trim)
        .filter(|value| !value.is_empty())
        .map(ToString::to_string);
    let subject = parts
        .next()
        .map(str::trim)
        .filter(|value| !value.is_empty())
        .map(ToString::to_string);
    let committed_at = parts
        .next()
        .map(str::trim)
        .filter(|value| !value.is_empty())
        .map(ToString::to_string);

    (sha, subject, committed_at)
}

fn git_dirty_since_epoch_ms(root: &Path, files: &[GitFileStatus]) -> Option<u64> {
    files
        .iter()
        .filter_map(|file| {
            std::fs::metadata(root.join(&file.relative_path))
                .ok()?
                .modified()
                .ok()?
                .duration_since(std::time::UNIX_EPOCH)
                .ok()
                .and_then(|duration| u64::try_from(duration.as_millis()).ok())
        })
        .min()
}

fn git_dirty_status_fingerprint(files: &[GitFileStatus]) -> String {
    let mut rows: Vec<String> = files
        .iter()
        .map(|file| {
            [
                file.relative_path.as_str(),
                file.index_status.as_str(),
                file.worktree_status.as_str(),
                file.status.as_str(),
                file.badge.as_str(),
            ]
            .join("\x1f")
        })
        .collect();
    rows.sort();

    let mut hash = 0xcbf2_9ce4_8422_2325u64;
    for row in rows {
        for byte in row.as_bytes().iter().copied().chain([0]) {
            hash ^= u64::from(byte);
            hash = hash.wrapping_mul(0x0000_0100_0000_01b3);
        }
    }

    format!("{hash:016x}")
}

fn parse_git_commit_history(output: &str) -> Result<Vec<GitCommitHistoryEntry>, String> {
    let mut entries = Vec::new();

    for line in output.lines().filter(|line| !line.trim().is_empty()) {
        let mut parts = line.split('\x1f');
        let short_sha = parts.next().unwrap_or_default().trim().to_string();
        let sha = parts.next().unwrap_or_default().trim().to_string();
        let subject = parts.next().unwrap_or_default().trim().to_string();
        let author = parts.next().unwrap_or_default().trim().to_string();
        let committed_at = parts.next().unwrap_or_default().trim().to_string();
        let refs = parts.next().unwrap_or_default().trim().to_string();
        let parent_shas = parts
            .next()
            .unwrap_or_default()
            .split_whitespace()
            .map(str::trim)
            .filter(|parent| !parent.is_empty())
            .map(ToOwned::to_owned)
            .collect::<Vec<_>>();
        let parent_count = parent_shas.len();

        if short_sha.is_empty() || sha.is_empty() {
            return Err("Could not parse Git history entry".to_string());
        }

        let (task_id, task_source) = match task_id_from_text(&refs) {
            Some(task_id) => (Some(task_id), Some("refs".to_string())),
            None => match task_id_from_text(&subject) {
                Some(task_id) => (Some(task_id), Some("subject".to_string())),
                None => (None, None),
            },
        };
        entries.push(GitCommitHistoryEntry {
            short_sha,
            sha,
            subject,
            author,
            committed_at,
            refs,
            parent_shas,
            parent_count,
            task_id,
            task_source,
        });
    }

    Ok(entries)
}

fn branch_task_id(branch: &str) -> Option<String> {
    task_id_from_text(branch)
}

fn task_id_from_text(text: &str) -> Option<String> {
    let lower_text = text.to_ascii_lowercase();
    for (index, _) in lower_text.match_indices("tsk") {
        let suffix = lower_text[index + 3..].trim_start_matches(['-', '_', '/', '#', '[', ' ']);
        let digits: String = suffix
            .chars()
            .take_while(|character| character.is_ascii_digit())
            .collect();
        if !digits.is_empty() {
            return Some(format!("TSK-{digits}"));
        }
    }

    None
}

pub(crate) fn list_runtime_contexts_sync(
    projects: Vec<RuntimeContextProject>,
) -> Result<Vec<RuntimeContext>, String> {
    let output = Command::new("lsof")
        .args(["-nP", "-iTCP", "-sTCP:LISTEN", "-Fpcn"])
        .output()
        .map_err(|error| format!("Could not run lsof: {error}"))?;

    if !output.status.success() && output.stdout.is_empty() {
        return Err(format!(
            "Could not list listening processes: {}",
            String::from_utf8_lossy(&output.stderr).trim()
        ));
    }

    let listeners = parse_lsof_tcp_listeners(&String::from_utf8_lossy(&output.stdout));
    let mut cwd_by_pid: HashMap<u32, Option<PathBuf>> = HashMap::new();
    let mut seen = HashSet::new();
    let mut contexts = Vec::new();

    for listener in listeners {
        if !seen.insert((listener.pid, listener.port)) {
            continue;
        }

        let cwd = cwd_by_pid
            .entry(listener.pid)
            .or_insert_with(|| process_cwd(listener.pid));
        let Some(cwd) = cwd.as_ref() else {
            continue;
        };
        let Some(project_match) = runtime_context_project_for_cwd(cwd, &projects) else {
            continue;
        };

        contexts.push(RuntimeContext {
            pid: listener.pid,
            command: listener.command,
            port: listener.port,
            cwd: cwd.display().to_string(),
            project_id: project_match.project_id,
            project_name: project_match.project_name,
            root_label: project_match.root_label,
        });
    }

    contexts.sort_by(|left, right| {
        left.project_name
            .cmp(&right.project_name)
            .then(left.root_label.cmp(&right.root_label))
            .then(left.port.cmp(&right.port))
            .then(left.command.cmp(&right.command))
    });
    Ok(contexts)
}

pub(crate) fn list_playwright_sessions_sync() -> Result<Vec<PlaywrightSessionInfo>, String> {
    let output = Command::new("ps")
        .args(["-axo", "pid=,pgid=,etime=,command="])
        .output()
        .map_err(|error| format!("Could not run ps: {error}"))?;

    if !output.status.success() {
        return Err(format!(
            "Could not list processes: {}",
            String::from_utf8_lossy(&output.stderr).trim()
        ));
    }

    Ok(group_playwright_processes(parse_playwright_processes(
        &String::from_utf8_lossy(&output.stdout),
    )))
}

/// How long a Playwright process is given to leave on its own before it is
/// forced. Waited out on a thread of its own, not inside the command: the
/// answer to "stop" used to be held for the whole grace period plus a second
/// process scan, so the button sat busy and the app sat still for the best part
/// of a second every time. Now TERM is sent, the receipt goes straight back,
/// and the forcing happens behind it — the same shape `resources.rs` uses.
const PLAYWRIGHT_STOP_GRACE: std::time::Duration = std::time::Duration::from_millis(800);

pub(crate) fn kill_playwright_sessions_sync() -> Result<PlaywrightCleanupResult, String> {
    let sessions = list_playwright_sessions_sync()?;
    let (result, survivors) = term_playwright_sessions(sessions, signal_process);
    force_playwright_survivors_after_grace(survivors);
    Ok(result)
}

pub(crate) fn kill_playwright_session_sync(pgid: i32) -> Result<PlaywrightCleanupResult, String> {
    let sessions = list_playwright_sessions_sync()?;
    let session = select_playwright_session(sessions, pgid)?;
    let (result, survivors) = term_playwright_sessions(vec![session], signal_process);
    force_playwright_survivors_after_grace(survivors);
    Ok(result)
}

/// The sessions just told to stop, ready for the forcing pass.
struct PlaywrightTermSweep {
    sessions: Vec<PlaywrightSessionInfo>,
    terminated_pids: HashSet<u32>,
}

/// After the grace period, force whatever the TERM sweep left running. Off the
/// command's thread, so nothing waits on it; the outcome is logged, not
/// returned, the way a forced stop is elsewhere in the app.
fn force_playwright_survivors_after_grace(sweep: PlaywrightTermSweep) {
    if sweep.terminated_pids.is_empty() {
        return;
    }
    std::thread::spawn(move || {
        std::thread::sleep(PLAYWRIGHT_STOP_GRACE);
        let failures = kill_playwright_survivors(
            &sweep.sessions,
            &sweep.terminated_pids,
            signal_process,
            list_playwright_sessions_sync,
        );
        for failure in failures {
            crate::debug_log::stderr_log!(
                "playwright: could not force process group {} after the stop grace period: {}",
                failure.pgid,
                failure.message
            );
        }
    });
}

/// Stops ONE Playwright process group, the same polite-then-forceful way the
/// stop-everything command does. The group has to be one this app just listed as a
/// Playwright session; anything else is refused, so this can never be used to stop an
/// arbitrary process by number.
///
/// The two phases run apart in the app (see `kill_playwright_session_sync`);
/// this composes them in one so the tests can watch the whole sequence.
#[cfg(test)]
fn kill_playwright_session_with<SignalProcess, SleepAfterTerm, ListSessions>(
    sessions: Vec<PlaywrightSessionInfo>,
    pgid: i32,
    signal_process: SignalProcess,
    sleep_after_term: SleepAfterTerm,
    list_sessions: ListSessions,
) -> Result<PlaywrightCleanupResult, String>
where
    SignalProcess: FnMut(u32, &str) -> Result<(), String>,
    SleepAfterTerm: FnOnce(),
    ListSessions: FnMut() -> Result<Vec<PlaywrightSessionInfo>, String>,
{
    let session = select_playwright_session(sessions, pgid)?;
    kill_playwright_sessions_with(
        vec![session],
        signal_process,
        sleep_after_term,
        list_sessions,
    )
}

fn select_playwright_session(
    sessions: Vec<PlaywrightSessionInfo>,
    pgid: i32,
) -> Result<PlaywrightSessionInfo, String> {
    let matched = u32::try_from(pgid).ok().and_then(|pgid| {
        sessions
            .into_iter()
            .find(|session| session.pgid == pgid && !session.pids.is_empty())
    });

    matched.ok_or_else(|| {
        format!("No Playwright session is running in process group {pgid}, so nothing was stopped")
    })
}

#[cfg(test)]
fn kill_playwright_sessions_with<SignalProcess, SleepAfterTerm, ListSessions>(
    sessions: Vec<PlaywrightSessionInfo>,
    mut signal_process: SignalProcess,
    sleep_after_term: SleepAfterTerm,
    list_sessions: ListSessions,
) -> Result<PlaywrightCleanupResult, String>
where
    SignalProcess: FnMut(u32, &str) -> Result<(), String>,
    SleepAfterTerm: FnOnce(),
    ListSessions: FnMut() -> Result<Vec<PlaywrightSessionInfo>, String>,
{
    let (mut result, sweep) = term_playwright_sessions(sessions, &mut signal_process);
    sleep_after_term();
    result.failed_pgids.extend(kill_playwright_survivors(
        &sweep.sessions,
        &sweep.terminated_pids,
        signal_process,
        list_sessions,
    ));
    Ok(result)
}

/// Ask every process in every session to leave. What could not even be asked is
/// reported straight away; what was asked is handed on for the forcing pass.
fn term_playwright_sessions<SignalProcess>(
    sessions: Vec<PlaywrightSessionInfo>,
    mut signal_process: SignalProcess,
) -> (PlaywrightCleanupResult, PlaywrightTermSweep)
where
    SignalProcess: FnMut(u32, &str) -> Result<(), String>,
{
    let mut terminated_pgids = Vec::new();
    let mut terminated_pids = Vec::new();
    let mut failed_pgids = Vec::new();

    for session in &sessions {
        let mut session_had_signal = false;
        for pid in &session.pids {
            if let Err(message) = signal_process(*pid, "TERM") {
                failed_pgids.push(PlaywrightCleanupFailure {
                    pgid: session.pgid,
                    pid: Some(*pid),
                    message,
                });
                continue;
            }
            session_had_signal = true;
            terminated_pids.push(*pid);
        }
        if session_had_signal {
            terminated_pgids.push(session.pgid);
        }
    }

    let sweep = PlaywrightTermSweep {
        sessions: sessions.clone(),
        terminated_pids: terminated_pids.iter().copied().collect(),
    };
    (
        PlaywrightCleanupResult {
            sessions,
            terminated_pgids,
            terminated_pids,
            failed_pgids,
        },
        sweep,
    )
}

/// Force whatever the TERM sweep asked to leave and is still here. Only a
/// process this app itself signalled is forced: a number that has since been
/// handed to something else is left alone.
fn kill_playwright_survivors<SignalProcess, ListSessions>(
    sessions: &[PlaywrightSessionInfo],
    terminated_pids: &HashSet<u32>,
    mut signal_process: SignalProcess,
    mut list_sessions: ListSessions,
) -> Vec<PlaywrightCleanupFailure>
where
    SignalProcess: FnMut(u32, &str) -> Result<(), String>,
    ListSessions: FnMut() -> Result<Vec<PlaywrightSessionInfo>, String>,
{
    let mut failed_pgids = Vec::new();
    let remaining_pids = match list_sessions() {
        Ok(remaining_sessions) => remaining_sessions
            .into_iter()
            .flat_map(|session| session.pids)
            .collect::<HashSet<_>>(),
        Err(error) => {
            for session in sessions {
                if session.pids.iter().any(|pid| terminated_pids.contains(pid)) {
                    failed_pgids.push(PlaywrightCleanupFailure {
                        pgid: session.pgid,
                        pid: None,
                        message: format!("Could not verify Playwright cleanup after TERM: {error}"),
                    });
                }
            }
            return failed_pgids;
        }
    };
    for session in sessions {
        for pid in session
            .pids
            .iter()
            .copied()
            .filter(|pid| terminated_pids.contains(pid) && remaining_pids.contains(pid))
        {
            if let Err(message) = signal_process(pid, "KILL") {
                failed_pgids.push(PlaywrightCleanupFailure {
                    pgid: session.pgid,
                    pid: Some(pid),
                    message,
                });
            }
        }
    }
    failed_pgids
}

pub(crate) fn kill_process_sync(pid: u32, expected_command: Option<String>) -> ProcessKillResult {
    // Process numbers get handed out again after a process exits. The panel the
    // reader clicked in may be minutes old, so before anything is signalled,
    // check the number still belongs to the command the panel showed them.
    if let Some(expected) = expected_command.as_deref().map(str::trim) {
        if !expected.is_empty() && pid > 1 {
            match process_command_name(pid) {
                None => {
                    return ProcessKillResult {
                        ok: false,
                        message: format!(
                            "Process {pid} has already exited, so there was nothing to stop."
                        ),
                    };
                }
                Some(actual) => {
                    if !process_commands_match(&actual, expected) {
                        return ProcessKillResult {
                            ok: false,
                            message: format!(
                                "Process {pid} now belongs to \"{actual}\", not \"{expected}\" — the number was reused by something else, so nothing was stopped. Refresh the list and try again."
                            ),
                        };
                    }
                }
            }
        }
    }
    kill_process_with(pid, std::process::id(), signal_process)
}

/// The executable a process number belongs to right now, or None when no such
/// process exists.
fn process_command_name(pid: u32) -> Option<String> {
    let output = std::process::Command::new("ps")
        .args(["-p", &pid.to_string(), "-o", "comm="])
        .output()
        .ok()?;
    if !output.status.success() {
        return None;
    }
    let text = String::from_utf8_lossy(&output.stdout).trim().to_string();
    if text.is_empty() {
        None
    } else {
        Some(text)
    }
}

/// The panel shows short names ("node") while `ps` answers with full paths
/// ("/usr/local/bin/node"), so the comparison is between file names, either
/// containing the other to survive versioned names like "node22".
fn process_commands_match(actual: &str, expected: &str) -> bool {
    fn file_name(command: &str) -> &str {
        command.trim().rsplit('/').next().unwrap_or(command)
    }
    let actual_name = file_name(actual);
    let expected_name = file_name(expected);
    if actual_name.is_empty() || expected_name.is_empty() {
        return false;
    }
    actual_name == expected_name
        || actual_name.contains(expected_name)
        || expected_name.contains(actual_name)
}

/// Stop one process, with the refusals spelled out.
///
/// Numbers 0 and 1 are not ordinary processes — 0 means "every process in my
/// group" to the kill signal and 1 is the system's own first process, so both
/// are refused before anything is sent. So is this app's own number, which
/// would otherwise close the window the reader clicked in.
fn kill_process_with<SignalProcess>(
    pid: u32,
    own_pid: u32,
    mut signal_process: SignalProcess,
) -> ProcessKillResult
where
    SignalProcess: FnMut(u32, &str) -> Result<(), String>,
{
    if pid <= 1 {
        return ProcessKillResult {
            ok: false,
            message: format!(
                "{pid} is not a process this app will stop — it is the system's own, and stopping it would take the machine down."
            ),
        };
    }
    if pid == own_pid {
        return ProcessKillResult {
            ok: false,
            message: "That number belongs to this app itself, so nothing was stopped.".to_string(),
        };
    }

    match signal_process(pid, "TERM") {
        Ok(()) => ProcessKillResult {
            ok: true,
            message: format!(
                "Asked process {pid} to stop. It may take a moment to finish shutting down."
            ),
        },
        Err(reason) => ProcessKillResult {
            ok: false,
            message: format!(
                "Could not stop process {pid}. It may have already exited, or it may belong to another user. ({reason})"
            ),
        },
    }
}

fn signal_process(pid: u32, signal: &str) -> Result<(), String> {
    if pid == 0 || pid == std::process::id() {
        return Err(format!("Refusing to signal unsafe process {pid}"));
    }

    let output = Command::new("kill")
        .args([format!("-{signal}"), pid.to_string()])
        .output()
        .map_err(|error| format!("Could not run kill -{signal} for pid {pid}: {error}"))?;

    if output.status.success() {
        Ok(())
    } else {
        Err(format!(
            "kill -{signal} {pid} failed: {}",
            String::from_utf8_lossy(&output.stderr).trim()
        ))
    }
}

fn parse_playwright_processes(output: &str) -> Vec<PlaywrightProcessInfo> {
    output
        .lines()
        .filter_map(parse_playwright_process_line)
        .filter_map(|process| {
            playwright_process_label(&process.command, &process.args)
                .map(|label| PlaywrightProcessInfo { label, ..process })
        })
        .collect()
}

fn parse_playwright_process_line(line: &str) -> Option<PlaywrightProcessInfo> {
    let (pid_text, rest) = next_ps_field(line)?;
    let (pgid_text, rest) = next_ps_field(rest)?;
    let (elapsed, args) = next_ps_field(rest)?;
    let pid = pid_text.parse::<u32>().ok()?;
    let pgid = pgid_text.parse::<u32>().ok()?;
    let elapsed = elapsed.to_string();
    let args = args.trim().to_string();
    let command = args.clone();
    let name = playwright_process_name(&args);

    Some(PlaywrightProcessInfo {
        pid,
        pgid,
        command,
        name,
        label: String::new(),
        elapsed,
        args,
    })
}

fn next_ps_field(line: &str) -> Option<(&str, &str)> {
    let trimmed = line.trim_start();
    let split_index = trimmed.find(char::is_whitespace)?;
    let (field, rest) = trimmed.split_at(split_index);
    (!field.is_empty()).then_some((field, rest.trim_start()))
}

fn playwright_process_name(args: &str) -> String {
    if args.contains("Google Chrome") {
        return "Google Chrome".to_string();
    }
    if args.contains("Chromium") {
        return "Chromium".to_string();
    }
    if has_playwright_mcp_command(args) {
        return "playwright-mcp".to_string();
    }
    if has_playwright_cli_daemon(args) {
        return "cliDaemon.js".to_string();
    }
    if has_playwright_cli_server(args) {
        return "playwright/cli.js".to_string();
    }

    args.split_whitespace()
        .next()
        .map(|command| {
            Path::new(command)
                .file_name()
                .and_then(|value| value.to_str())
                .unwrap_or(command)
                .to_string()
        })
        .unwrap_or_else(|| "process".to_string())
}

fn group_playwright_processes(processes: Vec<PlaywrightProcessInfo>) -> Vec<PlaywrightSessionInfo> {
    let mut by_pgid: HashMap<u32, Vec<PlaywrightProcessInfo>> = HashMap::new();
    for process in processes {
        by_pgid.entry(process.pgid).or_default().push(process);
    }

    let mut sessions = by_pgid
        .into_iter()
        .map(|(pgid, mut processes)| {
            processes.sort_by_key(|process| process.pid);
            let pids = processes
                .iter()
                .map(|process| process.pid)
                .collect::<Vec<_>>();
            let label = processes
                .iter()
                .find(|process| process.label != "Playwright browser")
                .or_else(|| processes.first())
                .map(|process| process.label.clone())
                .unwrap_or_else(|| "Playwright session".to_string());
            PlaywrightSessionInfo {
                pgid,
                label,
                pids,
                processes,
            }
        })
        .collect::<Vec<_>>();

    sessions.sort_by(|left, right| left.pgid.cmp(&right.pgid));
    sessions
}

fn playwright_process_label(command: &str, args: &str) -> Option<String> {
    let combined = format!("{command} {args}");
    if has_playwright_cli_daemon(&combined) {
        return Some("Playwright CLI daemon".to_string());
    }
    if has_playwright_cli_server(&combined) {
        return Some("Playwright CLI server".to_string());
    }
    if has_playwright_mcp_command(&combined) {
        return Some("Playwright MCP".to_string());
    }
    if looks_like_chrome_command(command, args) && has_playwright_profile_arg(&combined) {
        return Some("Playwright browser".to_string());
    }

    None
}

fn has_playwright_cli_daemon(command_line: &str) -> bool {
    command_line.split_whitespace().any(|arg| {
        let normalized = arg.replace('\\', "/");
        normalized.ends_with("/cliDaemon.js")
            && (normalized.contains("/node_modules/playwright-core/")
                || normalized.contains("/node_modules/playwright/")
                || normalized.contains("/node_modules/@playwright/"))
    })
}

fn has_playwright_cli_server(command_line: &str) -> bool {
    command_line
        .split_whitespace()
        .any(|arg| path_ends_with(arg, "/node_modules/playwright/cli.js"))
        && command_line
            .split_whitespace()
            .any(|arg| arg == "run-cli-server")
}

fn has_playwright_mcp_command(command_line: &str) -> bool {
    command_line.split_whitespace().any(|arg| {
        let name = Path::new(arg)
            .file_name()
            .and_then(|value| value.to_str())
            .unwrap_or(arg);
        matches!(name, "playwright-mcp" | "playwright-mcp.js")
    })
}

fn has_playwright_profile_arg(command_line: &str) -> bool {
    command_line.split_whitespace().any(|arg| {
        let Some(profile_path) = arg.strip_prefix("--user-data-dir=") else {
            return false;
        };
        Path::new(profile_path)
            .file_name()
            .and_then(|value| value.to_str())
            .is_some_and(|name| name.starts_with("playwright_chromiumdev_profile-"))
    })
}

fn path_ends_with(value: &str, suffix: &str) -> bool {
    value.replace('\\', "/").ends_with(suffix)
}

fn looks_like_chrome_command(command: &str, args: &str) -> bool {
    let lower_command = command.to_ascii_lowercase();
    let lower_args = args.to_ascii_lowercase();
    lower_command.contains("chrome")
        || lower_command.contains("chromium")
        || lower_args.contains("google chrome")
        || lower_args.contains("chromium")
}

fn parse_lsof_tcp_listeners(output: &str) -> Vec<ProcessListener> {
    let mut listeners = Vec::new();
    let mut current_pid: Option<u32> = None;
    let mut current_command = String::new();

    for line in output.lines().filter(|line| !line.is_empty()) {
        let (field, value) = line.split_at(1);
        match field {
            "p" => {
                current_pid = value.parse::<u32>().ok();
                current_command.clear();
            }
            "c" => {
                current_command = value.to_string();
            }
            "n" => {
                let Some(pid) = current_pid else {
                    continue;
                };
                let Some(port) = parse_lsof_tcp_port(value) else {
                    continue;
                };

                listeners.push(ProcessListener {
                    pid,
                    command: current_command.clone(),
                    port,
                });
            }
            _ => {}
        }
    }

    listeners
}

fn parse_lsof_tcp_port(name: &str) -> Option<u16> {
    let endpoint = name.split("->").next().unwrap_or(name).trim();
    let port_text = endpoint.rsplit(':').next()?.trim();
    let digits: String = port_text
        .chars()
        .take_while(|character| character.is_ascii_digit())
        .collect();

    if digits.is_empty() {
        return None;
    }

    digits.parse::<u16>().ok()
}

fn process_cwd(pid: u32) -> Option<PathBuf> {
    let output = Command::new("lsof")
        .args(["-a", "-p", &pid.to_string(), "-d", "cwd", "-Fn"])
        .output()
        .ok()?;

    if !output.status.success() && output.stdout.is_empty() {
        return None;
    }

    parse_lsof_cwd(&String::from_utf8_lossy(&output.stdout))
}

fn parse_lsof_cwd(output: &str) -> Option<PathBuf> {
    output
        .lines()
        .filter_map(|line| line.strip_prefix('n'))
        .map(str::trim)
        .find(|path| !path.is_empty())
        .map(PathBuf::from)
}

fn runtime_context_project_for_cwd(
    cwd: &Path,
    projects: &[RuntimeContextProject],
) -> Option<RuntimeContextProjectMatch> {
    let cwd_path = normalized_path_string(cwd);

    for project in projects {
        let project_path = normalized_path_string(Path::new(&project.path));
        if path_is_within(&cwd_path, &project_path) {
            return Some(RuntimeContextProjectMatch {
                project_id: Some(project.id.clone()),
                project_name: project.name.clone(),
                root_label: runtime_context_root_label(Path::new(&project.path)),
            });
        }
    }

    let (worktree_repo, _) = worktree_repo_and_slug(cwd)?;
    projects
        .iter()
        .find(|project| {
            project.name.eq_ignore_ascii_case(&worktree_repo)
                || path_last_segment(Path::new(&project.path))
                    .is_some_and(|segment| segment.eq_ignore_ascii_case(&worktree_repo))
        })
        .map(|project| RuntimeContextProjectMatch {
            project_id: Some(project.id.clone()),
            project_name: project.name.clone(),
            root_label: runtime_context_root_label(cwd),
        })
}

fn runtime_context_root_label(path: &Path) -> String {
    if let Some((_, slug)) = worktree_repo_and_slug(path) {
        return format!("worktree:{slug}");
    }

    let segments = path_segments(path);
    if segments
        .len()
        .checked_sub(2)
        .and_then(|index| segments.get(index))
        .is_some_and(|segment| segment == "work")
    {
        return "main checkout".to_string();
    }

    path_last_segment(path).unwrap_or_else(|| "unknown".to_string())
}

fn worktree_repo_and_slug(path: &Path) -> Option<(String, String)> {
    let segments = path_segments(path);
    let worktrees_index = segments.iter().position(|segment| segment == "worktrees")?;
    let repo = segments.get(worktrees_index + 1)?.clone();
    let slug = segments.get(worktrees_index + 2)?.clone();
    Some((repo, slug))
}

fn path_segments(path: &Path) -> Vec<String> {
    path.components()
        .filter_map(|component| component.as_os_str().to_str())
        .filter(|segment| !segment.is_empty() && *segment != "/")
        .map(ToString::to_string)
        .collect()
}

fn path_last_segment(path: &Path) -> Option<String> {
    path.file_name()
        .and_then(|segment| segment.to_str())
        .filter(|segment| !segment.is_empty())
        .map(ToString::to_string)
}

fn normalized_path_string(path: &Path) -> String {
    path.to_string_lossy().trim_end_matches('/').to_string()
}

fn path_is_within(path: &str, root: &str) -> bool {
    path == root
        || path
            .strip_prefix(root)
            .is_some_and(|rest| rest.starts_with('/'))
}

fn parse_project_git_status(output: &str) -> Result<ProjectGitStatus, String> {
    let mut git_status = ProjectGitStatus {
        branch: None,
        ahead: 0,
        behind: 0,
        has_upstream: false,
        files: Vec::new(),
        total_files: 0,
        additions: None,
        deletions: None,
    };

    for line in output.lines().filter(|line| !line.trim().is_empty()) {
        if let Some(header) = line.strip_prefix("## ") {
            let branch_header = parse_git_branch_header(header);
            git_status.branch = branch_header.branch;
            git_status.ahead = branch_header.ahead;
            git_status.behind = branch_header.behind;
            git_status.has_upstream = branch_header.has_upstream;
            continue;
        }

        if line.len() < 4 {
            continue;
        }

        let index_code = line.chars().next().unwrap_or(' ');
        let worktree_code = line.chars().nth(1).unwrap_or(' ');
        let relative_path = normalize_git_status_path(&line[3..]);
        if relative_path.is_empty() {
            continue;
        }

        let index_status = git_status_name(index_code).to_string();
        let worktree_status = git_status_name(worktree_code).to_string();
        let status = if !worktree_status.is_empty() {
            worktree_status.clone()
        } else {
            index_status.clone()
        };
        let badge = git_status_badge(index_code, worktree_code).to_string();

        git_status.files.push(GitFileStatus {
            relative_path,
            index_status,
            worktree_status,
            status,
            badge,
        });
    }

    git_status.total_files = git_status.files.len();
    git_status.files.truncate(MAX_GIT_STATUS_FILES);
    Ok(git_status)
}

#[derive(Debug, Clone, PartialEq, Eq)]
struct GitBranchHeader {
    branch: Option<String>,
    ahead: usize,
    behind: usize,
    has_upstream: bool,
}

fn parse_git_branch_header(header: &str) -> GitBranchHeader {
    let mut branch_part = header.trim();
    let mut ahead = 0;
    let mut behind = 0;

    if let Some(metadata_index) = branch_part.find(" [") {
        let metadata = branch_part[metadata_index + 2..].trim_end_matches(']');
        branch_part = &branch_part[..metadata_index];
        for item in metadata.split(',').map(str::trim) {
            if let Some(value) = item.strip_prefix("ahead ") {
                ahead = value.parse::<usize>().unwrap_or(0);
            } else if let Some(value) = item.strip_prefix("behind ") {
                behind = value.parse::<usize>().unwrap_or(0);
            }
        }
    }

    let mut branch_parts = branch_part.splitn(2, "...");
    let branch_name_part = branch_parts.next().unwrap_or_default().trim();
    let has_upstream = branch_parts
        .next()
        .map(str::trim)
        .is_some_and(|upstream| !upstream.is_empty());

    if let Some(branch) = branch_part.strip_prefix("No commits yet on ") {
        let branch = branch
            .split("...")
            .next()
            .unwrap_or(branch)
            .trim()
            .to_string();
        return GitBranchHeader {
            branch: Some(branch),
            ahead,
            behind,
            has_upstream,
        };
    }

    let branch = Some(branch_name_part)
        .filter(|value| !value.is_empty())
        .map(str::to_string);

    GitBranchHeader {
        branch,
        ahead,
        behind,
        has_upstream,
    }
}

fn normalize_git_status_path(path: &str) -> String {
    path.split(" -> ")
        .last()
        .unwrap_or(path)
        .trim_matches('"')
        .trim()
        .to_string()
}

fn git_status_name(code: char) -> &'static str {
    match code {
        'M' => "modified",
        'A' => "added",
        'D' => "deleted",
        'R' => "renamed",
        'C' => "copied",
        'U' => "unmerged",
        '?' => "untracked",
        '!' => "ignored",
        _ => "",
    }
}

fn git_status_badge(index_code: char, worktree_code: char) -> &'static str {
    let status_code = if worktree_code != ' ' {
        worktree_code
    } else {
        index_code
    };

    match status_code {
        '?' => "?",
        'M' => "M",
        'A' => "A",
        'D' => "D",
        'R' => "R",
        'C' => "C",
        'U' => "U",
        '!' => "!",
        _ => "",
    }
}

fn install_panic_hook() {
    let default_hook = std::panic::take_hook();
    std::panic::set_hook(Box::new(move |panic_info| {
        let message = panic_info
            .payload()
            .downcast_ref::<&str>()
            .copied()
            .or_else(|| {
                panic_info
                    .payload()
                    .downcast_ref::<String>()
                    .map(String::as_str)
            })
            .unwrap_or("non-string panic payload");
        let thread = std::thread::current();
        let thread_name = thread.name().unwrap_or("<unnamed>");
        let backtrace = std::backtrace::Backtrace::force_capture();

        crate::debug_log::stderr_log!(
            "panic in thread '{thread_name}': {message}\nForced backtrace:\n{backtrace}"
        );
        default_hook(panic_info);
    }));
}

/// Tells the frontend the main window is closing or the app is quitting, so it
/// can warn about sub-agents and commands still running on this Mac.
const EXIT_REQUESTED_EVENT: &str = "assembly_exit_requested";
/// The app menu's Quit item. The stock one terminates the process directly, so
/// Cmd-Q would never reach the exit guard.
const QUIT_MENU_ID: &str = "assembly-quit";

/// Holds a window close or app quit until the frontend has checked for local
/// background work. `continue_app_exit` lets exactly one through. A second
/// request while the first is unanswered also goes through, so a frozen
/// frontend can never make the app impossible to quit.
#[derive(Default)]
struct ExitRequestGuard {
    state: Mutex<ExitRequestState>,
}

#[derive(Default)]
struct ExitRequestState {
    approved: bool,
    pending: bool,
}

impl ExitRequestGuard {
    /// True when this request must wait for the frontend. An approval or an
    /// unanswered earlier request lets it through and is used up.
    fn hold(&self) -> bool {
        let mut state = self.state.lock().unwrap_or_else(|poisoned| poisoned.into_inner());
        let through = state.approved || state.pending;
        state.approved = false;
        state.pending = !through;
        !through
    }

    fn approve(&self) {
        let mut state = self.state.lock().unwrap_or_else(|poisoned| poisoned.into_inner());
        state.approved = true;
        state.pending = false;
    }

    /// The person chose Cancel, or Quit Anyway failed: the next request warns again.
    fn cancel(&self) {
        *self.state.lock().unwrap_or_else(|poisoned| poisoned.into_inner()) = ExitRequestState::default();
    }
}

/// The person chose Cancel in the quit warning.
#[tauri::command]
fn cancel_app_exit(guard: tauri::State<'_, ExitRequestGuard>) {
    guard.cancel();
}

/// The frontend found nothing to warn about, or the person chose Quit Anyway.
#[tauri::command]
fn continue_app_exit(
    app: tauri::AppHandle,
    guard: tauri::State<'_, ExitRequestGuard>,
    kind: String,
) -> Result<(), String> {
    guard.approve();
    let result = match kind.as_str() {
        "close" => app
            .get_webview_window("main")
            .ok_or_else(|| "The main window is already closed.".to_string())
            .and_then(|window| window.close().map_err(|error| error.to_string())),
        "quit" => {
            app.exit(0);
            Ok(())
        }
        other => Err(format!("Unknown exit kind: {other}")),
    };
    if result.is_err() {
        guard.cancel();
    }
    result
}

// Runs on the event-loop thread, which is outside any tokio runtime, so the
// timeout must be created inside `block_on`.
fn shutdown_agent_runtimes_on_exit(manager: &agent_conversation::manager::AgentRuntimeManager) {
    if tauri::async_runtime::block_on(async {
        tokio::time::timeout(Duration::from_secs(5), manager.shutdown()).await
    })
    .is_err()
    {
        crate::debug_log::stderr_log!("Adapter shutdown timed out after 5 seconds");
    }
}

fn main() {
    install_panic_hook();
    if let Err(error) = proof_mode::start_from_process().and_then(|()| agent_conversation::messaging::endpoint().map(drop)) {
        crate::debug_log::stderr_log!("Assembly could not start: {error}");
        std::process::exit(1);
    }
    if std::env::args().any(|argument| argument == "--assembly-server") {
        if let Err(error) = agent_conversation::remote::server::run_server_from_environment() {
            crate::debug_log::stderr_log!("Assembly remote server failed: {error}");
            std::process::exit(1);
        }
        return;
    }
    let builder = tauri::Builder::default()
        .manage(files::discovery::SourceScanRegistry::default())
        .manage(files::io::SourceFileReadOwner::default())
        .manage(files::discovery::root::ProjectRootValidationOwner::default())
        .manage(agent_conversation::terminal_projection::TerminalProjectionRegistry::default())
        .manage(lsp::SourceLspRegistry::default())
        .manage(projection_streams::ProjectionStreams::default())
        .manage(terminal::TerminalRegistry::default())
        .manage(browser::BrowserRegistry::default())
        .manage(resources::ResourceRegistry::default())
        .manage(usage_history::UsageHistoryState::default())
        .manage(WorkflowWatch::default())
        .plugin(tauri_plugin_clipboard_manager::init())
        .plugin(tauri_plugin_deep_link::init())
        .plugin(tauri_plugin_dialog::init())
        .plugin(tauri_plugin_fs::init())
        .plugin(tauri_plugin_notification::init())
        .plugin(tauri_plugin_opener::init())
        .plugin(tauri_plugin_process::init())
        .plugin(tauri_plugin_updater::Builder::new().build())
        .manage(ExitRequestGuard::default());
    // The stock menu, with its Quit item swapped for one that asks to exit, so
    // Cmd-Q goes through the exit guard like the window's close button.
    #[cfg(target_os = "macos")]
    let builder = builder
        .menu(|app| {
            let menu = tauri::menu::Menu::default(app)?;
            if let Some(tauri::menu::MenuItemKind::Submenu(app_menu)) =
                menu.items()?.into_iter().next()
            {
                if let Some(quit) = app_menu.items()?.pop() {
                    app_menu.remove(&quit)?;
                }
                app_menu.append(&tauri::menu::MenuItem::with_id(
                    app,
                    QUIT_MENU_ID,
                    format!("Quit {}", app.package_info().name),
                    true,
                    Some("CmdOrCtrl+Q"),
                )?)?;
            }
            Ok(menu)
        })
        .on_menu_event(|app, event| {
            if event.id() == QUIT_MENU_ID {
                app.exit(0);
            }
        });

    let app = builder
        .setup(|app| {
            let app_data_dir = app
                .path()
                .app_data_dir()
                .map_err(|error| format!("Application data directory is unavailable: {error}"))?;
            std::fs::create_dir_all(&app_data_dir).map_err(|error| {
                format!(
                    "Could not create application data directory {}: {error}",
                    app_data_dir.display()
                )
            })?;
            debug_log::install_file_mirror(app_data_dir.join("logs/backend.log"));
            let session_db_path = app_data_dir.join("sessions.db");
            let agent_runtime = agent_conversation::manager::AgentRuntimeManager::open(
                agent_conversation::providers::ProviderRegistry::bundled_from_environment_at(Some(
                    &app_data_dir,
                ))
                .map_err(|error| format!("Packaged ACP adapter configuration is invalid: {error}"))?,
                &session_db_path,
            )?;
            agent_runtime.start_closing_finished_helpers();
            project_folders::import_saved_project_roots(&agent_runtime)?;
            // Before any list: older local sessions join projects added since the last run.
            project_folders::backfill_local_session_projects(agent_runtime.store())?;
            if let Some(settings) = agent_runtime.read_app_setting(LANGUAGE_SERVER_SETTINGS_KEY)? {
                lsp::restore_language_server_settings(&settings)?;
            }
            import_legacy_orchestration_events(agent_runtime.store())?;
            // Validate the store-backed list and its runtime overlay before any
            // frontend activation can observe the manager.
            agent_runtime.list_sessions()?;
            let workflow_store = agent_runtime.store_handle();
            let remote_session_store = Arc::clone(&workflow_store);
            let workflow_engine = WorkflowEngine::managed(agent_runtime.clone(), workflow_store);
            usage_history::otel::start(&usage_history::usage_db_path(app.handle())?, agent_runtime.clone());
            let recovered_runs = workflow_engine.recover_after_restart().map_err(|error| error.to_string())?;
            let recovery_engine = workflow_engine.clone();
            tauri::async_runtime::spawn(async move {
                for run_id in recovered_runs {
                    if let Err(error) = recovery_engine.dispatch_ready(&run_id).await {
                        eprintln!("workflow run {run_id} could not restart: {error}");
                    }
                }
            });
            let live_session_ids = agent_runtime
                .resource_roots()
                .into_iter()
                .map(|root| root.owned_id)
                .collect();
            agent_conversation::reaper::start_startup_reaper(&app_data_dir, live_session_ids)?;
            let projection_streams = app
                .state::<projection_streams::ProjectionStreams>()
                .inner()
                .clone();
            let remote_projection_streams = projection_streams.clone();
            let remote_status_handle = app.handle().clone();
            let remote_connection =
                agent_conversation::remote::RemoteConnectionManager::from_environment(
                    Arc::new(move |event| remote_projection_streams.publish_agent_event(event)),
                    Arc::new(move |status| {
                        let _ = remote_status_handle.emit(REMOTE_CONNECTION_CHANGED_EVENT, status);
                    }),
                    remote_session_store,
                )?;
            let history_handle = app.handle().clone();
            remote_connection.set_history_download_sink(Arc::new(move |progress| {
                let _ = history_handle.emit("agent-conversation-history-download", progress);
            }));
            remote_connection.set_terminal_output(projection_streams.clone());
            let messages_handle = app.handle().clone();
            mcb_core::broker::on_messages_changed(move || { let _ = messages_handle.emit(AGENT_MESSAGES_CHANGED_EVENT, ()); });
            let messages_handle = app.handle().clone();
            remote_connection.set_messages_changed(Arc::new(move || { let _ = messages_handle.emit(AGENT_MESSAGES_CHANGED_EVENT, ()); }));
            // Sessions on this desktop and on connected machines message each other through it.
            remote_connection.set_message_host(agent_runtime.clone());
            agent_conversation::messaging::endpoint()?
                .set_relay(remote_connection.message_relay(agent_conversation::remote::LOCAL_MACHINE));
            agent_conversation::messaging::endpoint()?.set_workflow(workflow_engine.clone());
            if !remote_connection.is_configured() && proof_mode::active().is_none() {
                if let Some(profiles) = agent_runtime.read_app_setting(
                    agent_conversation::remote::REMOTE_ASSEMBLY_PROFILES_SETTING_KEY,
                )? {
                    remote_connection.restore_profiles(&profiles)?;
                } else if let Some(profile) = agent_runtime.read_app_setting(
                    agent_conversation::remote::REMOTE_ASSEMBLY_PROFILE_SETTING_KEY,
                )? {
                    let migrated = remote_connection.migrate_legacy_profile(&profile)?;
                    agent_runtime.write_app_setting(
                        agent_conversation::remote::REMOTE_ASSEMBLY_PROFILES_SETTING_KEY,
                        &serde_json::to_string(&migrated).map_err(|error| error.to_string())?,
                    )?;
                }
            }
            let workflow_events = workflow_engine.clone(); usage_history::start_acp_usage(usage_history::usage_db_path(app.handle())?, agent_runtime.store_handle());
            let workflow_handle = app.handle().clone();
            agent_runtime.set_emitter(Arc::new(move |event| {
                let is_terminal_turn = matches!(
                    &event.payload,
                    agent_conversation::protocol::AgentConversationPayload::Turn {
                        state: agent_conversation::protocol::TurnState::Completed
                            | agent_conversation::protocol::TurnState::Interrupted
                            | agent_conversation::protocol::TurnState::Failed,
                        ..
                    }
                );
                let is_usage = matches!(
                    &event.payload,
                    agent_conversation::protocol::AgentConversationPayload::Usage { .. }
                );
                projection_streams.publish_agent_event(event.clone());
                if !is_terminal_turn && !is_usage {
                    return;
                }
                let workflow_events = workflow_events.clone();
                let workflow_handle = workflow_handle.clone();
                tauri::async_runtime::spawn(async move {
                    if is_usage {
                        // New usage changes the run's cost, which a watching screen reads again.
                        match workflow_events.list_runs() {
                            Ok(runs) => if let Some(run) = runs.into_iter().find(|run| run.nodes.iter().any(|node| node.owned_id == event.owned_id)) {
                                emit_workflow_run_updated(&workflow_handle, &run);
                                send_to_watchers(&workflow_handle, run);
                            },
                            Err(error) => debug_log::stderr_log!("Could not show workflow usage: {error}"),
                        }
                        return;
                    }
                    match workflow_events.accept_agent_event(&event).await {
                        Ok(Some(run)) => emit_workflow_run_updated(&workflow_handle, &run),
                        Ok(None) => {}
                        Err(error) => debug_log::stderr_log!(
                            "Could not advance workflow from agent event: {error}"
                        ),
                    }
                });
            }));
            let handle = app.handle().clone();
            agent_runtime.set_broker_emitter(Arc::new(move |event| {
                let _ = handle.emit("workflow-broker-event", event);
            }));
            // Names a session from its first exchange. The manager decides
            // when a name is wanted and saves the answer; this is the call
            // itself, and the listener below puts the saved name on the rail.
            let handle = app.handle().clone();
            agent_runtime.set_session_namer(Arc::new(move |input: &str| {
                helper::name_session(&handle, input)
            }));
            let handle = app.handle().clone();
            agent_runtime.set_session_renamed_listener(Arc::new(move |owned_id, title| {
                let _ = handle.emit(
                    SESSION_TITLE_CHANGED_EVENT,
                    SessionTitleChanged {
                        owned_id: owned_id.to_string(),
                        title: title.to_string(),
                    },
                );
            }));
            tauri::async_runtime::spawn(forward_run_changes(app.handle().clone(), workflow_engine.clone()));
            app.manage(workflow_engine);
            app.manage(remote_connection);
            app.manage(agent_runtime);
            // Every time a language server starts, finishes reading a project, or stops,
            // tell the editor straight away. Without this the editor would have to ask
            // over and over to notice, which is what it used to do.
            let app = app.handle().clone();
            lsp::set_source_lsp_status_listener(Arc::new(move |change| {
                let _ = app.emit(SOURCE_LSP_STATUS_CHANGED_EVENT, change);
            }));
            Ok(())
        })
        .invoke_handler(proof_mode::guard(tauri::generate_handler![
            read_backend_capabilities,
            list_installed_font_families,
            list_source_files,
            list_source_directory,
            cancel_source_scan,
            validate_project_root,
            cancel_project_root_validation,
            read_source_file,
            read_source_image,
            cancel_source_file_reads,
            read_native_csharp_file,
            write_source_file,
            open_source_file,
            reveal_source_file,
            open_path,
            reveal_path,
            move_to_trash,
            open_terminal_path,
            open_terminal_command,
            search_source_files,
            search_source_tree,
            find_source_definitions,
            find_source_definitions_in_root,
            find_source_references,
            find_source_references_in_root,
            count_source_references,
            read_source_lsp_status,
            list_source_lsp_statuses,
            warm_source_lsp_for_root,
            ensure_native_csharp_language_client,
            mark_native_csharp_language_client_ready,
            set_csharp_language_server_enabled,
            set_language_servers_enabled,
            set_language_server_enabled,
            read_workspace_language_intelligence,
            set_workspace_language_intelligence,
            find_source_lsp_definitions,
            find_source_lsp_completions,
            find_source_lsp_implementations,
            find_source_lsp_type_definitions,
            find_source_lsp_document_highlights,
            find_source_lsp_signature_help,
            find_source_lsp_inlay_hints,
            find_source_lsp_semantic_tokens,
            find_source_lsp_workspace_symbols,
            format_source_with_lsp,
            rename_source_with_lsp,
            find_source_lsp_code_actions,
            find_source_lsp_references,
            find_source_lsp_hover,
            find_source_lsp_symbols,
            find_source_lsp_document_symbols,
            read_source_lsp_log,
            read_source_lsp_diagnostics,
            list_source_lsp_diagnostics_for_root,
            project_git_status,
            read_source_git_diff,
            read_git_branch_diff,
            stage_git_paths,
            unstage_git_paths,
            commit_git_repository,
            fetch_git_repository,
            pull_git_repository,
            push_git_repository,
            publish_git_repository,
            add_git_remote,
            read_git_commit_history,
            read_git_commit_files,
            read_git_commit_file_diff,
            git::pull_requests::generate_commit_message,
            git::pull_requests::read_pull_request_context,
            git::pull_requests::generate_pull_request_details,
            git::pull_requests::create_pull_request,
            git::pull_requests::read_pull_request_status,
            git::github::list_github_pull_requests,
            git::github::read_github_pull_request,
            git::github::read_github_pull_request_file,
            git::github::submit_github_pull_request_review,
            git::github::reply_github_pull_request_comment,
            git::github::comment_github_pull_request,
            git::github::merge_github_pull_request,
            git::workspace::discard_git_paths,
            git::workspace::discard_all_git_changes,
            git::workspace::list_git_branches,
            git::workspace::create_git_branch,
            git::workspace::switch_git_branch,
            git::workspace::stash_git_changes,
            git::workspace::pop_git_stash,
            git::workspace::list_git_stashes,
            git::workspace::amend_git_commit,
            git::workspace::list_open_pull_requests,
            list_project_worktrees,
            list_repository_checkouts,
            remove_project_worktree,
            archive_project_worktree,
            list_git_repository_summaries,
            list_agent_sessions,
            list_agent_sessions_for_project,
            read_agent_session_details,
            commands::agent_sessions::read_remote_saved_session_details,
            list_runtime_contexts,
            list_playwright_sessions,
            kill_playwright_session,
            kill_playwright_sessions,
            kill_process,
            resources::read_resource_sample,
            resources::read_resource_totals,
            resources::read_resource_snapshot,
            resources::cancel_resource_snapshot,
            resources::read_resource_disk_scan,
            resources::cancel_resource_disk_scan,
            resources::cleanup_workspace_disk_entry,
            resources::stop_owned_resource,
            resources::stop_resource_process_tree,
            resources_disk::read_resource_disk_usage,
            resources_disk::reclaim_resource_disk_entry,
            resources::restart_language_server_root,
            resources::set_active_source_root,
            resources::apply_resource_memory_pressure,
            resources::read_language_server_log,
            usage_current::read_current_provider_usage,
            usage_history::read_usage_summary,
            usage_history::read_usage_breakdown, usage_history::read_workflow_usage,
            usage_history::read_usage_provider_summary,
            usage_history::read_usage_daily,
            usage_history::read_usage_daily_totals,
            read_usage_token_breakdown,
            read_usage_provider_daily_totals,
            read_usage_cost_inputs,
            usage_history::refresh_usage_history,
            evidence::copy_evidence_original,
            evidence::delete_evidence,
            evidence::list_evidence_gallery,
            evidence::read_evidence_disk_usage,
            evidence::resolve_evidence_original,
            evidence::set_evidence_pinned,
            list_orchestration_runs,
            record_orchestration_event,
            list_workflow_runs,
            watch_workflow_runs,
            create_workflow_run,
            get_workflow_run,
            workflow_history, workflow_review_reports,
            list_workflow_files,
            list_owner_inbox,
            answer_workflow_pause,
            start_workflow_run,
            pause_workflow_run,
            resume_workflow_run,
            cancel_workflow_run,
            retry_workflow_node,
            approve_workflow_gate,
            agent_conversation::ensure_agent_conversation,
            agent_conversation::remote::read_remote_assembly_environment,
            agent_conversation::remote::connect_remote_assembly,
            agent_conversation::remote::install_flow::install_remote_assembly,
            #[cfg(debug_assertions)]
            agent_conversation::remote::deploy_remote_backend_dev,
            agent_conversation::remote::read_remote_backend_statuses,
            agent_conversation::remote::uninstall_remote_assembly,
            agent_conversation::remote::cancel_remote_connection,
            agent_conversation::remote::disconnect_remote_assembly,
            agent_conversation::remote::remote_workspace,
            agent_conversation::remote::remote_workflow,
            agent_conversation::remote::check_remote_provider_updates,
            agent_conversation::remote::install_remote_provider_updates,
            agent_conversation::remote::restart_remote_for_provider_updates,
            agent_conversation::remote::remove_remote_assembly_profile,
            agent_conversation::send_agent_conversation_message,
            agent_conversation::agent_conversation_set_session_draft,
            agent_conversation::agent_conversation_get_session_draft,
            agent_conversation::agent_conversation_clear_session_draft,
            agent_conversation::set_main_orchestrator,
            agent_conversation::list_agent_messages,
            agent_conversation::count_agent_messages, agent_conversation::messaging::board::board,
            agent_conversation::write_agent_conversation_workspace,
            agent_conversation::read_agent_conversation_workspace,
            agent_conversation::read_agent_conversation_workspace_expanded_paths,
            agent_conversation::write_agent_conversation_workspace_expanded_paths,
            agent_conversation::delete_agent_conversation_workspace,
            agent_conversation::clear_agent_conversation_workspace_editors,
            agent_conversation::clear_agent_conversation_workspace_tabs,
            agent_conversation::write_assembly_setting, agent_conversation::read_assembly_setting,
            agent_conversation::manager::session_project::set_session_project,
            agent_conversation::list_projects,
            agent_conversation::add_project,
            agent_conversation::rename_project,
            agent_conversation::set_project_root,
            agent_conversation::set_project_pinned,
            agent_conversation::remove_project,
            project_folders::list_folders,
            project_folders::project_root_is_bare,
            project_folders::workspace_git_init,
            agent_conversation::respond_agent_conversation_approval,
            agent_conversation::respond_agent_conversation_permission,
            agent_conversation::respond_agent_conversation_input,
            agent_conversation::stop_agent_conversation_turn,
            agent_conversation::change_agent_conversation_checkout,
            agent_conversation::set_agent_conversation_config,
            agent_conversation::set_agent_conversation_config_option,
            agent_conversation::read_agent_conversation_config,
            agent_conversation::warm_agent_conversation_config,
            agent_conversation::probe_agent_provider_config,
            agent_conversation::cancel_agent_provider_probe,
            agent_conversation::read_agent_conversation_capabilities,
            agent_conversation::close_agent_conversation,
            agent_conversation::delete_agent_conversation_session,
            agent_conversation::read_agent_conversation_history_download,
            agent_conversation::read_agent_conversation_selection,
            agent_conversation::cancel_agent_conversation_request,
            agent_conversation::list_agent_conversation_sessions,
            agent_conversation::list_remote_agent_conversation_sessions,
            agent_conversation::list_agent_conversation_events,
            agent_conversation::list_agent_conversation_events_before,
            agent_conversation::list_agent_conversation_items_before,
            agent_conversation::list_agent_conversation_items_after, agent_conversation::manager::tool_call::read_agent_conversation_tool_call,
            agent_conversation::update_agent_conversation_session_meta,
            agent_conversation::read_agent_conversation_child_history,
            agent_conversation::stop_agent_conversation_child_history,
            agent_conversation::begin_agent_conversation_import,
            agent_conversation::finish_agent_conversation_import,
            agent_conversation::extend_agent_conversation_import,
            agent_conversation::start_agent_conversation_terminal_projection,
            agent_conversation::stop_agent_conversation_terminal_projection,
            projection_streams::register_agent_conversation_stream,
            projection_streams::acknowledge_agent_conversation_stream,
            projection_streams::unregister_agent_conversation_stream,
            projection_streams::register_terminal_output_stream, projection_streams::acknowledge_terminal_output_stream,
            projection_streams::unregister_terminal_output_stream,
            agent_conversation::agui::register_agui_stream, agent_conversation::agui::acknowledge_agui_stream, agent_conversation::agui::unregister_agui_stream, agent_conversation::agui::run_agui, agent_conversation::agui::read_agui_snapshot, agent_conversation::agui::list_agui_events_before, agent_conversation::agui::list_agui_events_after, agent_conversation::agui::close_agui_view,
            agent_conversation::save_agent_conversation_attachment,
            agent_conversation::read_agent_conversation_attachments,
            agent_conversation::read_agent_conversation_selected_attachments,
            agent_conversation::read_agent_conversation_attachment_file,
            agent_conversation::discard_agent_conversation_original,
            agent_conversation::delete_agent_conversation_attachment,
            agent_conversation::agent_conversation_add_session_annotation,
            agent_conversation::agent_conversation_list_session_annotations,
            agent_conversation::agent_conversation_delete_session_annotation,
            agent_conversation::handoff::handoff_agent_conversation,
            agent_conversation::providers::cli_updates::check_provider_clis,
            agent_conversation::providers::cli_updates::update_provider_cli,
            agent_conversation::providers::updates::check_provider_updates, agent_conversation::providers::descriptors::list_provider_descriptors,
            agent_conversation::providers::updates::install_provider_updates,
            agent_conversation::providers::updates::restart_for_provider_updates,
            terminal::commands::start_terminal_session,
            terminal::commands::list_terminal_sessions,
            terminal::commands::read_terminal_session_scrollback,
            terminal::commands::write_terminal_session,
            terminal::commands::resize_terminal_session,
            terminal::commands::close_terminal_session,
            browser::create_browser_tab,
            browser::set_browser_tab_bounds,
            browser::set_browser_tab_viewport,
            browser::show_browser_tab,
            browser::hide_browser_workspace,
            browser::navigate_browser_tab,
            browser::reload_browser_tab,
            browser::go_back_browser_tab,
            browser::go_forward_browser_tab,
            browser::close_browser_tab,
            browser::clear_browser_workspace_data,
            browser::arm_browser_element_picker,
            browser::cancel_browser_element_picker,
            browser::inspect_browser_rect,
            browser::capture_browser_viewport,
            browser::open_browser_tab_devtools,
            browser::open_browser_tab_external,
            notion_tasks::read_notion_task_settings,
            notion_tasks::save_notion_task_settings,
            notion_tasks::clear_notion_task_settings,
            notion_tasks::list_notion_tasks,
            notion_tasks::read_cached_notion_task,
            notion_tasks::read_notion_task_detail,
            notion_tasks::cancel_notion_task_detail,
            notion_tasks::update_notion_task_status,
            notion_tasks::update_notion_task_property,
            notion_tasks::update_notion_block,
            notion_tasks::append_notion_task_text,
            notion_tasks::append_notion_task_image,
            notion_tasks::refresh_notion_tasks,
            notion_oauth::begin_notion_oauth,
            notion_oauth::claim_notion_oauth,
            helper::run_helper_job,
            helper::set_helper_key,
            helper::read_helper_settings,
            helper::write_helper_settings,
            helper::test_helper,
            continue_app_exit,
            cancel_app_exit
        ]))
        .on_window_event(|window, event| match event {
            tauri::WindowEvent::CloseRequested { api, .. }
                if window.label() == "main" && window.state::<ExitRequestGuard>().hold() =>
            {
                api.prevent_close();
                let _ = window
                    .app_handle()
                    .emit(EXIT_REQUESTED_EVENT, serde_json::json!({ "kind": "close" }));
            }
            tauri::WindowEvent::Destroyed => {
                window.state::<browser::BrowserRegistry>().shutdown();
                window
                    .state::<agent_conversation::remote::RemoteConnectionManager>()
                    .shutdown();
            }
            _ => {}
        })
        .build(tauri::generate_context!())
        .expect(product_identity::STARTUP_FAILURE_CONTEXT);

    app.run(|app_handle, event| match event {
        // A quit (code set) is held; the exit that follows the last window
        // closing (no code) was already approved, and a restart cannot be held.
        tauri::RunEvent::ExitRequested {
            code: Some(code), api, ..
        } if code != tauri::RESTART_EXIT_CODE
            && app_handle.state::<ExitRequestGuard>().hold() =>
        {
            api.prevent_exit();
            let _ = app_handle.emit(EXIT_REQUESTED_EVENT, serde_json::json!({ "kind": "quit" }));
        }
        tauri::RunEvent::Exit => {
            app_handle.state::<terminal::TerminalRegistry>().shutdown();
            shutdown_agent_runtimes_on_exit(
                &app_handle.state::<agent_conversation::manager::AgentRuntimeManager>(),
            );
            app_handle.state::<browser::BrowserRegistry>().shutdown();
            app_handle
                .state::<agent_conversation::remote::RemoteConnectionManager>()
                .shutdown();
        }
        _ => {}
    });
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::sync::atomic::Ordering;
    use std::time::Instant;
    use crate::files::discovery::{
        DEFAULT_SOURCE_LIST_LIMIT, SourceScanCancellation, SourceScanWalkProgress,
    };
    use crate::files::discovery::paths::{
        detect_language, source_collection_limit,
    };
    use crate::files::discovery::references::{
        count_source_references_sync, find_source_references_in_root_sync, find_source_references_sync,
    };
    use crate::files::discovery::root::{
        validate_project_root_sync,
    };
    use crate::files::discovery::search::{
        search_source_files_sync,
    };
    use crate::files::discovery::symbols::{
        find_source_definitions_in_root_sync, find_source_definitions_sync,
    };
    use crate::files::discovery::walk::{
        collect_source_files, list_source_files_sync, list_source_files_sync_with_cancellation,
    };
    use crate::files::io::{
        MAX_PREVIEW_BYTES, MAX_SOURCE_IMAGE_BYTES, read_source_file_sync, read_source_image_sync, write_source_file_sync,
    };
    use crate::files::types::{
        SourceRecord,
    };
    use crate::git::workspace::{
        add_git_remote_sync, commit_git_repository_sync, fetch_git_repository_sync, publish_git_repository_sync,
        pull_git_repository_sync, push_git_repository_sync, stage_git_paths_sync,
        unstage_git_paths_sync,
    };
    use std::time::{SystemTime, UNIX_EPOCH};

    #[test]
    fn exit_shutdown_runs_outside_a_runtime() {
        let manager = agent_conversation::manager::AgentRuntimeManager::new(
            agent_conversation::providers::ProviderRegistry::default(),
        );
        shutdown_agent_runtimes_on_exit(&manager);
    }

    #[test]
    fn exit_request_guard_requires_confirmation_once() {
        let guard = ExitRequestGuard::default();
        // The first close or quit waits for the frontend to check for work.
        assert!(guard.hold());
        // Cancel approves nothing, so the next request is held again.
        guard.cancel();
        assert!(guard.hold());
        // One approval lets exactly one request through...
        guard.approve();
        assert!(!guard.hold());
        // ...and is used up, so a later request warns again.
        assert!(guard.hold());
    }

    #[test]
    fn exit_request_guard_lets_a_repeat_through_when_unanswered() {
        let guard = ExitRequestGuard::default();
        assert!(guard.hold());
        // A frozen or absent frontend never answers; a second close or quit
        // goes ahead rather than leaving the app impossible to quit.
        assert!(!guard.hold());
        // That repeat used up the outstanding request, so the next one warns.
        assert!(guard.hold());
        // Cancel clears the outstanding request, so the next attempt warns again.
        guard.cancel();
        assert!(guard.hold());
    }

    #[test]
    fn exit_request_guard_forgets_an_approval_whose_close_failed() {
        let guard = ExitRequestGuard::default();
        assert!(guard.hold());
        guard.approve();
        // Quit Anyway could not close the window, so nothing used the approval.
        guard.cancel();
        assert!(guard.hold());
    }

    #[test]
    fn playwright_process_parser_groups_only_owned_markers() {
        let output = "\
  101   100 00:01:02 /usr/local/bin/node node /repo/node_modules/playwright/cli.js run-cli-server
  102   100 00:00:59 /Applications/Google Chrome.app/Contents/MacOS/Google Chrome --user-data-dir=/tmp/playwright_chromiumdev_profile-abc --type=browser
  103   100 00:00:03 /usr/local/bin/node node /important/unrelated-worker.js
  104   104 00:00:03 /Applications/Google Chrome.app/Contents/MacOS/Google Chrome --profile-directory=Default
  105   105 00:00:03 /usr/local/bin/node node server.js
";

        let sessions = group_playwright_processes(parse_playwright_processes(output));

        assert_eq!(sessions.len(), 1);
        assert_eq!(sessions[0].pgid, 100);
        assert_eq!(sessions[0].pids, vec![101, 102]);
        assert_eq!(sessions[0].label, "Playwright CLI server");
    }

    #[test]
    fn playwright_process_detector_ignores_normal_chrome_vite_and_node() {
        assert_eq!(
            playwright_process_label(
                "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome",
                "--profile-directory=Default"
            ),
            None
        );
        assert_eq!(
            playwright_process_label(
                "/usr/local/bin/node",
                "node ./node_modules/vite/bin/vite.js"
            ),
            None
        );
        assert_eq!(
            playwright_process_label("/usr/local/bin/node", "node ./scripts/worker.js"),
            None
        );
    }

    #[test]
    fn playwright_process_detector_ignores_substring_marker_false_positives() {
        let cases = [
            (
                "/usr/local/bin/node",
                "node /tmp/not-playwright-mcp-helper.js",
            ),
            (
                "/usr/local/bin/node",
                "node /tmp/playwright/cli.js.backup run-cli-server",
            ),
            ("/usr/local/bin/node", "node /tmp/cliDaemon.js.notes"),
        ];

        for (command, args) in cases {
            assert_eq!(playwright_process_label(command, args), None, "{args}");
        }
    }

    #[test]
    fn lsp_diagnostics_remember_which_file_they_came_from() {
        let message = serde_json::json!({
            "method": "textDocument/publishDiagnostics",
            "params": {
                "uri": "file:///repo/src/App.ts",
                "diagnostics": [{
                    "range": { "start": { "line": 4, "character": 2 } },
                    "severity": 1,
                    "message": "Cannot find name 'valu'.",
                    "source": "ts"
                }]
            }
        });

        let diagnostics = lsp::diagnostics_from_message(&message, "file:///repo/src/App.ts");

        assert_eq!(diagnostics.len(), 1);
        assert_eq!(diagnostics[0].path.as_deref(), Some("/repo/src/App.ts"));
        assert_eq!(diagnostics[0].line, 5);
        assert_eq!(diagnostics[0].column, 3);
        assert_eq!(diagnostics[0].severity, "error");
    }

    #[test]
    fn lsp_diagnostics_for_root_collect_every_file_inside_that_root() {
        let inside_root = PathBuf::from("/repo");
        let other_root = PathBuf::from("/elsewhere");

        let sessions = vec![
            (
                inside_root.clone(),
                HashMap::from([
                    (
                        "file:///repo/src/Later.ts".to_string(),
                        vec![lsp::SourceLspDiagnostic::for_test(
                            "warning",
                            "Unused import.",
                            2,
                            1,
                            Some("/repo/src/Later.ts"),
                        )],
                    ),
                    (
                        "file:///repo/src/App.ts".to_string(),
                        vec![
                            lsp::SourceLspDiagnostic::for_test(
                                "error",
                                "Second on the same file.",
                                9,
                                4,
                                Some("/repo/src/App.ts"),
                            ),
                            lsp::SourceLspDiagnostic::for_test(
                                "error",
                                "First on the same file.",
                                3,
                                1,
                                Some("/repo/src/App.ts"),
                            ),
                        ],
                    ),
                ]),
            ),
            (
                other_root.clone(),
                HashMap::from([(
                    "file:///elsewhere/src/Other.ts".to_string(),
                    vec![lsp::SourceLspDiagnostic::for_test(
                        "error",
                        "Belongs to another project.",
                        1,
                        1,
                        Some("/elsewhere/src/Other.ts"),
                    )],
                )]),
            ),
        ];

        let collected = lsp::collect_diagnostics_for_root(&sessions, &inside_root);

        assert_eq!(collected.len(), 3);
        // Grouped by file, then in the order they appear down the file, so a problems
        // list can render them without sorting again.
        assert_eq!(collected[0].path.as_deref(), Some("/repo/src/App.ts"));
        assert_eq!(collected[0].message, "First on the same file.");
        assert_eq!(collected[1].path.as_deref(), Some("/repo/src/App.ts"));
        assert_eq!(collected[1].message, "Second on the same file.");
        assert_eq!(collected[2].path.as_deref(), Some("/repo/src/Later.ts"));
        assert!(collected
            .iter()
            .all(|diagnostic| diagnostic.message != "Belongs to another project."));

        // A root nobody has diagnostics for reports an empty list, not an error.
        assert!(
            lsp::collect_diagnostics_for_root(&sessions, &PathBuf::from("/nothing")).is_empty()
        );
        assert!(lsp::collect_diagnostics_for_root(&[], &inside_root).is_empty());
    }

    #[test]
    fn lsp_diagnostics_for_root_do_not_match_a_sibling_with_the_same_name_prefix() {
        let sessions = vec![(
            PathBuf::from("/repo-backup"),
            HashMap::from([(
                "file:///repo-backup/src/App.ts".to_string(),
                vec![lsp::SourceLspDiagnostic::for_test(
                    "error",
                    "Different repository.",
                    1,
                    1,
                    Some("/repo-backup/src/App.ts"),
                )],
            )]),
        )];

        assert!(lsp::collect_diagnostics_for_root(&sessions, &PathBuf::from("/repo")).is_empty());
    }

    #[test]
    fn kill_playwright_session_only_signals_the_group_that_was_asked_for() {
        let sessions = vec![
            PlaywrightSessionInfo {
                pgid: 100,
                label: "Playwright CLI server".to_string(),
                pids: vec![101, 102],
                processes: Vec::new(),
            },
            PlaywrightSessionInfo {
                pgid: 200,
                label: "Playwright MCP server".to_string(),
                pids: vec![201],
                processes: Vec::new(),
            },
        ];
        let mut signals = Vec::new();

        let result = kill_playwright_session_with(
            sessions,
            200,
            |pid, signal| {
                signals.push((pid, signal.to_string()));
                Ok(())
            },
            || {},
            || Ok(Vec::new()),
        )
        .unwrap();

        // Only the requested group is touched; the other session's pids are never signalled.
        assert_eq!(signals, vec![(201, "TERM".to_string())]);
        assert_eq!(result.terminated_pgids, vec![200]);
        assert_eq!(result.terminated_pids, vec![201]);
        assert!(result.failed_pgids.is_empty());
        assert_eq!(result.sessions.len(), 1);
        assert_eq!(result.sessions[0].pgid, 200);
    }

    #[test]
    fn kill_playwright_session_escalates_to_kill_when_term_leaves_it_running() {
        let sessions = vec![PlaywrightSessionInfo {
            pgid: 100,
            label: "Playwright CLI server".to_string(),
            pids: vec![101, 102],
            processes: Vec::new(),
        }];
        let survivors = vec![PlaywrightSessionInfo {
            pgid: 100,
            label: "Playwright CLI server".to_string(),
            pids: vec![102],
            processes: Vec::new(),
        }];
        let mut signals = Vec::new();

        kill_playwright_session_with(
            sessions,
            100,
            |pid, signal| {
                signals.push((pid, signal.to_string()));
                Ok(())
            },
            || {},
            || Ok(survivors.clone()),
        )
        .unwrap();

        assert_eq!(
            signals,
            vec![
                (101, "TERM".to_string()),
                (102, "TERM".to_string()),
                (102, "KILL".to_string()),
            ]
        );
    }

    #[test]
    fn kill_playwright_session_refuses_a_group_that_is_not_a_listed_playwright_session() {
        let sessions = vec![PlaywrightSessionInfo {
            pgid: 100,
            label: "Playwright CLI server".to_string(),
            pids: vec![101],
            processes: Vec::new(),
        }];

        for unlisted in [200, 0, -1, 1] {
            let mut signals = Vec::new();
            let error = kill_playwright_session_with(
                sessions.clone(),
                unlisted,
                |pid, signal| {
                    signals.push((pid, signal.to_string()));
                    Ok(())
                },
                || {},
                || Ok(Vec::new()),
            )
            .unwrap_err();

            assert!(
                error.contains("Playwright"),
                "unexpected message for {unlisted}: {error}"
            );
            assert!(
                signals.is_empty(),
                "nothing may be signalled for {unlisted}"
            );
        }
    }

    #[test]
    fn kill_playwright_sessions_reports_post_term_rescan_failure() {
        let sessions = vec![PlaywrightSessionInfo {
            pgid: 100,
            label: "Playwright CLI server".to_string(),
            pids: vec![101, 102],
            processes: Vec::new(),
        }];
        let mut signals = Vec::new();
        let mut list_calls = 0;

        let result = kill_playwright_sessions_with(
            sessions,
            |pid, signal| {
                signals.push((pid, signal.to_string()));
                Ok(())
            },
            || {},
            || {
                list_calls += 1;
                Err("ps unavailable".to_string())
            },
        )
        .unwrap();

        assert_eq!(
            signals,
            vec![(101, "TERM".to_string()), (102, "TERM".to_string())]
        );
        assert_eq!(list_calls, 1);
        assert_eq!(result.terminated_pgids, vec![100]);
        assert_eq!(result.terminated_pids, vec![101, 102]);
        assert_eq!(result.failed_pgids.len(), 1);
        assert_eq!(result.failed_pgids[0].pgid, 100);
        assert_eq!(result.failed_pgids[0].pid, None);
        assert!(result.failed_pgids[0].message.contains("ps unavailable"));
    }

    #[test]
    fn source_scan_groups_supported_files_and_skips_build_dirs() {
        let root = unique_temp_root();
        std::fs::create_dir_all(root.join("packages/ui")).unwrap();
        std::fs::create_dir_all(root.join("src/Workers")).unwrap();
        std::fs::create_dir_all(root.join("target/debug")).unwrap();
        std::fs::write(
            root.join("packages/ui/Button.tsx"),
            "export function Button() {}",
        )
        .unwrap();
        std::fs::write(root.join("Package.swift"), "let package = 1").unwrap();
        std::fs::write(root.join("src/App.svelte"), "<script></script>").unwrap();
        std::fs::write(root.join("src/settings.json"), "{}").unwrap();
        std::fs::write(root.join("src/Workers/Worker.cs"), "public class Worker {}").unwrap();
        std::fs::write(root.join("target/debug/generated.rs"), "fn generated() {}").unwrap();
        std::fs::write(root.join("README.md"), "# docs").unwrap();
        std::fs::write(root.join("notes.txt"), "plain notes").unwrap();

        let scan = list_source_files_sync(root.clone(), 20, None).unwrap();
        let relative_paths = scan
            .records
            .iter()
            .map(|file| file.relative_path.as_str())
            .collect::<Vec<_>>();

        assert_eq!(
            relative_paths,
            vec![
                "packages/ui/Button.tsx",
                "src/App.svelte",
                "src/Workers/Worker.cs",
                "Package.swift",
                "src/settings.json",
                "README.md"
            ]
        );
        assert_eq!(scan.stats.matched_files, 6);
        assert!(scan.stats.visited_entries >= 8);
        assert!(scan.stats.skipped_directories >= 1);
        assert!(scan.stats.unsupported_files >= 1);

        std::fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn source_image_read_returns_bytes_and_rejects_oversized_files() {
        let root = unique_temp_root();
        std::fs::create_dir_all(&root).unwrap();
        let image = root.join("preview.png");
        std::fs::write(&image, [0x89, b'P', b'N', b'G']).unwrap();
        assert_eq!(read_source_image_sync(image).unwrap(), [0x89, b'P', b'N', b'G']);

        let oversized = root.join("oversized.png");
        std::fs::File::create(&oversized)
            .unwrap()
            .set_len(MAX_SOURCE_IMAGE_BYTES + 1)
            .unwrap();
        assert!(read_source_image_sync(oversized)
            .unwrap_err()
            .contains("too large to preview"));

        std::fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn project_root_validation_rejects_missing_paths() {
        let root = unique_temp_root();

        let validation = validate_project_root_sync(root.clone());

        assert_eq!(validation.path, root.display().to_string());
        assert!(!validation.exists);
        assert!(!validation.is_directory);
        assert!(!validation.is_git_repository);
        assert_eq!(validation.git_root, None);
        assert!(validation.message.contains("not found"));
    }

    #[test]
    fn project_root_validation_rejects_files() {
        let root = unique_temp_root();
        std::fs::create_dir_all(&root).unwrap();
        let file_path = root.join("Project.csproj");
        std::fs::write(&file_path, "<Project />").unwrap();

        let validation = validate_project_root_sync(file_path.clone());

        assert_eq!(validation.path, file_path.display().to_string());
        assert!(validation.exists);
        assert!(!validation.is_directory);
        assert!(!validation.is_git_repository);
        assert_eq!(validation.git_root, None);
        assert!(validation.message.contains("file"));

        std::fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn project_root_validation_accepts_git_worktrees() {
        let root = unique_temp_root();
        std::fs::create_dir_all(&root).unwrap();
        std::fs::write(root.join(".git"), "gitdir: /tmp/repo/.git/worktrees/test\n").unwrap();

        let validation = validate_project_root_sync(root.clone());

        assert_eq!(validation.path, root.display().to_string());
        assert!(validation.exists);
        assert!(validation.is_directory);
        assert!(validation.is_git_repository);
        assert_eq!(validation.git_root, Some(root.display().to_string()));
        assert!(validation.message.contains("ready"));

        std::fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn source_scan_allows_worktree_ancestor_paths() {
        let root = unique_temp_root();
        let worktree_root = root
            .join("worktrees")
            .join("EdiPlatform")
            .join("tsk-127-source-scan");
        std::fs::create_dir_all(worktree_root.join("EdiPlatform.Core/Services")).unwrap();
        std::fs::create_dir_all(worktree_root.join("EdiPlatform.Api/Controllers")).unwrap();
        std::fs::write(
            worktree_root.join(".git"),
            "gitdir: /tmp/repo/.git/worktrees/tsk-127\n",
        )
        .unwrap();
        std::fs::write(
            worktree_root.join("EdiPlatform.Core/Services/FormatDetector.cs"),
            "public sealed class FormatDetector {}",
        )
        .unwrap();
        std::fs::write(
            worktree_root.join("EdiPlatform.Api/Controllers/DashboardController.cs"),
            "public sealed class DashboardController {}",
        )
        .unwrap();

        let scan = list_source_files_sync(worktree_root, 20, None).unwrap();
        let relative_paths = scan
            .records
            .iter()
            .map(|file| file.relative_path.as_str())
            .collect::<Vec<_>>();

        assert_eq!(
            relative_paths,
            vec![
                "EdiPlatform.Core/Services/FormatDetector.cs",
                "EdiPlatform.Api/Controllers/DashboardController.cs"
            ]
        );
        assert!(!scan.truncated);

        std::fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn project_root_validation_finds_parent_git_root() {
        let root = unique_temp_root();
        let nested = root.join("src").join("Services");
        std::fs::create_dir_all(&nested).unwrap();
        std::fs::write(root.join(".git"), "gitdir: /tmp/repo/.git/worktrees/test\n").unwrap();

        let validation = validate_project_root_sync(nested.clone());

        assert_eq!(validation.path, nested.display().to_string());
        assert!(validation.exists);
        assert!(validation.is_directory);
        assert!(!validation.is_git_repository);
        assert_eq!(validation.git_root, Some(root.display().to_string()));
        assert!(validation.message.contains("inside a Git repository"));

        std::fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn project_root_validation_warns_for_non_git_directories() {
        let root = unique_temp_root();
        std::fs::create_dir_all(&root).unwrap();

        let validation = validate_project_root_sync(root.clone());

        assert_eq!(validation.path, root.display().to_string());
        assert!(validation.exists);
        assert!(validation.is_directory);
        assert!(!validation.is_git_repository);
        assert_eq!(validation.git_root, None);
        assert!(validation.message.contains("not a Git repository"));

        std::fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn source_scan_prioritizes_app_source_before_docs_when_truncated() {
        let root = unique_temp_root();
        std::fs::create_dir_all(root.join("Docs")).unwrap();
        std::fs::create_dir_all(root.join("EdiPlatform.Core/Models")).unwrap();
        std::fs::create_dir_all(root.join("EdiPlatform.Core/Services")).unwrap();
        std::fs::create_dir_all(root.join("src/Services")).unwrap();
        std::fs::write(root.join("Docs/A.md"), "# docs").unwrap();
        std::fs::write(root.join("Docs/B.md"), "# docs").unwrap();
        std::fs::write(
            root.join("EdiPlatform.Core/Models/RuntimeModel.cs"),
            "public sealed class RuntimeModel {}",
        )
        .unwrap();
        std::fs::write(
            root.join("EdiPlatform.Core/Services/RuntimeService.cs"),
            "public sealed class RuntimeService {}",
        )
        .unwrap();
        std::fs::write(
            root.join("src/Services/FormatResolver.cs"),
            "public sealed class FormatResolver {}",
        )
        .unwrap();

        let scan = list_source_files_sync(root.clone(), 3, None).unwrap();
        let relative_paths = scan
            .records
            .iter()
            .map(|file| file.relative_path.as_str())
            .collect::<Vec<_>>();

        assert_eq!(
            relative_paths,
            vec![
                "EdiPlatform.Core/Models/RuntimeModel.cs",
                "EdiPlatform.Core/Services/RuntimeService.cs",
                "src/Services/FormatResolver.cs"
            ]
        );
        assert!(scan.truncated);
        assert!(scan
            .records
            .iter()
            .all(|record| !record.relative_path.starts_with("Docs/")));

        std::fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn source_scan_final_sort_keeps_src_before_project_named_shared_dirs_when_truncated() {
        let temp_root = unique_temp_root();
        let root = temp_root.join("Project");
        std::fs::create_dir_all(root.join("Project.Shared")).unwrap();
        std::fs::create_dir_all(root.join("src")).unwrap();
        std::fs::write(
            root.join("Project.Shared/Alpha.cs"),
            "namespace Project.Shared;\npublic sealed class Alpha {}",
        )
        .unwrap();
        std::fs::write(
            root.join("Project.Shared/Beta.cs"),
            "namespace Project.Shared;\npublic sealed class Beta {}",
        )
        .unwrap();
        std::fs::write(
            root.join("src/App.cs"),
            "namespace Project;\npublic sealed class App {}",
        )
        .unwrap();

        let scan = list_source_files_sync(root, 2, None).unwrap();

        assert_eq!(
            scan.records
                .iter()
                .map(|file| file.relative_path.as_str())
                .collect::<Vec<_>>(),
            vec!["src/App.cs", "Project.Shared/Alpha.cs"]
        );
        assert!(scan.truncated);

        std::fs::remove_dir_all(temp_root).unwrap();
    }

    #[test]
    fn source_scan_low_requested_limit_still_walks_beyond_returned_files() {
        let root = unique_temp_root();
        std::fs::create_dir_all(root.join("src")).unwrap();
        std::fs::create_dir_all(root.join("node_modules/pkg")).unwrap();
        std::fs::write(root.join("src/App.ts"), "export const app = true;").unwrap();
        std::fs::write(root.join("src/Worker.ts"), "export const worker = true;").unwrap();
        std::fs::write(root.join("src/Widget.ts"), "export const widget = true;").unwrap();
        std::fs::write(
            root.join("node_modules/pkg/index.ts"),
            "export const dependency = true;",
        )
        .unwrap();

        let scan = list_source_files_sync(root.clone(), 2, None).unwrap();

        assert_eq!(scan.records.len(), 2);
        assert!(scan.truncated);
        assert_eq!(scan.stats.requested_limit, 2);
        assert_eq!(scan.stats.returned_files, 2);
        assert_eq!(scan.stats.matched_files, 3);
        assert_eq!(scan.stats.collection_limit, 10_001);
        assert!(!scan.stats.collection_limit_reached);
        assert_eq!(scan.stats.skipped_directories, 1);
        assert_eq!(scan.stats.skipped_directory_samples[0].name, "node_modules");

        std::fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn source_scan_skips_tool_cache_and_coverage_dirs() {
        let root = unique_temp_root();
        std::fs::create_dir_all(root.join("src")).unwrap();
        std::fs::create_dir_all(root.join(".cache/generated")).unwrap();
        std::fs::create_dir_all(root.join(".history/Docs")).unwrap();
        std::fs::create_dir_all(root.join(".idea")).unwrap();
        std::fs::create_dir_all(root.join(".pytest_cache")).unwrap();
        std::fs::create_dir_all(root.join(".vscode")).unwrap();
        std::fs::create_dir_all(root.join(".turbo/cache")).unwrap();
        std::fs::create_dir_all(root.join(".parcel-cache")).unwrap();
        std::fs::create_dir_all(root.join(".nuxt")).unwrap();
        std::fs::create_dir_all(root.join(".vite/deps")).unwrap();
        std::fs::create_dir_all(root.join("coverage/lcov-report")).unwrap();
        std::fs::create_dir_all(root.join("DerivedData/Build")).unwrap();
        std::fs::create_dir_all(root.join("TestResults/run")).unwrap();
        std::fs::create_dir_all(root.join("Pods/SomeDependency")).unwrap();
        std::fs::write(root.join("src/Keep.ts"), "export const keep = true;").unwrap();
        std::fs::write(
            root.join(".cache/generated/Cache.ts"),
            "export const cache = true;",
        )
        .unwrap();
        std::fs::write(root.join(".history/Docs/Old.md"), "# old").unwrap();
        std::fs::write(root.join(".idea/workspace.xml"), "<project />").unwrap();
        std::fs::write(root.join(".pytest_cache/README.md"), "# cache").unwrap();
        std::fs::write(root.join(".vscode/settings.json"), "{}").unwrap();
        std::fs::write(
            root.join(".turbo/cache/Turbo.ts"),
            "export const turbo = true;",
        )
        .unwrap();
        std::fs::write(
            root.join(".parcel-cache/Parcel.ts"),
            "export const parcel = true;",
        )
        .unwrap();
        std::fs::write(root.join(".nuxt/App.vue"), "<template></template>").unwrap();
        std::fs::write(root.join(".vite/deps/Vite.ts"), "export const vite = true;").unwrap();
        std::fs::write(
            root.join("coverage/lcov-report/Coverage.ts"),
            "export const covered = true;",
        )
        .unwrap();
        std::fs::write(
            root.join("DerivedData/Build/Generated.swift"),
            "let generated = true",
        )
        .unwrap();
        std::fs::write(
            root.join("TestResults/run/TestLog.cs"),
            "public class TestLog {}",
        )
        .unwrap();
        std::fs::write(
            root.join("Pods/SomeDependency/Dependency.swift"),
            "let dependency = true",
        )
        .unwrap();

        let scan = list_source_files_sync(root.clone(), 20, None).unwrap();
        let relative_paths = scan
            .records
            .iter()
            .map(|file| file.relative_path.as_str())
            .collect::<Vec<_>>();

        assert_eq!(relative_paths, vec!["src/Keep.ts"]);

        std::fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn source_scan_reports_skipped_directory_samples() {
        let root = unique_temp_root();
        std::fs::create_dir_all(root.join(".git/objects")).unwrap();
        std::fs::create_dir_all(root.join("node_modules/pkg")).unwrap();
        std::fs::create_dir_all(root.join("worktrees/session/src")).unwrap();
        std::fs::create_dir_all(root.join("src")).unwrap();
        std::fs::write(
            root.join(".git/objects/Hidden.ts"),
            "export const hidden = true;",
        )
        .unwrap();
        std::fs::write(
            root.join("node_modules/pkg/index.ts"),
            "export const dependency = true;",
        )
        .unwrap();
        std::fs::write(
            root.join("worktrees/session/src/Stale.cs"),
            "public class Stale {}",
        )
        .unwrap();
        std::fs::write(root.join("src/Keep.ts"), "export const keep = true;").unwrap();

        let scan = list_source_files_sync(root.clone(), 20, None).unwrap();
        let skipped_names = scan
            .stats
            .skipped_directory_samples
            .iter()
            .map(|sample| sample.name.as_str())
            .collect::<std::collections::BTreeSet<_>>();

        assert_eq!(scan.records.len(), 1);
        assert_eq!(scan.stats.skipped_directories, 3);
        assert_eq!(
            skipped_names,
            std::collections::BTreeSet::from([".git", "node_modules", "worktrees"])
        );
        assert!(scan
            .stats
            .skipped_directory_samples
            .iter()
            .all(|sample| !sample.reason.is_empty()));

        std::fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn source_language_detection_covers_common_project_files() {
        assert_eq!(detect_language(Path::new("Program.cs")), "csharp");
        assert_eq!(detect_language(Path::new("Package.swift")), "swift");
        assert_eq!(detect_language(Path::new("main.rs")), "rust");
        assert_eq!(
            detect_language(Path::new("src/routes/+page.svelte")),
            "svelte"
        );
        assert_eq!(detect_language(Path::new("src/main.tsx")), "tsx");
        assert_eq!(detect_language(Path::new("src/app.jsx")), "jsx");
        assert_eq!(detect_language(Path::new("README.md")), "markdown");
        assert_eq!(detect_language(Path::new("package.json")), "json");
        assert_eq!(detect_language(Path::new("pnpm-lock.yaml")), "yaml");
        assert_eq!(detect_language(Path::new("Cargo.toml")), "toml");
        assert_eq!(detect_language(Path::new("scripts/build.sh")), "shell");
        assert_eq!(detect_language(Path::new("tools/import.py")), "python");
        assert_eq!(detect_language(Path::new("Dockerfile")), "dockerfile");
        assert_eq!(detect_language(Path::new("Makefile")), "makefile");
        assert_eq!(detect_language(Path::new("EdiPlatform.Api.csproj")), "xml");
    }

    #[test]
    fn source_language_detection_covers_more_preview_formats() {
        assert_eq!(detect_language(Path::new("web/App.vue")), "html");
        assert_eq!(detect_language(Path::new("web/Page.astro")), "html");
        assert_eq!(detect_language(Path::new("index.php")), "php");
        assert_eq!(detect_language(Path::new("lib/main.dart")), "dart");
        assert_eq!(detect_language(Path::new("scripts/tool.lua")), "lua");
        assert_eq!(detect_language(Path::new("infra/main.tf")), "hcl");
        assert_eq!(detect_language(Path::new("infra/dev.tfvars")), "hcl");
        assert_eq!(
            detect_language(Path::new("schemas/service.proto")),
            "protobuf"
        );
    }

    #[test]
    fn source_scan_filters_by_query() {
        let root = unique_temp_root();
        std::fs::create_dir_all(root.join("src/Workers")).unwrap();
        std::fs::write(root.join("src/App.svelte"), "<script></script>").unwrap();
        std::fs::write(root.join("src/Workers/Worker.cs"), "public class Worker {}").unwrap();

        let scan = list_source_files_sync(root.clone(), 20, Some("worker".to_string())).unwrap();

        assert_eq!(scan.records.len(), 1);
        assert_eq!(scan.records[0].relative_path, "src/Workers/Worker.cs");

        std::fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn source_scan_default_limit_covers_large_project_trees() {
        let root = unique_temp_root();
        std::fs::create_dir_all(root.join("src")).unwrap();
        for index in 0..350 {
            std::fs::write(
                root.join(format!("src/File{index:03}.ts")),
                "export const value = 1;",
            )
            .unwrap();
        }

        let scan = list_source_files_sync(root.clone(), 0, None).unwrap();

        assert_eq!(scan.records.len(), 350);
        assert!(!scan.truncated);

        std::fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn source_scan_allows_explicit_limits_above_the_default_preview_cap() {
        let root = unique_temp_root();
        std::fs::create_dir_all(root.join("src")).unwrap();
        for index in 0..1_200 {
            std::fs::write(
                root.join(format!("src/File{index:04}.ts")),
                "export const value = 1;",
            )
            .unwrap();
        }

        let scan = list_source_files_sync(root.clone(), 1_200, None).unwrap();

        assert_eq!(scan.records.len(), 1_200);
        assert!(!scan.truncated);

        std::fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn source_scan_reports_when_the_limit_was_reached() {
        let root = unique_temp_root();
        std::fs::create_dir_all(root.join("src")).unwrap();
        std::fs::write(root.join("src/A.ts"), "export const a = 1;").unwrap();
        std::fs::write(root.join("src/B.ts"), "export const b = 1;").unwrap();

        let scan = list_source_files_sync(root.clone(), 1, None).unwrap();

        assert_eq!(scan.records.len(), 1);
        assert_eq!(scan.limit, 1);
        assert!(scan.truncated);
        assert_eq!(scan.stats.requested_limit, 1);
        assert_eq!(scan.stats.returned_files, 1);
        assert_eq!(scan.stats.matched_files, 2);

        std::fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn source_scan_can_be_cancelled_before_collection() {
        let root = unique_temp_root();
        std::fs::create_dir_all(root.join("src")).unwrap();
        std::fs::write(root.join("src/A.ts"), "export const a = 1;").unwrap();

        let cancellation = SourceScanCancellation::cancelled_for_test();
        let error = list_source_files_sync_with_cancellation(root.clone(), 20, None, cancellation)
            .unwrap_err();

        assert!(error.contains("Source scan cancelled"));

        std::fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn source_scan_reports_progress_during_collection() {
        let root = unique_temp_root();
        std::fs::create_dir_all(root.join("src")).unwrap();
        std::fs::write(root.join("src/A.ts"), "export const a = 1;").unwrap();
        std::fs::write(root.join("src/B.ts"), "export const b = 1;").unwrap();

        let progress = std::sync::Arc::new(std::sync::atomic::AtomicUsize::new(0));
        let progress_clone = std::sync::Arc::clone(&progress);
        let cancellation = SourceScanCancellation::active_for_test(move |_| {
            progress_clone.fetch_add(1, std::sync::atomic::Ordering::Relaxed);
        });

        let scan =
            list_source_files_sync_with_cancellation(root.clone(), 20, None, cancellation).unwrap();

        assert_eq!(scan.records.len(), 2);
        assert!(progress.load(std::sync::atomic::Ordering::Relaxed) > 0);

        std::fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn source_write_file_persists_utf8_content_and_returns_preview() {
        let root = unique_temp_root();
        std::fs::create_dir_all(root.join("src")).unwrap();
        let file_path = root.join("src/App.ts");
        std::fs::write(&file_path, "export const oldValue = 1;\n").unwrap();
        let original = read_source_file_sync(file_path.clone()).unwrap();

        let preview = write_source_file_sync(
            file_path.clone(),
            "export const newValue = 2;\n".to_string(),
            original.revision,
        )
        .unwrap();

        assert_eq!(
            std::fs::read_to_string(&file_path).unwrap(),
            "export const newValue = 2;\n"
        );
        assert_eq!(preview.path, file_path.display().to_string());
        assert_eq!(preview.file_name, "App.ts");
        assert_eq!(preview.language, "typescript");
        assert_eq!(preview.line_count, 1);
        assert_eq!(preview.content, "export const newValue = 2;\n");

        std::fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn source_write_accepts_plain_utf8_and_rejects_a_stale_revision() {
        let root = unique_temp_root();
        std::fs::create_dir_all(&root).unwrap();
        let file_path = root.join("notes.txt");
        std::fs::write(&file_path, "BASE\n").unwrap();
        let opened = read_source_file_sync(file_path.clone()).unwrap();

        std::fs::write(&file_path, "EXTERNAL\n").unwrap();
        let error = write_source_file_sync(
            file_path.clone(),
            "LOCAL\n".to_string(),
            opened.revision,
        )
        .unwrap_err();

        assert_eq!(error, "Source file changed on disk since it was opened.");
        assert_eq!(std::fs::read_to_string(&file_path).unwrap(), "EXTERNAL\n");

        let latest = read_source_file_sync(file_path.clone()).unwrap();
        let saved = write_source_file_sync(file_path.clone(), "LOCAL\n".to_string(), latest.revision)
            .unwrap();
        assert_eq!(saved.language, "plain");
        assert_eq!(saved.content, "LOCAL\n");
        assert_eq!(std::fs::read_to_string(&file_path).unwrap(), "LOCAL\n");

        std::fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn source_file_action_builds_open_and_reveal_commands() {
        let root = unique_temp_root();
        std::fs::create_dir_all(root.join("src")).unwrap();
        let file_path = root.join("src/App.svelte");
        std::fs::write(&file_path, "<script></script>").unwrap();

        let open_command = source_file_action_command(&file_path, SourceFileAction::Open).unwrap();
        assert_eq!(open_command.program, "open");
        assert_eq!(open_command.args, vec![file_path.display().to_string()]);

        let reveal_command =
            source_file_action_command(&file_path, SourceFileAction::Reveal).unwrap();
        assert_eq!(reveal_command.program, "open");
        assert_eq!(
            reveal_command.args,
            vec!["-R".to_string(), file_path.display().to_string()]
        );

        std::fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn source_file_action_rejects_directories() {
        let root = unique_temp_root();
        std::fs::create_dir_all(root.join("src")).unwrap();

        let error = source_file_action_command(&root, SourceFileAction::Open).unwrap_err();
        assert!(error.contains("Source path is not a file"));

        std::fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn path_action_builds_open_and_reveal_commands_for_files_and_directories() {
        let root = unique_temp_root();
        std::fs::create_dir_all(root.join("src")).unwrap();
        let file_path = root.join("src/App.svelte");
        std::fs::write(&file_path, "<script></script>").unwrap();

        let open_dir_command = path_action_command(&root, PathAction::Open).unwrap();
        assert_eq!(open_dir_command.program, "open");
        assert_eq!(open_dir_command.args, vec![root.display().to_string()]);

        let reveal_dir_command = path_action_command(&root, PathAction::Reveal).unwrap();
        assert_eq!(reveal_dir_command.program, "open");
        assert_eq!(
            reveal_dir_command.args,
            vec!["-R".to_string(), root.display().to_string()]
        );

        let open_file_command = path_action_command(&file_path, PathAction::Open).unwrap();
        assert_eq!(open_file_command.program, "open");
        assert_eq!(
            open_file_command.args,
            vec![file_path.display().to_string()]
        );

        std::fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn path_action_rejects_missing_paths() {
        let root = unique_temp_root();
        let error = path_action_command(&root.join("missing"), PathAction::Open).unwrap_err();

        assert!(error.contains("Could not read path metadata"));
    }

    #[test]
    fn terminal_path_action_builds_allowlisted_terminal_commands_for_directories() {
        let root = unique_temp_root();
        std::fs::create_dir_all(root.join("src")).unwrap();

        let warp_command = terminal_path_action_command(&root, Some("Warp".to_string())).unwrap();
        assert_eq!(warp_command.program, "open");
        assert_eq!(
            warp_command.args,
            vec![format!(
                "warp://action/new_tab?path={}",
                uri_query_encode(&root.display().to_string())
            )]
        );

        let default_command = terminal_path_action_command(&root, None).unwrap();
        assert_eq!(
            default_command.args,
            vec![
                "-a".to_string(),
                "Terminal".to_string(),
                root.display().to_string()
            ]
        );

        std::fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn terminal_path_action_rejects_files_missing_paths_and_unknown_apps() {
        let root = unique_temp_root();
        std::fs::create_dir_all(root.join("src")).unwrap();
        let file_path = root.join("src/App.svelte");
        std::fs::write(&file_path, "<script></script>").unwrap();

        let file_error =
            terminal_path_action_command(&file_path, Some("Warp".to_string())).unwrap_err();
        assert!(file_error.contains("Terminal path is not a directory"));

        let missing_error =
            terminal_path_action_command(&root.join("missing"), Some("Warp".to_string()))
                .unwrap_err();
        assert!(missing_error.contains("Could not read terminal path metadata"));

        let app_error =
            terminal_path_action_command(&root, Some("UnknownTerminal".to_string())).unwrap_err();
        assert!(app_error.contains("Unsupported terminal app"));

        std::fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn terminal_command_action_builds_osascript_for_terminal_and_iterm() {
        let root = unique_temp_root();
        let quoted_root = shell_quote(&root.display().to_string());
        std::fs::create_dir_all(&root).unwrap();

        let terminal_command = terminal_command_action_command(
            &root,
            "codex resume session-123",
            Some("Terminal".to_string()),
        )
        .unwrap();
        assert_eq!(terminal_command.program, "osascript");
        assert_eq!(terminal_command.args[0], "-e");
        assert!(terminal_command.args[1].contains("tell application \"Terminal\""));
        assert!(terminal_command.args[1]
            .contains(&format!("cd {quoted_root} && codex resume session-123")));

        let iterm_command = terminal_command_action_command(
            &root,
            "claude --resume abc",
            Some("iTerm2".to_string()),
        )
        .unwrap();
        assert_eq!(iterm_command.program, "osascript");
        assert!(iterm_command.args[1].contains("tell application \"iTerm2\""));
        assert!(iterm_command.args[1].contains("write text"));
        assert!(iterm_command.args[1].contains(&format!("cd {quoted_root} && claude --resume abc")));

        std::fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn terminal_command_action_rejects_unsafe_or_unsupported_commands() {
        let root = unique_temp_root();
        std::fs::create_dir_all(&root).unwrap();

        let empty_error =
            terminal_command_action_command(&root, "   ", Some("Terminal".to_string()))
                .unwrap_err();
        assert!(empty_error.contains("Terminal command is empty"));

        let multiline_error = terminal_command_action_command(
            &root,
            "codex resume one\nrm -rf nope",
            Some("Terminal".to_string()),
        )
        .unwrap_err();
        assert!(multiline_error.contains("single line"));

        let unsupported_error = terminal_command_action_command(
            &root,
            "codex resume session-123",
            Some("Warp".to_string()),
        )
        .unwrap_err();
        assert!(unsupported_error.contains("not supported for Warp"));

        std::fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn git_status_parser_reads_branch_counts_and_file_badges() {
        let status = parse_project_git_status(
            "## main...origin/main [ahead 1, behind 2]\n M src/App.ts\nA  src/Added.ts\n?? src/New.ts\n",
        )
        .unwrap();

        assert_eq!(status.branch.as_deref(), Some("main"));
        assert_eq!(status.ahead, 1);
        assert_eq!(status.behind, 2);
        assert!(status.has_upstream);
        assert_eq!(
            status
                .files
                .iter()
                .map(|file| (
                    file.relative_path.as_str(),
                    file.index_status.as_str(),
                    file.worktree_status.as_str(),
                    file.badge.as_str(),
                    file.status.as_str()
                ))
                .collect::<Vec<_>>(),
            vec![
                ("src/App.ts", "", "modified", "M", "modified"),
                ("src/Added.ts", "added", "", "A", "added"),
                ("src/New.ts", "untracked", "untracked", "?", "untracked")
            ]
        );
    }

    #[test]
    fn parse_git_numstat_totals_sums_text_files_and_skips_binary() {
        let output = "12\t3\tsrc/a.ts\n-\t-\tlogo.png\n6\t8\tsrc/b.rs\n0\t0\tempty\n";
        assert_eq!(parse_git_numstat_totals(output), (18, 11));
        assert_eq!(parse_git_numstat_totals(""), (0, 0));
    }

    #[test]
    fn git_status_parser_handles_clean_branch_header() {
        let status = parse_project_git_status("## feature/source-browser\n").unwrap();

        assert_eq!(status.branch.as_deref(), Some("feature/source-browser"));
        assert_eq!(status.ahead, 0);
        assert_eq!(status.behind, 0);
        assert!(!status.has_upstream);
        assert!(status.files.is_empty());
    }

    #[test]
    fn git_status_parser_caps_files_and_keeps_the_total() {
        let mut output = String::from("## main\n");
        for index in 0..MAX_GIT_STATUS_FILES + 5 {
            output.push_str(&format!("?? new-{index}.txt\n"));
        }

        let status = parse_project_git_status(&output).unwrap();

        assert_eq!(status.files.len(), MAX_GIT_STATUS_FILES);
        assert_eq!(status.total_files, MAX_GIT_STATUS_FILES + 5);
        assert_eq!(status.files[0].relative_path, "new-0.txt");
    }

    #[test]
    fn parse_git_branch_header_reads_upstream_backed_branch() {
        let parsed = parse_git_branch_header("main...origin/main");

        assert_eq!(parsed.branch.as_deref(), Some("main"));
        assert!(parsed.has_upstream);
        assert_eq!(parsed.ahead, 0);
        assert_eq!(parsed.behind, 0);
    }

    #[test]
    fn parse_git_branch_header_reads_no_upstream_branch() {
        let parsed = parse_git_branch_header("feature/source-browser");

        assert_eq!(parsed.branch.as_deref(), Some("feature/source-browser"));
        assert!(!parsed.has_upstream);
        assert_eq!(parsed.ahead, 0);
        assert_eq!(parsed.behind, 0);
    }

    #[test]
    fn parse_git_branch_header_reads_ahead_behind_and_diverged_counts() {
        let ahead = parse_git_branch_header("feature...origin/feature [ahead 1]");
        assert_eq!(ahead.branch.as_deref(), Some("feature"));
        assert!(ahead.has_upstream);
        assert_eq!(ahead.ahead, 1);
        assert_eq!(ahead.behind, 0);

        let behind = parse_git_branch_header("feature...origin/feature [behind 2]");
        assert_eq!(behind.branch.as_deref(), Some("feature"));
        assert!(behind.has_upstream);
        assert_eq!(behind.ahead, 0);
        assert_eq!(behind.behind, 2);

        let diverged = parse_git_branch_header("feature...origin/feature [ahead 3, behind 4]");
        assert_eq!(diverged.branch.as_deref(), Some("feature"));
        assert!(diverged.has_upstream);
        assert_eq!(diverged.ahead, 3);
        assert_eq!(diverged.behind, 4);
    }

    #[test]
    fn parse_git_branch_header_does_not_infer_upstream_for_detached_or_no_commits() {
        let detached = parse_git_branch_header("HEAD (no branch)");
        assert_eq!(detached.branch.as_deref(), Some("HEAD (no branch)"));
        assert!(!detached.has_upstream);

        let no_commits = parse_git_branch_header("No commits yet on feature/source-browser");
        assert_eq!(no_commits.branch.as_deref(), Some("feature/source-browser"));
        assert!(!no_commits.has_upstream);
    }

    #[test]
    fn branch_diff_covers_commits_and_working_changes_since_the_default_branch() {
        let root = unique_temp_root();
        std::fs::create_dir_all(&root).unwrap();
        let commit = |message: &str| {
            run_git_for_test(&root, &["add", "."]);
            run_git_for_test(
                &root,
                &["-c", "user.name=T", "-c", "user.email=t@example.invalid", "commit", "-m", message],
            );
        };
        run_git_for_test(&root, &["init", "-b", "main"]);
        std::fs::write(root.join("a.txt"), "one\n").unwrap();
        commit("initial");
        run_git_for_test(&root, &["update-ref", "refs/remotes/origin/main", "HEAD"]);
        run_git_for_test(&root, &["symbolic-ref", "refs/remotes/origin/HEAD", "refs/remotes/origin/main"]);
        run_git_for_test(&root, &["checkout", "-b", "feature"]);
        std::fs::write(root.join("a.txt"), "two\n").unwrap();
        commit("committed change");
        std::fs::write(root.join("b.txt"), "unstaged\n").unwrap();
        run_git_for_test(&root, &["add", "-N", "b.txt"]);
        std::fs::write(root.join("a.txt"), "three\n").unwrap();

        let branch = read_git_branch_diff_sync(root.clone()).unwrap();

        assert_eq!(branch.base, "origin/main");
        assert!(branch.diff.contains("-one"), "{}", branch.diff);
        assert!(branch.diff.contains("+three"), "{}", branch.diff);
        assert!(branch.diff.contains("+unstaged"), "{}", branch.diff);

        run_git_for_test(&root, &["symbolic-ref", "--delete", "refs/remotes/origin/HEAD"]);
        assert!(read_git_branch_diff_sync(root.clone()).unwrap_err().contains("origin/HEAD"));
        std::fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn source_git_diff_reads_selected_file_worktree_diff() {
        let root = unique_temp_root();
        std::fs::create_dir_all(root.join("src")).unwrap();
        let file_path = root.join("src/App.ts");
        std::fs::write(&file_path, "export const value = 1;\n").unwrap();

        run_git_for_test(&root, &["init"]);
        run_git_for_test(&root, &["config", "user.name", "MacCommandBar Test"]);
        run_git_for_test(&root, &["config", "user.email", "test@example.invalid"]);
        run_git_for_test(&root, &["add", "src/App.ts"]);
        run_git_for_test(
            &root,
            &[
                "-c",
                "user.name=MacCommandBar Test",
                "-c",
                "user.email=test@example.invalid",
                "commit",
                "-m",
                "initial",
            ],
        );
        std::fs::write(&file_path, "export const value = 2;\n").unwrap();

        let diff = read_source_git_diff_sync(root.clone(), file_path).unwrap();

        assert_eq!(diff.relative_path, "src/App.ts");
        assert_eq!(diff.status, "modified");
        assert!(!diff.is_binary);
        assert!(diff.diff.contains("-export const value = 1;"));
        assert!(diff.diff.contains("+export const value = 2;"));

        std::fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn source_git_diff_reads_deleted_file_from_head() {
        let root = unique_temp_root();
        std::fs::create_dir_all(&root).unwrap();
        let file_path = root.join("TODO.md");
        std::fs::write(&file_path, "one\ntwo\n").unwrap();

        run_git_for_test(&root, &["init"]);
        run_git_for_test(&root, &["add", "TODO.md"]);
        run_git_for_test(
            &root,
            &[
                "-c",
                "user.name=MacCommandBar Test",
                "-c",
                "user.email=test@example.invalid",
                "commit",
                "-m",
                "initial",
            ],
        );
        std::fs::remove_file(&file_path).unwrap();

        let diff = read_source_git_diff_sync(root.clone(), file_path).unwrap();

        assert_eq!(diff.relative_path, "TODO.md");
        assert_eq!(diff.status, "deleted");
        assert_eq!(diff.original_content.as_deref(), Some("one\ntwo\n"));
        assert_eq!(diff.modified_content.as_deref(), Some(""));
        assert!(diff.diff.contains("-one"));
        assert!(diff.diff.contains("-two"));

        std::fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn git_repository_summary_counts_dirty_files_and_task_branch() {
        let root = unique_temp_root();
        std::fs::create_dir_all(&root).unwrap();
        let readme_path = root.join("README.md");
        std::fs::write(&readme_path, "initial\n").unwrap();

        run_git_for_test(&root, &["init"]);
        run_git_for_test(&root, &["add", "README.md"]);
        run_git_for_test(
            &root,
            &[
                "-c",
                "user.name=MacCommandBar Test",
                "-c",
                "user.email=test@example.invalid",
                "commit",
                "-m",
                "initial",
            ],
        );
        run_git_for_test(&root, &["checkout", "-b", "tsk-127-repo-dashboard"]);

        std::fs::write(root.join("staged.txt"), "staged\n").unwrap();
        run_git_for_test(&root, &["add", "staged.txt"]);
        std::fs::write(&readme_path, "changed\n").unwrap();
        std::fs::write(root.join("untracked.txt"), "untracked\n").unwrap();

        let project = RuntimeContextProject {
            id: "mac-command-bar".to_string(),
            name: "MacCommandBar".to_string(),
            path: root.display().to_string(),
        };
        let summaries = list_git_repository_summaries_sync(vec![project]).unwrap();
        let summary = summaries
            .iter()
            .find(|summary| summary.path == root.display().to_string())
            .unwrap();

        assert_eq!(summary.project_id, "mac-command-bar");
        assert_eq!(summary.project_name, "MacCommandBar");
        assert_eq!(summary.branch, "tsk-127-repo-dashboard");
        assert_eq!(summary.task_id.as_deref(), Some("TSK-127"));
        assert_eq!(summary.staged_count, 1);
        assert_eq!(summary.unstaged_count, 1);
        assert_eq!(summary.untracked_count, 1);
        assert_eq!(summary.dirty_count, 3);
        assert!(!summary.has_upstream);
        assert!(summary.is_dirty);
        assert_eq!(summary.last_commit_subject.as_deref(), Some("initial"));
        assert!(summary
            .last_commit_sha
            .as_ref()
            .is_some_and(|sha| !sha.is_empty()));
        assert!(summary.dirty_since_epoch_ms.is_some());
        assert_ne!(
            summary.dirty_status_fingerprint,
            git_dirty_status_fingerprint(&[]),
            "dirty repositories should include a dirty status fingerprint"
        );

        std::fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn git_dirty_status_fingerprint_changes_for_path_or_status_changes() {
        let first = git_dirty_status_fingerprint(&[GitFileStatus {
            relative_path: "src/App.ts".to_string(),
            index_status: "".to_string(),
            worktree_status: "modified".to_string(),
            status: "modified".to_string(),
            badge: "M".to_string(),
        }]);
        let renamed_path = git_dirty_status_fingerprint(&[GitFileStatus {
            relative_path: "src/Renamed.ts".to_string(),
            index_status: "".to_string(),
            worktree_status: "modified".to_string(),
            status: "modified".to_string(),
            badge: "M".to_string(),
        }]);
        let staged = git_dirty_status_fingerprint(&[GitFileStatus {
            relative_path: "src/App.ts".to_string(),
            index_status: "modified".to_string(),
            worktree_status: "".to_string(),
            status: "modified".to_string(),
            badge: "M".to_string(),
        }]);

        assert_eq!(
            git_dirty_status_fingerprint(&[]),
            git_dirty_status_fingerprint(&[])
        );
        assert_ne!(first, renamed_path);
        assert_ne!(first, staged);
    }

    #[test]
    fn git_repository_summary_infers_task_from_last_commit_subject() {
        let root = unique_temp_root();
        std::fs::create_dir_all(&root).unwrap();
        std::fs::write(root.join("README.md"), "initial\n").unwrap();

        run_git_for_test(&root, &["init"]);
        run_git_for_test(&root, &["add", "README.md"]);
        run_git_for_test(
            &root,
            &[
                "-c",
                "user.name=MacCommandBar Test",
                "-c",
                "user.email=test@example.invalid",
                "commit",
                "-m",
                "feat: add TSK-127 repo task links",
            ],
        );

        let project = RuntimeContextProject {
            id: "mac-command-bar".to_string(),
            name: "MacCommandBar".to_string(),
            path: root.display().to_string(),
        };
        let summary = git_repository_summary_for_path_result(&project, &root, false).unwrap();

        assert_eq!(summary.task_id.as_deref(), Some("TSK-127"));
        assert_eq!(
            summary.last_commit_subject.as_deref(),
            Some("feat: add TSK-127 repo task links")
        );

        std::fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn git_commit_history_reads_graph_metadata_and_task_ids() {
        let root = unique_temp_root();
        std::fs::create_dir_all(&root).unwrap();
        std::fs::write(root.join("README.md"), "initial\n").unwrap();

        run_git_for_test(&root, &["init"]);
        run_git_for_test(&root, &["config", "user.name", "MacCommandBar Test"]);
        run_git_for_test(&root, &["config", "user.email", "test@example.invalid"]);
        run_git_for_test(&root, &["add", "README.md"]);
        run_git_for_test(&root, &["commit", "-m", "initial"]);

        std::fs::write(root.join("README.md"), "dashboard\n").unwrap();
        run_git_for_test(&root, &["commit", "-am", "[TSK-127] add dashboard"]);

        std::fs::write(root.join("README.md"), "remote\n").unwrap();
        run_git_for_test(
            &root,
            &["commit", "-am", "feat: add TSK-128 remote controls"],
        );

        run_git_for_test(&root, &["checkout", "-b", "tsk-129-history"]);
        std::fs::write(root.join("README.md"), "history\n").unwrap();
        run_git_for_test(&root, &["commit", "-am", "history panel"]);

        let history = read_git_commit_history_page_sync(root.clone(), None, None)
            .unwrap()
            .commits;

        assert_eq!(history.len(), 4);
        assert_eq!(history[0].subject, "history panel");
        assert_eq!(history[0].task_id.as_deref(), Some("TSK-129"));
        assert!(history[0].refs.contains("HEAD -> tsk-129-history"));
        assert!(!history[0].short_sha.is_empty());
        assert!(!history[0].sha.is_empty());
        assert_eq!(history[0].author, "MacCommandBar Test");
        assert!(!history[0].committed_at.is_empty());
        assert_eq!(history[0].parent_count, 1);
        assert_eq!(history[0].parent_shas.len(), 1);
        assert_eq!(history[0].task_source.as_deref(), Some("refs"));
        assert_eq!(history[1].task_id.as_deref(), Some("TSK-128"));
        assert_eq!(history[1].task_source.as_deref(), Some("subject"));
        assert_eq!(history[2].task_id.as_deref(), Some("TSK-127"));
        assert_eq!(history[3].task_id, None);
        assert_eq!(history[3].parent_count, 0);

        std::fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn git_commit_history_parser_tracks_merge_parent_metadata() {
        let output = "abc1234\x1fabc1234abc1234abc1234abc1234abc1234abc1234\x1fmerge TSK-127 pane work\x1fMacCommandBar Test\x1f2026-06-11T12:00:00Z\x1fHEAD -> main\x1fparent-one parent-two\n";

        let history = parse_git_commit_history(output).unwrap();

        assert_eq!(history.len(), 1);
        assert_eq!(history[0].parent_count, 2);
        assert_eq!(
            history[0].parent_shas,
            vec!["parent-one".to_string(), "parent-two".to_string()]
        );
        assert_eq!(history[0].task_id.as_deref(), Some("TSK-127"));
        assert_eq!(history[0].task_source.as_deref(), Some("subject"));
    }

    #[test]
    fn git_actions_stage_unstage_and_commit_repo_paths() {
        let root = unique_temp_root();
        std::fs::create_dir_all(root.join("src")).unwrap();
        let file_path = root.join("src/App.ts");
        std::fs::write(&file_path, "export const value = 1;\n").unwrap();

        run_git_for_test(&root, &["init"]);
        run_git_for_test(&root, &["config", "user.name", "MacCommandBar Test"]);
        run_git_for_test(&root, &["config", "user.email", "test@example.invalid"]);
        run_git_for_test(&root, &["add", "src/App.ts"]);
        run_git_for_test(
            &root,
            &[
                "-c",
                "user.name=MacCommandBar Test",
                "-c",
                "user.email=test@example.invalid",
                "commit",
                "-m",
                "initial",
            ],
        );
        std::fs::write(&file_path, "export const value = 2;\n").unwrap();

        let staged = stage_git_paths_sync(root.clone(), vec!["src/App.ts".to_string()]).unwrap();
        assert_eq!(staged.message, "Staged 1 path");
        assert_eq!(staged.status.files[0].index_status, "modified");
        assert_eq!(staged.status.files[0].worktree_status, "");

        let unstaged =
            unstage_git_paths_sync(root.clone(), vec!["src/App.ts".to_string()]).unwrap();
        assert_eq!(unstaged.message, "Unstaged 1 path");
        assert_eq!(unstaged.status.files[0].index_status, "");
        assert_eq!(unstaged.status.files[0].worktree_status, "modified");

        stage_git_paths_sync(root.clone(), vec!["src/App.ts".to_string()]).unwrap();
        let committed =
            commit_git_repository_sync(root.clone(), "Update app value".to_string()).unwrap();
        assert_eq!(committed.message, "Committed staged changes");
        assert!(committed.status.files.is_empty());
        let latest_subject = run_git_text(&root, &["log", "-1", "--format=%s"]).unwrap();
        assert_eq!(latest_subject.trim(), "Update app value");

        let empty_message_error =
            commit_git_repository_sync(root.clone(), "   ".to_string()).unwrap_err();
        assert!(empty_message_error.contains("Commit message is required"));

        let no_staged_error =
            commit_git_repository_sync(root.clone(), "No staged changes".to_string()).unwrap_err();
        assert!(no_staged_error.contains("No staged changes to commit"));

        let outside_path_error =
            stage_git_paths_sync(root.clone(), vec!["../outside.txt".to_string()]).unwrap_err();
        assert!(outside_path_error.contains("relative repo paths"));

        std::fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn git_remote_actions_fetch_pull_and_push_current_upstream() {
        let remote = unique_temp_root();
        let local = unique_temp_root();
        let peer = unique_temp_root();

        std::fs::create_dir_all(&remote).unwrap();
        std::fs::create_dir_all(&peer).unwrap();
        run_git_for_test(&remote, &["init", "--bare"]);
        std::fs::create_dir_all(local.join("src")).unwrap();
        std::fs::write(local.join("src/App.ts"), "export const value = 1;\n").unwrap();
        run_git_for_test(&local, &["init"]);
        run_git_for_test(&local, &["config", "user.name", "MacCommandBar Test"]);
        run_git_for_test(&local, &["config", "user.email", "test@example.invalid"]);
        run_git_for_test(&local, &["add", "src/App.ts"]);
        run_git_for_test(&local, &["commit", "-m", "initial"]);
        run_git_for_test(&local, &["branch", "-M", "main"]);
        let remote_path = remote.display().to_string();
        run_git_for_test(&local, &["remote", "add", "origin", remote_path.as_str()]);
        run_git_for_test(&local, &["push", "-u", "origin", "main"]);

        let remote_path = remote.display().to_string();
        run_git_for_test(&peer, &["clone", remote_path.as_str(), "."]);
        run_git_for_test(&peer, &["config", "user.name", "MacCommandBar Test"]);
        run_git_for_test(&peer, &["config", "user.email", "test@example.invalid"]);
        std::fs::write(peer.join("src/App.ts"), "export const value = 2;\n").unwrap();
        run_git_for_test(&peer, &["commit", "-am", "remote update"]);
        run_git_for_test(&peer, &["push"]);

        let fetched = fetch_git_repository_sync(local.clone()).unwrap();
        assert_eq!(fetched.message, "Fetched repository remotes");
        assert_eq!(fetched.status.behind, 1);

        let pulled = pull_git_repository_sync(local.clone()).unwrap();
        assert_eq!(pulled.message, "Pulled fast-forward updates");
        assert_eq!(pulled.status.behind, 0);
        assert_eq!(
            std::fs::read_to_string(local.join("src/App.ts")).unwrap(),
            "export const value = 2;\n"
        );

        std::fs::write(local.join("src/App.ts"), "export const value = 3;\n").unwrap();
        stage_git_paths_sync(local.clone(), vec!["src/App.ts".to_string()]).unwrap();
        commit_git_repository_sync(local.clone(), "local update".to_string()).unwrap();
        let ahead_status = project_git_status_sync(local.clone()).unwrap();
        assert_eq!(ahead_status.ahead, 1);

        let pushed = push_git_repository_sync(local.clone()).unwrap();
        assert_eq!(pushed.message, "Pushed current branch");
        assert_eq!(pushed.status.ahead, 0);
        let remote_subject = run_git_text(&remote, &["log", "-1", "--format=%s", "main"]).unwrap();
        assert_eq!(remote_subject.trim(), "local update");

        std::fs::remove_dir_all(remote).unwrap();
        std::fs::remove_dir_all(local).unwrap();
        std::fs::remove_dir_all(peer).unwrap();
    }

    #[test]
    fn git_add_remote_then_publish() {
        let origin = unique_temp_root();
        let local = unique_temp_root();
        for path in [&origin, &local] {
            std::fs::create_dir_all(path).unwrap();
        }
        run_git_for_test(&origin, &["init", "--bare"]);
        run_git_for_test(&local, &["init"]);
        run_git_for_test(&local, &["config", "user.name", "MacCommandBar Test"]);
        run_git_for_test(&local, &["config", "user.email", "test@example.invalid"]);
        run_git_for_test(&local, &["commit", "--allow-empty", "-m", "first"]);
        run_git_for_test(&local, &["branch", "-M", "tsk-1481-test"]);
        let root = local.display().to_string();
        let url = origin.display().to_string();
        assert!(run_git_text(&local, &["remote"]).unwrap().is_empty());
        let added = add_git_remote_sync(root.clone(), " origin ".into(), format!(" {url} ")).unwrap();
        assert_eq!(added.message, "Added remote origin");
        assert!(!added.status.has_upstream);
        assert!(add_git_remote_sync(root.clone(), "origin".into(), url.clone()).unwrap_err().contains("already exists"));
        assert!(add_git_remote_sync(root.clone(), " ".into(), url.clone()).is_err());
        assert!(add_git_remote_sync(root.clone(), "-x".into(), url.clone()).is_err());
        assert!(add_git_remote_sync(root.clone(), "backup".into(), " ".into()).is_err());
        assert!(add_git_remote_sync(root.clone(), "backup".into(), "-x".into()).is_err());
        assert!(add_git_remote_sync(root, "invalid name".into(), url).is_err());
        let published = publish_git_repository_sync(local.clone(), "tsk-1481-test".into(), "origin".into()).unwrap();
        assert!(published.status.has_upstream);
        assert_eq!(run_git_text(&origin, &["rev-parse", "tsk-1481-test"]).unwrap(), run_git_text(&local, &["rev-parse", "HEAD"]).unwrap());
        std::fs::remove_dir_all(origin).unwrap();
        std::fs::remove_dir_all(local).unwrap();
    }

    #[test]
    fn git_publish_checks_branch_remote_and_preserves_normal_push() {
        let origin = unique_temp_root();
        let backup = unique_temp_root();
        let local = unique_temp_root();
        for path in [&origin, &backup, &local] {
            std::fs::create_dir_all(path).unwrap();
        }
        run_git_for_test(&origin, &["init", "--bare"]);
        run_git_for_test(&backup, &["init", "--bare"]);
        run_git_for_test(&local, &["init"]);
        run_git_for_test(&local, &["config", "user.name", "MacCommandBar Test"]);
        run_git_for_test(&local, &["config", "user.email", "test@example.invalid"]);
        std::fs::write(local.join("file.txt"), "first\n").unwrap();
        run_git_for_test(&local, &["add", "file.txt"]);
        run_git_for_test(&local, &["commit", "-m", "first"]);
        run_git_for_test(&local, &["branch", "-M", "tsk-1233-test"]);
        let branch = "tsk-1233-test".to_string();

        let no_remote = publish_git_repository_sync(local.clone(), branch.clone(), String::new()).unwrap_err();
        assert!(no_remote.contains("selected remote"));
        run_git_for_test(&local, &["remote", "add", "origin", &origin.display().to_string()]);
        run_git_for_test(&local, &["remote", "add", "backup", &backup.display().to_string()]);
        assert!(publish_git_repository_sync(local.clone(), "another-branch".to_string(), "origin".to_string()).unwrap_err().contains("branch changed"));
        assert!(publish_git_repository_sync(local.clone(), branch.clone(), "missing".to_string()).unwrap_err().contains("selected remote"));
        let published = publish_git_repository_sync(local.clone(), branch.clone(), "backup".to_string()).unwrap();
        assert!(published.status.has_upstream);
        assert_eq!(run_git_text(&backup, &["rev-parse", branch.as_str()]).unwrap().trim(), run_git_text(&local, &["rev-parse", "HEAD"]).unwrap().trim());
        assert!(run_git_text(&origin, &["rev-parse", "--verify", branch.as_str()]).is_err());
        assert!(publish_git_repository_sync(local.clone(), branch.clone(), "origin".to_string()).unwrap_err().contains("already has an upstream"));

        std::fs::write(local.join("file.txt"), "second\n").unwrap();
        run_git_for_test(&local, &["commit", "-am", "second"]);
        push_git_repository_sync(local.clone()).unwrap();
        assert_eq!(run_git_text(&backup, &["rev-parse", branch.as_str()]).unwrap().trim(), run_git_text(&local, &["rev-parse", "HEAD"]).unwrap().trim());
        assert!(run_git_text(&origin, &["rev-parse", "--verify", branch.as_str()]).is_err());

        // A real non-fast-forward rejection leaves the branch unpublished; after
        // incorporating the remote commit, the same normal publish can retry.
        let retry_branch = "tsk-1233-retry";
        let peer = unique_temp_root();
        std::fs::create_dir_all(&peer).unwrap();
        run_git_for_test(&local, &["switch", "-c", retry_branch]);
        run_git_for_test(&local, &["push", "origin", &format!("HEAD:refs/heads/{retry_branch}")]);
        run_git_for_test(&peer, &["clone", "--branch", retry_branch, &origin.display().to_string(), "."]);
        run_git_for_test(&peer, &["config", "user.name", "MacCommandBar Test"]);
        run_git_for_test(&peer, &["config", "user.email", "test@example.invalid"]);
        std::fs::write(peer.join("remote.txt"), "remote\n").unwrap();
        run_git_for_test(&peer, &["add", "remote.txt"]);
        run_git_for_test(&peer, &["commit", "-m", "remote update"]);
        run_git_for_test(&peer, &["push"]);
        std::fs::write(local.join("local.txt"), "local\n").unwrap();
        run_git_for_test(&local, &["add", "local.txt"]);
        run_git_for_test(&local, &["commit", "-m", "local update"]);
        let rejected = publish_git_repository_sync(local.clone(), retry_branch.to_string(), "origin".to_string()).unwrap_err();
        assert!(rejected.contains("[rejected]"), "{rejected}");
        assert!(!project_git_status_sync(local.clone()).unwrap().has_upstream);
        run_git_for_test(&local, &["fetch", "origin", retry_branch]);
        run_git_for_test(&local, &["rebase", "FETCH_HEAD"]);
        let retried = publish_git_repository_sync(local.clone(), retry_branch.to_string(), "origin".to_string()).unwrap();
        assert!(retried.status.has_upstream);
        assert_eq!(run_git_text(&origin, &["rev-parse", retry_branch]).unwrap().trim(), run_git_text(&local, &["rev-parse", "HEAD"]).unwrap().trim());

        std::fs::remove_dir_all(origin).unwrap();
        std::fs::remove_dir_all(backup).unwrap();
        std::fs::remove_dir_all(local).unwrap();
        std::fs::remove_dir_all(peer).unwrap();
    }

    #[test]
    fn lsof_listener_parser_reads_pid_command_and_ports() {
        let listeners = parse_lsof_tcp_listeners(
            "p123\ncnode\nn*:5177\nn127.0.0.1:24678\np456\ncdotnet\nn[::1]:5001\n",
        );

        assert_eq!(
            listeners,
            vec![
                ProcessListener {
                    pid: 123,
                    command: "node".to_string(),
                    port: 5177,
                },
                ProcessListener {
                    pid: 123,
                    command: "node".to_string(),
                    port: 24678,
                },
                ProcessListener {
                    pid: 456,
                    command: "dotnet".to_string(),
                    port: 5001,
                }
            ]
        );
    }

    #[test]
    fn runtime_context_project_matching_uses_main_and_worktree_roots() {
        let projects = vec![RuntimeContextProject {
            id: "ediplatform".to_string(),
            name: "EdiPlatform".to_string(),
            path: "/Users/blackcolours/dev/work/EdiPlatform".to_string(),
        }];

        let main_context = runtime_context_project_for_cwd(
            Path::new("/Users/blackcolours/dev/work/EdiPlatform/EdiPlatform.Api"),
            &projects,
        )
        .unwrap();
        assert_eq!(main_context.project_id.as_deref(), Some("ediplatform"));
        assert_eq!(main_context.project_name, "EdiPlatform");
        assert_eq!(main_context.root_label, "main checkout");

        let worktree_context = runtime_context_project_for_cwd(
            Path::new(
                "/Users/blackcolours/dev/work/worktrees/EdiPlatform/tsk-126-m3-design-polish/ediplatform-web",
            ),
            &projects,
        )
        .unwrap();
        assert_eq!(worktree_context.project_id.as_deref(), Some("ediplatform"));
        assert_eq!(worktree_context.project_name, "EdiPlatform");
        assert_eq!(
            worktree_context.root_label,
            "worktree:tsk-126-m3-design-polish"
        );

        assert!(
            runtime_context_project_for_cwd(Path::new("/tmp/other-project"), &projects).is_none()
        );
    }

    #[test]
    fn project_worktree_parser_reads_porcelain_branches() {
        let records = parse_project_worktree_porcelain(
            "worktree /repo\nHEAD abc\nbranch refs/heads/main\n\nworktree /worktrees/feature\nHEAD def\nbranch refs/heads/cdx/tsk-126-feature\n\nworktree /detached\nHEAD fed\ndetached\n\nworktree /missing\nHEAD abc\nbranch refs/heads/cdx/tsk-127-missing\nprunable gitdir file points to non-existent location\n\nworktree /locked\nHEAD def\nbranch refs/heads/cdx/tsk-128-locked\nlocked agent still running\n",
        );

        assert_eq!(
            records
                .iter()
                .map(|record| {
                    (
                        record.path.as_str(),
                        record.branch.as_str(),
                        record.task_id.as_deref(),
                    )
                })
                .collect::<Vec<_>>(),
            vec![
                ("/repo", "main", None),
                ("/worktrees/feature", "cdx/tsk-126-feature", Some("TSK-126")),
                ("/detached", "detached", None),
                ("/missing", "cdx/tsk-127-missing", Some("TSK-127")),
                ("/locked", "cdx/tsk-128-locked", Some("TSK-128"))
            ]
        );
        assert!(records[3].is_prunable);
        assert_eq!(
            records[3].prunable_reason.as_deref(),
            Some("gitdir file points to non-existent location")
        );
        assert!(records[4].is_locked);
        assert_eq!(
            records[4].locked_reason.as_deref(),
            Some("agent still running")
        );
    }

    #[test]
    fn project_worktree_delete_eligibility_prioritizes_dirty_then_unmerged() {
        assert_eq!(
            project_worktree_delete_eligibility(true, true, true, true),
            "blocked: dirty worktree"
        );
        assert_eq!(
            project_worktree_delete_eligibility(false, true, true, true),
            "blocked: unmerged commits"
        );
        assert_eq!(
            project_worktree_delete_eligibility(false, false, false, true),
            "blocked: locked worktree"
        );
        assert_eq!(
            project_worktree_delete_eligibility(false, false, true, false),
            "review: prunable missing worktree metadata"
        );
        assert_eq!(
            project_worktree_delete_eligibility(false, false, false, false),
            "requires-confirmation"
        );
    }

    #[test]
    fn project_worktree_unmerged_detection_is_scoped_to_the_worktree_branch() {
        let root = unique_temp_root();
        let clean_sibling = unique_temp_root();
        let local_commit_sibling = unique_temp_root();
        let remote = unique_temp_root();
        std::fs::create_dir_all(root.join("src")).unwrap();
        std::fs::create_dir_all(&remote).unwrap();
        std::fs::write(root.join("src/App.ts"), "export const value = 1;\n").unwrap();

        run_git_for_test(&remote, &["init", "--bare"]);
        run_git_for_test(&root, &["init"]);
        run_git_for_test(&root, &["config", "user.name", "MacCommandBar Test"]);
        run_git_for_test(&root, &["config", "user.email", "test@example.invalid"]);
        run_git_for_test(&root, &["add", "src/App.ts"]);
        run_git_for_test(&root, &["commit", "-m", "initial"]);
        run_git_for_test(&root, &["branch", "-M", "main"]);
        run_git_for_test(
            &root,
            &["remote", "add", "origin", remote.to_str().unwrap()],
        );
        run_git_for_test(&root, &["push", "-u", "origin", "main"]);
        run_git_for_test(
            &root,
            &[
                "worktree",
                "add",
                "-b",
                "cdx/tsk-200-clean",
                clean_sibling.to_str().unwrap(),
            ],
        );
        run_git_for_test(
            &root,
            &[
                "worktree",
                "add",
                "-b",
                "cdx/tsk-201-local",
                local_commit_sibling.to_str().unwrap(),
            ],
        );
        std::fs::write(
            local_commit_sibling.join("src/App.ts"),
            "export const value = 2;\n",
        )
        .unwrap();
        run_git_for_test(
            &local_commit_sibling,
            &["commit", "-am", "local worktree commit"],
        );

        let clean_has_unmerged =
            project_worktree_has_unmerged_commits(clean_sibling.to_str().unwrap());
        let local_has_unmerged =
            project_worktree_has_unmerged_commits(local_commit_sibling.to_str().unwrap());

        run_git_for_test(
            &root,
            &[
                "worktree",
                "remove",
                "--force",
                clean_sibling.to_str().unwrap(),
            ],
        );
        run_git_for_test(
            &root,
            &[
                "worktree",
                "remove",
                "--force",
                local_commit_sibling.to_str().unwrap(),
            ],
        );
        std::fs::remove_dir_all(root).unwrap();
        std::fs::remove_dir_all(remote).unwrap();

        assert!(
            local_has_unmerged,
            "the worktree branch with a local commit should be blocked"
        );
        assert!(
            !clean_has_unmerged,
            "a clean sibling should not inherit unmerged state from another local branch"
        );
    }

    #[test]
    fn stopping_a_process_asks_it_politely_and_says_so() {
        let mut signalled = Vec::new();
        let result = kill_process_with(4242, 99, |pid, signal| {
            signalled.push((pid, signal.to_string()));
            Ok(())
        });

        assert_eq!(signalled, vec![(4242, "TERM".to_string())]);
        assert!(result.ok);
        assert!(result.message.contains("Asked process 4242 to stop"));
    }

    #[test]
    fn stopping_the_system_or_this_app_is_refused_before_any_signal() {
        for pid in [0, 1] {
            let result = kill_process_with(pid, 99, |_, _| panic!("must not signal pid {pid}"));
            assert!(!result.ok);
            assert!(result.message.contains("system's own"));
        }

        let result = kill_process_with(99, 99, |_, _| panic!("must not signal this app"));
        assert!(!result.ok);
        assert!(result.message.contains("this app itself"));
    }

    #[test]
    fn a_process_that_could_not_be_stopped_is_reported_as_a_sentence() {
        let result = kill_process_with(4242, 99, |_, _| Err("No such process".to_string()));

        assert!(!result.ok);
        assert!(result.message.contains("Could not stop process 4242"));
        assert!(result.message.contains("already exited"));
        assert!(result.message.contains("No such process"));
    }

    #[test]
    fn a_process_number_still_running_the_expected_command_is_recognised() {
        assert!(process_commands_match("node", "node"));
        assert!(
            process_commands_match("/usr/local/bin/node", "node"),
            "the list shows a short name while ps answers with a full path"
        );
        assert!(
            process_commands_match("node", "/usr/local/bin/node"),
            "and the full path can arrive from either side"
        );
        assert!(
            process_commands_match("node22", "node"),
            "a versioned executable is still the same program"
        );
    }

    #[test]
    fn a_process_number_now_running_something_else_is_not_recognised() {
        assert!(
            !process_commands_match("node", "RentalCommand.Api"),
            "a reused process number must not be mistaken for the original"
        );
        assert!(
            !process_commands_match("/usr/bin/python3", "node"),
            "comparing file names must not match across different programs"
        );
        assert!(
            !process_commands_match("", "node"),
            "no name is never a match"
        );
        assert!(
            !process_commands_match("node", ""),
            "and neither is no expectation"
        );
        assert!(!process_commands_match("   ", "node"));
    }

    #[test]
    fn switching_the_csharp_language_server_off_says_what_was_lost() {
        let stopped = describe_csharp_language_server_toggle(false, true, 1);
        assert!(stopped.contains("freeing its memory"));
        assert!(stopped.contains("Reference counts and project search still work"));

        let nothing_running = describe_csharp_language_server_toggle(false, true, 0);
        assert!(nothing_running.contains("was not running"));

        let back_on = describe_csharp_language_server_toggle(true, true, 0);
        assert!(back_on.contains("back on"));
        assert!(describe_csharp_language_server_toggle(true, false, 0).contains("already on"));
    }

    /// Every way the switch can fail to start a server has to say something. A
    /// switch that is on next to silence is what made this look broken.
    #[test]
    fn turning_the_switch_on_says_what_happened_to_the_language_server() {
        let running = describe_language_server_start(&Ok(lsp::LanguageServerStart::Running {
            server_name: "typescript-language-server",
        }));
        assert!(running.contains("typescript-language-server is running"));

        let unsupported =
            describe_language_server_start(&Ok(lsp::LanguageServerStart::NoServerForLanguage));
        assert!(unsupported.contains("no language server for the file you have open"));

        let missing = describe_language_server_start(&Ok(lsp::LanguageServerStart::NotInstalled {
            server_name: "rust-analyzer",
            command: "rust-analyzer",
        }));
        assert!(missing.contains("rust-analyzer is not installed"));

        let native =
            describe_language_server_start(&Ok(lsp::LanguageServerStart::NativeCsharpClient));
        assert!(native.contains("C# language server starts"));

        let switched_off =
            describe_language_server_start(&Ok(lsp::LanguageServerStart::CsharpSwitchedOff));
        assert!(switched_off.contains("switched off in Settings"));

        let no_project =
            describe_language_server_start(&Ok(lsp::LanguageServerStart::NoCsharpProject));
        assert!(no_project.contains("no C# project or solution"));

        let read_mode = describe_language_server_start(&Ok(lsp::LanguageServerStart::ReadMode));
        assert!(read_mode.contains("no language server was started"));

        let failed = describe_language_server_start(&Err("stdin unavailable".to_string()));
        assert!(failed.contains("could not be started: stdin unavailable"));
    }

    #[test]
    fn a_worktree_record_points_at_the_folder_above_its_git_file() {
        assert_eq!(
            worktree_record_gitdir_names("/Users/reader/work/trees/tsk-1/.git\n"),
            Some("/Users/reader/work/trees/tsk-1".to_string())
        );
        assert_eq!(worktree_record_gitdir_names("   "), None);
    }

    #[test]
    fn remove_project_worktree_removes_clean_sibling_and_refreshes_list() {
        let root = unique_temp_root();
        let sibling = unique_temp_root();
        let remote = unique_temp_root();
        std::fs::create_dir_all(root.join("src")).unwrap();
        std::fs::create_dir_all(&remote).unwrap();
        std::fs::write(root.join("src/App.ts"), "export const value = 1;\n").unwrap();

        run_git_for_test(&remote, &["init", "--bare"]);
        run_git_for_test(&root, &["init"]);
        run_git_for_test(&root, &["config", "user.name", "MacCommandBar Test"]);
        run_git_for_test(&root, &["config", "user.email", "test@example.invalid"]);
        run_git_for_test(&root, &["add", "src/App.ts"]);
        run_git_for_test(&root, &["commit", "-m", "initial"]);
        run_git_for_test(&root, &["branch", "-M", "main"]);
        run_git_for_test(
            &root,
            &["remote", "add", "origin", remote.to_str().unwrap()],
        );
        run_git_for_test(&root, &["push", "-u", "origin", "main"]);
        run_git_for_test(
            &root,
            &[
                "worktree",
                "add",
                "-b",
                "cdx/tsk-127-cleanup",
                sibling.to_str().unwrap(),
            ],
        );

        let result = remove_project_worktree_sync(root.clone(), sibling.clone(), false).unwrap();

        assert!(result.message.contains("Removed worktree"));
        assert!(!sibling.exists());
        assert!(!result
            .worktrees
            .iter()
            .any(|worktree| worktree.path == normalized_path_string(&sibling)));

        std::fs::remove_dir_all(root).unwrap();
        std::fs::remove_dir_all(remote).unwrap();
    }

    #[test]
    fn remove_project_worktree_prunes_missing_registered_sibling() {
        let root = unique_temp_root();
        let sibling = unique_temp_root();
        std::fs::create_dir_all(root.join("src")).unwrap();
        std::fs::write(root.join("src/App.ts"), "export const value = 1;\n").unwrap();

        run_git_for_test(&root, &["init"]);
        run_git_for_test(&root, &["config", "user.name", "MacCommandBar Test"]);
        run_git_for_test(&root, &["config", "user.email", "test@example.invalid"]);
        run_git_for_test(&root, &["add", "src/App.ts"]);
        run_git_for_test(&root, &["commit", "-m", "initial"]);
        run_git_for_test(
            &root,
            &[
                "worktree",
                "add",
                "-b",
                "cdx/tsk-127-missing",
                sibling.to_str().unwrap(),
            ],
        );
        std::fs::remove_dir_all(&sibling).unwrap();

        let before = list_project_worktrees_sync(root.clone()).unwrap();
        let missing = before
            .iter()
            .find(|worktree| worktree.branch == "cdx/tsk-127-missing")
            .expect("deleted sibling should still be registered before pruning");
        assert!(missing.is_prunable);
        assert!(missing.delete_eligibility.contains("prunable"));
        let missing_path = PathBuf::from(&missing.path);

        let result = remove_project_worktree_sync(root.clone(), missing_path, false).unwrap();

        assert!(result.message.contains("Cleared git's records for"));
        assert!(result.message.contains("cdx/tsk-127-missing"));
        assert!(!result
            .worktrees
            .iter()
            .any(|worktree| worktree.branch == "cdx/tsk-127-missing"));

        std::fs::remove_dir_all(root).unwrap();
    }

    /// The incident this replaced: a reader clicked one row whose folder was
    /// gone and watched every other gone-folder row disappear with it, because
    /// the removal ran a repo-wide prune.
    #[test]
    fn remove_project_worktree_clears_only_the_asked_for_missing_sibling() {
        let root = unique_temp_root();
        let first_sibling = unique_temp_root();
        let second_sibling = unique_temp_root();
        std::fs::create_dir_all(root.join("src")).unwrap();
        std::fs::write(root.join("src/App.ts"), "export const value = 1;\n").unwrap();

        run_git_for_test(&root, &["init"]);
        run_git_for_test(&root, &["config", "user.name", "MacCommandBar Test"]);
        run_git_for_test(&root, &["config", "user.email", "test@example.invalid"]);
        run_git_for_test(&root, &["add", "src/App.ts"]);
        run_git_for_test(&root, &["commit", "-m", "initial"]);
        for (branch, sibling) in [
            ("cdx/tsk-500-first", &first_sibling),
            ("cdx/tsk-500-second", &second_sibling),
        ] {
            run_git_for_test(
                &root,
                &["worktree", "add", "-b", branch, sibling.to_str().unwrap()],
            );
            std::fs::remove_dir_all(sibling).unwrap();
        }

        let before = list_project_worktrees_sync(root.clone()).unwrap();
        let first = before
            .iter()
            .find(|worktree| worktree.branch == "cdx/tsk-500-first")
            .expect("both deleted siblings should still be registered");
        assert!(first.is_prunable);
        let first_path = PathBuf::from(&first.path);

        let result = remove_project_worktree_sync(root.clone(), first_path, false).unwrap();

        assert!(!result
            .worktrees
            .iter()
            .any(|worktree| worktree.branch == "cdx/tsk-500-first"));
        assert!(
            result
                .worktrees
                .iter()
                .any(|worktree| worktree.branch == "cdx/tsk-500-second"),
            "clearing one gone-folder row must leave the other one alone"
        );

        std::fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn remove_project_worktree_refuses_locked_sibling() {
        let root = unique_temp_root();
        let sibling = unique_temp_root();
        std::fs::create_dir_all(root.join("src")).unwrap();
        std::fs::write(root.join("src/App.ts"), "export const value = 1;\n").unwrap();

        run_git_for_test(&root, &["init"]);
        run_git_for_test(&root, &["config", "user.name", "MacCommandBar Test"]);
        run_git_for_test(&root, &["config", "user.email", "test@example.invalid"]);
        run_git_for_test(&root, &["add", "src/App.ts"]);
        run_git_for_test(&root, &["commit", "-m", "initial"]);
        run_git_for_test(
            &root,
            &[
                "worktree",
                "add",
                "-b",
                "cdx/tsk-127-locked",
                sibling.to_str().unwrap(),
            ],
        );
        run_git_for_test(
            &root,
            &[
                "worktree",
                "lock",
                "--reason",
                "agent running",
                sibling.to_str().unwrap(),
            ],
        );

        let error = remove_project_worktree_sync(root.clone(), sibling.clone(), false).unwrap_err();

        assert!(error.contains("locked worktree"));
        assert!(sibling.exists());

        run_git_for_test(&root, &["worktree", "unlock", sibling.to_str().unwrap()]);
        run_git_for_test(
            &root,
            &["worktree", "remove", "--force", sibling.to_str().unwrap()],
        );
        std::fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn remove_project_worktree_refuses_dirty_sibling() {
        let root = unique_temp_root();
        let sibling = unique_temp_root();
        std::fs::create_dir_all(root.join("src")).unwrap();
        std::fs::write(root.join("src/App.ts"), "export const value = 1;\n").unwrap();

        run_git_for_test(&root, &["init"]);
        run_git_for_test(&root, &["config", "user.name", "MacCommandBar Test"]);
        run_git_for_test(&root, &["config", "user.email", "test@example.invalid"]);
        run_git_for_test(&root, &["add", "src/App.ts"]);
        run_git_for_test(&root, &["commit", "-m", "initial"]);
        run_git_for_test(
            &root,
            &[
                "worktree",
                "add",
                "-b",
                "cdx/tsk-127-dirty",
                sibling.to_str().unwrap(),
            ],
        );
        std::fs::write(sibling.join("dirty.txt"), "keep me\n").unwrap();

        let error = remove_project_worktree_sync(root.clone(), sibling.clone(), false).unwrap_err();

        assert!(error.contains("dirty worktree"));
        assert!(sibling.exists());

        run_git_for_test(
            &root,
            &["worktree", "remove", "--force", sibling.to_str().unwrap()],
        );
        std::fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn forced_worktree_removal_message_says_what_was_destroyed() {
        assert_eq!(
            describe_forced_worktree_removal("cdx/tsk-127-clean", Some(0), false, false, None),
            "Force removed worktree cdx/tsk-127-clean. It had no uncommitted changes."
        );
        assert_eq!(
            describe_forced_worktree_removal("cdx/tsk-127-one", Some(1), true, false, None),
            "Force removed worktree cdx/tsk-127-one. Deleted 1 file with changes that were never committed."
        );
        assert_eq!(
            describe_forced_worktree_removal(
                "cdx/tsk-127-all",
                Some(3),
                true,
                true,
                Some("agent running")
            ),
            "Force removed worktree cdx/tsk-127-all. Deleted 3 files with changes that were never committed. Deleted commits on this branch that were never pushed to a remote. Unlocked it first; it was locked because: agent running."
        );
        assert_eq!(
            describe_forced_worktree_removal("cdx/tsk-127-locked", Some(0), false, false, Some("")),
            "Force removed worktree cdx/tsk-127-locked. It had no uncommitted changes. Unlocked it first; it was locked with no reason given."
        );
    }

    #[test]
    fn forced_worktree_removal_never_claims_nothing_was_lost_when_it_could_not_look() {
        // The count failed but the listing already knew there were changes: say that,
        // rather than reporting a zero the count never actually established.
        assert_eq!(
            describe_forced_worktree_removal("cdx/tsk-127-blind", None, true, false, None),
            "Force removed worktree cdx/tsk-127-blind. Deleted files with changes that were never committed; they could not be counted before the removal."
        );
        // The count disagrees with what the listing said. Same answer: do not claim zero.
        assert_eq!(
            describe_forced_worktree_removal("cdx/tsk-127-disagree", Some(0), true, false, None),
            "Force removed worktree cdx/tsk-127-disagree. Deleted files with changes that were never committed; they could not be counted before the removal."
        );
        // Neither check could look. Do not promise the worktree was clean.
        assert_eq!(
            describe_forced_worktree_removal("cdx/tsk-127-unknown", None, false, false, None),
            "Force removed worktree cdx/tsk-127-unknown. Could not check for uncommitted changes before the removal, so anything not committed is gone."
        );
    }

    #[test]
    fn worktree_dirty_file_count_is_unknown_when_git_cannot_answer() {
        let missing = unique_temp_root();

        assert_eq!(
            project_worktree_dirty_file_count(missing.to_str().unwrap()),
            None,
            "a path git cannot read reports unknown, not a confident zero"
        );

        let root = unique_temp_root();
        std::fs::create_dir_all(&root).unwrap();
        std::fs::write(root.join("README.md"), "initial\n").unwrap();
        run_git_for_test(&root, &["init"]);
        run_git_for_test(&root, &["config", "user.name", "MacCommandBar Test"]);
        run_git_for_test(&root, &["config", "user.email", "test@example.invalid"]);
        run_git_for_test(&root, &["add", "README.md"]);
        run_git_for_test(&root, &["commit", "-m", "initial"]);

        assert_eq!(
            project_worktree_dirty_file_count(root.to_str().unwrap()),
            Some(0)
        );

        std::fs::write(root.join("untracked.txt"), "new\n").unwrap();
        std::fs::write(root.join("README.md"), "changed\n").unwrap();

        assert_eq!(
            project_worktree_dirty_file_count(root.to_str().unwrap()),
            Some(2)
        );

        std::fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn force_remove_project_worktree_deletes_a_dirty_sibling_and_says_so() {
        let root = unique_temp_root();
        let sibling = unique_temp_root();
        std::fs::create_dir_all(root.join("src")).unwrap();
        std::fs::write(root.join("src/App.ts"), "export const value = 1;\n").unwrap();

        run_git_for_test(&root, &["init"]);
        run_git_for_test(&root, &["config", "user.name", "MacCommandBar Test"]);
        run_git_for_test(&root, &["config", "user.email", "test@example.invalid"]);
        run_git_for_test(&root, &["add", "src/App.ts"]);
        run_git_for_test(&root, &["commit", "-m", "initial"]);
        run_git_for_test(
            &root,
            &[
                "worktree",
                "add",
                "-b",
                "cdx/tsk-127-force-dirty",
                sibling.to_str().unwrap(),
            ],
        );
        std::fs::write(sibling.join("dirty.txt"), "never committed\n").unwrap();
        std::fs::write(sibling.join("src/App.ts"), "export const value = 2;\n").unwrap();

        let result = remove_project_worktree_sync(root.clone(), sibling.clone(), true).unwrap();

        assert_eq!(
            result.message,
            // This fixture repo has no remote, so the branch's commit counts as never
            // pushed and the message says so alongside the uncommitted file count.
            "Force removed worktree cdx/tsk-127-force-dirty. Deleted 2 files with changes that were never committed. Deleted commits on this branch that were never pushed to a remote."
        );
        assert!(!sibling.exists());
        assert!(!result
            .worktrees
            .iter()
            .any(|worktree| worktree.branch == "cdx/tsk-127-force-dirty"));

        std::fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn force_remove_project_worktree_unlocks_a_locked_sibling_first() {
        let root = unique_temp_root();
        let sibling = unique_temp_root();
        std::fs::create_dir_all(root.join("src")).unwrap();
        std::fs::write(root.join("src/App.ts"), "export const value = 1;\n").unwrap();

        run_git_for_test(&root, &["init"]);
        run_git_for_test(&root, &["config", "user.name", "MacCommandBar Test"]);
        run_git_for_test(&root, &["config", "user.email", "test@example.invalid"]);
        run_git_for_test(&root, &["add", "src/App.ts"]);
        run_git_for_test(&root, &["commit", "-m", "initial"]);
        run_git_for_test(
            &root,
            &[
                "worktree",
                "add",
                "-b",
                "cdx/tsk-127-force-locked",
                sibling.to_str().unwrap(),
            ],
        );
        run_git_for_test(
            &root,
            &[
                "worktree",
                "lock",
                "--reason",
                "agent running",
                sibling.to_str().unwrap(),
            ],
        );

        let result = remove_project_worktree_sync(root.clone(), sibling.clone(), true).unwrap();

        assert!(
            result
                .message
                .contains("Unlocked it first; it was locked because: agent running."),
            "unexpected message: {}",
            result.message
        );
        assert!(!sibling.exists());

        std::fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn force_remove_project_worktree_still_refuses_the_primary_checkout() {
        let root = unique_temp_root();
        std::fs::create_dir_all(root.join("src")).unwrap();
        std::fs::write(root.join("src/App.ts"), "export const value = 1;\n").unwrap();

        run_git_for_test(&root, &["init"]);
        run_git_for_test(&root, &["config", "user.name", "MacCommandBar Test"]);
        run_git_for_test(&root, &["config", "user.email", "test@example.invalid"]);
        run_git_for_test(&root, &["add", "src/App.ts"]);
        run_git_for_test(&root, &["commit", "-m", "initial"]);

        let error = remove_project_worktree_sync(root.clone(), root.clone(), true).unwrap_err();

        assert!(
            error.contains("primary checkout"),
            "unexpected message: {error}"
        );
        assert!(root.join("src/App.ts").exists());

        std::fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn force_remove_project_worktree_refuses_the_primary_checkout_reached_through_a_sibling() {
        let root = unique_temp_root();
        let sibling = unique_temp_root();
        std::fs::create_dir_all(root.join("src")).unwrap();
        std::fs::write(root.join("src/App.ts"), "export const value = 1;\n").unwrap();

        run_git_for_test(&root, &["init"]);
        run_git_for_test(&root, &["config", "user.name", "MacCommandBar Test"]);
        run_git_for_test(&root, &["config", "user.email", "test@example.invalid"]);
        run_git_for_test(&root, &["add", "src/App.ts"]);
        run_git_for_test(&root, &["commit", "-m", "initial"]);
        run_git_for_test(
            &root,
            &[
                "worktree",
                "add",
                "-b",
                "cdx/tsk-127-sneak",
                sibling.to_str().unwrap(),
            ],
        );

        // Ask through the LINKED worktree for the primary checkout. The two paths differ,
        // so comparing them to each other proves nothing; the answer has to come from the
        // repository's own list of worktrees, whose first entry is the main working tree.
        let error = remove_project_worktree_sync(sibling.clone(), root.clone(), true).unwrap_err();

        assert!(
            error.contains("primary checkout"),
            "unexpected message: {error}"
        );
        assert!(root.join("src/App.ts").exists());
        assert!(sibling.exists());

        // The same refusal without force, so neither path depends on the other.
        let error = remove_project_worktree_sync(sibling.clone(), root.clone(), false).unwrap_err();
        assert!(
            error.contains("primary checkout"),
            "unexpected message: {error}"
        );

        run_git_for_test(
            &root,
            &["worktree", "remove", "--force", sibling.to_str().unwrap()],
        );
        std::fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn archive_project_worktree_preserves_dirty_state_without_removing_it() {
        let root = unique_temp_root();
        let sibling = unique_temp_root();
        std::fs::create_dir_all(root.join("src")).unwrap();
        std::fs::write(root.join("src/App.ts"), "export const value = 1;\n").unwrap();

        run_git_for_test(&root, &["init"]);
        run_git_for_test(&root, &["config", "user.name", "MacCommandBar Test"]);
        run_git_for_test(&root, &["config", "user.email", "test@example.invalid"]);
        run_git_for_test(&root, &["add", "src/App.ts"]);
        run_git_for_test(&root, &["commit", "-m", "initial"]);
        run_git_for_test(
            &root,
            &[
                "worktree",
                "add",
                "-b",
                "cdx/tsk-127-dirty-backup",
                sibling.to_str().unwrap(),
            ],
        );
        std::fs::write(sibling.join("src/App.ts"), "export const value = 2;\n").unwrap();
        std::fs::create_dir_all(sibling.join("notes")).unwrap();
        std::fs::write(sibling.join("notes/handoff.md"), "keep this\n").unwrap();

        let result = archive_project_worktree_sync(root.clone(), sibling.clone()).unwrap();

        let archive_path = PathBuf::from(result.archive_path);
        assert!(archive_path.join("status.txt").exists());
        assert!(archive_path.join("commits.txt").exists());
        assert!(archive_path.join("unstaged.patch").exists());
        assert!(archive_path.join("staged.patch").exists());
        assert!(archive_path.join("untracked.txt").exists());
        assert!(archive_path.join("head.bundle").exists());
        assert!(archive_path.join("untracked/notes/handoff.md").exists());
        assert!(std::fs::read_to_string(archive_path.join("unstaged.patch"))
            .unwrap()
            .contains("value = 2"));
        assert_eq!(
            std::fs::read_to_string(archive_path.join("untracked/notes/handoff.md")).unwrap(),
            "keep this\n"
        );
        assert!(sibling.exists());
        assert!(project_worktree_is_dirty(sibling.to_str().unwrap()));
        assert!(result.message.contains("Archived worktree"));

        run_git_for_test(
            &root,
            &["worktree", "remove", "--force", sibling.to_str().unwrap()],
        );
        std::fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn source_search_reads_indexed_files_case_insensitively_and_caps_results() {
        let root = unique_temp_root();
        std::fs::create_dir_all(root.join("src")).unwrap();
        let first_path = root.join("src/A.ts");
        let second_path = root.join("src/B.ts");
        std::fs::write(
            &first_path,
            "export function createWidget() {}\nconst label = \"Widget\";\n",
        )
        .unwrap();
        std::fs::write(&second_path, "export const WidgetName = \"B\";\n").unwrap();

        let matches = search_source_files_sync(
            vec![
                SourceRecord {
                    path: first_path.display().to_string(),
                    relative_path: "src/A.ts".to_string(),
                    file_name: "A.ts".to_string(),
                    language: "typescript".to_string(),
                    byte_count: 64,
                },
                SourceRecord {
                    path: second_path.display().to_string(),
                    relative_path: "src/B.ts".to_string(),
                    file_name: "B.ts".to_string(),
                    language: "typescript".to_string(),
                    byte_count: 32,
                },
            ],
            "widget".to_string(),
            Some(2),
        )
        .unwrap();

        assert_eq!(
            matches
                .iter()
                .map(|source_match| (
                    source_match.relative_path.as_str(),
                    source_match.line,
                    source_match.column,
                    source_match.excerpt.as_str()
                ))
                .collect::<Vec<_>>(),
            vec![
                ("src/A.ts", 1, 23, "export function createWidget() {}"),
                ("src/A.ts", 2, 16, "const label = \"Widget\";")
            ]
        );

        std::fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn source_definition_lookup_reads_indexed_symbols_and_caps_results() {
        let root = unique_temp_root();
        std::fs::create_dir_all(root.join("src")).unwrap();
        let resolver_path = root.join("src/FormatResolver.cs");
        let detector_path = root.join("src/FormatDetector.cs");
        std::fs::write(
            &resolver_path,
            [
                "namespace Demo;",
                "public sealed class FormatResolver",
                "{",
                "    private readonly FormatDetector _detector;",
                "}",
            ]
            .join("\n"),
        )
        .unwrap();
        std::fs::write(
            &detector_path,
            [
                "namespace Demo;",
                "public sealed class FormatDetector",
                "{",
                "    public Task DetectAsync() => Task.CompletedTask;",
                "}",
            ]
            .join("\n"),
        )
        .unwrap();

        let targets = find_source_definitions_sync(
            vec![
                SourceRecord {
                    path: resolver_path.display().to_string(),
                    relative_path: "src/FormatResolver.cs".to_string(),
                    file_name: "FormatResolver.cs".to_string(),
                    language: "csharp".to_string(),
                    byte_count: 96,
                },
                SourceRecord {
                    path: detector_path.display().to_string(),
                    relative_path: "src/FormatDetector.cs".to_string(),
                    file_name: "FormatDetector.cs".to_string(),
                    language: "csharp".to_string(),
                    byte_count: 96,
                },
            ],
            "formatdetector".to_string(),
            Some(1),
        )
        .unwrap();

        assert_eq!(
            targets
                .iter()
                .map(|target| (
                    target.relative_path.as_str(),
                    target.symbol_name.as_str(),
                    target.kind.as_str(),
                    target.line,
                    target.column,
                    target.detail.as_str()
                ))
                .collect::<Vec<_>>(),
            vec![(
                "src/FormatDetector.cs",
                "FormatDetector",
                "class",
                2,
                21,
                "public sealed class FormatDetector"
            )]
        );

        let root_targets = find_source_definitions_in_root_sync(
            root.clone(),
            "FormatDetector".to_string(),
            Some(1),
        )
        .unwrap();
        assert_eq!(root_targets.len(), 1);
        assert_eq!(root_targets[0].relative_path, "src/FormatDetector.cs");
        assert_eq!(root_targets[0].line, 2);

        std::fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn source_reference_lookup_reads_token_bounded_matches_and_caps_results() {
        let root = unique_temp_root();
        std::fs::create_dir_all(root.join("src")).unwrap();
        let resolver_path = root.join("src/FormatResolver.cs");
        let detector_path = root.join("src/FormatDetector.cs");
        std::fs::write(
            &resolver_path,
            [
                "public sealed class FormatResolver",
                "{",
                "    private readonly FormatDetector _detector;",
                "    private readonly FormatDetectorFactory _factory;",
                "}",
            ]
            .join("\n"),
        )
        .unwrap();
        std::fs::write(
            &detector_path,
            ["public sealed class FormatDetector", "{"].join("\n"),
        )
        .unwrap();

        let targets = find_source_references_sync(
            vec![
                SourceRecord {
                    path: resolver_path.display().to_string(),
                    relative_path: "src/FormatResolver.cs".to_string(),
                    file_name: "FormatResolver.cs".to_string(),
                    language: "csharp".to_string(),
                    byte_count: 128,
                },
                SourceRecord {
                    path: detector_path.display().to_string(),
                    relative_path: "src/FormatDetector.cs".to_string(),
                    file_name: "FormatDetector.cs".to_string(),
                    language: "csharp".to_string(),
                    byte_count: 64,
                },
            ],
            "formatdetector".to_string(),
            Some(2),
        )
        .unwrap();

        assert_eq!(
            targets
                .iter()
                .map(|target| (
                    target.relative_path.as_str(),
                    target.symbol_name.as_str(),
                    target.line,
                    target.column,
                    target.excerpt.as_str()
                ))
                .collect::<Vec<_>>(),
            vec![
                (
                    "src/FormatResolver.cs",
                    "FormatDetector",
                    3,
                    22,
                    "private readonly FormatDetector _detector;"
                ),
                (
                    "src/FormatDetector.cs",
                    "FormatDetector",
                    1,
                    21,
                    "public sealed class FormatDetector"
                )
            ]
        );

        let root_targets = find_source_references_in_root_sync(
            root.clone(),
            "formatdetector".to_string(),
            Some(2),
        )
        .unwrap();
        assert_eq!(root_targets.len(), 2);
        let mut root_paths = root_targets
            .iter()
            .map(|target| target.relative_path.as_str())
            .collect::<Vec<_>>();
        root_paths.sort_unstable();
        assert_eq!(
            root_paths,
            vec!["src/FormatDetector.cs", "src/FormatResolver.cs"]
        );

        std::fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn reference_counts_answer_every_symbol_from_one_walk() {
        let root = unique_temp_root();
        std::fs::create_dir_all(root.join("src")).unwrap();
        std::fs::write(
            root.join("src/FormatResolver.cs"),
            [
                "public sealed class FormatResolver",
                "{",
                "    private readonly FormatDetector _detector;",
                // Two mentions on one line still count as one, matching the
                // reference list this count stands in for.
                "    private readonly FormatDetector _other = new FormatDetector();",
                "    private readonly FormatDetectorFactory _factory;",
                "}",
            ]
            .join("\n"),
        )
        .unwrap();
        std::fs::write(
            root.join("src/FormatDetector.cs"),
            ["public sealed class FormatDetector", "{", "}"].join("\n"),
        )
        .unwrap();
        // Skipped by the walk, so its mentions must not be counted.
        std::fs::create_dir_all(root.join("node_modules/pkg")).unwrap();
        std::fs::write(
            root.join("node_modules/pkg/index.ts"),
            "export const FormatDetector = 1;",
        )
        .unwrap();

        let result = count_source_references_sync(
            root.clone(),
            vec![
                "FormatDetector".to_string(),
                "FormatResolver".to_string(),
                "MissingSymbol".to_string(),
            ],
            Some(5_000),
        )
        .unwrap();

        assert!(!result.approximate);
        assert_eq!(result.scanned_files, 2);
        assert_eq!(result.counts.get("FormatDetector"), Some(&3));
        assert_eq!(result.counts.get("FormatResolver"), Some(&1));
        assert_eq!(result.counts.get("MissingSymbol"), Some(&0));

        std::fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn reference_counts_are_case_sensitive() {
        let root = unique_temp_root();
        std::fs::create_dir_all(&root).unwrap();
        std::fs::write(
            root.join("Sample.cs"),
            ["var formatdetector = 1;", "var FormatDetector = 2;"].join("\n"),
        )
        .unwrap();

        let result = count_source_references_sync(
            root.clone(),
            vec!["FormatDetector".to_string()],
            Some(5_000),
        )
        .unwrap();

        assert_eq!(result.counts.get("FormatDetector"), Some(&1));

        std::fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn reference_counts_stop_at_the_deadline_and_say_so() {
        let root = unique_temp_root();
        std::fs::create_dir_all(&root).unwrap();
        for index in 0..120 {
            std::fs::write(
                root.join(format!("File{index}.cs")),
                "var formatDetector = new FormatDetector();\n".repeat(400),
            )
            .unwrap();
        }

        let result =
            count_source_references_sync(root.clone(), vec!["FormatDetector".to_string()], Some(1))
                .unwrap();

        assert!(result.approximate);
        assert!(result.scanned_files < 120);

        std::fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn reference_counts_ignore_blank_and_repeated_symbols() {
        let root = unique_temp_root();
        std::fs::create_dir_all(&root).unwrap();
        std::fs::write(root.join("Sample.cs"), "var detector = new Detector();").unwrap();

        let result = count_source_references_sync(
            root.clone(),
            vec![
                "  Detector  ".to_string(),
                "Detector".to_string(),
                "   ".to_string(),
            ],
            Some(5_000),
        )
        .unwrap();

        assert_eq!(result.counts.len(), 1);
        assert_eq!(result.counts.get("Detector"), Some(&1));

        std::fs::remove_dir_all(root).unwrap();
    }

    /// Not part of the normal suite: it reads a real, large checkout. Run it by
    /// hand when the margin-count budget is in question:
    ///
    /// ```text
    /// cargo test --release --manifest-path tauri-svelte-preview/src-tauri/Cargo.toml \
    ///   reference_count_pass_over_a_large_project -- --ignored --nocapture
    /// ```
    #[test]
    #[ignore]
    fn reference_count_pass_over_a_large_project() {
        let root = PathBuf::from(
            std::env::var("MCB_REFERENCE_COUNT_BENCH_ROOT")
                .unwrap_or_else(|_| "/Users/blackcolours/dev/work/EdiPlatform".to_string()),
        );
        if !root.is_dir() {
            crate::debug_log::stderr_log!("skipping: {} is not a directory", root.display());
            return;
        }

        let symbol_names = (0..120)
            .map(|index| format!("BenchmarkSymbol{index}"))
            .collect::<Vec<_>>();

        {
            let started = Instant::now();
            let mut records = Vec::new();
            let _ = collect_source_files(
                &root,
                &root,
                source_collection_limit(DEFAULT_SOURCE_LIST_LIMIT),
                None,
                &mut records,
                &SourceScanCancellation::none(),
                &mut SourceScanWalkProgress::default(),
            );
            crate::debug_log::stderr_log!(
                "walk only: {} files in {} ms",
                records.len(),
                started.elapsed().as_millis()
            );

            let started = Instant::now();
            let mut bytes_read = 0usize;
            for record in &records {
                if record.byte_count > MAX_PREVIEW_BYTES {
                    continue;
                }
                if let Ok(bytes) = std::fs::read(&record.path) {
                    bytes_read += bytes.len();
                }
            }
            crate::debug_log::stderr_log!(
                "read only: {} MB in {} ms",
                bytes_read / 1_000_000,
                started.elapsed().as_millis()
            );
        }

        // First pass warms the file cache; the second is the number that matters.
        for attempt in 1..=2 {
            let started = Instant::now();
            let result =
                count_source_references_sync(root.clone(), symbol_names.clone(), Some(600_000))
                    .unwrap();
            crate::debug_log::stderr_log!(
                "pass {attempt}: {} files in {} ms (approximate: {})",
                result.scanned_files,
                started.elapsed().as_millis(),
                result.approximate
            );
        }
    }

    #[test]
    fn backend_capabilities_name_every_addition_the_frontend_cannot_otherwise_detect() {
        // Pinned on purpose. Each name is a promise the frontend checks before it offers a
        // feature, so quietly renaming one turns that feature off in the app instead of
        // failing loudly.
        assert_eq!(
            backend_capabilities(),
            vec![
                "worktreeForceRemove".to_string(),
                "playwrightSessionKill".to_string(),
                "lspDiagnosticsForRoot".to_string(),
                "terminalCommandSpawn".to_string(),
                "referenceCounts".to_string(),
                "processKill".to_string(),
                "worktreePruneSingle".to_string(),
                "csharpLanguageServerToggle".to_string(),
                "languageServersToggle".to_string(),
                "workspaceLanguageIntelligence".to_string(),
                "lspDocumentSymbols".to_string(),
                "lspStatusEvents".to_string(),
                "lspLog".to_string(),
                "resourceSample".to_string(),
                "resourceSnapshot".to_string(),
                "resourceDiskScan".to_string(),
                "resourceDiskCleanup".to_string(),
                "resourceStopOwned".to_string(),
                "providerUsageQuota".to_string(),
                "usageHistory".to_string(),
                "usageHistoryIncremental".to_string(),
                "usageProviderSummary".to_string(),
                "usageDailyTotals".to_string(),
                "generate_commit_message".to_string(),
                "read_pull_request_context".to_string(),
                "generate_pull_request_details".to_string(),
                "create_pull_request".to_string(),
                "read_pull_request_status".to_string(),
                "acpLiveConversationEvents".to_string(),
            ]
        );
    }

    #[test]
    fn agent_conversation_commands_match_frontend_invokes_and_registration() {
        let conversation_frontend = include_str!(concat!(
            env!("CARGO_MANIFEST_DIR"),
            "/../src/lib/shell/conversation/conversationService.ts"
        ));
        let source_frontend = include_str!(concat!(
            env!("CARGO_MANIFEST_DIR"),
            "/../src/lib/tauriSource.ts"
        ));
        let native = include_str!(concat!(env!("CARGO_MANIFEST_DIR"), "/src/main.rs"));
        let handler = native
            .split_once(".invoke_handler(proof_mode::guard(tauri::generate_handler![")
            .expect("main must register a Tauri invoke handler").1
            .split_once("])")
            .expect("Tauri invoke handler must have a closing delimiter")
            .0;
        let mut commands = Vec::new();
        let mut remaining = conversation_frontend;
        while let Some(index) = remaining.find("invoke") {
            remaining = &remaining[index + "invoke".len()..];
            let arguments = if let Some(arguments) = remaining.strip_prefix('(') {
                arguments
            } else if let Some(generic) = remaining.strip_prefix('<') {
                let mut depth = 1;
                let mut generic_end = None;
                for (index, ch) in generic.char_indices() {
                    match ch {
                        '<' => depth += 1,
                        '>' => {
                            depth -= 1;
                            if depth == 0 {
                                generic_end = Some(index + ch.len_utf8());
                                break;
                            }
                        }
                        _ => {}
                    }
                }
                let Some(generic_end) = generic_end else {
                    continue;
                };
                let Some(arguments) = generic[generic_end..].trim_start().strip_prefix('(') else {
                    continue;
                };
                arguments
            } else {
                continue;
            };
            let arguments = arguments.trim_start();
            let Some(quote) = arguments
                .chars()
                .next()
                .filter(|ch| matches!(ch, '\'' | '"'))
            else {
                continue;
            };
            let command = arguments[quote.len_utf8()..]
                .split_once(quote)
                .map(|(command, _)| command)
                .expect("literal invoke command must have a closing quote");
            commands.push(command);
        }
        commands.sort_unstable();
        commands.dedup();
        assert!(
            !commands.is_empty(),
            "frontend must invoke conversation commands"
        );
        for command in commands {
            assert!(
                handler.contains(&format!("::{command},")),
                "generate_handler must register {command}"
            );
        }
        for command in [
            "list_agent_conversation_sessions",
            "list_agent_conversation_events",
            "update_agent_conversation_session_meta",
        ] {
            assert!(
                source_frontend.contains(&format!("invoke<{command}"))
                    || source_frontend
                        .contains(&format!("invoke<AgentConversationEvent[]>(\'{command}\'"))
                    || source_frontend.contains(&format!(
                        "invoke<AgentConversationSessionRecord[]>(\'{command}\'"
                    ))
                    || source_frontend.contains(&format!(
                        "invoke<AgentConversationSessionRecord>(\'{command}\'"
                    )),
                "frontend invoke must pin {command}"
            );
            assert!(
                native.contains(&format!("agent_conversation::{command}")),
                "generate_handler must register {command}"
            );
        }
    }

    #[test]
    fn git_commit_file_changes_parse_name_status_lines_with_status_badges() {
        let output =
            "M\tsrc/App.ts\nA\tsrc/New.ts\nD\tsrc/Old.ts\nR100\tsrc/Old.ts\tsrc/Renamed.ts\n";

        let changes = parse_git_commit_file_changes(output);

        assert_eq!(changes.len(), 4);
        assert_eq!(changes[0].relative_path, "src/App.ts");
        assert_eq!(changes[0].status, "modified");
        assert_eq!(changes[0].badge, "M");
        assert_eq!(changes[1].relative_path, "src/New.ts");
        assert_eq!(changes[1].status, "added");
        assert_eq!(changes[1].badge, "A");
        assert_eq!(changes[2].relative_path, "src/Old.ts");
        assert_eq!(changes[2].status, "deleted");
        assert_eq!(changes[2].badge, "D");
        // A rename reports the path the file ended up at, the way the status panel does.
        assert_eq!(changes[3].relative_path, "src/Renamed.ts");
        assert_eq!(changes[3].status, "renamed");
        assert_eq!(changes[3].badge, "R");
    }

    #[test]
    fn git_commit_file_reads_refuse_option_shaped_commit_ids_and_absolute_paths() {
        let root = unique_temp_root();
        std::fs::create_dir_all(&root).unwrap();

        let error =
            read_git_commit_files_sync(root.clone(), "--upload-pack=evil".to_string()).unwrap_err();
        assert!(error.contains("commit id"), "unexpected message: {error}");

        let error = read_git_commit_file_diff_sync(
            root.clone(),
            "HEAD".to_string(),
            "/etc/passwd".to_string(),
        )
        .unwrap_err();
        assert!(
            error.contains("relative repo paths"),
            "unexpected message: {error}"
        );

        std::fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn git_commit_files_and_diff_read_one_commit() {
        let root = unique_temp_root();
        std::fs::create_dir_all(root.join("src")).unwrap();
        std::fs::write(root.join("src/App.ts"), "export const value = 1;\n").unwrap();
        std::fs::write(root.join("README.md"), "initial\n").unwrap();

        run_git_for_test(&root, &["init"]);
        run_git_for_test(&root, &["config", "user.name", "MacCommandBar Test"]);
        run_git_for_test(&root, &["config", "user.email", "test@example.invalid"]);
        run_git_for_test(&root, &["add", "."]);
        run_git_for_test(&root, &["commit", "-m", "initial"]);

        std::fs::write(root.join("src/App.ts"), "export const value = 2;\n").unwrap();
        std::fs::write(root.join("src/Added.ts"), "export const added = true;\n").unwrap();
        std::fs::remove_file(root.join("README.md")).unwrap();
        run_git_for_test(&root, &["add", "-A"]);
        run_git_for_test(&root, &["commit", "-m", "second"]);

        let history = read_git_commit_history_page_sync(root.clone(), None, None)
            .unwrap()
            .commits;
        let sha = history[0].sha.clone();

        let mut files = read_git_commit_files_sync(root.clone(), sha.clone()).unwrap();
        files.sort_by(|left, right| left.relative_path.cmp(&right.relative_path));

        assert_eq!(files.len(), 3);
        assert_eq!(files[0].relative_path, "README.md");
        assert_eq!(files[0].status, "deleted");
        assert_eq!(files[0].badge, "D");
        assert_eq!(files[1].relative_path, "src/Added.ts");
        assert_eq!(files[1].status, "added");
        assert_eq!(files[1].badge, "A");
        assert_eq!(files[2].relative_path, "src/App.ts");
        assert_eq!(files[2].status, "modified");
        assert_eq!(files[2].badge, "M");

        let diff =
            read_git_commit_file_diff_sync(root.clone(), sha, "src/App.ts".to_string()).unwrap();

        assert_eq!(diff.relative_path, "src/App.ts");
        assert_eq!(diff.status, "modified");
        assert!(!diff.is_binary);
        assert!(diff.diff.contains("--- a/src/App.ts"));
        assert!(diff.diff.contains("+export const value = 2;"));
        assert!(diff.diff.contains("-export const value = 1;"));
        // The commit subject line never leaks into the patch text, so the same unified
        // diff parser the working-copy diff uses can read this one.
        assert!(!diff.diff.contains("second"));

        std::fs::remove_dir_all(root).unwrap();
    }

    fn unique_temp_root() -> PathBuf {
        static TEMP_ROOT_COUNTER: std::sync::atomic::AtomicUsize =
            std::sync::atomic::AtomicUsize::new(0);

        let unique = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .unwrap()
            .as_nanos();
        let counter = TEMP_ROOT_COUNTER.fetch_add(1, Ordering::Relaxed);
        std::env::temp_dir().join(format!(
            "mac-command-bar-tauri-source-test-{}-{unique}-{counter}",
            std::process::id()
        ))
    }

    fn run_git_for_test(root: &Path, args: &[&str]) {
        let output = Command::new("git")
            .args(["-C", root.to_str().unwrap()])
            .args(args)
            .output()
            .unwrap_or_else(|error| panic!("could not run git {args:?}: {error}"));
        assert!(
            output.status.success(),
            "git {args:?} failed: {}",
            String::from_utf8_lossy(&output.stderr)
        );
    }
}
