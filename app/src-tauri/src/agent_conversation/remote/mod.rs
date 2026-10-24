//! One authenticated WebSocket boundary for conversations owned by a remote machine.

mod handshake;
mod history;
mod relay_trust;
mod transcripts;
#[cfg(test)] mod routing_tests;
pub(in crate::agent_conversation) mod install;
pub(crate) mod install_flow;
mod workspace;
pub(crate) mod server;
pub(in crate::agent_conversation) mod wire;
#[cfg(test)] mod live_gap_tests;

use std::collections::{BTreeMap, HashMap, HashSet};
#[cfg(debug_assertions)]
use std::collections::VecDeque;
use std::net::{TcpListener as StdTcpListener, TcpStream};
use std::path::PathBuf;
use std::process::{Child, Command, Stdio};
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant};

use base64::{engine::general_purpose::STANDARD as BASE64, Engine as _};
use futures_util::{future::join_all, SinkExt, StreamExt};
use mcb_core::session_store::{HistoryDownloadFacts, SessionStore};
use serde::{Deserialize, Serialize};
#[cfg(debug_assertions)]
use tokio::io::{AsyncBufReadExt, BufReader};
use tokio::sync::{broadcast, mpsc, oneshot};
use tokio_tungstenite::tungstenite::Message as TungsteniteMessage;

use self::wire::{decode_history_chunk, parse_server_frame, send_client_frame,
    ClientFrame, RemoteCommand, RemoteResponse, ServerFrame};
#[cfg(test)]
use self::wire::{encode_history_chunk, encode_server_frame};
#[cfg(test)]
use self::server::{abort_attachment_upload, execute_remote_command, execute_server_command,
    wait_for_provider_restart, write_attachment_chunk};
#[cfg(test)]
use self::server::socket::{next_outbound_frame, replay_session, stream_history_download, upgrade_remote_socket};
#[cfg(test)]
use axum::{routing::get, Router};
#[cfg(test)]
use std::future::IntoFuture;
use super::manager::AgentRuntimeManager;
use self::history::RemoteHistory;
use crate::projection_streams::ProjectionStreams;
use crate::terminal::{
    TerminalOutputFrame, TerminalRegistry, TerminalScrollbackSnapshot,
    TerminalSessionInfo, TerminalStartRequest,
};
type RemoteEventSink = Arc<dyn Fn(Vec<AgentConversationEvent>) -> Result<(), String> + Send + Sync>;
type RemoteTerminalSink = Arc<dyn Fn(TerminalOutputFrame) + Send + Sync>;
use super::attachments::{self, DeleteConversationAttachmentRequest, SavedConversationAttachment};
use super::protocol::{
    AgentCapabilities, AgentConfigOption, AgentConversationConfigState,
    AgentConversationChildHistorySelection, AgentConversationConnection, AgentConversationEvent,
    AgentConversationEventPage, AgentConversationItemPage, AgentConversationProvider, AgentConversationSelectionSnapshot,
    AgentConversationSendReceipt, AgentConversationSessionRecord, AgentConversationSnapshot,
    ChangeAgentConversationCheckoutRequest,
    EnsureAgentConversationRequest, ExecutionEnvironment,
    RespondAgentConversationApprovalRequest, RespondAgentConversationInputRequest,
    RespondAgentConversationPermissionRequest, SendAgentConversationMessageRequest,
    SetAgentConversationConfigRequest, StopAgentConversationTurnRequest,
    UpdateAgentConversationSessionMetaRequest,
};
use super::providers::ProviderRegistry;

pub(super) const PROTOCOL_VERSION: u16 = 24;
const MAX_WIRE_FRAME_BYTES: usize = 1024 * 1024;
// Requests stay small; history pages can include one indivisible event beyond
// their byte budget. Match the existing desktop WebSocket frame ceiling.
const MAX_SERVER_FRAME_BYTES: usize = 16 * 1024 * 1024;
const ATTACHMENT_CHUNK_BYTES: usize = 128 * 1024;
const OUTBOUND_FRAME_CAPACITY: usize = 8;
pub(super) static MESSAGE_CHANGES: std::sync::LazyLock<tokio::sync::watch::Sender<u64>> =
    std::sync::LazyLock::new(|| tokio::sync::watch::channel(0).0);
static MESSAGES_CHANGED_SINK: std::sync::OnceLock<Arc<dyn Fn() + Send + Sync>> = std::sync::OnceLock::new();
const REPLAY_PAGE_BYTES: u32 = 1024 * 1024;
const HISTORY_DOWNLOAD_CHUNK_BYTES: u32 = 512 * 1024;
pub const REMOTE_ASSEMBLY_PROFILE_SETTING_KEY: &str = "remote-assembly.profile.v1";
pub const REMOTE_ASSEMBLY_PROFILES_SETTING_KEY: &str = "remote-assembly.profiles.v1";
const REMOTE_SESSION_PROJECTIONS_SETTING_KEY: &str = "remote-assembly.session-projections.v1";
const REMOTE_SERVER_PORT: u16 = 7777;
const READINESS_REQUEST_ID: u64 = u64::MAX;
/// What messages call this desktop; a remote machine is called by its profile id.
pub const LOCAL_MACHINE: &str = "mac";

#[derive(Clone)]
pub(super) struct ServerState {
    pub(super) manager: AgentRuntimeManager,
    pub(super) data_dir: PathBuf,
    pub(super) token: Arc<str>,
    maintenance: Arc<tokio::sync::RwLock<()>>,
    restarting: Arc<AtomicBool>,
    restart_requested: Arc<tokio::sync::Notify>,
    restart_flushed: Arc<tokio::sync::Notify>,
    pub(super) events: broadcast::Sender<AgentConversationEvent>,
    /// Runs started on this machine, kept in its own `sessions.db`.
    pub(super) workflow: crate::workflow::WorkflowEngine,
    pub(super) terminals: RemoteTerminals,
    relay: Arc<RelayLink>,
    /// The one phone pairing code that works right now (`phone`).
    pub(super) pairing: Arc<Mutex<Option<super::phone::PairingCode>>>,
}

/// The desktops connected to this server, through which its sessions message other machines.
#[derive(Default)]
struct RelayLink {
    desktops: Mutex<Vec<(u64, mpsc::Sender<ServerFrame>)>>,
    pending: Mutex<HashMap<u64, oneshot::Sender<Result<serde_json::Value, String>>>>,
    next_id: AtomicU64,
}

impl RelayLink {
    /// Asks the most recently connected desktop to carry one messaging operation.
    async fn ask(&self, op: String, args: serde_json::Value) -> Result<serde_json::Value, String> {
        let desktop = self.desktops.lock().unwrap_or_else(std::sync::PoisonError::into_inner)
            .last().map(|(_, outbound)| outbound.clone())
            .ok_or("target machine not reachable: no desktop is connected to this machine")?;
        let request_id = self.next_id.fetch_add(1, Ordering::Relaxed);
        let (reply, answer) = oneshot::channel();
        self.pending.lock().unwrap_or_else(std::sync::PoisonError::into_inner).insert(request_id, reply);
        let sent = desktop.send(ServerFrame::RelayRequest { request_id, op, args }).await.is_ok();
        // A closing socket waits for every sender, so this one is not held through the wait.
        drop(desktop);
        let answer = if sent { tokio::time::timeout(Duration::from_secs(30), answer).await.ok() } else { None };
        self.pending.lock().unwrap_or_else(std::sync::PoisonError::into_inner).remove(&request_id);
        answer.and_then(Result::ok)
            .unwrap_or_else(|| Err("target machine not reachable: the desktop did not answer".into()))
    }

    fn relay(self: &Arc<Self>) -> super::messaging::Relay {
        let link = self.clone();
        Arc::new(move |op, args| {
            let link = link.clone();
            Box::pin(async move { link.ask(op, args).await })
        })
    }
}

/// The server's state over `manager`: its events go to the sockets, and a
/// finished turn also advances the workflow run that owns the session. Runs
/// interrupted by the last shutdown are recovered before any request is served.
pub(super) fn server_state(manager: AgentRuntimeManager, data_dir: PathBuf, token: Arc<str>) -> Result<ServerState, String> {
    let (events, _) = broadcast::channel(256);
    let workflow = crate::workflow::WorkflowEngine::managed(manager.clone(), manager.store_handle()); crate::usage_history::start_acp_usage(data_dir.join("usage-history.sqlite3"), manager.store_handle()); crate::usage_history::otel::start(&data_dir.join("usage-history.sqlite3"), manager.clone());
    let event_sender = events.clone();
    let workflow_events = workflow.clone();
    let runtime = tokio::runtime::Handle::current();
    manager.set_emitter(Arc::new(move |event| {
        if matches!(&event.payload, super::protocol::AgentConversationPayload::Turn {
            state: super::protocol::TurnState::Completed | super::protocol::TurnState::Interrupted
                | super::protocol::TurnState::Failed, ..
        }) {
            let workflow_events = workflow_events.clone();
            let event = event.clone();
            runtime.spawn(async move {
                if let Err(error) = workflow_events.accept_agent_event(&event).await {
                    eprintln!("Could not advance workflow from agent event: {error}");
                }
            });
        }
        let _ = event_sender.send(event);
    }));
    for run_id in workflow.recover_after_restart().map_err(|error| error.to_string())? {
        let workflow = workflow.clone();
        tokio::spawn(async move {
            if let Err(error) = workflow.dispatch_ready(&run_id).await {
                eprintln!("workflow run {run_id} could not restart: {error}");
            }
        });
    }
    mcb_core::broker::on_messages_changed(|| MESSAGE_CHANGES.send_modify(|version| *version = version.wrapping_add(1)));
    Ok(ServerState {
        manager,
        data_dir,
        token,
        maintenance: Arc::new(tokio::sync::RwLock::new(())),
        restarting: Arc::new(AtomicBool::new(false)),
        restart_requested: Arc::new(tokio::sync::Notify::new()),
        restart_flushed: Arc::new(tokio::sync::Notify::new()),
        events,
        workflow,
        terminals: RemoteTerminals::new(),
        relay: Arc::default(),
        pairing: Arc::default(),
    })
}

/// Shells that desktops opened on this machine. Output fans out to every connected socket.
#[derive(Clone)]
pub(super) struct RemoteTerminals {
    pub(super) registry: TerminalRegistry,
    streams: ProjectionStreams,
    pub(super) output: broadcast::Sender<TerminalOutputFrame>,
}

impl RemoteTerminals {
    fn new() -> Self {
        let (output, _) = broadcast::channel(1024);
        Self {
            registry: TerminalRegistry::default(),
            streams: ProjectionStreams::with_terminal_tap(output.clone()),
            output,
        }
    }
}

/// A desktop names a remote shell by its machine and the machine's own terminal id.
const REMOTE_TERMINAL_PREFIX: &str = "assembly-remote-terminal://";

pub fn remote_terminal_id(profile_id: &str, session_id: &str) -> String {
    format!("{REMOTE_TERMINAL_PREFIX}{profile_id}/{session_id}")
}

/// `(profile id, the machine's terminal id)` for a remote shell's id.
pub fn split_remote_terminal_id(id: &str) -> Option<(&str, &str)> {
    id.strip_prefix(REMOTE_TERMINAL_PREFIX)?.split_once('/')
}

fn terminal_bool(response: RemoteResponse) -> Result<bool, String> {
    match response {
        RemoteResponse::Bool(value) => Ok(value),
        _ => Err("The remote backend returned an incompatible terminal response".into()),
    }
}

/// `(profile id, native folder)` for a session folder on a saved machine.
pub fn split_remote_workspace_path(path: &str) -> Option<(&str, &str)> {
    let rest = path.strip_prefix("assembly-remote://")?;
    let slash = rest.find('/')?;
    Some((&rest[..slash], &rest[slash..]))
}

#[derive(Debug)]
struct PendingAttachment {
    upload_id: String,
    owned_id: String,
    mime_type: String,
    path: PathBuf,
    file: std::fs::File,
    bytes: usize,
    deadline: tokio::time::Instant,
}

impl Drop for PendingAttachment {
    fn drop(&mut self) {
        let _ = std::fs::remove_file(&self.path);
    }
}

type ClientReply = oneshot::Sender<Result<RemoteResponse, String>>;

enum ClientRequest {
    Download { id: u64, command: RemoteCommand },
    Execute {
        id: u64,
        command: RemoteCommand,
        reply: ClientReply,
        progress: Option<mpsc::UnboundedSender<super::providers::updates::ProviderDownloadProgress>>,
    },
    Cancel {
        id: u64,
    },
}

#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct HistoryDownloadProgress {
    pub profile_id: String,
    pub source_owned_id: String,
    pub workspace_owned_id: String,
    pub child_session_id: Option<String>,
    pub total_bytes: u64,
    pub covered_bytes: u64,
    pub phase: String,
    pub ready: bool,
    pub error: Option<String>,
}

struct HistoryDownload {
    request_id: Option<u64>,
    epoch: u64,
    low: Option<i64>,
    high: Option<i64>,
    row_count: u64,
    progress: tokio::sync::watch::Sender<HistoryDownloadProgress>,
}

/// This desktop's record of the owner's latest main orchestrator choice: `{machine, owned_id}`.
const ORCHESTRATOR_CHOICE: &str = "orchestrator.choice";

/// Owns the remote socket, SSH tunnels, and compact routing data needed to reconnect.
#[derive(Clone)]
pub struct RemoteConnectionManager {
    client: Arc<Mutex<RemoteClientState>>,
    tunnel_processes: Arc<Mutex<HashMap<u32, Child>>>,
    remote_sessions: Arc<Mutex<HashMap<String, String>>>,
    cached_sessions: Arc<Mutex<Vec<AgentConversationSessionRecord>>>,
    store: Arc<SessionStore>,
    history: Arc<Mutex<RemoteHistory>>,
    next_request_id: Arc<AtomicU64>,
    event_sink: Arc<dyn Fn(AgentConversationEvent) + Send + Sync>,
    status_sink: Arc<dyn Fn(RemoteConnectionStatus) + Send + Sync>,
    connection_lock: Arc<tokio::sync::Mutex<()>>,
    downloads: Arc<Mutex<HashMap<String, HistoryDownload>>>,
    download_sink: Arc<Mutex<Option<Arc<dyn Fn(HistoryDownloadProgress) + Send + Sync>>>>,
    /// Announces each cancelled frontend request id to page reads waiting on the Mac.
    cancelled_requests: broadcast::Sender<u64>,
    attempts: Arc<Mutex<HashMap<String, tokio::task::AbortHandle>>>,
    /// The attempt token of each profile whose connection attempt is running
    /// before any client exists. Removing the profile drops its token, which
    /// both answers disconnected at once and tells the running attempt that it
    /// no longer owns the profile.
    connecting: Arc<Mutex<HashMap<String, u64>>>,
    next_attempt_token: Arc<AtomicU64>,
    /// Where remote shell output is shown; set once the desktop's streams exist.
    terminal_output: Arc<Mutex<Option<ProjectionStreams>>>,
    /// This desktop's own sessions, which messages from other machines reach.
    message_host: Arc<Mutex<Option<AgentRuntimeManager>>>,
    /// Held while the owner's orchestrator choice is changed or put back on a machine, so a
    /// restore that read the old choice cannot clear a newer one.
    orchestrator_choice: Arc<tokio::sync::Mutex<()>>,
}

#[derive(Default)]
struct RemoteClientState {
    clients: HashMap<String, RemoteClient>,
}

struct RemoteClient {
    requests: Option<mpsc::Sender<ClientRequest>>,
    profile: Option<RemoteAssemblyProfile>,
    target_key: Option<String>,
    task: Option<tokio::task::AbortHandle>,
    ready: Arc<AtomicBool>,
}

impl Drop for RemoteClient {
    fn drop(&mut self) {
        if let Some(task) = &self.task {
            eprintln!("Remote client for {:?} was dropped", self.profile.as_ref().map(|profile| &profile.id));
            task.abort();
        }
    }
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub struct RemoteConnectionResult {
    profile: RemoteAssemblyProfile,
    sessions: Vec<AgentConversationSessionRecord>,
    replaced_profile_id: Option<String>,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub struct RemoteUninstallResult {
    environment: RemoteAssemblyEnvironment,
    replaced_profile_id: Option<String>,
}

#[derive(Clone, Debug, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct RemoteAssemblyProfile {
    pub id: String,
    pub name: String,
    pub ssh_target: String,
    pub source_root: String,
    pub default_cwd: String,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct LegacyRemoteAssemblyProfile {
    ssh_target: String,
    source_root: String,
    default_cwd: String,
}

#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct RemoteAssemblyEnvironment {
    pub profiles: Vec<RemoteAssemblyProfile>,
    pub ready_profile_ids: Vec<String>,
}

#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct RemoteBackendProfileStatus {
    pub profile_id: String,
    #[serde(flatten)]
    pub backend: self::install::BackendStatus,
}

/// What one saved machine's transport is doing. The manager derives it from the
/// live client only: a session row, a saved record, or a transcript read never
/// implies it.
#[derive(Clone, Copy, Debug, Serialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub enum RemoteConnectionState {
    /// The socket answered the readiness session list.
    Connected,
    /// A requested client or tunnel is attempting or retrying.
    Reconnecting,
    /// Stopped on request, or no retry remains and the client is gone.
    Disconnected,
}

#[derive(Clone, Debug, Serialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct RemoteConnectionStatus {
    pub profile_id: String,
    pub state: RemoteConnectionState,
}

/// Holds one profile in Reconnecting for as long as its attempt runs, including
/// the cancelled and timed-out attempts, which drop it.
struct ConnectingAttempt {
    manager: RemoteConnectionManager,
    profile_id: String,
    token: u64,
}

impl ConnectingAttempt {
    fn start(manager: &RemoteConnectionManager, profile_id: &str) -> Self {
        let token = manager.next_attempt_token.fetch_add(1, Ordering::Relaxed);
        manager
            .connecting
            .lock()
            .unwrap_or_else(std::sync::PoisonError::into_inner)
            .insert(profile_id.to_string(), token);
        manager.publish_status(profile_id);
        Self { manager: manager.clone(), profile_id: profile_id.to_string(), token }
    }
}

impl Drop for ConnectingAttempt {
    fn drop(&mut self) {
        {
            let mut connecting = self
                .manager
                .connecting
                .lock()
                .unwrap_or_else(std::sync::PoisonError::into_inner);
            // The profile was removed, or a later attempt took it over: this
            // attempt owns nothing and must not report for it.
            if connecting.get(&self.profile_id) != Some(&self.token) {
                return;
            }
            connecting.remove(&self.profile_id);
        }
        self.manager.publish_status(&self.profile_id);
    }
}

/// Reports the end of a live connection once, however the actor ends: normal
/// exit, error return, or an aborted task.
struct ConnectionEnd {
    ready: Arc<AtomicBool>,
    status: Arc<dyn Fn() + Send + Sync>,
}

impl Drop for ConnectionEnd {
    fn drop(&mut self) {
        if self.ready.swap(false, Ordering::AcqRel) {
            (self.status)();
        }
    }
}

/// Machines' SQL-ordered pages as one page, each message tagged with its machine.
fn merge_message_pages(pages: Vec<(String, Vec<serde_json::Value>)>, limit: usize, sort: &str) -> Vec<serde_json::Value> {
    let mut messages: Vec<_> = pages.into_iter().flat_map(|(machine, page)| page.into_iter()
        .map(move |mut message| { message["machine"] = serde_json::json!(machine); message })).collect();
    let key = match sort { "sender" => Some("senderName"), "receiver" => Some("receiver"), _ => None };
    messages.sort_by(|a, b| {
        key.map(|key| a[key].as_str().unwrap_or_default().cmp(b[key].as_str().unwrap_or_default()))
            .unwrap_or(std::cmp::Ordering::Equal)
            .then_with(|| b["createdAtMs"].as_i64().cmp(&a["createdAtMs"].as_i64()))
            .then_with(|| b["id"].as_str().cmp(&a["id"].as_str()))
    });
    messages.truncate(limit.min(super::messaging::MAX_MESSAGE_PAGE as usize));
    messages
}

fn read_cached_sessions(store: &SessionStore) -> Result<Vec<AgentConversationSessionRecord>, String> {
    // One unreadable saved session must not hide every remote machine's list.
    Ok(store.remote_session_controls().map_err(|error| error.to_string())?
        .into_iter().filter_map(|(metadata, controls)| (|| -> Result<AgentConversationSessionRecord, String> {
            let mut row: serde_json::Value = serde_json::from_str(&metadata).map_err(|e| e.to_string())?;
            let controls: serde_json::Value = serde_json::from_str(controls.as_deref().unwrap_or("{}"))
                .map_err(|e| e.to_string())?;
            for (field, saved, default) in [
                ("state", "runtimeState", serde_json::json!("suspended")),
                ("suspended", "suspended", serde_json::json!(true)),
                ("activeTurnId", "activeTurnId", serde_json::Value::Null),
                ("pendingPermission", "pendingPermission", serde_json::json!(false)),
                ("pendingInput", "pendingInput", serde_json::json!(false)),
            ] {
                // A list saved before activity moved into the history controls
                // still carries it; it serves until the next listing replaces it.
                row[field] = controls.get(saved).or_else(|| row.get(field)).cloned().unwrap_or(default);
            }
            serde_json::from_value(row).map_err(|e| e.to_string())
        })().map_err(|error| eprintln!("Skipped an unreadable saved remote session: {error}")).ok())
        .collect())
}

impl RemoteConnectionManager {
    pub fn from_environment(
        event_sink: Arc<dyn Fn(AgentConversationEvent) + Send + Sync>,
        status_sink: Arc<dyn Fn(RemoteConnectionStatus) + Send + Sync>,
        store: Arc<SessionStore>,
    ) -> Result<Self, String> {
        // A saved claim on one of this Mac's own session ids is dropped (see `may_route`).
        let cached_sessions: Vec<_> = read_cached_sessions(&store)?.into_iter().filter(|session| matches!(store.get_session(&session.owned_id), Ok(None))).collect();
        let remote_sessions = cached_sessions
            .iter()
            .filter_map(|session| {
                session
                    .remote_profile_id
                    .as_ref()
                    .map(|profile_id| (session.owned_id.clone(), profile_id.clone()))
            })
            .collect();
        let manager = Self {
            client: Arc::new(Mutex::new(RemoteClientState::default())),
            tunnel_processes: Arc::new(Mutex::new(HashMap::new())),
            remote_sessions: Arc::new(Mutex::new(remote_sessions)),
            cached_sessions: Arc::new(Mutex::new(cached_sessions)),
            history: Arc::new(Mutex::new(RemoteHistory::new(store.clone()))),
            store,
            next_request_id: Arc::new(AtomicU64::new(1)),
            event_sink,
            status_sink,
            connection_lock: Arc::new(tokio::sync::Mutex::new(())),
            downloads: Arc::new(Mutex::new(HashMap::new())),
            download_sink: Arc::new(Mutex::new(None)),
            cancelled_requests: broadcast::channel(64).0,
            attempts: Arc::new(Mutex::new(HashMap::new())),
            connecting: Arc::new(Mutex::new(HashMap::new())),
            next_attempt_token: Arc::new(AtomicU64::new(1)),
            terminal_output: Arc::new(Mutex::new(None)),
            message_host: Arc::new(Mutex::new(None)),
            orchestrator_choice: Arc::new(tokio::sync::Mutex::new(())),
        };
        let Some(url) = std::env::var("ASSEMBLY_REMOTE_WS_URL").ok() else {
            return Ok(manager);
        };
        let token = std::env::var("ASSEMBLY_REMOTE_TOKEN").map_err(|_| {
            "ASSEMBLY_REMOTE_TOKEN is required when a remote URL is configured".to_string()
        })?;
        let default_cwd = std::env::var("ASSEMBLY_REMOTE_DEFAULT_CWD").map_err(|_| {
            "ASSEMBLY_REMOTE_DEFAULT_CWD is required when a remote URL is configured".to_string()
        })?;
        if !PathBuf::from(&default_cwd).is_absolute() {
            return Err("ASSEMBLY_REMOTE_DEFAULT_CWD must be an absolute path".to_string());
        }
        let (request_tx, mut request_rx) = mpsc::channel(32);
        let event_sink = manager.history_event_sink("environment", true);
        let ready = Arc::new(AtomicBool::new(false));
        let actor_ready = ready.clone();
        manager
            .client
            .lock()
            .unwrap_or_else(std::sync::PoisonError::into_inner)
            .clients
            .insert(
                "environment".to_string(),
                RemoteClient {
                    requests: Some(request_tx),
                    profile: None,
                    target_key: None,
                    task: None,
                    ready,
                },
            );
        let history_client = manager.clone();
        tauri::async_runtime::spawn(async move {
            // The development environment client has no saved profile, so no rail
            // row maps to it and its transitions have nobody to tell.
            let status: Arc<dyn Fn() + Send + Sync> = Arc::new(|| {});
            client_loop(url, token, &mut request_rx, event_sink, None, None, actor_ready, &mut None, status, None, (history_client, "environment".into()))
                .await;
        });
        Ok(manager)
    }

    /// The compact local rail projection. Conversation events and transcripts
    /// are cached in source-marked rows in the ordinary local session database.
    pub fn cached_sessions(&self) -> Vec<AgentConversationSessionRecord> {
        self.cached_sessions
            .lock()
            .unwrap_or_else(std::sync::PoisonError::into_inner)
            .clone()
    }

    fn save_cached_sessions(
        &self,
        next: Vec<AgentConversationSessionRecord>,
    ) -> Result<(), String> {
        let mut metadata = serde_json::to_value(&next).map_err(|error| error.to_string())?;
        for row in metadata.as_array_mut().expect("session list") {
            for field in ["state", "suspended", "activeTurnId", "pendingPermission", "pendingInput"] {
                row.as_object_mut().expect("session record").remove(field);
            }
        }
        let json = serde_json::to_string(&metadata).map_err(|error| error.to_string())?;
        self.store
            .upsert_app_setting(REMOTE_SESSION_PROJECTIONS_SETTING_KEY, &json)
            .map_err(|error| error.to_string())?;
        *self
            .cached_sessions
            .lock()
            .unwrap_or_else(std::sync::PoisonError::into_inner) = next;
        Ok(())
    }

    fn replace_cached_profile_sessions(
        &self,
        profile_id: &str,
        sessions: &[AgentConversationSessionRecord],
    ) -> Result<(), String> {
        let sessions = &sessions.iter().filter(|session| self.may_route(&session.owned_id, profile_id)).cloned().collect::<Vec<_>>();
        let incoming_ids = sessions.iter().map(|session| session.owned_id.as_str()).collect::<std::collections::HashSet<_>>();
        let mut next = self.cached_sessions();
        let mut moved = Vec::new();
        // Read again from its transcript there, a session is numbered on above its old head: a copy saved here before
        // that would show every turn twice. A server too old to say leaves the copy alone.
        for session in sessions.iter().filter(|session| session.numbered_after.is_some()) {
            if next.iter().any(|old| old.owned_id == session.owned_id && old.remote_profile_id.as_deref() == Some(profile_id)
                && old.numbered_after != session.numbered_after) {
                // A download still running is of the replaced journal: its chunks are dropped, and the next one starts afresh.
                self.downloads.lock().unwrap().remove(&RemoteHistory::key(profile_id, &session.owned_id));
                moved.extend(crate::agent_conversation::manager::session_project::saved_project(&self.store, profile_id, &session.owned_id)?);
                self.history.lock().unwrap_or_else(std::sync::PoisonError::into_inner).forget(profile_id, &session.owned_id)?;
            }
        }
        next.retain(|session| {
            session.remote_profile_id.as_deref() != Some(profile_id)
                && !incoming_ids.contains(session.owned_id.as_str())
        });
        next.extend(sessions.iter().cloned().map(|mut session| {
            session.execution_environment = ExecutionEnvironment::Remote;
            session.remote_profile_id = Some(profile_id.to_string());
            session
        }));
        next.sort_by(|left, right| right.last_activity_at_ms.cmp(&left.last_activity_at_ms).then_with(|| left.owned_id.cmp(&right.owned_id)));
        self.save_cached_sessions(next)?;
        self.history.lock().unwrap_or_else(std::sync::PoisonError::into_inner).file_sessions(profile_id, sessions)?;
        for (key, project) in moved { self.store.move_session_project(&key, &project).map_err(|e| e.to_string())?; }
        let mut routing = self.remote_sessions.lock().unwrap_or_else(std::sync::PoisonError::into_inner);
        routing.retain(|_, stored_profile_id| stored_profile_id != profile_id);
        routing.extend(sessions.iter().map(|session| (session.owned_id.clone(), profile_id.to_string())));
        Ok(())
    }

    fn reassign_cached_profile_sessions(
        &self,
        from_profile_id: &str,
        to_profile_id: &str,
    ) -> Result<(), String> {
        if from_profile_id == to_profile_id {
            return Ok(());
        }
        let mut next = self.cached_sessions();
        let mut changed = false;
        for session in &mut next {
            if session.remote_profile_id.as_deref() == Some(from_profile_id) {
                session.remote_profile_id = Some(to_profile_id.to_string());
                changed = true;
            }
        }
        if !changed {
            return Ok(());
        }
        self.save_cached_sessions(next)?;
        let mut routing = self
            .remote_sessions
            .lock().unwrap_or_else(std::sync::PoisonError::into_inner);
        for profile_id in routing.values_mut() {
            if profile_id == from_profile_id {
                *profile_id = to_profile_id.to_string();
            }
        }
        Ok(())
    }

    fn cache_session(&self, mut session: AgentConversationSessionRecord) -> Result<(), String> {
        session.execution_environment = ExecutionEnvironment::Remote;
        let profile_id = session.remote_profile_id.clone().ok_or_else(|| "Remote session did not identify its machine".to_string())?;
        if !self.remember(session.owned_id.clone(), profile_id) {
            return Err(format!("Remote session {} uses the id of another session", session.owned_id));
        }
        let mut next = self.cached_sessions();
        next.retain(|stored| stored.owned_id != session.owned_id);
        next.push(session);
        next.sort_by(|left, right| right.last_activity_at_ms.cmp(&left.last_activity_at_ms).then_with(|| left.owned_id.cmp(&right.owned_id)));
        self.save_cached_sessions(next)
    }

    fn forget_cached_session(&self, owned_id: &str) -> Result<(), String> {
        let mut next = self.cached_sessions();
        next.retain(|session| session.owned_id != owned_id);
        self.save_cached_sessions(next)
    }

    pub fn is_configured(&self) -> bool {
        self.client
            .lock()
            .unwrap_or_else(std::sync::PoisonError::into_inner)
            .clients
            .values()
            .any(|client| client.requests.is_some())
    }

    pub fn environment(&self) -> RemoteAssemblyEnvironment {
        let mut profiles = self
            .client
            .lock()
            .unwrap_or_else(std::sync::PoisonError::into_inner)
            .clients
            .values()
            .filter_map(|client| client.profile.clone())
            .collect::<Vec<_>>();
        profiles.sort_by(|left, right| left.name.cmp(&right.name));
        let ready_profile_ids = self.client.lock().unwrap_or_else(std::sync::PoisonError::into_inner)
            .clients.iter().filter(|(_, client)| client.ready.load(Ordering::Acquire)
                && client.requests.as_ref().is_some_and(|sender| !sender.is_closed()))
            .map(|(id, _)| id.clone()).collect();
        RemoteAssemblyEnvironment { profiles, ready_profile_ids }
    }

    /// The one answer the rail paints, read from the live client each time so a
    /// stale actor's report cannot contradict it.
    fn connection_state(&self, profile_id: &str) -> RemoteConnectionState {
        if self
            .connecting
            .lock()
            .unwrap_or_else(std::sync::PoisonError::into_inner)
            .contains_key(profile_id)
        {
            return RemoteConnectionState::Reconnecting;
        }
        let state = self.client.lock().unwrap_or_else(std::sync::PoisonError::into_inner);
        match state.clients.get(profile_id) {
            Some(client)
                if client.requests.as_ref().is_some_and(|sender| !sender.is_closed()) =>
            {
                if client.ready.load(Ordering::Acquire) {
                    RemoteConnectionState::Connected
                } else {
                    RemoteConnectionState::Reconnecting
                }
            }
            _ => RemoteConnectionState::Disconnected,
        }
    }

    fn publish_status(&self, profile_id: &str) {
        let state = self.connection_state(profile_id);
        if state == RemoteConnectionState::Connected {
            let (manager, machine) = (self.clone(), profile_id.to_string());
            tauri::async_runtime::spawn(async move { manager.restore_orchestrator_choice(&machine).await });
        }
        (self.status_sink)(RemoteConnectionStatus { profile_id: profile_id.to_string(), state });
    }

    pub fn shutdown(&self) {
        for (_, attempt) in self.attempts.lock().unwrap_or_else(std::sync::PoisonError::into_inner).drain() {
            attempt.abort();
        }
        self.client
            .lock()
            .unwrap_or_else(std::sync::PoisonError::into_inner)
            .clients
            .clear();
        let mut tunnels = self
            .tunnel_processes
            .lock()
            .unwrap_or_else(std::sync::PoisonError::into_inner);
        for (_, mut child) in tunnels.drain() {
            let _ = child.kill();
            let _ = child.wait();
        }
    }

    pub fn restore_profiles(&self, profiles_json: &str) -> Result<(), String> {
        let profiles: Vec<RemoteAssemblyProfile> = serde_json::from_str(profiles_json)
            .map_err(|error| format!("Invalid Remote Assembly profiles: {error}"))?;
        for profile in profiles {
            self.register_profile(profile)?;
        }
        Ok(())
    }

    pub fn migrate_legacy_profile(
        &self,
        profile_json: &str,
    ) -> Result<Vec<RemoteAssemblyProfile>, String> {
        let legacy: LegacyRemoteAssemblyProfile = serde_json::from_str(profile_json)
            .map_err(|error| format!("Invalid legacy Remote Assembly profile: {error}"))?;
        let profile = RemoteAssemblyProfile {
            id: "agent-workbox".to_string(),
            name: "Agent Workbox".to_string(),
            ssh_target: legacy.ssh_target,
            source_root: legacy.source_root,
            default_cwd: legacy.default_cwd,
        };
        self.register_profile(profile.clone())?;
        Ok(vec![profile])
    }

    fn register_profile(&self, profile: RemoteAssemblyProfile) -> Result<(), String> {
        validate_profile(&profile)?;
        self.client.lock().unwrap_or_else(std::sync::PoisonError::into_inner).clients.insert(
            profile.id.clone(), RemoteClient {
                requests: None, profile: Some(profile), target_key: None, task: None,
                ready: Arc::new(AtomicBool::new(false)),
            });
        Ok(())
    }

    pub fn cancel_connection(&self, operation_id: &str) {
        if let Some(task) = self.attempts.lock().unwrap_or_else(std::sync::PoisonError::into_inner).get(operation_id) {
            task.abort();
        }
    }

    pub async fn install_and_connect_profile(&self, profile: RemoteAssemblyProfile, operation_id: String,
        status: tauri::ipc::Channel<String>) -> Result<RemoteConnectionResult, String> {
        validate_profile(&profile)?;
        let manager = self.clone();
        let progress = status.clone();
        let mut task = {
            let mut attempts = self.attempts.lock().unwrap_or_else(std::sync::PoisonError::into_inner);
            if attempts.contains_key(&operation_id) { return Err("Connection attempt already exists".into()); }
            let task = tokio::spawn(async move {
                let _owner = manager.connection_lock.lock().await;
                let (profile, _, _) = manager.resolve_profile_identity(profile).await?;
                manager.stop_profile(&profile.id);
                let receipt = match self::install::install_latest(&profile.ssh_target, progress.clone()).await {
                    Ok(receipt) => receipt,
                    Err(error) if self::install_flow::keep_dev_install_profile(&error, cfg!(debug_assertions)) => {
                        manager.register_profile(profile)?;
                        return Err(format!("{error} Machine saved. Use Deploy from this checkout under Saved machines, then Connect."));
                    }
                    Err(error) => return Err(error),
                };
                let _ = progress.send(format!("Installed backend {} from {}. Connecting…",
                    receipt.version, &receipt.commit[..12]));
                manager.connect_profile_inner_locked(profile, progress).await
            });
            attempts.insert(operation_id.clone(), task.abort_handle());
            task
        };
        let _ = status.send("Preparing installation…".into());
        let result = match tokio::time::timeout(Duration::from_secs(900), &mut task).await {
            Ok(Ok(result)) => result,
            Ok(Err(_)) => Err("Installation cancelled".into()),
            Err(_) => {
                task.abort();
                let _ = task.await;
                Err("Installation timed out after 15 minutes".into())
            }
        };
        self.attempts.lock().unwrap_or_else(std::sync::PoisonError::into_inner).remove(&operation_id);
        result
    }

    pub async fn uninstall_profile(
        &self,
        profile: RemoteAssemblyProfile,
        delete_data: bool,
    ) -> Result<Option<String>, String> {
        validate_profile(&profile)?;
        let _owner = self.connection_lock.lock().await;
        let (profile, _, replaced_profile_id) = self.resolve_profile_identity(profile).await?;
        self.stop_profile(&profile.id);
        self::install::uninstall(&profile.ssh_target, delete_data).await?;
        if delete_data {
            self.downloads.lock().unwrap().retain(|_, entry| entry.progress.borrow().profile_id != profile.id);
            self.history.lock().unwrap_or_else(std::sync::PoisonError::into_inner).purge(&profile.id)?;
            self.replace_cached_profile_sessions(&profile.id, &[])?;
        } else {
            self.history.lock().unwrap_or_else(std::sync::PoisonError::into_inner).disconnect(&profile.id);
        }
        Ok(replaced_profile_id)
    }

    pub async fn connect_profile(&self, profile: RemoteAssemblyProfile, operation_id: String,
        status: tauri::ipc::Channel<String>) -> Result<RemoteConnectionResult, String> {
        validate_profile(&profile)?;
        let manager = self.clone();
        let progress = status.clone();
        let mut task = {
            let mut attempts = self.attempts.lock().unwrap_or_else(std::sync::PoisonError::into_inner);
            if attempts.contains_key(&operation_id) { return Err("Connection attempt already exists".into()); }
            let task = tokio::spawn(async move { manager.connect_profile_inner(profile, progress).await });
            attempts.insert(operation_id.clone(), task.abort_handle());
            task
        };
        // The UI enables cancellation only after this registration acknowledgment.
        let _ = status.send("Connecting…".into());
        let result = match tokio::time::timeout(Duration::from_secs(30), &mut task).await {
            Ok(Ok(result)) => result,
            Ok(Err(_)) => Err("Connection cancelled".into()),
            Err(_) => {
                task.abort();
                let _ = task.await;
                Err("Connection timed out after 30 seconds".into())
            }
        };
        self.attempts.lock().unwrap_or_else(std::sync::PoisonError::into_inner).remove(&operation_id);
        result
    }

    async fn connect_profile_inner(&self, profile: RemoteAssemblyProfile,
        status: tauri::ipc::Channel<String>) -> Result<RemoteConnectionResult, String> {
        let _owner = self.connection_lock.lock().await;
        self.connect_profile_inner_locked(profile, status).await
    }

    async fn connect_profile_inner_locked(&self, profile: RemoteAssemblyProfile,
        status: tauri::ipc::Channel<String>) -> Result<RemoteConnectionResult, String> {
        let _ = status.send("Resolving SSH configuration…".into());
        let (profile, target_key, replaced_profile_id) = self.resolve_profile_identity(profile).await?;
        let attempt = ConnectingAttempt::start(self, &profile.id);
        let reusable = self.client.lock().unwrap_or_else(std::sync::PoisonError::into_inner)
            .clients.get(&profile.id).is_some_and(|client| client.target_key.as_ref() == Some(&target_key)
                && client.requests.as_ref().is_some_and(|sender| !sender.is_closed()));
        let (mut sessions, candidate) = if reusable {
            let id = self.next_request_id.fetch_add(1, Ordering::Relaxed);
            let RemoteResponse::Sessions(sessions) = self.request_with_id(&profile.id, id, RemoteCommand::ListSessions, None).await?
                else { return Err("Backend returned an invalid session list".into()); };
            (sessions, None)
        } else {
            let _ = status.send("Checking the installed backend…".into());
            let (requests, receiver) = mpsc::channel(32);
            let (ready_tx, ready_rx) = oneshot::channel();
            let ready = Arc::new(AtomicBool::new(false));
            let manager = self.clone();
            let reported_id = profile.id.clone();
            let report: Arc<dyn Fn() + Send + Sync> =
                Arc::new(move || manager.publish_status(&reported_id));
            let task = tokio::spawn(profile_client_loop(profile.clone(), receiver, self.history_event_sink(&profile.id, true),
                self.terminal_sink(&profile.id), self.tunnel_processes.clone(), ready.clone(), Some(ready_tx), report,
                self.message_relay(&profile.id), self.clone()));
            // Dropping a failed/cancelled candidate aborts only its own actor and tunnel.
            let candidate = RemoteClient { requests: Some(requests), profile: Some(profile.clone()),
                target_key: Some(target_key), task: Some(task.abort_handle()), ready };
            let sessions = ready_rx.await.map_err(|_| "Backend connection ended before readiness".to_string())??;
            (sessions, Some(candidate))
        };
        for session in &mut sessions {
            session.remote_profile_id = Some(profile.id.clone());
        }
        self.replace_cached_profile_sessions(&profile.id, &sessions)?;
        // A removal while this attempt was in flight wins: the profile neither
        // gets its client back nor any report or session from this attempt.
        self.adopt_client(&attempt, candidate)?;
        sessions.retain(|session| self.remember(session.owned_id.clone(), profile.id.clone()));
        if let Some(client) = self.client.lock().unwrap_or_else(std::sync::PoisonError::into_inner).clients.get_mut(&profile.id) {
            client.profile = Some(profile.clone());
        }
        // Grouped after caching, so the cache holds the server's records as sent.
        super::attach_project_groups(&self.store, &mut sessions)?;
        Ok(RemoteConnectionResult { profile, sessions, replaced_profile_id })
    }

    async fn resolve_profile_identity(&self, mut profile: RemoteAssemblyProfile)
        -> Result<(RemoteAssemblyProfile, String, Option<String>), String> {
        let target_key = resolve_ssh_target(&profile.ssh_target).await?;
        let mut replaced_profile_id = None;
        let saved = self.environment().profiles;
        for existing in saved {
            let key = if existing.ssh_target == profile.ssh_target { Some(target_key.clone()) }
                else { resolve_ssh_target(&existing.ssh_target).await.ok() };
            if key.as_ref() != Some(&target_key) { continue; }
            // Alias and address forms of the same host keep the original profile/session IDs.
            if existing.id != profile.id { profile = existing; }
            break;
        }
        if profile.id != "agent-workbox" {
            if let Some(json) = self.store.get_app_setting(REMOTE_ASSEMBLY_PROFILE_SETTING_KEY)
                .map_err(|error| error.to_string())?
            {
                if let Ok(legacy) = serde_json::from_str::<LegacyRemoteAssemblyProfile>(&json) {
                    let legacy_key = if legacy.ssh_target == profile.ssh_target {
                        Some(target_key.clone())
                    } else {
                        resolve_ssh_target(&legacy.ssh_target).await.ok()
                    };
                    if legacy_key.as_ref() == Some(&target_key) {
                        self.reassign_cached_profile_sessions("agent-workbox", &profile.id)?;
                        replaced_profile_id = Some("agent-workbox".to_string());
                    }
                }
            }
        }
        Ok((profile, target_key, replaced_profile_id))
    }

    fn stop_profile(&self, profile_id: &str) {
        {
            let mut state = self.client.lock().unwrap_or_else(std::sync::PoisonError::into_inner);
            if let Some(client) = state.clients.get_mut(profile_id) {
                if let Some(task) = client.task.take() {
                    eprintln!("Remote client for {profile_id} was stopped");
                    task.abort();
                }
                client.requests = None;
                client.target_key = None;
                client.ready.store(false, Ordering::Release);
            }
        }
        self.publish_status(profile_id);
    }

    pub fn disconnect_profile(&self, profile_id: &str) -> Result<(), String> {
        self.stop_profile(profile_id);
        self.history.lock().unwrap_or_else(std::sync::PoisonError::into_inner).disconnect(profile_id);
        self.pause_history_downloads(profile_id);
        Ok(())
    }

    fn saved_cursors(&self, profile: &str) -> Result<BTreeMap<String, i64>, String> {
        self.store.remote_saved_maxima(profile).map_err(|e| e.to_string())
    }

    /// Returns true when the caller must fetch the newest page after a replacement.
    fn complete_replay(&self, profile: &str, snapshot: AgentConversationSnapshot,
        newest: Option<AgentConversationEvent>, saved: Option<i64>) -> Result<bool, String> {
        if self.history.lock().unwrap_or_else(std::sync::PoisonError::into_inner)
            .forget_if_shrunk(profile, &snapshot.connection.owned_id, snapshot.last_sequence, saved)? {
            return Ok(true);
        }
        let newest = match newest {
            Some(event) => Some(event),
            None => self.store.newest_remote_event(&RemoteHistory::key(profile, &snapshot.connection.owned_id))
                .map_err(|e| e.to_string())?
                .map(|row| serde_json::from_str::<AgentConversationEvent>(&row.payload_json))
                .transpose().map_err(|e| e.to_string())?,
        };
        self.save_replay_snapshot(profile, snapshot, newest)?;
        Ok(false)
    }

    fn save_replay_snapshot(&self, profile: &str, mut snapshot: AgentConversationSnapshot,
        newest: Option<AgentConversationEvent>) -> Result<(), String> {
        {
            let history = self.history.lock().unwrap_or_else(std::sync::PoisonError::into_inner);
            history.snapshot(profile, history.epoch(profile), None, &mut snapshot)?;
        }

        if let Some(event) = newest { self.history_event_sink(profile, true)(vec![event])?; }
        Ok(())
    }

    fn history_event_sink(&self, profile_id: &str, publish: bool) -> RemoteEventSink {
        let history = self.history.clone();
        let profile = profile_id.to_string();
        let epoch = history.lock().unwrap_or_else(std::sync::PoisonError::into_inner).epoch(&profile);
        let sink = self.event_sink.clone();
        Arc::new(move |mut events| {
            let started = Instant::now();
            {
                let history = history.lock().unwrap_or_else(std::sync::PoisonError::into_inner);
                // A replaced connection must stop; a failed cache write must not.
                // The next pushed event then skips past the missed row, which asks for its replay.
                history.check(&profile, epoch)?;
                let mut by_session = BTreeMap::<String, Vec<usize>>::new();
                for (index, event) in events.iter().enumerate() {
                    by_session.entry(event.owned_id.clone()).or_default().push(index);
                }
                for (owned_id, indices) in by_session {
                    let mut batch: Vec<_> = indices.iter().map(|&index| events[index].clone()).collect();
                    if let Err(error) = history.live_batch(&profile, epoch, &mut batch) {
                        eprintln!("Remote history cache write failed for {owned_id}: {error}");
                    }
                    for (index, prepared) in indices.into_iter().zip(batch) {
                        events[index] = prepared;
                    }
                }
                for event in &mut events {
                    if let Some(history_owned_id) =
                        history.delivery_owned_id(&profile, &event.owned_id)
                    {
                        event.owned_id = history_owned_id;
                    }
                }
            }
            let cache_ms = started.elapsed().as_millis();
            let count = events.len();
            if publish { for event in events { sink(event); } }
            if cache_ms > 16 {
                eprintln!("Remote event batch: {count} events, cache {cache_ms} ms, publish {} ms", started.elapsed().as_millis().saturating_sub(cache_ms));
            }
            Ok(())
        })
    }

    pub fn remove_profile(&self, profile_id: &str) -> Result<(), String> {
        self.downloads.lock().unwrap().retain(|_, entry| entry.progress.borrow().profile_id != profile_id);
        // Dropping the attempt token first makes removal authoritative: the
        // state derived below is disconnected even mid-attempt, and the running
        // attempt can no longer install a client for this profile. A later
        // connect starts a fresh attempt with a new token, so nothing is
        // permanently blocked.
        self.connecting
            .lock()
            .unwrap_or_else(std::sync::PoisonError::into_inner)
            .remove(profile_id);
        // Dropping the client aborts its own actor and tunnel.
        self.client
            .lock()
            .unwrap_or_else(std::sync::PoisonError::into_inner)
            .clients
            .remove(profile_id);
        self.publish_status(profile_id);
        self.history.lock().unwrap_or_else(std::sync::PoisonError::into_inner).disconnect(profile_id);
        Ok(())
    }

    /// Installs a freshly connected client only while this attempt still owns
    /// the profile, holding the attempt token while it writes so a concurrent
    /// removal cannot be overtaken. A dropped candidate aborts its own actor
    /// and tunnel.
    fn adopt_client(
        &self,
        attempt: &ConnectingAttempt,
        candidate: Option<RemoteClient>,
    ) -> Result<(), String> {
        let connecting = self
            .connecting
            .lock()
            .unwrap_or_else(std::sync::PoisonError::into_inner);
        if connecting.get(&attempt.profile_id) != Some(&attempt.token) {
            return Err(format!(
                "Remote machine {} was removed during the connection attempt",
                attempt.profile_id
            ));
        }
        if let Some(candidate) = candidate {
            self.client
                .lock()
                .unwrap_or_else(std::sync::PoisonError::into_inner)
                .clients
                .insert(attempt.profile_id.clone(), candidate);
        }
        Ok(())
    }

    pub fn owns(&self, owned_id: &str) -> bool {
        self.remote_sessions.lock().unwrap_or_else(std::sync::PoisonError::into_inner).contains_key(owned_id)
    }

    /// The key this Mac's saved copy of remote session `owned_id` is stored under.
    pub(crate) fn history_key(&self, owned_id: &str) -> Result<String, String> { Ok(RemoteHistory::key(&self.profile_for_owned_id(owned_id)?, owned_id)) }

    /// A machine may route only ids that are neither this Mac's own sessions nor routed to another machine;
    /// otherwise a hostile backend could list a local session's id and receive its prompts and approvals.
    fn may_route(&self, owned_id: &str, profile_id: &str) -> bool {
        let other_machine = self.remote_sessions.lock().unwrap_or_else(std::sync::PoisonError::into_inner).get(owned_id).is_some_and(|owner| owner != profile_id);
        let allowed = !other_machine && matches!(self.store.get_session(owned_id), Ok(None));
        if !allowed { eprintln!("Ignored remote session {owned_id} from {profile_id}: the id belongs to another session"); }
        allowed
    }

    fn remember(&self, owned_id: impl Into<String>, profile_id: impl Into<String>) -> bool {
        let (owned_id, profile_id) = (owned_id.into(), profile_id.into());
        let allowed = self.may_route(&owned_id, &profile_id);
        if allowed { self.remote_sessions.lock().unwrap_or_else(std::sync::PoisonError::into_inner).insert(owned_id, profile_id); }
        allowed
    }

    pub(crate) fn profile_for_owned_id(&self, owned_id: &str) -> Result<String, String> {
        self.remote_sessions
            .lock()
            .unwrap_or_else(std::sync::PoisonError::into_inner)
            .get(owned_id)
            .cloned()
            .ok_or_else(|| format!("No remote machine owns session {owned_id}"))
    }

    pub fn set_message_host(&self, manager: AgentRuntimeManager) {
        *self.message_host.lock().unwrap_or_else(std::sync::PoisonError::into_inner) = Some(manager);
    }

    /// Routes messaging operations from sessions on `source`: this desktop (`LOCAL_MACHINE`) or a profile.
    pub fn message_relay(&self, source: &str) -> super::messaging::Relay {
        let manager = self.clone();
        let source = source.to_string();
        Arc::new(move |op, args| {
            let manager = manager.clone();
            let source = source.clone();
            Box::pin(async move { manager.route_message(&source, &op, args).await })
        })
    }

    /// Routes messages across machines with a replyable label identifying their source.
    async fn route_message(&self, source: &str, op: &str, mut args: serde_json::Value) -> Result<serde_json::Value, String> {
        use serde_json::json;
        relay_trust::prepare(&self.store, source, op, &mut args)?;
        match op {
            "project_root" => self.store.project_root(if source == LOCAL_MACHINE { "local" } else { source },
                args["project"].as_str().ok_or("project must be a project folder or title")?)
                .map(|root| json!(root)).map_err(|error| error.to_string()),
            "directory" => {
                let mut entries = Vec::new();
                for machine in self.message_machines(source) {
                    let Ok(serde_json::Value::Array(list)) = self.message_machine(&machine, "directory", json!({"role": args["role"]}), None).await else { continue };
                    entries.extend(list.into_iter().map(|mut entry| { entry["machine"] = json!(machine); entry }));
                }
                Ok(json!({"machine": source, "entries": entries}))
            }
            "enqueue" => {
                let to = args["to"].as_str().ok_or("to must be a session name")?;
                let (name, machine) = super::messaging::split_address(to);
                let machine = match machine {
                    Some(machine) if self.message_machines("").iter().any(|known| known == machine) => machine.to_string(),
                    Some(machine) => return Err(format!("target machine not reachable: {machine}")),
                    None => {
                        let mut found = Vec::new();
                        for machine in self.message_machines(source) {
                            if self.message_machine(&machine, "resolve", json!({"to": name, "role": args["role"]}), None).await.is_ok() { found.push(machine); }
                        }
                        match found.len() {
                            0 => return Err("Unknown session name".into()),
                            1 => found.remove(0),
                            _ => return Err(format!("Ambiguous session name: {name} is on {}; add @machine", found.join(" and "))),
                        }
                    }
                };
                let sender = args["sender"].as_str().unwrap_or_default();
                let sender = if source != LOCAL_MACHINE { format!("{source}: {sender}") } else { match sender.split_once(" · ") {
                    Some((handle, label)) => format!("{handle}@{source} · {label}"),
                    None => format!("{sender}@{source}"),
                }};
                let id = self.message_machine(&machine, "enqueue",
                    json!({"to": name, "text": args["text"], "from": args["from"], "sender": sender, "role": args["role"]}), None).await?;
                Ok(json!(format!("{machine}:{}", id.as_str().unwrap_or_default())))
            }
            "status" => {
                let (machine, id) = args["id"].as_str().and_then(|id| id.split_once(':')).ok_or("Unknown message")?;
                self.message_machine(machine, "status", json!({"id": id, "from": args["from"]}), None).await
            }
            _ => Err(format!("Unknown messaging operation {op}")),
        }
    }

    /// Marks `owned_id` on `machine` as the owner's main orchestrator, then clears the
    /// mark on every other reachable machine; `None` clears it everywhere. The choice is
    /// kept on this desktop, and a machine that is away gets it when it connects.
    pub async fn set_main_orchestrator(&self, machine: &str, owned_id: Option<&str>) -> Result<(), String> {
        let _choosing = self.orchestrator_choice.lock().await;
        let away = !self.message_machines("").iter().any(|connected| connected == machine);
        if owned_id.is_some() && !away {
            self.message_machine(machine, "designate", serde_json::json!({"owned_id": owned_id}), None).await?;
        }
        if let Some(host) = self.message_host.lock().unwrap_or_else(std::sync::PoisonError::into_inner).clone() {
            host.store().upsert_app_setting(ORCHESTRATOR_CHOICE,
                &serde_json::json!({"machine": machine, "owned_id": owned_id}).to_string()).map_err(|error| error.to_string())?;
        }
        self.remember_cached_orchestrator(machine, owned_id)?;
        for other in self.message_machines(if owned_id.is_some() { machine } else { "" }) {
            if let Err(error) = self.message_machine(&other, "designate", serde_json::json!({"owned_id": null}), None).await {
                crate::debug_log::stderr_log!("Could not clear the orchestrator mark on {other}: {error}");
            }
        }
        Ok(())
    }

    /// Rows kept for machines that are away show the owner's choice too, also after a restart.
    fn remember_cached_orchestrator(&self, machine: &str, owned_id: Option<&str>) -> Result<(), String> {
        let cached = self.cached_sessions();
        let chosen = |row: &AgentConversationSessionRecord|
            row.remote_profile_id.as_deref() == Some(machine) && Some(row.owned_id.as_str()) == owned_id;
        if cached.iter().any(|row| row.orchestrator_marked_at_ms.is_some() != chosen(row)) {
            let now = chrono::Utc::now().timestamp_millis();
            self.save_cached_sessions(cached.into_iter().map(|mut row| {
                row.orchestrator_marked_at_ms = if chosen(&row) { row.orchestrator_marked_at_ms.or(Some(now)) } else { None };
                row
            }).collect())?;
        }
        Ok(())
    }

    /// Puts the owner's latest orchestrator choice on `machine`, which has just connected:
    /// its session is marked if the owner chose one there, and any older mark is cleared otherwise.
    pub(crate) async fn restore_orchestrator_choice(&self, machine: &str) {
        let _choosing = self.orchestrator_choice.lock().await;
        let Some(host) = self.message_host.lock().unwrap_or_else(std::sync::PoisonError::into_inner).clone() else { return };
        let Ok(Some(choice)) = host.store().get_app_setting(ORCHESTRATOR_CHOICE) else { return };
        let Ok(choice) = serde_json::from_str::<serde_json::Value>(&choice) else { return };
        let owned_id = if choice["machine"].as_str() == Some(machine) { choice["owned_id"].clone() } else { serde_json::Value::Null };
        if let Err(error) = self.message_machine(machine, "designate", serde_json::json!({"owned_id": owned_id}), None).await {
            crate::debug_log::stderr_log!("Could not restore the orchestrator mark on {machine}: {error}");
        }
    }

    /// Runs `work` until it ends or the desktop cancels `request_id`; a cancelled call gives `None`.
    pub(crate) async fn unless_cancelled<T>(&self, request_id: u64, work: impl std::future::Future<Output = T>) -> Option<T> {
        let mut cancellations = self.cancelled_requests.subscribe();
        tokio::select! {
            value = work => Some(value),
            _ = wait_for_cancel(&mut cancellations, request_id) => None,
        }
    }

    /// This desktop and every connected machine, except `source`.
    pub(crate) fn message_machines(&self, source: &str) -> Vec<String> {
        std::iter::once(LOCAL_MACHINE.to_string()).chain(self.environment().ready_profile_ids)
            .filter(|machine| machine != source).collect()
    }

    /// A frontend `request_id` is the wire id, so cancelling it stops the read on the server.
    pub(crate) async fn message_machine(&self, machine: &str, op: &str, args: serde_json::Value, request_id: Option<u64>) -> Result<serde_json::Value, String> {
        if machine == LOCAL_MACHINE {
            let host = self.message_host.lock().unwrap_or_else(std::sync::PoisonError::into_inner).clone()
                .ok_or("target machine not reachable: this desktop's sessions are not open yet")?;
            return super::messaging::answer(host.store(), Some(&host), op, &args);
        }
        let id = request_id.unwrap_or_else(|| self.next_request_id.fetch_add(1, Ordering::Relaxed));
        match self.request_with_id(machine, id, RemoteCommand::Messaging { op: op.into(), args }, None).await
            .map_err(|error| format!("target machine not reachable: {error}"))? {
            RemoteResponse::Messaging(value) => Ok(value),
            _ => Err("The remote backend does not relay messages; update its backend first".into()),
        }
    }

    pub fn set_messages_changed(&self, sink: Arc<dyn Fn() + Send + Sync>) {
        let _ = MESSAGES_CHANGED_SINK.set(sink);
    }

    pub fn set_terminal_output(&self, streams: ProjectionStreams) {
        *self.terminal_output.lock().unwrap_or_else(std::sync::PoisonError::into_inner) = Some(streams);
    }

    /// Shows a machine's shell output in the desktop's terminal stream under the desktop's id for it.
    fn terminal_sink(&self, profile_id: &str) -> RemoteTerminalSink {
        let streams = self.terminal_output.clone();
        let profile_id = profile_id.to_string();
        Arc::new(move |mut frame| {
            let Some(streams) = streams.lock().unwrap_or_else(std::sync::PoisonError::into_inner).clone() else {
                return;
            };
            frame.output.session_id = remote_terminal_id(&profile_id, &frame.output.session_id);
            streams.publish_terminal_output(frame.output.session_id.clone(), frame.generation, frame.sequence, frame.output);
        })
    }

    /// Starts a shell on the machine that owns `request.cwd`, a native folder there.
    /// A frontend `request_id` is the wire id, so cancelling it aborts the start on the server.
    pub async fn start_terminal(&self, profile_id: &str, request: TerminalStartRequest, request_id: Option<u64>) -> Result<TerminalSessionInfo, String> {
        let id = request_id.unwrap_or_else(|| self.next_request_id.fetch_add(1, Ordering::Relaxed));
        let RemoteResponse::TerminalSession(mut info) =
            self.request_with_id(profile_id, id, RemoteCommand::TerminalStart(request), None).await?
        else {
            return Err("The remote backend returned an incompatible terminal response".into());
        };
        info.session_id = remote_terminal_id(profile_id, &info.session_id);
        Ok(info)
    }

    pub async fn write_terminal(&self, id: &str, data: String) -> Result<bool, String> {
        terminal_bool(self.terminal_request(id, |session_id| RemoteCommand::TerminalWrite { session_id, data }).await?)
    }

    pub async fn resize_terminal(&self, id: &str, cols: Option<u16>, rows: Option<u16>) -> Result<bool, String> {
        terminal_bool(self.terminal_request(id, |session_id| RemoteCommand::TerminalResize { session_id, cols, rows }).await?)
    }

    pub async fn close_terminal(&self, id: &str) -> Result<bool, String> {
        terminal_bool(self.terminal_request(id, |session_id| RemoteCommand::TerminalClose { session_id }).await?)
    }

    pub async fn read_terminal_scrollback(&self, id: &str, max_bytes: Option<usize>) -> Result<Option<TerminalScrollbackSnapshot>, String> {
        match self.terminal_request(id, |session_id| RemoteCommand::TerminalScrollback { session_id, max_bytes }).await? {
            RemoteResponse::TerminalScrollback(snapshot) => Ok(snapshot),
            _ => Err("The remote backend returned an incompatible terminal response".into()),
        }
    }

    async fn terminal_request(
        &self,
        id: &str,
        command: impl FnOnce(String) -> RemoteCommand,
    ) -> Result<RemoteResponse, String> {
        let (profile_id, session_id) = split_remote_terminal_id(id)
            .ok_or_else(|| format!("{id} is not a remote terminal"))?;
        self.request_for_profile(profile_id, command(session_id.to_string())).await
    }

    async fn request_for_owned(
        &self,
        owned_id: &str,
        command: RemoteCommand,
    ) -> Result<RemoteResponse, String> {
        let profile_id = self.profile_for_owned_id(owned_id)?;
        self.request_for_profile(&profile_id, command).await
    }

    async fn request_for_profile(
        &self,
        profile_id: &str,
        command: RemoteCommand,
    ) -> Result<RemoteResponse, String> {
        let id = self.next_request_id.fetch_add(1, Ordering::Relaxed);
        self.request_with_id(profile_id, id, command, None).await
    }

    /// Runs one workflow operation on the remote machine's own engine.
    /// A frontend `request_id` is the wire id, so cancelling it stops the operation on the server.
    pub async fn workflow_operation(&self, profile_id: &str, operation: String, args: serde_json::Value, request_id: Option<u64>) -> Result<serde_json::Value, String> {
        let id = request_id.unwrap_or_else(|| self.next_request_id.fetch_add(1, Ordering::Relaxed));
        match self.request_with_id(profile_id, id, RemoteCommand::Workflow { operation, args }, None).await? {
            RemoteResponse::Workflow(value) => Ok(value),
            _ => Err("The remote backend does not run workflows; update its backend first".into()),
        }
    }

    /// Runs one file or Git operation on the remote machine that owns the workspace.
    pub async fn workspace_operation(
        &self,
        profile_id: &str,
        operation: String,
        args: serde_json::Value,
        request_id: Option<u64>,
    ) -> Result<serde_json::Value, String> {
        // A frontend id is the wire id, so cancelling it aborts the server task.
        let id = request_id.unwrap_or_else(|| self.next_request_id.fetch_add(1, Ordering::Relaxed));
        match self.request_with_id(profile_id, id, RemoteCommand::Workspace { operation, args }, None).await? {
            RemoteResponse::Workspace(value) => Ok(value),
            _ => Err("The remote backend returned an incompatible workspace response".into()),
        }
    }

    async fn request_with_id(
        &self,
        profile_id: &str,
        id: u64,
        command: RemoteCommand,
        progress: Option<mpsc::UnboundedSender<super::providers::updates::ProviderDownloadProgress>>,
    ) -> Result<RemoteResponse, String> {
        let sender = self
            .client
            .lock()
            .unwrap_or_else(std::sync::PoisonError::into_inner)
            .clients
            .get(profile_id)
            .and_then(|client| client.requests.as_ref())
            .cloned()
            .ok_or_else(|| format!("Remote machine {profile_id} is not connected"))?;
        // Metadata can take two 30-second reads. Installation additionally streams
        // seven payloads, each bounded to 180 seconds, then verifies them on disk.
        let timeout_seconds = match &command {
            RemoteCommand::CheckProviderUpdates => 75,
            RemoteCommand::InstallProviderUpdates { .. } => 25 * 60,
            RemoteCommand::Workspace { operation, .. } => workspace_wait_seconds(operation),
            // Starting a run creates its worktree and starts the first agent.
            RemoteCommand::Workflow { .. } => 125,
            _ => 15,
        };
        let (reply, answer) = oneshot::channel();
        sender
            .try_send(ClientRequest::Execute { id, command, reply, progress })
            .map_err(|error| {
                format!("The Remote Assembly request queue is unavailable: {error}")
            })?;
        match tokio::time::timeout(Duration::from_secs(timeout_seconds), answer).await {
            Ok(answer) => answer.map_err(|_| {
                eprintln!("Remote request {id} for {profile_id} had its reply channel closed before a response");
                "The Remote Assembly connection closed before answering".to_string()
            })?,
            Err(_) => {
                let _ = sender.try_send(ClientRequest::Cancel { id });
                Err(format!("The remote machine did not answer within {timeout_seconds} seconds"))
            }
        }
    }

    pub async fn list_sessions(
        &self,
        request_id: u64,
    ) -> Result<Vec<AgentConversationSessionRecord>, String> {
        let profile_ids = self
            .client
            .lock()
            .unwrap_or_else(std::sync::PoisonError::into_inner)
            .clients
            .iter()
            .filter(|(_, client)| client.requests.as_ref().is_some_and(|sender| !sender.is_closed()))
            .map(|(id, _)| id.clone())
            .collect::<Vec<_>>();
        let responses = join_all(profile_ids.into_iter().map(|profile_id| async move {
            let response = self
                .request_with_id(&profile_id, request_id, RemoteCommand::ListSessions, None)
                .await;
            (profile_id, response)
        }))
        .await;
        let mut all_sessions = Vec::new();
        let mut successful_profiles = 0;
        let mut first_error = None;
        for (profile_id, response) in responses {
            let mut sessions = match response {
                Ok(RemoteResponse::Sessions(sessions)) => {
                    successful_profiles += 1;
                    sessions
                }
                Ok(_) => {
                    first_error.get_or_insert_with(|| {
                        "Remote Assembly returned the wrong list response".to_string()
                    });
                    continue;
                }
                Err(error) => {
                    first_error.get_or_insert(error);
                    continue;
                }
            };
            for session in &mut sessions {
                session.remote_profile_id = Some(profile_id.clone());
            }
            self.replace_cached_profile_sessions(&profile_id, &sessions)?;
            sessions.retain(|session| self.remember(session.owned_id.clone(), profile_id.clone()));
            all_sessions.extend(sessions);
        }
        if successful_profiles > 0 || first_error.is_none() {
            Ok(all_sessions)
        } else {
            Err(first_error.unwrap_or_else(|| "No remote machines are connected".to_string()))
        }
    }

    pub async fn ensure(
        &self,
        request: EnsureAgentConversationRequest,
    ) -> Result<AgentConversationConnection, String> {
        let owned_id = request.owned_id.clone();
        let profile_id = request
            .remote_profile_id
            .clone()
            .or_else(|| {
                self.remote_sessions
                    .lock()
                    .unwrap_or_else(std::sync::PoisonError::into_inner)
                    .get(&owned_id)
                    .cloned()
            })
            .ok_or_else(|| "Choose a remote machine for this session".to_string())?;
        let RemoteResponse::Connection(connection) = self
            .request_for_profile(&profile_id, RemoteCommand::Ensure(request))
            .await?
        else {
            return Err("Remote Assembly returned the wrong ensure response".to_string());
        };
        let RemoteResponse::Sessions(mut sessions) = self
            .request_for_profile(&profile_id, RemoteCommand::ListSessions)
            .await?
        else {
            return Err("Remote Assembly returned the wrong list response".to_string());
        };
        for session in &mut sessions {
            session.remote_profile_id = Some(profile_id.clone());
        }
        if !sessions.iter().any(|session| session.owned_id == owned_id) {
            return Err(format!("Ensured session {owned_id} was not stored"));
        }
        self.replace_cached_profile_sessions(&profile_id, &sessions)?;
        self.remember(owned_id, profile_id);
        Ok(connection)
    }

    /// Opens from the Mac's saved copy at once. Only a session never opened on
    /// this Mac waits for the server. Either way the background copy follows.
    #[cfg(test)]
    pub async fn snapshot(
        &self, owned_id: String, request_id: u64,
    ) -> Result<Option<AgentConversationSnapshot>, String> {
        let profile = self.profile_for_owned_id(&owned_id)?;
        let saved = self.history.lock().unwrap_or_else(std::sync::PoisonError::into_inner)
            .cached_snapshot(&profile, &owned_id)?;
        let mut snapshot = match saved {
            Some(snapshot) => snapshot,
            None => {
                if !self.save_newer(&profile, &owned_id, request_id, false, None).await? { return Ok(None); }
                let history = self.history.lock().unwrap_or_else(std::sync::PoisonError::into_inner);
                history.read_snapshot(&profile, history.epoch(&profile), &owned_id)?
            }
        };
        if self.connection_state(&profile) != RemoteConnectionState::Connected {
            snapshot.connection.state = super::protocol::ConversationConnectionState::Disconnected;
        }
        Ok(Some(snapshot))
    }

    pub async fn selection_snapshot(
        &self,
        owned_id: String,
        request_id: u64,
        max_bytes: u32,
        minimum_generation: Option<u64>,
    ) -> Result<Option<AgentConversationSelectionSnapshot>, String> {
        let mut cancellations = self.cancelled_requests.subscribe();
        let profile = self.profile_for_owned_id(&owned_id)?;
        if self.connection_state(&profile) == RemoteConnectionState::Connected {
            self.ensure_history_download(&profile, &owned_id).await?;
            self.wait_history_download_ready(&profile, &owned_id, request_id, &mut cancellations).await?;
        }
        let mut saved = self
            .history
            .lock()
            .unwrap_or_else(std::sync::PoisonError::into_inner)
            .read_selection_snapshot(&profile, &owned_id, max_bytes)?;
        if saved.as_ref().is_some_and(|snapshot| {
            minimum_generation.is_some_and(|minimum| snapshot.connection.generation < minimum)
        }) && self.connection_state(&profile) == RemoteConnectionState::Connected
        {
            if !self
                .save_newer(&profile, &owned_id, request_id, false, minimum_generation)
                .await?
            {
                return Ok(None);
            }
            saved = self
                .history
                .lock()
                .unwrap_or_else(std::sync::PoisonError::into_inner)
                .read_selection_snapshot(&profile, &owned_id, max_bytes)?;
        }
        let mut snapshot = match saved {
            Some(snapshot) => snapshot,
            None => {
                if !self
                    .save_newer(&profile, &owned_id, request_id, false, minimum_generation)
                    .await?
                {
                    return Ok(None);
                }
                self.history
                    .lock()
                    .unwrap_or_else(std::sync::PoisonError::into_inner)
                    .read_selection_snapshot(&profile, &owned_id, max_bytes)?
                    .ok_or("Remote history has no synchronized selection")?
            }
        };
        if self.connection_state(&profile) != RemoteConnectionState::Connected {
            snapshot.connection.state = super::protocol::ConversationConnectionState::Disconnected;
        }
        Ok(Some(snapshot))
    }

    pub async fn child_selection_snapshot(
        &self,
        profile: String,
        source_owned_id: String,
        request_id: u64,
        max_bytes: u32,
    ) -> Result<Option<AgentConversationSelectionSnapshot>, String> {
        let mut cancellations = self.cancelled_requests.subscribe();
        if self.connection_state(&profile) == RemoteConnectionState::Connected {
            self.ensure_history_download(&profile, &source_owned_id).await?;
            self.wait_history_download_ready(&profile, &source_owned_id, request_id, &mut cancellations).await?;
        }
        let mut snapshot = self
            .history
            .lock()
            .unwrap_or_else(std::sync::PoisonError::into_inner)
            .read_child_selection_snapshot(&profile, &source_owned_id, max_bytes)?;
        if self.connection_state(&profile) != RemoteConnectionState::Connected {
            if let Some(snapshot) = &mut snapshot {
                snapshot.connection.state =
                    super::protocol::ConversationConnectionState::Disconnected;
            }
        }
        Ok(snapshot)
    }

    pub async fn item_page(
        &self,
        owned_id: String,
        cursor: i64,
        max_bytes: u32,
        before: bool,
        request_id: u64,
    ) -> Result<AgentConversationItemPage, String> {
        let profile = self.profile_for_owned_id(&owned_id)?;
        let mut progress = self.download_watch(&profile, &owned_id).ok();
        let mut cancellations = self.cancelled_requests.subscribe();
        loop {
            let page = self
                .history
                .lock()
                .unwrap_or_else(std::sync::PoisonError::into_inner)
                .read_item_page(&profile, &owned_id, cursor, max_bytes, before)?;
            if let Some(page) = page {
                return Ok(page);
            }
            let progress = progress.as_mut().ok_or("This part of the conversation is not saved on this Mac yet")?;
            if let Some(error) = progress.borrow().error.clone() { return Err(error); }
            if progress.borrow().phase == "paused" && self.connection_state(&profile) == RemoteConnectionState::Disconnected {
                return Err(format!("Remote machine {profile} is not connected"));
            }
            tokio::select! {
                result = progress.changed() => { result.map_err(|e| e.to_string())?; }
                _ = wait_for_cancel(&mut cancellations, request_id) => return Err(PAGE_CANCELLED.into()),
            }
        }
    }

    pub async fn child_item_page(
        &self,
        profile: String,
        source_owned_id: String,
        cursor: i64,
        max_bytes: u32,
        before: bool,
        request_id: u64,
    ) -> Result<AgentConversationItemPage, String> {
        let mut progress = self.download_watch(&profile, &source_owned_id).ok();
        let mut cancellations = self.cancelled_requests.subscribe();
        loop {
            let page = self
                .history
                .lock()
                .unwrap_or_else(std::sync::PoisonError::into_inner)
                .read_child_item_page(
                    &profile,
                    &source_owned_id,
                    cursor,
                    max_bytes,
                    before,
                )?;
            if let Some(page) = page {
                return Ok(page);
            }
            let progress = progress.as_mut().ok_or("This part of the conversation is not saved on this Mac yet")?;
            if let Some(error) = progress.borrow().error.clone() { return Err(error); }
            if progress.borrow().phase == "paused" && self.connection_state(&profile) == RemoteConnectionState::Disconnected {
                return Err(format!("Remote machine {profile} is not connected"));
            }
            tokio::select! {
                result = progress.changed() => { result.map_err(|e| e.to_string())?; }
                _ = wait_for_cancel(&mut cancellations, request_id) => return Err(PAGE_CANCELLED.into()),
            }
        }
    }

    /// Saves every server event newer than the Mac's saved copy, one page
    /// at a time. `publish` then sends only the newest saved event: an open
    /// transcript reads the rest from the Mac's copy, and replayed turns would
    /// otherwise look live. Answers false when the server has no such session.
    async fn save_newer(
        &self,
        profile: &str,
        owned_id: &str,
        request_id: u64,
        publish: bool,
        minimum_generation: Option<u64>,
    ) -> Result<bool, String> {
        let epoch = self.history.lock().unwrap_or_else(std::sync::PoisonError::into_inner).epoch(profile);
        let mut after = self.store.remote_saved_bounds(&RemoteHistory::key(profile, owned_id)).map_err(|e| e.to_string())?.1;
        let mut newest = None;
        loop {
            let saved = self.store.latest_seq(&RemoteHistory::key(profile, owned_id)).map_err(|e| e.to_string())?;
            let RemoteResponse::Snapshot(snapshot) = self.request_with_id(profile, request_id,
                RemoteCommand::Snapshot { owned_id: owned_id.into(), request_id, after_sequence: after }, None).await?
            else { return Err("Remote Assembly returned the wrong snapshot response".into()); };
            let Some(mut snapshot) = snapshot else { return Ok(false); };
            if minimum_generation.is_some_and(|minimum| snapshot.connection.generation < minimum) {
                return Err("Remote Assembly returned stale conversation metadata".into());
            }
            let end = {
                let history = self.history.lock().unwrap_or_else(std::sync::PoisonError::into_inner);
                history.check(profile, epoch)?;
                if history.forget_if_shrunk(profile, owned_id, snapshot.last_sequence, Some(saved))? {
                    // An authoritative journal was replaced/truncated, rather than
                    // appended. Its previous cache is no longer the same history.
                    newest = None;
                    after = None;
                    continue;
                }
                history.snapshot(profile, epoch, after, &mut snapshot)?
            };

            let head = snapshot.last_sequence;
            newest = snapshot.events.pop().or(newest);
            if end >= head {
                if let Some(mut event) = newest.filter(|_| publish) {
                    if let Some(history_owned_id) = self
                        .history
                        .lock()
                        .unwrap_or_else(std::sync::PoisonError::into_inner)
                        .delivery_owned_id(profile, &event.owned_id)
                    {
                        event.owned_id = history_owned_id;
                    }
                    (self.event_sink)(event);
                }
                return Ok(true);
            }
            if after == Some(end) { return Err("Remote history catch-up made no progress".into()); }
            after = Some(end);
        }
    }

    /// Copies the rest of one session into the Mac's history in the background:
    /// newer events first, then older pages back to its start. Pages go to
    /// SQLite one at a time; only newer ones reach the open transcript.
    pub fn set_history_download_sink(&self, sink: Arc<dyn Fn(HistoryDownloadProgress) + Send + Sync>) {
        *self.download_sink.lock().unwrap() = Some(sink);
    }

    fn publish_download(&self, progress: HistoryDownloadProgress) {
        if let Some(sink) = self.download_sink.lock().unwrap().as_ref() { sink(progress); }
    }

    fn download_watch(&self, profile: &str, owned: &str) -> Result<tokio::sync::watch::Receiver<HistoryDownloadProgress>, String> {
        self.downloads.lock().unwrap().get(&RemoteHistory::key(profile, owned)).map(|download| download.progress.subscribe())
            .ok_or_else(|| "This part of the conversation is not saved on this Mac yet".into())
    }

    fn create_download(&self, profile: &str, owned: &str, id: u64) -> Result<HistoryDownload, String> {
        let key = RemoteHistory::key(profile, owned);
        let child = self.store.private_remote_child_source(&key).map_err(|e| e.to_string())?;
        let workspace_owned_id = match &child {
            Some(child) => serde_json::from_str::<(String, String)>(&child.parent_owned_id).map_err(|e| e.to_string())?.1,
            None => owned.into(),
        };
        let child_session_id = if child.is_some() { Some(serde_json::from_str::<(String, String, String)>(owned).map_err(|e| e.to_string())?.2) } else { None };
        let history = self.history.lock().unwrap();
        let saved = history.download_state(profile, owned)?.unwrap_or_default();
        let progress = HistoryDownloadProgress { profile_id: profile.into(), source_owned_id: owned.into(), workspace_owned_id,
            child_session_id, total_bytes: saved.total_bytes, covered_bytes: saved.covered_bytes,
            phase: "downloading".into(), ready: false, error: None };
        Ok(HistoryDownload { request_id: Some(id), epoch: history.epoch(profile), low: None, high: None, row_count: 0,
            progress: tokio::sync::watch::channel(progress).0 })
    }

    async fn ensure_history_download(&self, profile: &str, owned_id: &str) -> Result<(), String> {
        if self.history.lock().unwrap().download_state(profile, owned_id)?.is_some_and(|state| state.complete) { return Ok(()); }
        let key = RemoteHistory::key(profile, owned_id);
        let mut downloads = self.downloads.lock().unwrap();
        if let Some(entry) = downloads.get(&key).filter(|entry| entry.request_id.is_some()) {
            let progress = entry.progress.borrow().clone(); drop(downloads); self.publish_download(progress); return Ok(());
        }
        let sender = self.client.lock().unwrap().clients.get(profile).and_then(|client| client.requests.clone())
            .ok_or_else(|| format!("Remote machine {profile} is not connected"))?;
        self.history.lock().unwrap().prepare_download(profile, owned_id)?;
        let before_sequence = self.store.remote_saved_bounds(&key).map_err(|e| e.to_string())?.0;
        let id = self.next_request_id.fetch_add(1, Ordering::Relaxed);
        let download = self.create_download(profile, owned_id, id)?;
        let progress = download.progress.borrow().clone();
        // Keep existing receivers across reconnects.
        if let Some(previous) = downloads.get_mut(&key) {
            previous.request_id = Some(id); previous.epoch = download.epoch; previous.low = None; previous.high = None; previous.row_count = 0;
            previous.progress.send_replace(progress.clone());
        } else { downloads.insert(key.clone(), download); }
        if let Err(error) = sender.try_send(ClientRequest::Download { id, command: RemoteCommand::DownloadHistory { owned_id: owned_id.into(), before_sequence } }) {
            let message = error.to_string();
            let entry = downloads.get_mut(&key).unwrap();
            entry.request_id = None;
            entry.progress.send_modify(|progress| { progress.phase = "error".into(); progress.error = Some(message.clone()); });
            let progress = entry.progress.borrow().clone();
            drop(downloads);
            self.publish_download(progress);
            return Err(message);
        }
        drop(downloads);
        self.publish_download(progress);
        Ok(())
    }

    async fn wait_history_download_ready(&self, profile: &str, owned: &str, request_id: u64,
        cancellations: &mut broadcast::Receiver<u64>) -> Result<(), String> {
        if self.history.lock().unwrap().download_state(profile, owned)?.is_some_and(|state| state.complete) { return Ok(()); }
        let mut progress = self.download_watch(profile, owned)?;
        loop {
            let state = progress.borrow_and_update().clone();
            if state.phase == "error" { return Err(state.error.unwrap_or_else(|| "History download failed".into())); }
            if state.ready { return Ok(()); }
            if state.phase == "paused" && self.connection_state(profile) == RemoteConnectionState::Disconnected {
                return Err(format!("Remote machine {profile} is not connected"));
            }
            tokio::select! {
                result = progress.changed() => { result.map_err(|e| e.to_string())?; }
                _ = wait_for_cancel(cancellations, request_id) => return Err(PAGE_CANCELLED.into()),
            }
        }
    }

    fn receive_history_start(&self, profile: &str, id: u64, owned: &str, low: Option<i64>, high: Option<i64>,
        row_count: u64, total_bytes: u64, covered_bytes: u64, mut snapshot: AgentConversationSnapshot) -> Result<(), String> {
        let mut downloads = self.downloads.lock().unwrap();
        let Some(entry) = downloads.get_mut(&RemoteHistory::key(profile, owned)).filter(|entry| entry.request_id == Some(id)) else { return Ok(()); };
        self.history.lock().unwrap().begin_download(profile, entry.epoch, HistoryDownloadFacts { low_sequence: low, high_sequence: high, row_count, total_bytes, covered_bytes }, &mut snapshot)?;
        entry.low = low; entry.high = high; entry.row_count = row_count;
        entry.progress.send_modify(|progress| { progress.total_bytes = total_bytes; progress.covered_bytes = covered_bytes;
            progress.ready = covered_bytes >= total_bytes / 5 + u64::from(total_bytes % 5 != 0); });
        let progress = entry.progress.borrow().clone(); drop(downloads); self.publish_download(progress); Ok(())
    }

    fn receive_history_chunk(&self, profile: &str, id: u64, owned: &str, covered_bytes: u64, gzip_base64: String) -> Result<(), String> {
        let mut downloads = self.downloads.lock().unwrap();
        let Some(entry) = downloads.get_mut(&RemoteHistory::key(profile, owned)).filter(|entry| entry.request_id == Some(id)) else { return Ok(()); };
        let mut events = decode_history_chunk(&gzip_base64)?;
        self.history.lock().unwrap().download_chunk(profile, entry.epoch, owned, covered_bytes, &mut events)?;
        entry.progress.send_modify(|progress| { progress.covered_bytes = covered_bytes;
            progress.ready = covered_bytes >= progress.total_bytes / 5 + u64::from(progress.total_bytes % 5 != 0); });
        let progress = entry.progress.borrow().clone(); drop(downloads); self.publish_download(progress); Ok(())
    }

    async fn finish_history_download(&self, profile: &str, owned: &str, id: u64) -> Result<(), String> {
        let (low, high, row_count, epoch, covered) = {
            let downloads = self.downloads.lock().unwrap();
            let Some(entry) = downloads.get(&RemoteHistory::key(profile, owned)).filter(|entry| entry.request_id == Some(id)) else { return Ok(()); };
            let covered = entry.progress.borrow().covered_bytes;
            (entry.low, entry.high, entry.row_count, entry.epoch, covered)
        };
        if self.history.lock().unwrap().download_state(profile, owned)?.is_some_and(|state| state.complete) { return Ok(()); }
        let saved_rows = self.store.remote_saved_row_count(&RemoteHistory::key(profile, owned), low, high).map_err(|e| e.to_string())?;
        let gaps = if saved_rows < row_count {
            self.store.remote_history_gaps(&RemoteHistory::key(profile, owned), low, high).map_err(|e| e.to_string())?
        } else { Vec::new() };
        for gap in gaps {
            let mut after = gap.first_sequence.saturating_sub(1);
            while after < gap.last_sequence {
                let RemoteResponse::EventPage(mut page) = self.request_for_profile(profile, RemoteCommand::EventsAfter {
                    owned_id: owned.into(), after_sequence: after, max_bytes: HISTORY_DOWNLOAD_CHUNK_BYTES,
                }).await? else { return Err("Remote Assembly returned the wrong repair response".into()); };
                let mut downloads = self.downloads.lock().unwrap();
                let Some(entry) = downloads.get_mut(&RemoteHistory::key(profile, owned)).filter(|entry| entry.request_id == Some(id)) else { return Ok(()); };
                let Some(last) = page.events.last() else { break; };
                if last.sequence <= after { return Err("History repair made no progress".into()); }
                after = last.sequence;
                self.history.lock().unwrap().download_chunk(profile, epoch, owned, covered, &mut page.events)?;
                entry.progress.send_modify(|_| {});
            }
        }
        let mut downloads = self.downloads.lock().unwrap();
        let Some(entry) = downloads.get_mut(&RemoteHistory::key(profile, owned)).filter(|entry| entry.request_id == Some(id)) else { return Ok(()); };
        self.history.lock().unwrap().complete_download(profile, epoch, owned)?;
        entry.request_id = None;
        entry.progress.send_modify(|progress| { progress.phase = "complete".into(); progress.ready = true; progress.covered_bytes = progress.total_bytes; });
        let progress = entry.progress.borrow().clone(); drop(downloads); self.publish_download(progress); Ok(())
    }

    fn pause_history_downloads(&self, profile: &str) {
        let mut downloads = self.downloads.lock().unwrap();
        for entry in downloads.values_mut().filter(|entry| entry.progress.borrow().profile_id == profile && entry.request_id.is_some()) {
            entry.request_id = None;
            entry.progress.send_modify(|progress| { progress.phase = "paused".into(); });
            self.publish_download(entry.progress.borrow().clone());
        }
    }

    fn fail_history_download(&self, profile: &str, id: u64, error: String) {
        let mut downloads = self.downloads.lock().unwrap();
        if let Some(entry) = downloads.values_mut().find(|entry| entry.progress.borrow().profile_id == profile && entry.request_id == Some(id)) {
            entry.request_id = None;
            entry.progress.send_modify(|progress| { progress.phase = "error".into(); progress.error = Some(error); });
            self.publish_download(entry.progress.borrow().clone());
        }
    }

    pub fn history_download_progress(&self, owned_id: &str) -> Result<Option<HistoryDownloadProgress>, String> {
        let (profile, owned) = if let Some(child) = self.store.private_remote_child_source(owned_id).map_err(|e| e.to_string())? {
            (child.remote_profile_id, child.source_owned_id)
        } else if self.owns(owned_id) { (self.profile_for_owned_id(owned_id)?, owned_id.into()) } else { return Ok(None); };
        Ok(self.downloads.lock().unwrap().get(&RemoteHistory::key(&profile, &owned)).map(|entry| entry.progress.borrow().clone()))
    }

    pub async fn cancel_request(&self, request_id: u64) {
        let senders = self
            .client
            .lock()
            .unwrap_or_else(std::sync::PoisonError::into_inner)
            .clients
            .values()
            .filter_map(|client| client.requests.clone())
            .collect::<Vec<_>>();
        for sender in senders {
            let _ = sender.try_send(ClientRequest::Cancel { id: request_id });
        }
        let _ = self.cancelled_requests.send(request_id);
    }

    pub async fn events_before(&self, owned_id: String, before_sequence: i64, max_bytes: u32)
        -> Result<AgentConversationEventPage, String> {
        self.history_page(owned_id, before_sequence, max_bytes, true).await
    }

    /// Event paging reads only saved rows and waits for committed chunks.
    async fn history_page(&self, owned_id: String, cursor: i64, max_bytes: u32, before: bool)
        -> Result<AgentConversationEventPage, String> {
        let profile = self.profile_for_owned_id(&owned_id)?;
        let mut progress = self.download_watch(&profile, &owned_id).ok();
        loop {
            let page = self.history.lock().unwrap().read_page(&profile, &owned_id, cursor, max_bytes, before)?;
            if let Some(page) = page { return Ok(page); }
            let progress = progress.as_mut().ok_or("This part of the conversation is not saved on this Mac yet")?;
            if let Some(error) = progress.borrow().error.clone() { return Err(error); }
            if progress.borrow().phase == "paused" && self.connection_state(&profile) == RemoteConnectionState::Disconnected {
                return Err(format!("Remote machine {profile} is not connected"));
            }
            progress.changed().await.map_err(|e| e.to_string())?;
        }
    }

    pub async fn capabilities(
        &self,
        owned_id: String,
        request_id: u64,
    ) -> Result<AgentCapabilities, String> {
        let RemoteResponse::Capabilities(capabilities) = self
            .request_with_id(
                &self.profile_for_owned_id(&owned_id)?,
                request_id,
                RemoteCommand::Capabilities { owned_id },
                None,
            )
            .await?
        else {
            return Err("Remote Assembly returned the wrong capabilities response".to_string());
        };
        Ok(capabilities)
    }

    pub async fn config(
        &self,
        owned_id: String,
        request_id: u64,
    ) -> Result<AgentConversationConfigState, String> {
        let RemoteResponse::Config(config) = self
            .request_with_id(
                &self.profile_for_owned_id(&owned_id)?,
                request_id,
                RemoteCommand::Config { owned_id },
                None,
            )
            .await
            .map_err(|error| format!("Remote session settings request failed: {error}"))?
        else {
            return Err("Remote Assembly returned the wrong config response".to_string());
        };
        Ok(config)
    }

    pub async fn probe_provider_config(
        &self,
        profile_id: &str,
        provider: AgentConversationProvider,
        cwd: String,
        request_id: u64,
    ) -> Result<AgentConversationConfigState, String> {
        match self.request_with_id(
            profile_id,
            request_id,
            RemoteCommand::ProbeProviderConfig { provider, cwd },
            None,
        ).await? {
            RemoteResponse::Config(config) => Ok(config),
            _ => Err("Remote Assembly returned the wrong provider catalog response".into()),
        }
    }

    pub async fn warm_config(
        &self,
        owned_id: String,
        generation: u64,
    ) -> Result<AgentConversationConfigState, String> {
        let route = owned_id.clone();
        let RemoteResponse::Config(config) = self
            .request_for_owned(
                &route,
                RemoteCommand::WarmConfig {
                    owned_id,
                    generation,
                },
            )
            .await?
        else {
            return Err("Remote Assembly returned the wrong config response".to_string());
        };
        Ok(config)
    }

    pub async fn set_config(
        &self,
        request: SetAgentConversationConfigRequest,
    ) -> Result<AgentConversationConfigState, String> {
        let owned_id = request.owned_id.clone();
        let RemoteResponse::Config(config) = self
            .request_for_owned(&owned_id, RemoteCommand::SetConfig(request))
            .await?
        else {
            return Err("Remote Assembly returned the wrong config response".to_string());
        };
        Ok(config)
    }

    pub async fn set_config_option(
        &self,
        request: super::SetAgentConversationConfigOptionRequest,
    ) -> Result<Vec<AgentConfigOption>, String> {
        let owned_id = request.owned_id.clone();
        let RemoteResponse::ConfigOptions(options) = self
            .request_for_owned(&owned_id, RemoteCommand::SetConfigOption(request))
            .await?
        else {
            return Err("Remote Assembly returned the wrong config-option response".to_string());
        };
        Ok(options)
    }

    pub async fn set_draft(&self, owned_id: String, text: String) -> Result<(), String> {
        let route = owned_id.clone();
        self.empty_for_owned(&route, RemoteCommand::SetDraft { owned_id, text })
            .await
    }

    pub async fn get_draft(&self, owned_id: String) -> Result<Option<String>, String> {
        let route = owned_id.clone();
        let RemoteResponse::OptionalString(draft) = self
            .request_for_owned(&route, RemoteCommand::GetDraft { owned_id })
            .await?
        else {
            return Err("Remote Assembly returned the wrong draft response".to_string());
        };
        Ok(draft)
    }

    pub async fn clear_draft(&self, owned_id: String) -> Result<(), String> {
        let route = owned_id.clone();
        self.empty_for_owned(&route, RemoteCommand::ClearDraft { owned_id })
            .await
    }

    pub async fn write_workspace(
        &self,
        owned_id: String,
        snapshot_json: String,
    ) -> Result<(), String> {
        let route = owned_id.clone();
        self.empty_for_owned(
            &route,
            RemoteCommand::WriteWorkspace {
                owned_id,
                snapshot_json,
            },
        )
        .await
    }

    pub async fn read_workspace(&self, owned_id: String) -> Result<Option<String>, String> {
        let route = owned_id.clone();
        let RemoteResponse::OptionalString(snapshot) = self
            .request_for_owned(&route, RemoteCommand::ReadWorkspace { owned_id })
            .await?
        else {
            return Err("Remote Assembly returned the wrong workspace response".to_string());
        };
        Ok(snapshot)
    }

    pub async fn read_expanded_paths(
        &self,
        owned_id: String,
        root: String,
        request_id: u64,
    ) -> Result<Vec<String>, String> {
        let RemoteResponse::Strings(paths) = self
            .request_with_id(
                &self.profile_for_owned_id(&owned_id)?,
                request_id,
                RemoteCommand::ReadExpandedPaths { owned_id, root },
                None,
            )
            .await?
        else {
            return Err("Remote Assembly returned the wrong expanded-path response".to_string());
        };
        Ok(paths)
    }

    pub async fn write_expanded_paths(
        &self,
        owned_id: String,
        root: String,
        paths: Vec<String>,
    ) -> Result<(), String> {
        let route = owned_id.clone();
        self.empty_for_owned(
            &route,
            RemoteCommand::WriteExpandedPaths {
                owned_id,
                root,
                paths,
            },
        )
        .await
    }

    pub async fn delete_workspace(&self, owned_id: String) -> Result<(), String> {
        let route = owned_id.clone();
        self.empty_for_owned(&route, RemoteCommand::DeleteWorkspace { owned_id })
            .await
    }

    pub async fn change_checkout(
        &self,
        request: ChangeAgentConversationCheckoutRequest,
    ) -> Result<AgentConversationSessionRecord, String> {
        let owned_id = request.owned_id.clone();
        let profile_id = self.profile_for_owned_id(&owned_id)?;
        let RemoteResponse::Session(mut session) = self
            .request_for_owned(&owned_id, RemoteCommand::ChangeCheckout(request))
            .await?
        else {
            return Err("Remote Assembly returned the wrong session response".to_string());
        };
        session.remote_profile_id = Some(profile_id);
        self.cache_session(session.clone())?;
        Ok(session)
    }

    pub async fn update_meta(
        &self,
        request: UpdateAgentConversationSessionMetaRequest,
    ) -> Result<AgentConversationSessionRecord, String> {
        let owned_id = request.owned_id.clone();
        let profile_id = self.profile_for_owned_id(&owned_id)?;
        let RemoteResponse::Session(mut session) = self
            .request_for_owned(&owned_id, RemoteCommand::UpdateMeta(request))
            .await?
        else {
            return Err("Remote Assembly returned the wrong session response".to_string());
        };
        session.remote_profile_id = Some(profile_id);
        self.cache_session(session.clone())?;
        Ok(session)
    }

    pub async fn close(&self, owned_id: String, generation: u64) -> Result<bool, String> {
        let route = owned_id.clone();
        let RemoteResponse::Bool(closed) = self
            .request_for_owned(
                &route,
                RemoteCommand::Close {
                    owned_id,
                    generation,
                },
            )
            .await?
        else {
            return Err("Remote Assembly returned the wrong close response".to_string());
        };
        Ok(closed)
    }

    pub async fn delete(&self, owned_id: String) -> Result<bool, String> {
        let route = owned_id.clone();
        let RemoteResponse::Bool(deleted) = self
            .request_for_owned(
                &route,
                RemoteCommand::Delete {
                    owned_id: owned_id.clone(),
                },
            )
            .await?
        else {
            return Err("Remote Assembly returned the wrong delete response".to_string());
        };
        if deleted {
            let profile = self.profile_for_owned_id(&owned_id)?;
            let active = self.downloads.lock().unwrap().remove(&RemoteHistory::key(&profile, &owned_id))
                .and_then(|entry| entry.request_id);
            if let Some(id) = active { self.cancel_request(id).await; }
            self.history.lock().unwrap_or_else(std::sync::PoisonError::into_inner).forget(&profile, &owned_id)?;
            self.forget_cached_session(&owned_id)?;
            self.remote_sessions
                .lock()
                .unwrap_or_else(std::sync::PoisonError::into_inner)
                .remove(&owned_id);
        }
        Ok(deleted)
    }

    async fn empty_for_owned(&self, owned_id: &str, command: RemoteCommand) -> Result<(), String> {
        match self.request_for_owned(owned_id, command).await? {
            RemoteResponse::Empty => Ok(()),
            _ => Err("Remote Assembly returned the wrong command response".to_string()),
        }
    }

    pub async fn send(&self, request: SendAgentConversationMessageRequest) -> Result<AgentConversationSendReceipt, String> {
        let owned_id = request.owned_id.clone();
        match self.request_for_owned(&owned_id, RemoteCommand::Send(request)).await? {
            RemoteResponse::SendReceipt(receipt) => Ok(receipt),
            _ => Err("Remote Assembly returned the wrong command response".to_string()),
        }
    }

    pub async fn save_attachment(&self, owned_id: String, mime_type: String, bytes: Vec<u8>) -> Result<SavedConversationAttachment, String> {
        if bytes.is_empty() || bytes.len() > attachments::MAX_ATTACHMENT_BYTES {
            return Err("Image must be between 1 byte and 20 MB".into());
        }
        let upload_id = uuid::Uuid::new_v4().to_string();
        let mut saved = None;
        for (index, chunk) in bytes.chunks(ATTACHMENT_CHUNK_BYTES).enumerate() {
            let first = index == 0;
            let last = (index + 1) * ATTACHMENT_CHUNK_BYTES >= bytes.len();
            let chunk = chunk.to_vec();
            let encoded = tokio::task::spawn_blocking(move || BASE64.encode(chunk))
                .await.map_err(|error| error.to_string())?;
            let result = self.request_for_owned(&owned_id, RemoteCommand::AttachmentChunk {
                upload_id: upload_id.clone(), owned_id: owned_id.clone(), mime_type: mime_type.clone(), first, last, bytes: encoded,
            }).await;
            match result {
                Ok(RemoteResponse::Attachment(attachment)) if last => saved = Some(attachment),
                Ok(RemoteResponse::Empty) if !last => {},
                Ok(_) => {
                    let _ = self.empty_for_owned(&owned_id, RemoteCommand::AbortAttachmentUpload { upload_id }).await;
                    return Err("Remote Assembly returned the wrong attachment response".into());
                }
                Err(error) => {
                    let _ = self.empty_for_owned(&owned_id, RemoteCommand::AbortAttachmentUpload { upload_id }).await;
                    return Err(error);
                }
            }
        }
        saved.ok_or_else(|| "Remote attachment was not saved".into())
    }

    pub async fn read_attachments(&self, owned_id: String) -> Result<Vec<SavedConversationAttachment>, String> {
        let RemoteResponse::Attachments(rows) = self.request_for_owned(&owned_id, RemoteCommand::ReadAttachments { owned_id: owned_id.clone() }).await? else {
            return Err("Remote Assembly returned the wrong attachment list".into());
        };
        Ok(rows)
    }

    pub async fn read_selected_attachments(
        &self,
        owned_id: String,
        attachment_ids: Vec<String>,
    ) -> Result<Vec<SavedConversationAttachment>, String> {
        let RemoteResponse::Attachments(rows) = self
            .request_for_owned(
                &owned_id,
                RemoteCommand::ReadSelectedAttachments {
                    owned_id: owned_id.clone(),
                    attachment_ids,
                },
            )
            .await?
        else {
            return Err("Remote Assembly returned the wrong attachment response".into());
        };
        Ok(rows)
    }

    pub async fn select_child_history(
        &self,
        parent_owned_id: String,
        child_session_id: String,
        request_id: u64,
        max_bytes: u32,
    ) -> Result<AgentConversationChildHistorySelection, String> {
        let mut cancellations = self.cancelled_requests.subscribe();
        let profile = self.profile_for_owned_id(&parent_owned_id)?;
        let expected_server_child =
            super::history::child_owned_id(&parent_owned_id, &child_session_id)?;
        let expected_history_id = RemoteHistory::key(&profile, &expected_server_child);
        if self.connection_state(&profile) != RemoteConnectionState::Connected {
            let source = self
                .store
                .private_remote_child_source(&expected_history_id)
                .map_err(|error| error.to_string())?
                .ok_or_else(|| "This child transcript is not saved on this Mac yet".to_string())?;
            let page = self
                .history
                .lock()
                .unwrap_or_else(std::sync::PoisonError::into_inner)
                .read_child_selection_snapshot(
                    &source.remote_profile_id,
                    &source.source_owned_id,
                    max_bytes,
                )?
                .map(|snapshot| snapshot.page)
                .ok_or_else(|| "This child transcript is not saved on this Mac yet".to_string())?;
            return Ok(AgentConversationChildHistorySelection {
                history_owned_id: expected_history_id,
                page,
            });
        }
        let RemoteResponse::ChildHistoryId(server_child_owned_id) = self
            .request_with_id(
                &profile,
                request_id,
                RemoteCommand::SelectChildHistory {
                    parent_owned_id: parent_owned_id.clone(),
                    child_session_id: child_session_id.clone(),
                    request_id,
                },
                None,
            )
            .await?
        else {
            return Err("Remote Assembly returned the wrong child history response".into());
        };
        let history_owned_id = self
            .history
            .lock()
            .unwrap_or_else(std::sync::PoisonError::into_inner)
            .bind_child(
                &profile,
                &parent_owned_id,
                &server_child_owned_id,
                &child_session_id,
            )?;
        self.ensure_history_download(&profile, &server_child_owned_id).await?;
        self.wait_history_download_ready(&profile, &server_child_owned_id, request_id, &mut cancellations).await?;
        let page = self
            .history
            .lock()
            .unwrap_or_else(std::sync::PoisonError::into_inner)
            .read_child_selection_snapshot(&profile, &server_child_owned_id, max_bytes)?
            .map(|snapshot| snapshot.page)
            .ok_or_else(|| "Remote child history has no synchronized page".to_string())?;
        Ok(AgentConversationChildHistorySelection {
            history_owned_id,
            page,
        })
    }

    pub async fn stop_child_history(
        &self,
        parent_owned_id: String,
        request_id: u64,
    ) -> Result<bool, String> {
        let profile = self.profile_for_owned_id(&parent_owned_id)?;
        if self.connection_state(&profile) != RemoteConnectionState::Connected {
            return Ok(false);
        }
        let RemoteResponse::Bool(stopped) = self
            .request_for_profile(
                &profile,
                RemoteCommand::StopChildHistory {
                    parent_owned_id,
                    request_id,
                },
            )
            .await?
        else {
            return Err("Remote Assembly returned the wrong child stop response".into());
        };
        Ok(stopped)
    }

    pub async fn read_attachment_chunk(&self, owned_id: String, attachment_id: String, thumbnail: bool, offset: u64) -> Result<Vec<u8>, String> {
        let RemoteResponse::Bytes(bytes) = self.request_for_owned(&owned_id, RemoteCommand::ReadAttachmentChunk {
            owned_id: owned_id.clone(), attachment_id, thumbnail, offset,
        }).await? else {
            return Err("Remote Assembly returned the wrong attachment bytes".into());
        };
        tokio::task::spawn_blocking(move || BASE64.decode(bytes).map_err(|error| error.to_string()))
            .await.map_err(|error| error.to_string())?
    }

    pub async fn delete_attachment(&self, request: DeleteConversationAttachmentRequest) -> Result<(), String> {
        let owned_id = request.owned_id.clone();
        self.empty_for_owned(&owned_id, RemoteCommand::DeleteAttachment(request)).await
    }

    pub async fn respond_approval(
        &self,
        request: RespondAgentConversationApprovalRequest,
    ) -> Result<(), String> {
        let owned_id = request.owned_id.clone();
        self.empty_for_owned(&owned_id, RemoteCommand::RespondApproval(request))
            .await
    }

    pub async fn respond_permission(
        &self,
        request: RespondAgentConversationPermissionRequest,
    ) -> Result<(), String> {
        let owned_id = request.owned_id.clone();
        self.empty_for_owned(&owned_id, RemoteCommand::RespondPermission(request))
            .await
    }

    pub async fn respond_input(
        &self,
        request: RespondAgentConversationInputRequest,
    ) -> Result<(), String> {
        let owned_id = request.owned_id.clone();
        self.empty_for_owned(&owned_id, RemoteCommand::RespondInput(request))
            .await
    }

    pub async fn stop(&self, request: StopAgentConversationTurnRequest) -> Result<(), String> {
        let owned_id = request.owned_id.clone();
        self.empty_for_owned(&owned_id, RemoteCommand::Stop(request))
            .await
    }
}

#[tauri::command]
pub fn read_remote_assembly_environment(
    remote: tauri::State<'_, RemoteConnectionManager>,
) -> RemoteAssemblyEnvironment {
    remote.environment()
}

#[tauri::command]
pub async fn connect_remote_assembly(
    manager: tauri::State<'_, AgentRuntimeManager>, remote: tauri::State<'_, RemoteConnectionManager>,
    profile: RemoteAssemblyProfile, operation_id: String, status: tauri::ipc::Channel<String>,
) -> Result<RemoteConnectionResult, super::protocol::CommandError> {
    let result = remote.connect_profile(profile, operation_id, status).await
        .map_err(super::protocol::CommandError::from)?;
    let profiles_json = serde_json::to_string(&remote.environment().profiles)
        .map_err(|error| super::protocol::CommandError::from(error.to_string()))?;
    manager.write_app_setting(REMOTE_ASSEMBLY_PROFILES_SETTING_KEY, &profiles_json)
        .map_err(super::protocol::CommandError::from)?;
    Ok(result)
}

#[cfg(debug_assertions)]
fn remote_backend_dev_process(destination: &str) -> Result<(&'static str, Vec<String>), String> {
    validate_ssh_target(destination)?;
    let preview_root = PathBuf::from(env!("CARGO_MANIFEST_DIR"))
        .parent()
        .ok_or_else(|| "Could not resolve the development checkout".to_string())?
        .to_path_buf();
    let quoted_root = format!("'{}'", preview_root.display().to_string().replace('\'', "'\\''"));
    Ok((
        "/bin/zsh",
        vec![
            "-lic".into(),
            format!("cd {quoted_root} && exec node --experimental-strip-types scripts/deployRemoteBackendDev.ts \"$1\""),
            "zsh".into(),
            destination.into(),
        ],
    ))
}

#[cfg(debug_assertions)]
#[tauri::command]
pub async fn deploy_remote_backend_dev(
    destination: String,
    on_event: tauri::ipc::Channel<String>,
) -> Result<(), String> {
    let (program, args) = remote_backend_dev_process(&destination)?;
    // Own process group, so a timeout also stops the script's rsync and ssh children.
    let mut child = tokio::process::Command::new(program)
        .args(args)
        .stdin(Stdio::null())
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .process_group(0)
        .kill_on_drop(true)
        .spawn()
        .map_err(|error| format!("Could not start development backend deploy: {error}"))?;
    let process_group = child.id().map(|pid| -(pid as i32));
    let operation = async {
        let mut stdout = BufReader::new(child.stdout.take().expect("stdout is piped")).lines();
        let mut stderr = BufReader::new(child.stderr.take().expect("stderr is piped")).lines();
        let mut stdout_done = false;
        let mut stderr_done = false;
        let mut stderr_tail = VecDeque::with_capacity(20);
        while !stdout_done || !stderr_done {
            tokio::select! {
                line = stdout.next_line(), if !stdout_done => match line {
                    Ok(Some(line)) => { let _ = on_event.send(line); }
                    Ok(None) => stdout_done = true,
                    Err(error) => return Err(format!("Could not read development deploy output: {error}")),
                },
                line = stderr.next_line(), if !stderr_done => match line {
                    Ok(Some(line)) => {
                        let _ = on_event.send(line.clone());
                        if stderr_tail.len() == 20 { stderr_tail.pop_front(); }
                        stderr_tail.push_back(line);
                    }
                    Ok(None) => stderr_done = true,
                    Err(error) => return Err(format!("Could not read development deploy error output: {error}")),
                },
            }
        }
        let status = child.wait().await
            .map_err(|error| format!("Could not wait for development backend deploy: {error}"))?;
        if status.success() {
            Ok(())
        } else {
            let detail = stderr_tail.into_iter().collect::<Vec<_>>().join("\n");
            Err(if detail.is_empty() {
                format!("Development backend deploy failed with {status}")
            } else {
                format!("Development backend deploy failed:\n{detail}")
            })
        }
    };
    let outcome = tokio::time::timeout(Duration::from_secs(30 * 60), operation).await;
    if outcome.is_err() {
        if let Some(process_group) = process_group {
            // SAFETY: `process_group(0)` made the child's PID its process-group ID.
            unsafe { libc::kill(process_group, libc::SIGKILL) };
        }
    }
    outcome.map_err(|_| "Development backend deploy timed out after 30 minutes".to_string())?
}

#[tauri::command]
pub async fn read_remote_backend_statuses(
    profiles: Vec<RemoteAssemblyProfile>,
) -> Result<Vec<RemoteBackendProfileStatus>, super::protocol::CommandError> {
    for profile in &profiles {
        validate_profile(profile).map_err(super::protocol::CommandError::from)?;
    }
    let latest_version = self::install::latest_version()
        .await
        .map_err(super::protocol::CommandError::from)?;
    let statuses = join_all(profiles.into_iter().map(|profile| {
        let latest_version = latest_version.clone();
        async move {
            let backend = self::install::read_status(&profile.ssh_target, &latest_version)
                .await?;
            Ok::<_, String>(RemoteBackendProfileStatus {
                profile_id: profile.id,
                backend,
            })
        }
    }))
    .await
    .into_iter()
    .collect::<Result<Vec<_>, _>>()
    .map_err(super::protocol::CommandError::from)?;
    Ok(statuses)
}

#[tauri::command]
pub async fn uninstall_remote_assembly(
    remote: tauri::State<'_, RemoteConnectionManager>,
    profile: RemoteAssemblyProfile,
    delete_data: bool,
) -> Result<RemoteUninstallResult, super::protocol::CommandError> {
    let replaced_profile_id = remote
        .uninstall_profile(profile, delete_data)
        .await
        .map_err(super::protocol::CommandError::from)?;
    Ok(RemoteUninstallResult {
        environment: remote.environment(),
        replaced_profile_id,
    })
}

#[tauri::command]
pub fn cancel_remote_connection(remote: tauri::State<'_, RemoteConnectionManager>, operation_id: String) {
    remote.cancel_connection(&operation_id);
}

#[tauri::command]
pub fn disconnect_remote_assembly(
    remote: tauri::State<'_, RemoteConnectionManager>, profile_id: String,
) -> Result<RemoteAssemblyEnvironment, super::protocol::CommandError> {
    remote.disconnect_profile(&profile_id).map_err(super::protocol::CommandError::from)?;
    Ok(remote.environment())
}

#[tauri::command]
pub fn remove_remote_assembly_profile(
    manager: tauri::State<'_, AgentRuntimeManager>,
    remote: tauri::State<'_, RemoteConnectionManager>,
    profile_id: String,
) -> Result<RemoteAssemblyEnvironment, super::protocol::CommandError> {
    remote.remove_profile(&profile_id).map_err(super::protocol::CommandError::from)?;
    let environment = remote.environment();
    let profiles_json = serde_json::to_string(&environment.profiles)
        .map_err(|error| super::protocol::CommandError::from(error.to_string()))?;
    manager
        .write_app_setting(REMOTE_ASSEMBLY_PROFILES_SETTING_KEY, &profiles_json)
        .map_err(super::protocol::CommandError::from)?;
    Ok(environment)
}

fn validate_profile(profile: &RemoteAssemblyProfile) -> Result<(), String> {
    if profile.id.trim().is_empty()
        || !profile
            .id
            .bytes()
            .all(|value| value.is_ascii_alphanumeric() || b"_-".contains(&value))
    {
        return Err(
            "Remote machine id may contain only letters, numbers, '_', and '-'".to_string(),
        );
    }
    if profile.id == LOCAL_MACHINE {
        return Err(format!("Remote machine id {LOCAL_MACHINE} is reserved for this desktop"));
    }
    if profile.name.trim().is_empty() {
        return Err("Remote machine name is required".to_string());
    }
    validate_ssh_target(&profile.ssh_target)?;
    Ok(())
}

async fn profile_client_loop(
    profile: RemoteAssemblyProfile,
    mut requests: mpsc::Receiver<ClientRequest>,
    event_sink: RemoteEventSink,
    terminal_sink: RemoteTerminalSink,
    tunnel_processes: Arc<Mutex<HashMap<u32, Child>>>,
    ready: Arc<AtomicBool>,
    mut initial_ready: Option<oneshot::Sender<Result<Vec<AgentConversationSessionRecord>, String>>>,
    status: Arc<dyn Fn() + Send + Sync>,
    relay: super::messaging::Relay,
    history_client: RemoteConnectionManager,
) {
    while !requests.is_closed() {
        let target = profile.ssh_target.clone();
        let processes = tunnel_processes.clone();
        let tunnel = match open_remote_tunnel(&target, processes).await {
            Ok(tunnel) => tunnel,
            Err(error) => {
                if let Some(reply) = initial_ready.take() { let _ = reply.send(Err(error)); return; }
                fail_queued_requests(&mut requests, error);
                tokio::time::sleep(Duration::from_secs(1)).await;
                continue;
            }
        };
        let url = format!("ws://127.0.0.1:{}/assembly", tunnel.local_port);
        let token = tunnel.token.clone();
        client_loop(url, token, &mut requests, event_sink.clone(), Some(terminal_sink.clone()), Some(4), ready.clone(),
            &mut initial_ready, status.clone(), Some(relay.clone()), (history_client.clone(), profile.id.clone())).await;
        drop(tunnel);
    }
}

struct RemoteTunnel {
    pid: u32,
    processes: Arc<Mutex<HashMap<u32, Child>>>,
    local_port: u16,
    token: String,
}

impl Drop for RemoteTunnel {
    fn drop(&mut self) {
        stop_remote_tunnel(&self.processes, self.pid);
    }
}

fn stop_remote_tunnel(processes: &Mutex<HashMap<u32, Child>>, pid: u32) {
    let child = processes
        .lock()
        .unwrap_or_else(std::sync::PoisonError::into_inner)
        .remove(&pid);
    if let Some(mut child) = child {
        let _ = child.kill();
        let _ = child.wait();
    }
}

async fn open_remote_tunnel(
    target: &str,
    processes: Arc<Mutex<HashMap<u32, Child>>>,
) -> Result<RemoteTunnel, String> {
    let token = read_remote_token(target).await?;
    let local_port = StdTcpListener::bind("127.0.0.1:0")
        .and_then(|listener| listener.local_addr())
        .map_err(|error| format!("Could not reserve a local Remote Assembly port: {error}"))?
        .port();
    let forwarding = format!("127.0.0.1:{local_port}:127.0.0.1:{REMOTE_SERVER_PORT}");
    let mut command = Command::new("ssh");
    command.args([
        "-o",
        "BatchMode=yes",
        "-o",
        "ConnectTimeout=10",
        "-o",
        "ExitOnForwardFailure=yes",
        "-o", "ControlMaster=no", "-o", "ControlPath=none",
        "-o",
        "ServerAliveInterval=15",
        "-C",
        "-T",
        "-L",
        &forwarding,
        "--",
        target,
        "cat >/dev/null",
    ]);
    let child = command
        // Keep the remote command alive through this parent-owned pipe. If
        // Assembly is terminated before its shutdown hook runs, EOF closes
        // the remote command and the SSH forward exits instead of orphaning.
        .stdin(Stdio::piped())
        .stdout(Stdio::null())
        .stderr(Stdio::piped())
        .spawn()
        .map_err(|error| format!("Could not start the Remote Assembly tunnel: {error}"))?;
    let pid = child.id();
    processes
        .lock()
        .unwrap_or_else(std::sync::PoisonError::into_inner)
        .insert(pid, child);
    let tunnel = RemoteTunnel { pid, processes: processes.clone(), local_port, token };
    for _ in 0..50 {
        if TcpStream::connect(("127.0.0.1", local_port)).is_ok() { return Ok(tunnel); }
        let status = {
            let mut tunnels = processes
                .lock()
                .unwrap_or_else(std::sync::PoisonError::into_inner);
            let Some(child) = tunnels.get_mut(&pid) else {
                return Err("Remote Assembly tunnel was stopped".to_string());
            };
            child
                .try_wait()
                .map_err(|error| format!("Could not inspect the Remote Assembly tunnel: {error}"))?
        };
        if let Some(status) = status {
            processes
                .lock()
                .unwrap_or_else(std::sync::PoisonError::into_inner)
                .remove(&pid);
            return Err(format!("Remote Assembly tunnel exited with {status}"));
        }
        tokio::time::sleep(Duration::from_millis(100)).await;
    }
    stop_remote_tunnel(&processes, pid);
    Err("Remote Assembly tunnel did not become ready".to_string())
}

async fn ssh_output(target: &str, command: &str) -> Result<std::process::Output, String> {
    tokio::process::Command::new("ssh")
        .args(["-o", "BatchMode=yes", "-o", "ConnectTimeout=10", "-o", "ControlMaster=no",
            "-o", "ControlPath=none", "--", target, command])
        .stdin(Stdio::null()).kill_on_drop(true).output().await
        .map_err(|error| format!("Could not start SSH: {error}"))
}

async fn read_remote_token(target: &str) -> Result<String, String> {
    let output = ssh_output(target, "test -f \"$HOME/.config/assembly/server.env\" || { echo Backend-not-installed >&2; exit 44; }; sed -n 's/^ASSEMBLY_SERVER_TOKEN=//p' \"$HOME/.config/assembly/server.env\"").await?;
    if !output.status.success() {
        if output.status.code() == Some(44) { return Err("Backend not installed on this SSH account".into()); }
        return Err(format!("SSH connection failed: {}", String::from_utf8_lossy(&output.stderr).trim()));
    }
    let token = String::from_utf8_lossy(&output.stdout).lines().last().unwrap_or_default().trim().to_string();
    if token.len() < 32 { return Err("Installed backend authentication is missing or invalid".into()); }
    Ok(token)
}

fn ssh_target_key(output: &str) -> Result<String, String> {
    let value = |name: &str| output.lines().find_map(|line| line.strip_prefix(name));
    let (Some(host), Some(user), Some(port)) = (value("hostname "), value("user "), value("port "))
        else { return Err("SSH configuration did not resolve hostname, user and port".into()); };
    Ok(format!("{}@{}:{}", user.trim(), host.trim().to_lowercase(), port.trim()))
}

async fn resolve_ssh_target(target: &str) -> Result<String, String> {
    validate_ssh_target(target)?;
    let output = tokio::process::Command::new("ssh").args(["-G", "--", target])
        .stdin(Stdio::null()).kill_on_drop(true).output().await.map_err(|error| error.to_string())?;
    if !output.status.success() { return Err(format!("SSH configuration failed: {}", String::from_utf8_lossy(&output.stderr).trim())); }
    ssh_target_key(&String::from_utf8_lossy(&output.stdout))
}

async fn client_loop(
    url: String,
    token: String,
    requests: &mut mpsc::Receiver<ClientRequest>,
    event_sink: RemoteEventSink,
    terminal_sink: Option<RemoteTerminalSink>,
    reconnect_tunnel_after_failures: Option<u8>,
    ready: Arc<AtomicBool>,
    initial_ready: &mut Option<oneshot::Sender<Result<Vec<AgentConversationSessionRecord>, String>>>,
    status: Arc<dyn Fn() + Send + Sync>,
    relay: Option<super::messaging::Relay>,
    history_client: (RemoteConnectionManager, String),
) {
    let _end = ConnectionEnd { ready: ready.clone(), status: status.clone() };
    let cursors = Arc::new(Mutex::new(BTreeMap::<String, i64>::new()));
    let mut failed_connections = 0_u8;
    loop {
        ready.store(false, Ordering::Release);
        let (request, challenge) = match handshake::upgrade_request(&url) {
            Ok(request) => request,
            Err(error) => { fail_queued_requests(requests, error); return; }
        };
        let connected = tokio_tungstenite::connect_async_with_config(request, None, true).await;
        let (mut socket, _) = match connected {
            Ok(socket) => socket,
            Err(error) => {
                if let Some(reply) = initial_ready.take() {
                    let message = if matches!(&error, tokio_tungstenite::tungstenite::Error::Http(response) if matches!(response.status().as_u16(), 401 | 426)) {
                        "Remote backend needs an update: challenge authentication is required".to_string()
                    } else { format!("Backend connection/authentication failed: {error}") };
                    let _ = reply.send(Err(message));
                    return;
                }
            failed_connections = failed_connections.saturating_add(1);
            if reconnect_tunnel_after_failures.is_some_and(|limit| failed_connections >= limit) {
                return;
            }
            tokio::time::sleep(Duration::from_secs(1)).await;
            continue;
            }
        };
        failed_connections = 0;
        let handshake = handshake::sign_in(&mut socket, &token, &challenge).await;
        if let Err(error) = handshake {
            if let Some(reply) = initial_ready.take() { let _ = reply.send(Err(error.clone())); }
            fail_queued_requests(requests, error);
            return;
        }
        let saved = match history_client.0.saved_cursors(&history_client.1) {
            Ok(saved) => saved,
            Err(error) => {
                if let Some(reply) = initial_ready.take() { let _ = reply.send(Err(error.clone())); }
                fail_queued_requests(requests, error);
                return;
            }
        };
        *cursors.lock().unwrap_or_else(std::sync::PoisonError::into_inner) = saved.clone();
        let mut resume_cursors = saved.clone();
        let resume = ClientFrame::Resume { cursors: saved };
        if send_client_frame(&mut socket, &resume).await.is_err() {
            continue;
        }
        if send_client_frame(&mut socket, &ClientFrame::Request { id: READINESS_REQUEST_ID, command: RemoteCommand::ListSessions }).await.is_err() { return; }
        let mut replaying = BTreeMap::<String, (Option<i64>, Option<AgentConversationEvent>)>::new();
        let quiet_sink = history_client.0.history_event_sink(&history_client.1, false);
        let mut replacements = HashSet::<u64>::new();
        let mut pending = HashMap::<u64, ClientReply>::new();
        let mut pending_progress = HashMap::<u64, mpsc::UnboundedSender<super::providers::updates::ProviderDownloadProgress>>::new();
        let mut interruption = "The Remote Assembly connection was interrupted".to_string();
        let (relay_replies, mut relay_outgoing) = mpsc::channel::<ClientFrame>(8);
        'connected: loop {
            tokio::select! {
                Some(reply) = relay_outgoing.recv() => {
                    if send_client_frame(&mut socket, &reply).await.is_err() { break; }
                }
                request = requests.recv() => {
                    let Some(request) = request else {
                        return;
                    };
                    match request {
                        ClientRequest::Download { id, command } => {
                            if send_client_frame(&mut socket, &ClientFrame::Request { id, command }).await.is_err() { break; }
                        }
                        ClientRequest::Execute { id, command, reply, progress } => {
                            let frame = ClientFrame::Request { id, command };
                            pending.insert(id, reply);
                            if let Some(progress) = progress { pending_progress.insert(id, progress); }
                            if let Err(error) = send_client_frame(&mut socket, &frame).await {
                                pending_progress.remove(&id);
                                if let Some(reply) = pending.remove(&id) {
                                    let _ = reply.send(Err(error));
                                }
                                break;
                            }
                        }
                        ClientRequest::Cancel { id } => {
                            pending_progress.remove(&id);
                            if let Some(reply) = pending.remove(&id) {
                                let _ = reply.send(Err("Remote Assembly request was canceled".into()));
                            }
                            if send_client_frame(&mut socket, &ClientFrame::Cancel { id }).await.is_err() {
                                break;
                            }
                        }
                    }
                }
                message = socket.next() => {
                    let message = match message {
                        Some(Ok(TungsteniteMessage::Close(frame))) => {
                            interruption = match frame.as_ref() {
                                Some(frame) if !frame.reason.is_empty() => format!(
                                    "The Remote Assembly connection closed ({:?}): {}", frame.code, frame.reason
                                ),
                                Some(frame) => format!("The Remote Assembly connection closed ({:?})", frame.code),
                                None => "The Remote Assembly connection closed without a reason".into(),
                            };
                            eprintln!("Remote socket closed with {} pending requests: {frame:?}", pending.len());
                            break;
                        }
                        Some(Ok(message)) => message,
                        Some(Err(error)) => {
                            interruption = format!("The Remote Assembly connection failed: {error}");
                            eprintln!("Remote socket failed with {} pending requests: {error}", pending.len());
                            break;
                        }
                        None => {
                            interruption = "The Remote Assembly connection ended without a close frame".into();
                            eprintln!("Remote socket ended with {} pending requests", pending.len());
                            break;
                        }
                    };
                    let mut frame = match parse_server_frame(message) {
                        Ok(Some(frame)) => frame,
                        Ok(None) => continue,
                        Err(error) => {
                            if let Some(reply) = initial_ready.take() { let _ = reply.send(Err(error.clone())); }
                            for (_, reply) in pending.drain() { let _ = reply.send(Err(error.clone())); }
                            return;
                        }
                    };
                    if let ServerFrame::Event { event } = frame {
                        let received = Instant::now();
                        let mut events = vec![event];
                        let mut ended = false;
                        let mut deferred = None;
                        let deadline = tokio::time::Instant::now() + Duration::from_millis(16);
                        loop {
                            if events.len() >= 64 { break; }
                            let Ok(next) = tokio::time::timeout_at(deadline, socket.next()).await else { break; };
                            let next = match next {
                                Some(Ok(next)) => next,
                                Some(Err(error)) => {
                                    interruption = format!("The Remote Assembly connection failed: {error}");
                                    ended = true;
                                    break;
                                }
                                None => { ended = true; break; }
                            };
                            if let TungsteniteMessage::Close(close) = &next {
                                interruption = close.as_ref().map_or_else(
                                    || "The Remote Assembly connection closed without a reason".to_string(),
                                    |frame| format!("The Remote Assembly connection closed ({:?}): {}", frame.code, frame.reason),
                                );
                                ended = true;
                                break;
                            }
                            match parse_server_frame(next) {
                                Ok(Some(ServerFrame::Event { event })) => events.push(event),
                                Ok(Some(other)) => { deferred = Some(other); break; }
                                Ok(None) => continue,
                                Err(error) => { interruption = error; ended = true; break; }
                            }
                        }
                        let count = events.len();
                        let (quiet, mut live): (Vec<_>, Vec<_>) = events.into_iter()
                            .partition(|event| replaying.contains_key(&event.owned_id));
                        // A session waiting for a replay it asked for drops pushed events; the replay re-sends them in order.
                        live.retain(|event| !resume_cursors.contains_key(&event.owned_id));
                        let skipped = history_client.0.history.lock().unwrap_or_else(std::sync::PoisonError::into_inner)
                            .skipped(&history_client.1, &live).unwrap_or_else(|error| { eprintln!("Remote gap check failed: {error}"); BTreeMap::new() });
                        if !skipped.is_empty() {
                            eprintln!("Remote events skipped past the saved history of {} sessions; replaying them", skipped.len());
                            live.retain(|event| !skipped.contains_key(&event.owned_id));
                            cursors.lock().unwrap_or_else(std::sync::PoisonError::into_inner).extend(skipped.clone());
                            resume_cursors.extend(skipped.clone());
                            if send_client_frame(&mut socket, &ClientFrame::Resume { cursors: skipped }).await.is_err() { break 'connected; }
                        }
                        for event in &quiet {
                            replaying.get_mut(&event.owned_id).unwrap().1 = Some(event.clone());
                        }
                        if apply_event_batch(quiet, &quiet_sink, &cursors).is_err()
                            || apply_event_batch(live, &event_sink, &cursors).is_err() { break 'connected; }
                        if received.elapsed().as_millis() > 32 {
                            eprintln!("Remote socket receive: {count} events in {} ms", received.elapsed().as_millis());
                        }
                        if ended { break 'connected; }
                        let Some(other) = deferred else { continue; };
                        frame = other;
                    }
                    match frame {
                        ServerFrame::HistoryDownloadStart { id, owned_id, low_sequence, high_sequence, row_count, total_bytes, covered_bytes, snapshot } => {
                            if let Err(error) = history_client.0.receive_history_start(&history_client.1, id, &owned_id, low_sequence, high_sequence, row_count, total_bytes, covered_bytes, snapshot) {
                                history_client.0.fail_history_download(&history_client.1, id, error);
                                if send_client_frame(&mut socket, &ClientFrame::Cancel { id }).await.is_err() { break 'connected; }
                            }
                        }
                        ServerFrame::HistoryDownloadChunk { id, owned_id, covered_bytes, gzip_base64, .. } => {
                            if let Err(error) = history_client.0.receive_history_chunk(&history_client.1, id, &owned_id, covered_bytes, gzip_base64) {
                                history_client.0.fail_history_download(&history_client.1, id, error);
                                if send_client_frame(&mut socket, &ClientFrame::Cancel { id }).await.is_err() { break 'connected; }
                            }
                        }
                        ServerFrame::HistoryDownloadDone { id, owned_id } => {
                            let (manager, profile) = history_client.clone();
                            tokio::spawn(async move {
                                if let Err(error) = manager.finish_history_download(&profile, &owned_id, id).await {
                                    manager.fail_history_download(&profile, id, error);
                                }
                            });
                        }
                        ServerFrame::ReplayStart { owned_id } => {
                            let saved = resume_cursors.remove(&owned_id).or_else(||
                                cursors.lock().unwrap_or_else(std::sync::PoisonError::into_inner).get(&owned_id).copied());
                            replaying.insert(owned_id, (saved, None));
                        }
                        ServerFrame::ReplayComplete { owned_id, head, snapshot } => {
                            debug_assert_eq!(head, snapshot.last_sequence);
                            let (saved, newest) = replaying.remove(&owned_id).unwrap_or_default();
                            match history_client.0.complete_replay(&history_client.1, snapshot, newest, saved) {
                                Ok(true) => {
                                    cursors.lock().unwrap_or_else(std::sync::PoisonError::into_inner).remove(&owned_id);
                                    let id = history_client.0.next_request_id.fetch_add(1, Ordering::Relaxed);
                                    replacements.insert(id);
                                    if send_client_frame(&mut socket, &ClientFrame::Request { id,
                                        command: RemoteCommand::Snapshot { owned_id, request_id: id, after_sequence: None },
                                    }).await.is_err() { break 'connected; }
                                }
                                Ok(false) => {}
                                Err(error) => { interruption = error; break 'connected; }
                            }
                        }
                        ServerFrame::Ready { protocol_version, .. } if protocol_version != PROTOCOL_VERSION => {
                            for (_, reply) in pending.drain() {
                                let _ = reply.send(Err(format!("Unsupported remote protocol {protocol_version}")));
                            }
                            return;
                        }
                        ServerFrame::Ready { .. } => {}
                        ServerFrame::Response { id: READINESS_REQUEST_ID, response } => {
                            let RemoteResponse::Sessions(sessions) = response else {
                                let error = "Backend returned an invalid readiness response";
                                if let Some(reply) = initial_ready.take() { let _ = reply.send(Err(error.into())); }
                                for (_, reply) in pending.drain() { let _ = reply.send(Err(error.into())); }
                                return;
                            };
                            if let Err(error) = history_client.0.replace_cached_profile_sessions(&history_client.1, &sessions) { interruption = error; break 'connected; }
                            ready.store(true, Ordering::Release);
                            let incomplete = match history_client.0.store.remote_incomplete_downloads(&history_client.1) { Ok(incomplete) => incomplete, Err(error) => { interruption = error.to_string(); break 'connected; } };
                            for owned in incomplete {
                                let id = history_client.0.next_request_id.fetch_add(1, Ordering::Relaxed);
                                let download = match history_client.0.create_download(&history_client.1, &owned, id) { Ok(download) => download, Err(error) => { interruption = error; break 'connected; } };
                                let key = RemoteHistory::key(&history_client.1, &owned);
                                {
                                    let mut downloads = history_client.0.downloads.lock().unwrap();
                                    if let Some(previous) = downloads.get_mut(&key) {
                                        previous.request_id = Some(id); previous.epoch = download.epoch;
                                        previous.low = None; previous.high = None; previous.row_count = 0;
                                        previous.progress.send_replace(download.progress.borrow().clone());
                                    } else { downloads.insert(key.clone(), download); }
                                }
                                let before_sequence = match history_client.0.store.remote_saved_bounds(&key) { Ok(bounds) => bounds.0, Err(error) => { interruption = error.to_string(); break 'connected; } };
                                if send_client_frame(&mut socket, &ClientFrame::Request { id, command: RemoteCommand::DownloadHistory { owned_id: owned, before_sequence } }).await.is_err() { break 'connected; }
                            }
                            status();
                            if let Some(reply) = initial_ready.take() { let _ = reply.send(Ok(sessions)); }
                        }
                        ServerFrame::Response { id, response } => {
                            if replacements.remove(&id) {
                                if let RemoteResponse::Snapshot(Some(snapshot)) = response {
                                    let newest = snapshot.events.last().cloned();
                                    let owned_id = snapshot.connection.owned_id.clone();
                                    let head = snapshot.last_sequence;
                                    if history_client.0.save_replay_snapshot(&history_client.1, snapshot, newest).is_err() {
                                        break 'connected;
                                    }
                                    let mut cursors = cursors.lock().unwrap_or_else(std::sync::PoisonError::into_inner);
                                    let cursor = cursors.entry(owned_id).or_insert(head);
                                    *cursor = (*cursor).max(head);
                                }
                                continue;
                            }
                            pending_progress.remove(&id);
                            if let Some(reply) = pending.remove(&id) {
                                let _ = reply.send(Ok(response));
                            }
                        }
                        ServerFrame::Error { id: READINESS_REQUEST_ID, message } => {
                            if let Some(reply) = initial_ready.take() { let _ = reply.send(Err(message.clone())); }
                            for (_, reply) in pending.drain() { let _ = reply.send(Err(message.clone())); }
                            return;
                        }
                        ServerFrame::Error { id, message } => {
                            history_client.0.fail_history_download(&history_client.1, id, message.clone());
                            if replacements.remove(&id) { interruption = message; break 'connected; }
                            pending_progress.remove(&id);
                            if let Some(reply) = pending.remove(&id) {
                                let _ = reply.send(Err(message));
                            }
                        }
                        ServerFrame::Event { .. } => unreachable!(),
                        ServerFrame::MessagesChanged => {
                            if let Some(sink) = MESSAGES_CHANGED_SINK.get() { sink(); }
                        }
                        ServerFrame::TerminalOutput { frame } => {
                            if let Some(sink) = &terminal_sink { sink(frame); }
                        }
                        ServerFrame::ProviderDownloadProgress { id, progress } => {
                            if let Some(sender) = pending_progress.get(&id) { let _ = sender.send(progress); }
                        }
                        ServerFrame::RelayRequest { request_id, op, args } => {
                            let relay = relay.clone();
                            let replies = relay_replies.clone();
                            tokio::spawn(async move {
                                let result = match relay {
                                    Some(relay) => relay(op, args).await,
                                    None => Err("target machine not reachable: this desktop does not relay messages".into()),
                                };
                                let _ = replies.send(ClientFrame::RelayReply { request_id, result }).await;
                            });
                        }
                    }
                }
            }
        }
        history_client.0.pause_history_downloads(&history_client.1);
        if ready.swap(false, Ordering::AcqRel) { status(); }
        if let Some(reply) = initial_ready.take() {
            let _ = reply.send(Err("Backend disconnected before readiness".into()));
            return;
        }
        for (_, reply) in pending.drain() {
            let _ = reply.send(Err(interruption.clone()));
        }
        tokio::time::sleep(Duration::from_millis(250)).await;
    }
}

fn apply_event_batch(
    events: Vec<AgentConversationEvent>,
    event_sink: &RemoteEventSink,
    cursors: &Mutex<BTreeMap<String, i64>>,
) -> Result<(), String> {
    let current = cursors.lock().unwrap_or_else(std::sync::PoisonError::into_inner);
    let mut next = BTreeMap::<String, i64>::new();
    let mut accepted = Vec::new();
    for event in events {
        let cursor = next.entry(event.owned_id.clone()).or_insert_with(||
            *current.get(&event.owned_id).unwrap_or(&i64::MIN));
        if event.sequence > *cursor {
            *cursor = event.sequence;
            accepted.push(event);
        }
    }
    drop(current);
    if accepted.is_empty() { return Ok(()); }
    event_sink(accepted)?;
    cursors.lock().unwrap_or_else(std::sync::PoisonError::into_inner).extend(next);
    Ok(())
}

fn fail_queued_requests(requests: &mut mpsc::Receiver<ClientRequest>, message: String) {
    while let Ok(request) = requests.try_recv() {
        if let ClientRequest::Execute { reply, .. } = request {
            let _ = reply.send(Err(message.clone()));
        }
    }
}

const PAGE_CANCELLED: &str = "The conversation page request was cancelled";

/// Resolves once `request_id` is cancelled. A missed (lagged) id is not retried:
/// the read then waits for its history copy, as it did before cancellation existed.
async fn wait_for_cancel(cancellations: &mut broadcast::Receiver<u64>, request_id: u64) {
    loop {
        match cancellations.recv().await {
            Ok(id) if id == request_id => return,
            Err(broadcast::error::RecvError::Closed) => std::future::pending::<()>().await,
            _ => {}
        }
    }
}

pub(super) fn validate_ssh_target(target: &str) -> Result<(), String> {
    if target.is_empty() || target.starts_with('-')
        || !target.bytes().all(|value| value.is_ascii_alphanumeric() || b"._@-".contains(&value)) {
        return Err("SSH destination may contain only letters, numbers, '.', '_', '@', and '-'".into());
    }
    Ok(())
}

/// Remote git network operations are bounded to 120 seconds on the remote machine,
/// so the wait here must outlast that; every other workspace operation stays quick.
fn workspace_wait_seconds(operation: &str) -> u64 {
    match operation {
        "fetch_git_repository" | "pull_git_repository" | "push_git_repository" | "publish_git_repository"
        | "workspace_git_clone" => 125,
        _ => 15,
    }
}

#[cfg(test)]
mod connection_tests {
    use super::*;
    use super::super::protocol::{BackgroundWorkItem, BackgroundWorkKind};

    pub(super) fn add_session(manager: &AgentRuntimeManager, owned_id: &str, title: &str) {
        manager.store().upsert_session(&mcb_core::session_store::SessionRow {
            owned_id: owned_id.into(), native_session_id: None, provider: "codex".into(), model: None,
            effort: None, cwd: "/work".into(), worktree: None, branch: None, title: Some(title.into()),
            title_source: None, project: None, project_id: None, state: "suspended".into(), suspended: true,
            created_at_ms: 1, last_activity_at_ms: 1,
            extra_json: super::super::manager::imported_session_extra(AgentConversationProvider::Codex).unwrap(),
        }).unwrap();
    }

    /// A message's sender label and text, read from the store file, whatever delivery did to it since.
    pub(super) fn stored_message(database: &std::path::Path, id: &str) -> (String, String) {
        rusqlite::Connection::open(database).unwrap().query_row(
            "SELECT sender_name, body FROM workflow_messages WHERE id = ?", [id], |row| Ok((row.get(0)?, row.get(1)?))).unwrap()
    }

    async fn tool(endpoint: &super::super::messaging::MessagingEndpoint, token: &str, call: serde_json::Value) -> serde_json::Value {
        let result = endpoint.handle(token, call).await.unwrap();
        serde_json::from_str(result["content"][0]["text"].as_str().unwrap()).unwrap()
    }

    #[tokio::test]
    async fn extend_import_during_a_download_restarts_below_the_saved_minimum() {
        let manager = download_manager();
        let (id, mut requests) = begin_test_download(&manager, 200, Some(2), Some(3)).await;
        manager.receive_history_chunk("cache-test", id, "large-history", 100,
            encode_history_chunk(&[history_event(3)]).unwrap()).unwrap();
        let extender = manager.clone();
        let extension = tokio::spawn(async move { extender.extend_import("large-history".into()).await });
        let ClientRequest::Execute { reply, .. } = requests.recv().await.unwrap() else { panic!("expected import"); };
        reply.send(Ok(RemoteResponse::ImportProgress { added: 1, reached_start: true })).unwrap();
        extension.await.unwrap().unwrap();
        assert!(matches!(requests.try_recv(), Ok(ClientRequest::Cancel { id: cancelled }) if cancelled == id),
            "the frozen download must be cancelled");
        let ClientRequest::Download { id: restarted, command } = requests.try_recv().expect("restart download") else { panic!("expected download"); };
        assert_ne!(restarted, id);
        assert!(matches!(command, RemoteCommand::DownloadHistory { before_sequence: Some(3), .. }));
        manager.finish_history_download("cache-test", "large-history", id).await.unwrap();
        assert!(!manager.history.lock().unwrap().download_state("cache-test", "large-history").unwrap().unwrap().complete);
        manager.receive_history_start("cache-test", restarted, "large-history", Some(1), Some(3), 3, 300, 100,
            history_snapshot(vec![], 3)).unwrap();
        manager.receive_history_chunk("cache-test", restarted, "large-history", 300,
            encode_history_chunk(&[history_event(2), history_event(1)]).unwrap()).unwrap();
        manager.finish_history_download("cache-test", "large-history", restarted).await.unwrap();
        assert_eq!(manager.store.remote_saved_bounds(&RemoteHistory::key("cache-test", "large-history")).unwrap().0, Some(1));
        assert!(manager.history.lock().unwrap().download_state("cache-test", "large-history").unwrap().unwrap().complete);
    }

    #[tokio::test]
    async fn deleting_a_session_drops_its_download_and_ignores_late_frames() {
        let manager = download_manager();
        let (id, mut requests) = begin_test_download(&manager, 100, Some(1), Some(1)).await;
        let mut progress = manager.download_watch("cache-test", "large-history").unwrap();
        progress.borrow_and_update();
        let deleter = manager.clone();
        let deletion = tokio::spawn(async move { deleter.delete("large-history".into()).await });
        let ClientRequest::Execute { reply, .. } = requests.recv().await.unwrap() else { panic!("expected delete"); };
        reply.send(Ok(RemoteResponse::Bool(true))).unwrap();
        assert!(deletion.await.unwrap().unwrap());
        manager.receive_history_start("cache-test", id, "large-history", Some(1), Some(1), 1, 100, 0,
            history_snapshot(vec![], 1)).unwrap();
        manager.receive_history_chunk("cache-test", id, "large-history", 100,
            encode_history_chunk(&[history_event(1)]).unwrap()).unwrap();
        manager.finish_history_download("cache-test", "large-history", id).await.unwrap();
        assert!(manager.history.lock().unwrap().download_state("cache-test", "large-history").unwrap().is_none(),
            "late frames must not recreate the deleted cache");
        assert!(progress.changed().await.is_err(), "deletion must release waiting readers");
        assert!(matches!(requests.try_recv(), Ok(ClientRequest::Cancel { id: cancelled }) if cancelled == id));
    }

    #[tokio::test]
    async fn cancellation_before_admission_releases_the_read() {
        let manager = download_manager();
        manager.history.lock().unwrap().prepare_download("cache-test", "large-history").unwrap();
        let mut requests = connect(&manager);
        let reader = manager.clone();
        let mut read = tokio::spawn(async move {
            reader.select_child_history("large-history".into(), "child".into(), 73, 1024).await
        });
        let ClientRequest::Execute { reply, .. } = requests.recv().await.unwrap() else { panic!("expected child identity request"); };
        let owned = super::super::history::child_owned_id("large-history", "child").unwrap();
        reply.send(Ok(RemoteResponse::ChildHistoryId(owned))).unwrap();
        // No yield: cancellation lands after the identity reply, before admission subscribes.
        manager.cancel_request(73).await;
        let result = tokio::time::timeout(Duration::from_millis(100), &mut read).await;
        read.abort();
        assert_eq!(result.expect("cancelled read must leave admission").unwrap().unwrap_err(), PAGE_CANCELLED);
    }

    #[tokio::test]
    async fn explicit_disconnect_releases_a_history_admission_wait() {
        let manager = download_manager();
        let (_id, _) = begin_test_download(&manager, 100, Some(1), Some(3)).await;
        let reader = manager.clone();
        let mut cancellations = reader.cancelled_requests.subscribe();
        let wait = tokio::spawn(async move { reader.wait_history_download_ready("cache-test", "large-history", 72, &mut cancellations).await });
        tokio::task::yield_now().await;
        manager.disconnect_profile("cache-test").unwrap();
        let result = tokio::time::timeout(Duration::from_millis(100), wait).await.expect("disconnect must release admission").unwrap();
        assert!(result.unwrap_err().contains("not connected"));
    }

    #[tokio::test]
    async fn one_history_request_pushes_all_chunks_newest_first() {
        let directory = std::env::temp_dir().join(format!("download-{}", uuid::Uuid::new_v4()));
        std::fs::create_dir(&directory).unwrap();
        let manager = AgentRuntimeManager::open(ProviderRegistry::default(), &directory.join("sessions.db")).unwrap();
        // Saving through the manager's store gives the stream its normal journal input.
        manager.store_handle().upsert_session(&mcb_core::session_store::SessionRow {
            owned_id: "large-history".into(), provider: "codex".into(), native_session_id: None,
            model: None, effort: None, cwd: directory.to_string_lossy().into(), worktree: None, branch: None,
            title: None, title_source: None, project: None, project_id: None, state: "suspended".into(), suspended: true,
            created_at_ms: 0, last_activity_at_ms: 0, extra_json: super::super::manager::imported_session_extra(AgentConversationProvider::Codex).unwrap(),
        }).unwrap();
        for sequence in 1..=4 {
            let event = AgentConversationEvent { sequence, ..large_history_event(300_000) };
            manager.store_handle().append_event(&mcb_core::session_store::EventRow {
                owned_id: event.owned_id.clone(), seq: sequence, turn_id: None, kind: "content.delta".into(),
                payload_json: serde_json::to_string(&event).unwrap(), created_at_ms: 1,
            }).unwrap();
        }
        let state = server_state(manager, directory.clone(), Arc::from("test")).unwrap();
        let (sender, mut frames) = mpsc::channel(1);
        let producer = tokio::spawn(async move { stream_history_download(&state, 44, "large-history", None, &sender).await });
        assert!(matches!(frames.recv().await.unwrap(), ServerFrame::HistoryDownloadStart { id: 44, snapshot, .. } if snapshot.events.is_empty()));
        let mut received = Vec::new();
        let mut covered = 0;
        while let Some(frame) = frames.recv().await {
            match frame {
                ServerFrame::HistoryDownloadChunk { gzip_base64, covered_bytes, .. } => {
                    assert!(covered_bytes > covered); covered = covered_bytes;
                    received.extend(decode_history_chunk(&gzip_base64).unwrap().iter().map(|event| event.sequence));
                }
                ServerFrame::HistoryDownloadDone { id: 44, .. } => break,
                other => panic!("unexpected frame {other:?}"),
            }
        }
        producer.await.unwrap().unwrap();
        assert_eq!(received, [4,3,2,1]);
        std::fs::remove_dir_all(directory).unwrap();
    }

    #[test]
    fn oversized_download_chunk_returns_error_without_done() {
        let frame = ServerFrame::HistoryDownloadChunk { id: 55, owned_id: "s".into(), oldest_sequence: 1,
            covered_bytes: 100, gzip_base64: "x".repeat(MAX_SERVER_FRAME_BYTES) };
        let encoded = encode_server_frame(&frame).unwrap();
        assert!(matches!(serde_json::from_str::<ServerFrame>(&encoded).unwrap(), ServerFrame::Error { id: 55, message } if message.contains("16 MiB")));
    }

    #[tokio::test]
    async fn history_page_waits_locally_and_cancellation_stops_the_wait() {
        let manager = download_manager();
        let (id, mut requests) = begin_test_download(&manager, 300, Some(1), Some(3)).await;
        manager.receive_history_chunk("cache-test", id, "large-history", 100, encode_history_chunk(&[history_event(3)]).unwrap()).unwrap();
        let reader = manager.clone();
        let page = tokio::spawn(async move { reader.item_page("large-history".into(), 3, 1024, true, 90).await });
        tokio::task::yield_now().await;
        assert!(!page.is_finished());
        manager.receive_history_chunk("cache-test", id, "large-history", 200, encode_history_chunk(&[history_event(2)]).unwrap()).unwrap();
        assert!(page.await.unwrap().is_ok());
        let reader = manager.clone();
        let page = tokio::spawn(async move { reader.item_page("large-history".into(), 2, 1024, true, 91).await });
        tokio::task::yield_now().await;
        manager.cancel_request(91).await;
        assert!(page.await.unwrap().is_err());
        assert!(matches!(requests.try_recv().unwrap(), ClientRequest::Cancel { id: 91 }));
        assert!(requests.try_recv().is_err());
    }

    #[tokio::test]
    async fn child_history_uses_the_same_download_and_exact_profile_key() {
        let manager = download_manager();
        manager.history.lock().unwrap().prepare_download("cache-test", "large-history").unwrap();
        let mut requests = connect(&manager);
        let reader = manager.clone();
        let child = tokio::spawn(async move { reader.select_child_history("large-history".into(), "child".into(), 55, 1024).await });
        let ClientRequest::Execute { reply, .. } = requests.recv().await.unwrap() else { panic!("expected identity request"); };
        let owned = super::super::history::child_owned_id("large-history", "child").unwrap();
        reply.send(Ok(RemoteResponse::ChildHistoryId(owned.clone()))).unwrap();
        let ClientRequest::Download { id, command } = requests.recv().await.unwrap() else { panic!("expected child download"); };
        assert!(matches!(command, RemoteCommand::DownloadHistory { owned_id, .. } if owned_id == owned));
        let mut snapshot = history_snapshot(vec![], 0); snapshot.connection.owned_id = owned.clone();
        manager.receive_history_start("cache-test", id, &owned, None, None, 0, 0, 0, snapshot).unwrap();
        manager.finish_history_download("cache-test", &owned, id).await.unwrap();
        let selected = child.await.unwrap().unwrap();
        assert_eq!(selected.history_owned_id, RemoteHistory::key("cache-test", &owned));
        assert!(manager.history.lock().unwrap().download_state("other", &owned).unwrap().is_none());
        manager.client.lock().unwrap().clients.remove("cache-test");
        assert_eq!(manager.select_child_history("large-history".into(), "child".into(), 56, 1024).await.unwrap().history_owned_id, selected.history_owned_id);
    }

    #[tokio::test]
    async fn extend_import_downloads_new_saved_rows_without_rechecking_gaps() {
        // The amended contract accepts a fresh gap check after each import extension.
        let manager = download_manager();
        let (id, mut requests) = begin_test_download(&manager, 100, Some(2), Some(2)).await;
        manager.receive_history_chunk("cache-test", id, "large-history", 100, encode_history_chunk(&[history_event(2)]).unwrap()).unwrap();
        manager.finish_history_download("cache-test", "large-history", id).await.unwrap();
        let extender = manager.clone();
        let extension = tokio::spawn(async move { extender.extend_import("large-history".into()).await });
        let ClientRequest::Execute { command, reply, .. } = requests.recv().await.unwrap() else { panic!("expected import"); };
        assert!(matches!(command, RemoteCommand::ExtendImport { .. }));
        reply.send(Ok(RemoteResponse::ImportProgress { added: 1, reached_start: true })).unwrap();
        extension.await.unwrap().unwrap();
        assert!(matches!(requests.recv().await.unwrap(), ClientRequest::Download { command: RemoteCommand::DownloadHistory { before_sequence: Some(2), .. }, .. }));
        assert!(!manager.history.lock().unwrap().download_state("cache-test", "large-history").unwrap().unwrap().complete);
    }

    #[test]
    fn history_download_protocol_24_round_trips_compressed_chunks() {
        assert_eq!(PROTOCOL_VERSION, 24);
        let rows = vec![history_event(3), history_event(-2)];
        let encoded = encode_history_chunk(&rows).unwrap();
        assert_eq!(decode_history_chunk(&encoded).unwrap(), rows);
        let request = ClientFrame::Request { id: 50, command: RemoteCommand::DownloadHistory { owned_id: "large-history".into(), before_sequence: Some(-1) } };
        assert!(matches!(serde_json::from_str::<ClientFrame>(&serde_json::to_string(&request).unwrap()).unwrap(), ClientFrame::Request { id: 50, command: RemoteCommand::DownloadHistory { before_sequence: Some(-1), .. } }));
        for frame in [ServerFrame::HistoryDownloadStart { id: 50, owned_id: "large-history".into(), low_sequence: Some(-2), high_sequence: Some(3), row_count: 2, total_bytes: 100, covered_bytes: 0, snapshot: history_snapshot(vec![], 3) }, ServerFrame::HistoryDownloadChunk { id: 50, owned_id: "large-history".into(), oldest_sequence: -2, covered_bytes: 100, gzip_base64: encoded }, ServerFrame::HistoryDownloadDone { id: 50, owned_id: "large-history".into() }] {
            let json = encode_server_frame(&frame).unwrap();
            assert_eq!(serde_json::to_value(parse_server_frame(TungsteniteMessage::Text(json.into())).unwrap().unwrap()).unwrap(), serde_json::to_value(frame).unwrap());
        }
    }

    #[tokio::test]
    async fn live_outbound_precedes_the_next_history_chunk() {
        let (live, mut outbound) = mpsc::channel(8);
        let (history, mut chunks) = mpsc::channel(1);
        history.send(ServerFrame::HistoryDownloadDone { id: 1, owned_id: "s".into() }).await.unwrap();
        live.send(ServerFrame::MessagesChanged).await.unwrap();
        assert!(matches!(next_outbound_frame(&mut outbound, &mut chunks).await, Some(ServerFrame::MessagesChanged)));
        drop(live);
        assert!(matches!(next_outbound_frame(&mut outbound, &mut chunks).await, Some(ServerFrame::HistoryDownloadDone { .. })));
        drop(history);
        assert!(next_outbound_frame(&mut outbound, &mut chunks).await.is_none());
    }

    pub(super) fn download_manager() -> RemoteConnectionManager {
        let manager = RemoteConnectionManager::from_environment(Arc::new(|_| {}), Arc::new(|_| {}), store()).unwrap();
        manager.remember("large-history", "cache-test");
        manager
    }

    pub(super) async fn begin_test_download(manager: &RemoteConnectionManager, total: u64, low: Option<i64>, high: Option<i64>) -> (u64, mpsc::Receiver<ClientRequest>) {
        let mut requests = connect(manager);
        manager.ensure_history_download("cache-test", "large-history").await.unwrap();
        assert!(!manager.download_watch("cache-test", "large-history").unwrap().borrow().ready, "placeholder is not authoritative zero-byte history");
        let ClientRequest::Download { id, command } = requests.recv().await.unwrap() else { panic!("expected download"); };
        assert!(matches!(command, RemoteCommand::DownloadHistory { .. }));
        manager.receive_history_start("cache-test", id, "large-history", low, high, low.zip(high).map_or(0, |(low, high)| (high - low + 1) as u64), total, 0, history_snapshot(vec![], high.unwrap_or(0))).unwrap();
        (id, requests)
    }

    #[tokio::test]
    async fn never_saved_history_opens_at_twenty_percent_without_a_cap() {
        let manager = download_manager();
        let (id, mut requests) = begin_test_download(&manager, 10_000_000_000, Some(1), Some(3)).await;
        let reader = manager.clone();
        let read = tokio::spawn(async move { reader.selection_snapshot("large-history".into(), 51, 1024, None).await });
        tokio::task::yield_now().await;
        manager.receive_history_chunk("cache-test", id, "large-history", 1_999_999_999, encode_history_chunk(&[history_event(3)]).unwrap()).unwrap();
        tokio::task::yield_now().await;
        assert!(!read.is_finished());
        manager.receive_history_chunk("cache-test", id, "large-history", 2_000_000_000, encode_history_chunk(&[history_event(2)]).unwrap()).unwrap();
        assert!(read.await.unwrap().unwrap().is_some());
        assert!(requests.try_recv().is_err());
    }

    #[tokio::test]
    async fn empty_history_download_opens_and_completes() {
        let manager = download_manager();
        let (id, _) = begin_test_download(&manager, 0, None, None).await;
        manager.finish_history_download("cache-test", "large-history", id).await.unwrap();
        assert!(manager.history.lock().unwrap().download_state("cache-test", "large-history").unwrap().unwrap().complete);
        assert!(manager.selection_snapshot("large-history".into(), 1, 1024, None).await.unwrap().is_some());
    }

    #[tokio::test]
    async fn history_download_resumes_from_saved_minimum_after_reconnect() {
        let manager = download_manager();
        let (id, _) = begin_test_download(&manager, 300, Some(-1), Some(3)).await;
        manager.receive_history_chunk("cache-test", id, "large-history", 200, encode_history_chunk(&[history_event(3), history_event(2)]).unwrap()).unwrap();
        manager.pause_history_downloads("cache-test");
        let mut requests = connect(&manager);
        manager.ensure_history_download("cache-test", "large-history").await.unwrap();
        assert!(matches!(requests.recv().await.unwrap(), ClientRequest::Download { command: RemoteCommand::DownloadHistory { before_sequence: Some(2), .. }, .. }));
        assert_eq!(manager.saved_cursors("cache-test").unwrap()["large-history"], 3);
        manager.receive_history_chunk("cache-test", id, "large-history", 300, encode_history_chunk(&[history_event(1)]).unwrap()).unwrap();
        assert_eq!(manager.store.remote_saved_bounds(&RemoteHistory::key("cache-test", "large-history")).unwrap().0, Some(2));
    }

    #[tokio::test]
    async fn download_with_pruned_holes_completes_without_repair_requests() {
        let manager = download_manager();
        let (id, mut requests) = begin_test_download(&manager, 400, Some(1), Some(9)).await;
        manager.receive_history_start("cache-test", id, "large-history", Some(1), Some(9), 4, 400, 0,
            history_snapshot(vec![], 9)).unwrap();
        manager.receive_history_chunk("cache-test", id, "large-history", 400,
            encode_history_chunk(&[history_event(9), history_event(5), history_event(2), history_event(1)]).unwrap()).unwrap();
        tokio::time::timeout(Duration::from_millis(100),
            manager.finish_history_download("cache-test", "large-history", id)).await
            .expect("retained rows must complete without repair").unwrap();
        assert!(requests.try_recv().is_err(), "pruned holes must not request EventsAfter");
        assert!(manager.history.lock().unwrap().download_state("cache-test", "large-history").unwrap().unwrap().complete);
    }

    #[tokio::test]
    async fn download_missing_a_row_still_repairs() {
        let manager = download_manager();
        let (id, mut requests) = begin_test_download(&manager, 500, Some(1), Some(9)).await;
        manager.receive_history_start("cache-test", id, "large-history", Some(1), Some(9), 5, 500, 0,
            history_snapshot(vec![], 9)).unwrap();
        manager.receive_history_chunk("cache-test", id, "large-history", 500,
            encode_history_chunk(&[history_event(9), history_event(5), history_event(2), history_event(1)]).unwrap()).unwrap();
        let finisher = manager.clone();
        let finish = tokio::spawn(async move { finisher.finish_history_download("cache-test", "large-history", id).await });
        for after in [2, 5] {
            let ClientRequest::Execute { command, reply, .. } = tokio::time::timeout(
                Duration::from_millis(100), requests.recv()).await.expect("missing rows must request repair").unwrap()
                else { panic!("expected repair"); };
            assert!(matches!(command, RemoteCommand::EventsAfter { after_sequence, .. } if after_sequence == after));
            assert!(!manager.history.lock().unwrap().download_state("cache-test", "large-history").unwrap().unwrap().complete);
            reply.send(Ok(RemoteResponse::EventPage(AgentConversationEventPage { events: vec![], has_more: false }))).unwrap();
        }
        finish.await.unwrap().unwrap();
        assert!(requests.try_recv().is_err());
        assert!(manager.history.lock().unwrap().download_state("cache-test", "large-history").unwrap().unwrap().complete);
    }

    #[tokio::test]
    async fn download_done_repairs_gaps_with_events_after_then_marks_complete() {
        let manager = download_manager();
        let (id, mut requests) = begin_test_download(&manager, 500, Some(1), Some(5)).await;
        manager.receive_history_chunk("cache-test", id, "large-history", 500, encode_history_chunk(&[history_event(5), history_event(1)]).unwrap()).unwrap();
        let finisher = manager.clone();
        let finish = tokio::spawn(async move { finisher.finish_history_download("cache-test", "large-history", id).await });
        let ClientRequest::Execute { command, reply, .. } = requests.recv().await.unwrap() else { panic!("expected repair"); };
        assert!(matches!(command, RemoteCommand::EventsAfter { after_sequence: 1, .. }));
        assert!(!manager.history.lock().unwrap().download_state("cache-test", "large-history").unwrap().unwrap().complete);
        reply.send(Ok(RemoteResponse::EventPage(AgentConversationEventPage { events: vec![history_event(2)], has_more: false }))).unwrap();
        let ClientRequest::Execute { command, reply, .. } = requests.recv().await.unwrap() else { panic!("expected next repair page"); };
        assert!(matches!(command, RemoteCommand::EventsAfter { after_sequence: 2, .. }));
        assert!(!manager.history.lock().unwrap().download_state("cache-test", "large-history").unwrap().unwrap().complete);
        reply.send(Ok(RemoteResponse::EventPage(AgentConversationEventPage { events: vec![history_event(3), history_event(4)], has_more: false }))).unwrap();
        finish.await.unwrap().unwrap();
        assert!(manager.history.lock().unwrap().download_state("cache-test", "large-history").unwrap().unwrap().complete);
        manager.finish_history_download("cache-test", "large-history", id).await.unwrap();
        assert!(requests.try_recv().is_err());
    }

    #[tokio::test]
    async fn remote_message_relay_uses_connected_desktop_and_target_store() {
        use serde_json::json;
        use super::super::messaging::MessagingEndpoint;
        let directory = std::env::temp_dir().join(format!("assembly-message-relay-{}", uuid::Uuid::new_v4()));
        std::fs::create_dir(&directory).unwrap();
        let remote = AgentRuntimeManager::open(ProviderRegistry::default(), &directory.join("remote.db")).unwrap();
        add_session(&remote, "there", "There");
        let state = server_state(remote.clone(), directory.clone(), Arc::from("test-token")).unwrap();
        let listener = tokio::net::TcpListener::bind("127.0.0.1:0").await.unwrap();
        let address = listener.local_addr().unwrap();
        let server = tokio::spawn(axum::serve(listener, Router::new().route("/assembly", get(upgrade_remote_socket)).with_state(state.clone())).into_future());
        // The desktop and its session, connected to that machine as `workbox-test`.
        let desktop = AgentRuntimeManager::open(ProviderRegistry::default(), &directory.join("desktop.db")).unwrap();
        add_session(&desktop, "here", "Here");
        desktop.store().set_main_orchestrator(Some("here")).unwrap();
        let connections = RemoteConnectionManager::from_environment(Arc::new(|_| {}), Arc::new(|_| {}), desktop.store_handle()).unwrap();
        connections.set_message_host(desktop.clone());
        let (requests, mut receiver) = mpsc::channel(8);
        let ready = Arc::new(AtomicBool::new(false));
        let (ready_reply, ready_answer) = oneshot::channel();
        let (actor_ready, relay) = (ready.clone(), connections.message_relay("workbox-test"));
        let actor = tokio::spawn(async move {
            client_loop(format!("ws://{address}/assembly"), "test-token".into(), &mut receiver, Arc::new(|_| Ok(())), None,
                Some(1), actor_ready, &mut Some(ready_reply), Arc::new(|| {}), Some(relay), client_history()).await;
        });
        ready_answer.await.unwrap().unwrap();
        connections.client.lock().unwrap().clients.insert("workbox-test".into(), RemoteClient {
            requests: Some(requests), profile: Some(profile("workbox-test")), target_key: None, task: None, ready });
        connections.remember("there", "workbox-test");
        let there = MessagingEndpoint::start().unwrap();
        there.set_relay(state.relay.relay());
        let there_token = there.issue("there").token;
        there.bind_store(&there_token, &remote.store_handle(), None);
        // The backend has no projects; the desktop owns registrations for each machine.
        let root = directory.join(".assembly/workflows");
        std::fs::create_dir_all(&root).unwrap();
        std::fs::write(root.join("flow.yml"), "name: Remote\nsteps:\n  check: { run: [ls] }\n").unwrap();
        for (machine, path) in [("workbox-test", directory.display().to_string()), ("local", "/wrong/local".into()), ("other", "/wrong/other".into())] {
            desktop.store().insert_or_get_project(&crate::project_folders::new_project_row(machine.into(),
                crate::project_folders::ProjectFolderInspection { root_path: path, title: "remote-project".into(), repo_key: String::new(), is_git: false })).unwrap();
        }
        assert!(remote.store().project_roots("local").unwrap().is_empty());
        for project in ["remote-project", directory.to_str().unwrap()] {
            let checked = there.handle(&there_token, json!({"name":"check_workflow","arguments":{"project":project,"path":".assembly/workflows/flow.yml"}})).await.unwrap();
            assert_eq!(checked["content"][0]["text"], "ok\ncheck run");
        }
        let listing = tool(&there, &there_token, json!({"name":"list_sessions"})).await;
        let machine_of = |owned_id: &str| listing.as_array().unwrap().iter()
            .find(|entry| entry["ownedId"] == owned_id).map(|entry| entry["machine"].clone());
        assert_eq!(machine_of("there"), None, "a session does not list itself: {listing}");
        assert_eq!(machine_of("here"), Some(json!("mac")), "{listing}");
        let sent = tool(&there, &there_token, json!({"name":"send_message","arguments":{"to":"Here","text":"hello from there"}})).await;
        let id = sent["id"].as_str().unwrap();
        assert!(id.starts_with("mac:"), "{sent}");
        let (sender, body) = stored_message(&directory.join("desktop.db"), &id["mac:".len()..]);
        let there_handle = remote.store().directory_name("there").unwrap().unwrap().0;
        assert_eq!(sender, format!("workbox-test: {there_handle} · There"));
        assert_eq!(body, "hello from there");
        let traffic = connections.list_messages(None, json!({"session":"there","limit":10}), 90).await;
        assert_eq!(traffic.len(), 1);
        assert_eq!(traffic[0]["fromOwnedId"], "workbox-test:there");
        assert_eq!(connections.count_messages("there", 0, 91).await, 1);
        let status = tool(&there, &there_token, json!({"name":"message_status","arguments":{"id":id}})).await;
        assert!(["queued", "delivered", "failed"].contains(&status["status"].as_str().unwrap()), "{status}");
        let here = MessagingEndpoint::start().unwrap();
        here.set_relay(connections.message_relay(LOCAL_MACHINE));
        let here_token = here.issue("here").token;
        here.bind_store(&here_token, &desktop.store_handle(), None);
        let reply = tool(&here, &here_token, json!({"name":"send_message","arguments":{"to":sender,"text":"hello back"}})).await;
        let reply_id = reply["id"].as_str().unwrap();
        assert!(reply_id.starts_with("workbox-test:"), "{reply}");
        let (sender, body) = stored_message(&directory.join("remote.db"), &reply_id["workbox-test:".len()..]);
        let here_handle = desktop.store().directory_name("here").unwrap().unwrap().0;
        assert_eq!(sender, format!("{here_handle}@mac · Here"));
        assert_eq!(body, "hello back");
        assert_eq!(connections.count_messages("here", 0, 92).await, 2);
        let status = tool(&here, &here_token, json!({"name":"message_status","arguments":{"id":reply_id}})).await;
        assert!(["queued", "delivered", "failed"].contains(&status["status"].as_str().unwrap()), "{status}");
        // Reconnecting restores the latest orchestrator choice.
        connections.set_main_orchestrator(LOCAL_MACHINE, Some("here")).await.unwrap();
        remote.store().set_main_orchestrator(Some("there")).unwrap();
        connections.restore_orchestrator_choice("workbox-test").await;
        assert!(remote.store().main_orchestrator().unwrap().is_none());
        connections.set_main_orchestrator("workbox-test", Some("there")).await.unwrap();
        assert!(desktop.store().main_orchestrator().unwrap().is_none());
        remote.store().set_main_orchestrator(None).unwrap();
        connections.restore_orchestrator_choice("workbox-test").await;
        assert_eq!(remote.store().main_orchestrator().unwrap().map(|(id, _)| id).as_deref(), Some("there"));
        // A restore that runs while the owner picks a session there cannot undo the pick.
        connections.set_main_orchestrator(LOCAL_MACHINE, Some("here")).await.unwrap();
        let (chosen, ()) = tokio::join!(
            connections.set_main_orchestrator("workbox-test", Some("there")),
            connections.restore_orchestrator_choice("workbox-test"));
        chosen.unwrap();
        assert_eq!(remote.store().main_orchestrator().unwrap().map(|(id, _)| id).as_deref(), Some("there"));
        connections.client.lock().unwrap().clients.clear();
        actor.await.unwrap();
        server.abort();
        let _ = server.await;
        std::fs::remove_dir_all(directory).unwrap();
    }

    #[test]
    fn machine_pages_merge_by_time() {
        use serde_json::json;
        let message = |id: &str, at: i64| json!({"id": id, "createdAtMs": at});
        let merged = super::merge_message_pages(vec![
            ("mac".into(), vec![message("m3", 30), message("m1", 10)]),
            ("workbox-test".into(), vec![message("r2", 20), message("r1", 10)]),
        ], 3, "time");
        assert_eq!(merged.iter().map(|m| (m["id"].as_str().unwrap(), m["machine"].as_str().unwrap())).collect::<Vec<_>>(),
            [("m3", "mac"), ("r2", "workbox-test"), ("r1", "workbox-test")]);
    }

    #[tokio::test]
    async fn remote_message_relay_rejects_disconnected_target() {
        let connections = RemoteConnectionManager::from_environment(Arc::new(|_| {}), Arc::new(|_| {}), store()).unwrap();
        let error = connections.route_message(LOCAL_MACHINE, "enqueue", serde_json::json!({
            "to": "brisk-heron@workbox-test", "text": "hi", "from": "a", "sender": "calm-otter · A"})).await.unwrap_err();
        assert!(error.contains("target machine not reachable"), "{error}");
        // No saved machine can take the desktop's own name.
        assert!(validate_profile(&profile(LOCAL_MACHINE)).unwrap_err().contains("reserved"));
        // A remote machine with no desktop connected cannot reach any other machine.
        let error = Arc::new(RelayLink::default()).ask("directory".into(), serde_json::json!({})).await.unwrap_err();
        assert!(error.contains("target machine not reachable"), "{error}");
    }

    #[test]
    fn workspace_network_ops_wait_past_the_remote_git_limit() {
        for operation in ["fetch_git_repository", "pull_git_repository", "push_git_repository", "publish_git_repository", "workspace_git_clone"] {
            assert!(workspace_wait_seconds(operation) > 120, "{operation}");
        }
        assert_eq!(workspace_wait_seconds("project_git_status"), 15);
    }

    #[test]
    fn deploy_remote_backend_dev_uses_a_positional_validated_destination() {
        let preview_root = PathBuf::from(env!("CARGO_MANIFEST_DIR"))
            .parent()
            .unwrap()
            .display()
            .to_string();
        let (program, args) = remote_backend_dev_process("person@host.example").unwrap();
        assert_eq!(program, "/bin/zsh");
        assert_eq!(args, vec![
            "-lic".to_string(),
            format!("cd '{preview_root}' && exec node --experimental-strip-types scripts/deployRemoteBackendDev.ts \"$1\""),
            "zsh".to_string(),
            "person@host.example".to_string(),
        ]);
        assert!(remote_backend_dev_process("host; false").is_err());
    }

    #[test]
    fn attachment_chunks_use_base64_on_both_wire_directions() {
        let bytes: Vec<u8> = (0..ATTACHMENT_CHUNK_BYTES).map(|index| index as u8).collect();
        let encoded = BASE64.encode(&bytes);
        let request = ClientFrame::Request { id: 1, command: RemoteCommand::AttachmentChunk {
            upload_id: "upload".into(), owned_id: "owned".into(), mime_type: "image/png".into(),
            first: true, last: true, bytes: encoded.clone(),
        } };
        let response = RemoteResponse::Bytes(encoded);
        let request_json = serde_json::to_string(&request).unwrap();
        let response_json = serde_json::to_string(&response).unwrap();
        assert!(request_json.len() < 180_000);
        assert!(response_json.len() < 180_000);
        let ClientFrame::Request { command: RemoteCommand::AttachmentChunk { bytes: upload, .. }, .. } =
            serde_json::from_str(&request_json).unwrap() else { panic!("wrong upload frame"); };
        let RemoteResponse::Bytes(download) = serde_json::from_str(&response_json).unwrap() else {
            panic!("wrong download frame");
        };
        assert_eq!(BASE64.decode(upload).unwrap(), bytes);
        assert_eq!(BASE64.decode(download).unwrap(), bytes);
    }

    #[test]
    fn send_admission_receipt_crosses_the_remote_response_boundary() {
        let receipt = AgentConversationSendReceipt {
            owned_id: "owned-remote".into(),
            generation: 7,
            turn_id: "turn-admitted".into(),
            user_item_id: "user-turn-admitted".into(),
            admitted_sequence: 42,
        };
        let frame = ServerFrame::Response {
            id: 17,
            response: RemoteResponse::SendReceipt(receipt.clone()),
        };
        let json = encode_server_frame(&frame).unwrap();
        let wire: serde_json::Value = serde_json::from_str(&json).unwrap();
        assert_eq!(wire["response"]["result"], "sendReceipt");
        assert_eq!(wire["response"]["value"]["admittedSequence"], 42);
        let decoded = parse_server_frame(TungsteniteMessage::Text(json.into())).unwrap().unwrap();
        assert!(matches!(decoded, ServerFrame::Response {
            id: 17, response: RemoteResponse::SendReceipt(received),
        } if received == receipt));
    }

    #[test]
    fn desktop_markdown_remote_save_publishes_prepared_event() {
        use super::super::protocol::AgentConversationPayload;
        let store = store();
        let delivered = Arc::new(Mutex::new(Vec::new()));
        let captured = delivered.clone();
        let manager = RemoteConnectionManager::from_environment(
            Arc::new(move |event| captured.lock().unwrap().push(event)),
            Arc::new(|_| {}), store.clone(),
        ).unwrap();
        let mut event = history_event(1);
        event.payload = AgentConversationPayload::AssistantMessage {
            item_id: "a".into(), text: "**hello**".into(), completed: true, blocks: None,
        };
        manager.history_event_sink("cache-test", true)(vec![event]).unwrap();
        // Catch-up has already cached this event before its live delivery arrives.
        let mut cached = history_event(2);
        cached.payload = AgentConversationPayload::AssistantMessage {
            item_id: "cached".into(), text: "**cached**".into(), completed: true,
            blocks: Some(Vec::new()),
        };
        manager.history.lock().unwrap().live_batch("cache-test", 0, &mut [cached.clone()]).unwrap();
        if let AgentConversationPayload::AssistantMessage { blocks, .. } = &mut cached.payload {
            *blocks = None;
        }
        // Include a new suffix to exercise both skipped and written events in one batch.
        manager.history_event_sink("cache-test", true)(vec![cached.clone(), history_event(3)]).unwrap();
        manager.history_event_sink("cache-test", true)(vec![cached]).unwrap();
        let delivered = delivered.lock().unwrap();
        let rows = store.list_events(&RemoteHistory::key("cache-test", "large-history"), 2, 100).unwrap();
        let cached: AgentConversationEvent = serde_json::from_str(&rows[0].payload_json).unwrap();
        assert_eq!(delivered[1], cached);
        assert_eq!(delivered[3], cached);
        let expected = super::super::safe_markdown::parse_safe_markdown("**hello**");
        assert!(matches!(&delivered[0].payload,
            AgentConversationPayload::AssistantMessage { blocks: Some(blocks), .. } if blocks == &expected));
        let saved = store.list_events(&RemoteHistory::key("cache-test", "large-history"), 0, 100).unwrap();
        let saved: AgentConversationEvent = serde_json::from_str(&saved[0].payload_json).unwrap();
        assert_eq!(saved, delivered[0]);
    }

    #[test]
    fn history_event_sink_delivers_the_event_when_the_cache_write_fails() {
        let store = store();
        // A local session under the cache key makes the remote cache write fail.
        store.upsert_session(&mcb_core::session_store::SessionRow {
            owned_id: serde_json::to_string(&("cache-test", "large-history")).unwrap(),
            native_session_id: None, provider: "local".into(), model: None, effort: None,
            cwd: String::new(), worktree: None, branch: None, title: None, title_source: None,
            project: None, state: "idle".into(), suspended: false, created_at_ms: 0,
            project_id: None,
            last_activity_at_ms: 0, extra_json: "{}".into(),
        }).unwrap();
        let delivered = Arc::new(Mutex::new(Vec::new()));
        let captured = delivered.clone();
        let manager = RemoteConnectionManager::from_environment(
            Arc::new(move |event| captured.lock().unwrap().push(event)),
            Arc::new(|_| {}),
            store,
        ).unwrap();
        let sink = manager.history_event_sink("cache-test", true);
        let event = large_history_event(32);

        assert!(sink(vec![event.clone()]).is_ok(), "a cache write failure must not drop the connection");
        assert_eq!(*delivered.lock().unwrap(), vec![event]);
    }

    pub(super) fn history_event(sequence: i64) -> AgentConversationEvent {
        AgentConversationEvent { sequence, ..large_history_event(32) }
    }

    pub(super) fn history_snapshot(events: Vec<AgentConversationEvent>, last_sequence: i64) -> AgentConversationSnapshot {
        AgentConversationSnapshot {
            connection: AgentConversationConnection {
                owned_id: "large-history".into(), provider: AgentConversationProvider::Codex, generation: 1,
                native_session_id: None, state: super::super::protocol::ConversationConnectionState::Disconnected,
                config: Default::default(),
            },
            suspended: true, last_sequence, events, has_earlier_transcript: false,
            pending_events: Vec::new(), active_turn_id: None,
        }
    }

    pub(super) fn sequences(events: &[AgentConversationEvent]) -> Vec<i64> {
        events.iter().map(|event| event.sequence).collect()
    }

    fn connect(manager: &RemoteConnectionManager) -> mpsc::Receiver<ClientRequest> {
        let (sender, requests) = mpsc::channel(8);
        manager.client.lock().unwrap().clients.insert("cache-test".into(), RemoteClient {
            requests: Some(sender), profile: Some(profile("cache-test")), target_key: None,
            task: None, ready: Arc::new(AtomicBool::new(true)),
        });
        requests
    }

    #[tokio::test]
    async fn workspace_call_sent_with_a_request_id_is_cancelled_by_that_id() {
        let manager = RemoteConnectionManager::from_environment(Arc::new(|_| {}), Arc::new(|_| {}), store()).unwrap();
        let mut requests = connect(&manager);
        let caller = manager.clone();
        let call = tokio::spawn(async move {
            caller.workspace_operation("cache-test", "read_source_git_diff".into(), serde_json::json!({}), Some(77)).await
        });
        let ClientRequest::Execute { id, reply, .. } = requests.recv().await.unwrap() else { panic!("expected the workspace request"); };
        assert_eq!(id, 77, "the frontend's request id must be the wire id the server keys its task on");
        manager.cancel_request(77).await;
        let ClientRequest::Cancel { id } = requests.recv().await.unwrap() else { panic!("expected the cancel"); };
        assert_eq!(id, 77);
        drop(reply);
        assert!(call.await.unwrap().is_err());
    }

    #[tokio::test]
    async fn message_and_inbox_reads_are_cancelled_by_the_frontend_request_id() {
        let manager = RemoteConnectionManager::from_environment(Arc::new(|_| {}), Arc::new(|_| {}), store()).unwrap();
        let mut requests = connect(&manager);
        let caller = manager.clone();
        let messages = tokio::spawn(async move { caller.list_messages(Some("cache-test"), serde_json::json!({}), 78).await });
        let caller = manager.clone();
        let inbox = tokio::spawn(async move {
            caller.workflow_operation("cache-test", "inbox".into(), serde_json::Value::Null, Some(79)).await
        });
        let mut ids = Vec::new();
        let mut replies = Vec::new();
        for _ in 0..2 {
            let ClientRequest::Execute { id, reply, .. } = requests.recv().await.unwrap() else { panic!("expected a read"); };
            ids.push(id);
            replies.push(reply);
        }
        ids.sort();
        assert_eq!(ids, [78, 79], "each read must go out under the id its view cancels");
        manager.cancel_request(78).await;
        let ClientRequest::Cancel { id } = requests.recv().await.unwrap() else { panic!("expected the cancel"); };
        assert_eq!(id, 78);
        drop(replies);
        assert!(messages.await.unwrap().is_empty());
        assert!(inbox.await.unwrap().is_err());
    }

    #[tokio::test]
    async fn two_page_top_up_of_completed_turns_leaves_no_active_turn() {
        use super::super::protocol::{AgentConversationPayload, TurnState};
        let published = Arc::new(Mutex::new(Vec::new()));
        let captured = published.clone();
        let manager = RemoteConnectionManager::from_environment(
            Arc::new(move |event: AgentConversationEvent| captured.lock().unwrap().push(event)),
            Arc::new(|_| {}), store(),
        ).unwrap();
        manager.remember("large-history", "cache-test");
        manager.history.lock().unwrap()
            .snapshot("cache-test", 0, None, &mut history_snapshot(vec![history_event(1)], 1)).unwrap();
        let mut requests = connect(&manager);
        let turn = |sequence: i64, turn_id: &str, state: TurnState| AgentConversationEvent {
            turn_id: Some(turn_id.into()),
            payload: AgentConversationPayload::Turn { turn_id: turn_id.into(), state },
            ..history_event(sequence)
        };
        let server = tokio::spawn(async move {
            // Two turns finished while the app was closed; the first page ends
            // just after the second one started.
            let ClientRequest::Execute { command, reply, .. } = requests.recv().await.unwrap() else { panic!("expected top-up"); };
            assert!(matches!(command, RemoteCommand::Snapshot { after_sequence: Some(1), .. }));
            reply.send(Ok(RemoteResponse::Snapshot(Some(history_snapshot(vec![
                turn(2, "a", TurnState::Started), history_event(3), turn(4, "a", TurnState::Completed),
                turn(5, "b", TurnState::Started),
            ], 6))))).unwrap();
            let ClientRequest::Execute { command, reply, .. } = requests.recv().await.unwrap() else { panic!("expected second page"); };
            assert!(matches!(command, RemoteCommand::Snapshot { after_sequence: Some(5), .. }));
            reply.send(Ok(RemoteResponse::Snapshot(Some(history_snapshot(vec![turn(6, "b", TurnState::Completed)], 6))))).unwrap();
        });
        manager.save_newer("cache-test", "large-history", 1, true, None).await.unwrap();
        server.await.unwrap();

        // The window's event stream is bounded and drops frames under load, so
        // a replayed turn start can arrive without its end and leave the
        // session working. Finished turns stay in the Mac's copy; only the
        // newest event tells the open transcript to read it.
        let published = published.lock().unwrap();
        assert!(!published.iter().any(|event| matches!(event.payload,
            AgentConversationPayload::Turn { state: TurnState::Started, .. })), "a finished turn must not start again");
        assert_eq!(sequences(&published), vec![6]);
    }

    #[tokio::test]
    async fn empty_import_at_confirmed_start_keeps_history_local() {
        let manager = RemoteConnectionManager::from_environment(Arc::new(|_| {}), Arc::new(|_| {}), store()).unwrap();
        let (sender, mut requests) = mpsc::channel(8);
        manager.client.lock().unwrap().clients.insert("cache-test".into(), RemoteClient {
            requests: Some(sender), profile: Some(profile("cache-test")), target_key: None,
            task: None, ready: Arc::new(AtomicBool::new(true)),
        });
        manager.remember("large-history", "cache-test");
        let event = large_history_event(32);
        {
            let history = manager.history.lock().unwrap();
            let epoch = history.epoch("cache-test");
            history.snapshot("cache-test", epoch, None, &mut AgentConversationSnapshot {
                connection: AgentConversationConnection {
                    owned_id: event.owned_id.clone(), provider: event.provider, generation: 1,
                    native_session_id: None, state: super::super::protocol::ConversationConnectionState::Disconnected,
                    config: Default::default(),
                },
                suspended: true, last_sequence: event.sequence, events: vec![event.clone()],
                has_earlier_transcript: true,
                pending_events: Vec::new(), active_turn_id: None,
            }).unwrap();
            history.page("cache-test", epoch, &event.owned_id, event.sequence, true,
                &mut AgentConversationEventPage { events: vec![], has_more: false }).unwrap();
        }
        let server = tokio::spawn(async move {
            let ClientRequest::Execute { command, reply, .. } = requests.recv().await.unwrap() else { panic!("expected import request"); };
            assert!(matches!(command, RemoteCommand::ExtendImport { .. }));
            reply.send(Ok(RemoteResponse::ImportProgress { added: 0, reached_start: true })).unwrap();
            requests
        });
        let progress = manager.extend_import(event.owned_id.clone()).await.unwrap();
        assert_eq!(progress.added, 0);
        assert!(progress.reached_start);
        let mut requests = server.await.unwrap();
        let page = manager.events_before(event.owned_id, event.sequence, 1024).await.unwrap();
        assert!(page.events.is_empty());
        assert!(!page.has_more);
        assert!(requests.try_recv().is_err(), "confirmed start must not request the workbox again");
    }

    pub(super) fn large_history_event(bytes: usize) -> AgentConversationEvent {
        AgentConversationEvent {
            runtime_state: None,
            owned_id: "large-history".into(),
            provider: AgentConversationProvider::Codex,
            generation: 1,
            sequence: 42,
            timestamp_ms: 1,
            turn_id: None,
            payload: super::super::protocol::AgentConversationPayload::AssistantDelta {
                item_id: "answer".into(), delta: "x".repeat(bytes),
            },
        }
    }

    #[tokio::test]
    async fn large_history_response_and_live_event_cross_the_socket_intact() {
        let event = large_history_event(MAX_WIRE_FRAME_BYTES + 4096);
        let page = ServerFrame::Response { id: 7, response: RemoteResponse::EventPage(
            AgentConversationEventPage { events: vec![event.clone()], has_more: true },
        ) };
        let live = ServerFrame::Event { event: event.clone() };
        let listener = tokio::net::TcpListener::bind("127.0.0.1:0").await.unwrap();
        let address = listener.local_addr().unwrap();
        let server = tokio::spawn(async move {
            let (stream, _) = listener.accept().await.unwrap();
            let mut socket = tokio_tungstenite::accept_async(stream).await.unwrap();
            for frame in [page, live] {
                let json = encode_server_frame(&frame).unwrap();
                assert!(json.len() > MAX_WIRE_FRAME_BYTES);
                socket.send(TungsteniteMessage::Text(json.into())).await.unwrap();
            }
        });
        let (mut client, _) = tokio_tungstenite::connect_async(format!("ws://{address}")).await.unwrap();
        let request = ClientFrame::Request { id: 8, command: RemoteCommand::Workspace {
            operation: "test".into(), args: serde_json::Value::String("x".repeat(MAX_WIRE_FRAME_BYTES)),
        } };
        assert!(send_client_frame(&mut client, &request).await.unwrap_err().contains("one MiB"));
        let next = |message| parse_server_frame(message).unwrap().unwrap();
        let response = tokio::time::timeout(Duration::from_secs(2), client.next()).await.unwrap().unwrap().unwrap();
        match next(response) {
            ServerFrame::Response { id: 7, response: RemoteResponse::EventPage(page) } => {
                assert_eq!(page.events, vec![event.clone()]);
                assert!(page.has_more);
            }
            other => panic!("Unexpected response: {other:?}"),
        }
        let response = tokio::time::timeout(Duration::from_secs(2), client.next()).await.unwrap().unwrap().unwrap();
        assert!(matches!(next(response), ServerFrame::Event { event: received } if received == event));
        server.await.unwrap();
    }

    #[test]
    fn oversized_history_returns_a_request_error_instead_of_disappearing() {
        let event = large_history_event(MAX_SERVER_FRAME_BYTES);
        let response = ServerFrame::Response { id: 9, response: RemoteResponse::EventPage(
            AgentConversationEventPage { events: vec![event.clone()], has_more: false },
        ) };
        let json = encode_server_frame(&response).unwrap();
        assert!(json.len() < MAX_WIRE_FRAME_BYTES);
        assert!(matches!(serde_json::from_str::<ServerFrame>(&json).unwrap(),
            ServerFrame::Error { id: 9, message } if message.contains("16 MiB")));
        assert!(encode_server_frame(&ServerFrame::Event { event }).is_err());
    }

    #[test]
    fn attachment_upload_is_single_flight_and_cleans_only_its_owner() {
        let directory = std::env::temp_dir().join(format!("assembly-attachment-test-{}", uuid::Uuid::new_v4()));
        std::fs::create_dir(&directory).unwrap();
        let upload = Mutex::new(None);
        let first = uuid::Uuid::new_v4().to_string();
        let second = uuid::Uuid::new_v4().to_string();
        let chunk = |id: &str, start, end, data: Vec<u8>| {
            write_attachment_chunk(&directory, &upload, id.into(), "owned-a".into(), "image/png".into(), start, end, data)
        };
        assert!(chunk(&first, true, false, vec![1]).unwrap().is_none());
        let path = directory.join(format!(".attachment-upload-{first}"));
        assert!(chunk(&second, true, true, vec![2]).unwrap_err().contains("Another attachment"));
        abort_attachment_upload(&upload, &second);
        assert!(path.exists());
        let finished = chunk(&first, false, true, vec![3]).unwrap().unwrap();
        assert_eq!(std::fs::read(&path).unwrap(), vec![1, 3]);
        drop(finished);
        assert!(!path.exists());

        assert!(chunk(&first, true, false, vec![4]).unwrap().is_none());
        assert!(chunk(&first, false, false, Vec::new()).is_err());
        assert!(!path.exists());

        assert!(chunk(&first, true, false, vec![5]).unwrap().is_none());
        assert!(path.exists());
        let partial = upload.lock().unwrap().take().unwrap();
        drop(partial);
        assert!(!path.exists());

        assert!(chunk(&first, true, false, vec![6]).unwrap().is_none());
        let wrong_owner = write_attachment_chunk(&directory, &upload, first.clone(),
            "owned-b".into(), "image/png".into(), false, true, vec![7]).unwrap_err();
        assert!(wrong_owner.contains("invalid"));
        assert!(!path.exists());
        assert!(upload.lock().unwrap().is_none());
        assert!(chunk(&second, true, false, vec![8]).unwrap().is_none());
        let second_path = directory.join(format!(".attachment-upload-{second}"));
        let finished = chunk(&second, false, true, vec![9]).unwrap().unwrap();
        assert_eq!(std::fs::read(&second_path).unwrap(), vec![8, 9]);
        drop(finished);
        assert!(!second_path.exists());
        std::fs::remove_dir(&directory).unwrap();
    }

    fn client_history() -> (RemoteConnectionManager, String) {
        (RemoteConnectionManager::from_environment(Arc::new(|_| {}), Arc::new(|_| {}), store()).unwrap(), "test".into())
    }

    pub(super) fn store() -> Arc<SessionStore> {
        Arc::new(SessionStore::open_in_memory().unwrap())
    }

    fn profile(id: &str) -> RemoteAssemblyProfile {
        RemoteAssemblyProfile { id: id.into(), name: "Workbox".into(), ssh_target: "agent-workbox".into(), source_root: String::new(), default_cwd: String::new() }
    }

    pub(super) fn session(owned_id: &str, profile_id: &str, last_activity_at_ms: i64) -> AgentConversationSessionRecord {
        AgentConversationSessionRecord {
            owned_id: owned_id.into(), can_change_checkout: true,
            execution_environment: ExecutionEnvironment::Remote,
            remote_profile_id: Some(profile_id.into()),
            provider: super::super::protocol::AgentConversationProvider::Codex,
            model: Some("gpt-test".into()),
            effort: None,
            cwd: "/work/project".into(),
            state: super::super::protocol::AgentRuntimeState::Suspended,
            suspended: true,
            created_at_ms: 1,
            last_activity_at_ms,
            active_turn_id: None,
            pending_permission: false,
            pending_input: false,
            background_work: vec![BackgroundWorkItem {
                id: "background-task:remote-1".into(),
                kind: BackgroundWorkKind::Command,
                label: "Remote command".into(),
                started_at_ms: 2,
            }],
            native_session_id: Some(format!("native-{owned_id}")),
            project_id: None,
            handle: None,
            project_group_key: String::new(),
            project_group_label: String::new(),
            orchestrator_marked_at_ms: None,
            helper_of: None,
            run_id: None,
            run_label: None,
            run_lead: None, numbered_after: None,
            meta: super::super::protocol::AgentConversationSessionMeta {
                title: Some(format!("Session {owned_id}")),
                ..Default::default()
            },
        }
    }

    #[test]
    fn cached_remote_rows_survive_restart_and_retain_routing() {
        let shared = store();
        let manager = RemoteConnectionManager::from_environment(
            Arc::new(|_| {}),
            Arc::new(|_| {}),
            shared.clone(),
        )
        .unwrap();
        manager
            .replace_cached_profile_sessions("workbox", &[session("remote-1", "workbox", 10)])
            .unwrap();

        let restarted = RemoteConnectionManager::from_environment(
            Arc::new(|_| {}),
            Arc::new(|_| {}),
            shared,
        )
        .unwrap();
        assert_eq!(restarted.cached_sessions(), vec![session("remote-1", "workbox", 10)]);
        assert!(restarted.owns("remote-1"));
        assert_eq!(restarted.connection_state("workbox"), RemoteConnectionState::Disconnected);
    }

    #[test]
    fn remote_history_offline_restart_uses_live_controls_after_replay() {
        use super::super::protocol::{AgentConversationPayload, AgentRuntimeState, ApprovalState, TurnState};
        let shared = store();
        let manager = RemoteConnectionManager::from_environment(
            Arc::new(|_| {}), Arc::new(|_| {}), shared.clone()).unwrap();
        let mut listed = session("large-history", "workbox", 90);
        listed.state = AgentRuntimeState::WaitingApproval;
        listed.suspended = false;
        listed.active_turn_id = Some("turn".into());
        listed.pending_permission = true;
        manager.replace_cached_profile_sessions("workbox", &[listed]).unwrap();
        let mut requested = history_event(90);
        requested.payload = AgentConversationPayload::Approval {
            request_id: "permission".into(), state: ApprovalState::Requested, summary: "Run command".into(), options: Vec::new(),
        };
        let mut replay = history_snapshot(vec![requested.clone()], 90);
        replay.suspended = false;
        replay.active_turn_id = Some("turn".into());
        replay.pending_events = vec![requested.clone()];
        manager.complete_replay("workbox", replay, Some(requested), None).unwrap();
        let mut accepted = history_event(91);
        accepted.payload = AgentConversationPayload::Approval {
            request_id: "permission".into(), state: ApprovalState::Accepted, summary: "Run command".into(), options: Vec::new(),
        };
        let mut completed = history_event(92);
        completed.timestamp_ms = 100;
        completed.runtime_state = Some(AgentRuntimeState::Ready);
        completed.payload = AgentConversationPayload::Turn { turn_id: "turn".into(), state: TurnState::Completed };
        manager.history_event_sink("workbox", true)(vec![accepted, completed]).unwrap();
        let restarted = RemoteConnectionManager::from_environment(
            Arc::new(|_| {}), Arc::new(|_| {}), shared).unwrap();
        let selected = restarted.history.lock().unwrap()
            .read_selection_snapshot("workbox", "large-history", 1024).unwrap().unwrap();
        assert!(selected.active_turn_id.is_none());
        assert!(selected.pending_events.is_empty());
        let row = restarted.cached_sessions().remove(0);
        assert_eq!(row.active_turn_id, None);
        assert!(!row.pending_permission);
        assert!(!row.pending_input);
        assert_eq!(row.state, AgentRuntimeState::Ready);
    }

    #[test]
    fn remote_history_offline_restart_uses_snapshot_without_saved_head() {
        let shared = store();
        let manager = RemoteConnectionManager::from_environment(
            Arc::new(|_| {}), Arc::new(|_| {}), shared.clone()).unwrap();
        let mut listed = session("large-history", "workbox", 900);
        listed.state = super::super::protocol::AgentRuntimeState::Working;
        listed.suspended = false;
        listed.active_turn_id = Some("old-turn".into());
        manager.replace_cached_profile_sessions("workbox", &[listed]).unwrap();
        manager.history.lock().unwrap().snapshot("workbox", 0, None,
            &mut history_snapshot(vec![], 92)).unwrap();
        let restarted = RemoteConnectionManager::from_environment(
            Arc::new(|_| {}), Arc::new(|_| {}), shared).unwrap();
        let row = restarted.cached_sessions().remove(0);
        assert_eq!(row.active_turn_id, None);
        assert!(row.suspended);
        assert_eq!(row.state, super::super::protocol::AgentRuntimeState::Suspended);
    }

    #[test]
    fn remote_history_offline_restart_uses_listing_before_history() {
        let shared = store();
        let manager = RemoteConnectionManager::from_environment(
            Arc::new(|_| {}), Arc::new(|_| {}), shared.clone()).unwrap();
        let mut listed = session("large-history", "workbox", 90);
        listed.state = super::super::protocol::AgentRuntimeState::WaitingInput;
        listed.suspended = false;
        listed.active_turn_id = Some("turn".into());
        listed.pending_input = true;
        manager.replace_cached_profile_sessions("workbox", &[listed.clone()]).unwrap();
        let saved = shared.get_session(&RemoteHistory::key("workbox", "large-history")).unwrap().unwrap();
        let controls: serde_json::Value = serde_json::from_str(&saved.extra_json).unwrap();
        assert_eq!(controls["runtimeState"], "waiting-input");
        let restarted = RemoteConnectionManager::from_environment(
            Arc::new(|_| {}), Arc::new(|_| {}), shared).unwrap();
        let row = restarted.cached_sessions().remove(0);
        assert_eq!(row.state, listed.state);
        assert_eq!(row.active_turn_id, listed.active_turn_id);
        assert!(row.pending_input);
        assert!(!row.suspended);
    }

    #[test]
    fn remote_history_offline_restart_uses_the_newest_listing_before_history() {
        let shared = store();
        let manager = RemoteConnectionManager::from_environment(
            Arc::new(|_| {}), Arc::new(|_| {}), shared.clone()).unwrap();
        let mut first = session("large-history", "workbox", 90);
        first.state = super::super::protocol::AgentRuntimeState::WaitingInput;
        first.suspended = false;
        first.active_turn_id = Some("turn".into());
        first.pending_input = true;
        manager.replace_cached_profile_sessions("workbox", &[first]).unwrap();
        let mut second = session("large-history", "workbox", 91);
        second.state = super::super::protocol::AgentRuntimeState::Ready;
        second.suspended = false;
        second.active_turn_id = None;
        second.pending_input = false;
        manager.replace_cached_profile_sessions("workbox", &[second.clone()]).unwrap();
        let restarted = RemoteConnectionManager::from_environment(
            Arc::new(|_| {}), Arc::new(|_| {}), shared).unwrap();
        let row = restarted.cached_sessions().remove(0);
        assert_eq!(row.state, second.state);
        assert_eq!(row.active_turn_id, None);
        assert!(!row.pending_input);
    }

    #[test]
    fn remote_history_offline_restart_keeps_activity_saved_before_the_upgrade() {
        let shared = store();
        let mut listed = session("large-history", "workbox", 90);
        listed.state = super::super::protocol::AgentRuntimeState::WaitingApproval;
        listed.suspended = false;
        listed.active_turn_id = Some("turn".into());
        listed.pending_permission = true;
        // The earlier format: activity in the saved list, none in the history controls.
        shared.upsert_app_setting(REMOTE_SESSION_PROJECTIONS_SETTING_KEY,
            &serde_json::to_string(&[listed.clone()]).unwrap()).unwrap();
        shared.file_remote_sessions("workbox", &serde_json::json!([[
            super::history::RemoteHistory::key("workbox", "large-history"),
            listed.project_id, 90,
            {"remoteProfileId": "workbox", "suspended": false, "activeTurnId": "turn"},
        ]]).to_string()).unwrap();
        let restarted = RemoteConnectionManager::from_environment(
            Arc::new(|_| {}), Arc::new(|_| {}), shared).unwrap();
        let row = restarted.cached_sessions().remove(0);
        assert_eq!(row.state, super::super::protocol::AgentRuntimeState::WaitingApproval);
        assert!(row.pending_permission);
        assert_eq!(row.active_turn_id.as_deref(), Some("turn"));
    }

    #[test]
    fn remote_history_offline_restart_keeps_a_failed_session_failed() {
        let shared = store();
        let manager = RemoteConnectionManager::from_environment(
            Arc::new(|_| {}), Arc::new(|_| {}), shared.clone()).unwrap();
        let mut listed = session("large-history", "workbox", 90);
        listed.state = super::super::protocol::AgentRuntimeState::Working;
        listed.suspended = false;
        manager.replace_cached_profile_sessions("workbox", &[listed]).unwrap();
        let mut failed = history_snapshot(vec![], 90);
        failed.connection.state = super::super::protocol::ConversationConnectionState::Failed;
        failed.suspended = false;
        manager.history.lock().unwrap().snapshot("workbox", 0, None, &mut failed).unwrap();
        let restarted = RemoteConnectionManager::from_environment(
            Arc::new(|_| {}), Arc::new(|_| {}), shared).unwrap();
        assert_eq!(restarted.cached_sessions().remove(0).state, super::super::protocol::AgentRuntimeState::Failed);
    }

    #[test]
    fn remote_history_offline_restart_skips_one_unreadable_session() {
        let shared = store();
        let manager = RemoteConnectionManager::from_environment(
            Arc::new(|_| {}), Arc::new(|_| {}), shared.clone()).unwrap();
        manager.replace_cached_profile_sessions("workbox", &[
            session("broken", "workbox", 90), session("intact", "workbox", 80),
        ]).unwrap();
        let mut row = shared.get_session(&RemoteHistory::key("workbox", "broken")).unwrap().unwrap();
        row.extra_json = "not json".into();
        shared.upsert_session(&row).unwrap();
        let restarted = RemoteConnectionManager::from_environment(
            Arc::new(|_| {}), Arc::new(|_| {}), shared).unwrap();
        let listed: Vec<_> = restarted.cached_sessions().into_iter().map(|session| session.owned_id).collect();
        assert_eq!(listed, ["intact"]);
    }

    #[test]
    fn listing_files_the_remote_row_under_its_project_before_any_event() {
        let shared = store();
        let manager = RemoteConnectionManager::from_environment(
            Arc::new(|_| {}),
            Arc::new(|_| {}),
            shared.clone(),
        )
        .unwrap();
        let mut listed = session("large-history", "workbox", 10);
        listed.project_id = Some("p1".into());
        manager.replace_cached_profile_sessions("workbox", &[listed]).unwrap();

        let key = RemoteHistory::key("workbox", "large-history");
        let row = shared.get_session(&key).unwrap().unwrap();
        assert_eq!((row.project_id.as_deref(), row.last_activity_at_ms), (Some("p1"), 10));
        assert!(manager.history.lock().unwrap().read_selection_snapshot("workbox", "large-history", 1024).unwrap().is_none(),
            "an event-less row still opens from the server");

        manager.history_event_sink("workbox", true)(vec![history_event(5)]).unwrap();
        assert_eq!(shared.get_session(&key).unwrap().unwrap().project_id.as_deref(), Some("p1"), "live events keep the project");
    }

    #[tokio::test]
    async fn removing_a_remote_project_deletes_its_sessions_on_the_machine() {
        let directory = std::env::temp_dir().join(format!("assembly-remove-project-{}", uuid::Uuid::new_v4()));
        std::fs::create_dir(&directory).unwrap();
        let database = directory.join("sessions.db");
        let manager = AgentRuntimeManager::open(ProviderRegistry::default(), &database).unwrap();
        let remote = RemoteConnectionManager::from_environment(
            Arc::new(|_| {}), Arc::new(|_| {}), Arc::new(SessionStore::open(&database).unwrap()),
        ).unwrap();
        manager.store().insert_or_get_project(&mcb_core::session_store::ProjectRow {
            id: "p".into(), machine: "cache-test".into(), root_path: "/work/project".into(), title: "project".into(),
            repo_key: String::new(), created_at_ms: 1, group_key: String::new(), pinned_at_ms: None, last_used_ms: None,
        }).unwrap();
        let mut listed = session("large-history", "cache-test", 10);
        listed.project_id = Some("p".into());
        remote.replace_cached_profile_sessions("cache-test", &[listed]).unwrap();
        let mut requests = connect(&remote);
        let server = tokio::spawn(async move {
            let ClientRequest::Execute { command, reply, .. } = requests.recv().await.unwrap() else { panic!("expected delete"); };
            assert!(matches!(command, RemoteCommand::Delete { ref owned_id } if owned_id == "large-history"));
            reply.send(Ok(RemoteResponse::Bool(true))).unwrap();
        });

        super::super::remove_project_on(&manager, &remote, "p", true).await.unwrap();
        server.await.unwrap();
        assert!(manager.store().get_session(&RemoteHistory::key("cache-test", "large-history")).unwrap().is_none(), "the cached row is gone");
        assert!(!remote.owns("large-history"));
        assert_eq!(manager.store().get_project("p").unwrap(), None);
        drop((manager, remote));
        std::fs::remove_dir_all(directory).unwrap();
    }

    #[tokio::test]
    async fn a_new_orchestrator_choice_clears_an_offline_machines_cached_mark() {
        let shared = store();
        let manager = RemoteConnectionManager::from_environment(Arc::new(|_| {}), Arc::new(|_| {}), shared.clone()).unwrap();
        let mut marked = session("remote-1", "workbox", 10);
        marked.orchestrator_marked_at_ms = Some(5);
        manager.replace_cached_profile_sessions("workbox", &[marked, session("remote-2", "workbox", 9)]).unwrap();
        // The owner clears the mark while that machine is away; a restart must not bring it back.
        manager.set_main_orchestrator("", None).await.unwrap();
        let restarted = RemoteConnectionManager::from_environment(Arc::new(|_| {}), Arc::new(|_| {}), shared).unwrap();
        assert!(restarted.cached_sessions().iter().all(|row| row.orchestrator_marked_at_ms.is_none()));
    }

    #[tokio::test]
    async fn a_chosen_remote_orchestrator_stays_marked_in_the_offline_cache() {
        let shared = store();
        let manager = RemoteConnectionManager::from_environment(Arc::new(|_| {}), Arc::new(|_| {}), shared.clone()).unwrap();
        let mut marked = session("remote-1", "workbox", 10);
        marked.orchestrator_marked_at_ms = Some(5);
        manager.replace_cached_profile_sessions("workbox", &[marked, session("remote-2", "workbox", 9)]).unwrap();
        // The owner picks a session on a machine that is away; it is marked there when it connects.
        manager.set_main_orchestrator("workbox", Some("remote-2")).await.unwrap();
        // After a restart, with that machine still away, the rail shows the new choice.
        let restarted = RemoteConnectionManager::from_environment(Arc::new(|_| {}), Arc::new(|_| {}), shared).unwrap();
        let marked: Vec<_> = restarted.cached_sessions().into_iter()
            .filter(|row| row.orchestrator_marked_at_ms.is_some()).map(|row| row.owned_id).collect();
        assert_eq!(marked, ["remote-2"]);
    }

    #[test]
    fn successful_profile_refresh_reconciles_only_that_machines_rows() {
        let manager = RemoteConnectionManager::from_environment(
            Arc::new(|_| {}),
            Arc::new(|_| {}),
            store(),
        )
        .unwrap();
        manager
            .replace_cached_profile_sessions("one", &[session("old", "one", 1)])
            .unwrap();
        manager
            .replace_cached_profile_sessions("two", &[session("keep", "two", 2)])
            .unwrap();
        manager
            .replace_cached_profile_sessions("one", &[session("new", "one", 3)])
            .unwrap();

        assert_eq!(
            manager
                .cached_sessions()
                .into_iter()
                .map(|session| session.owned_id)
                .collect::<Vec<_>>(),
            vec!["new", "keep"]
        );
        assert!(!manager.owns("old"));
        assert!(manager.owns("new"));
        assert!(manager.owns("keep"));

        manager.replace_cached_profile_sessions("one", &[]).unwrap();
        assert_eq!(
            manager
                .cached_sessions()
                .into_iter()
                .map(|session| session.owned_id)
                .collect::<Vec<_>>(),
            vec!["keep"]
        );
        assert!(!manager.owns("new"));
        assert!(manager.owns("keep"));
    }

    #[test]
    fn legacy_cached_rows_move_to_the_current_profile_before_reconciliation() {
        let manager = RemoteConnectionManager::from_environment(
            Arc::new(|_| {}), Arc::new(|_| {}), store(),
        ).unwrap();
        manager.replace_cached_profile_sessions(
            "agent-workbox", &[session("legacy", "agent-workbox", 2)],
        ).unwrap();
        manager.replace_cached_profile_sessions(
            "other", &[session("keep", "other", 1)],
        ).unwrap();

        manager.reassign_cached_profile_sessions("agent-workbox", "current").unwrap();

        let sessions = manager.cached_sessions();
        assert_eq!(sessions[0].remote_profile_id.as_deref(), Some("current"));
        assert_eq!(sessions[1].remote_profile_id.as_deref(), Some("other"));
        assert_eq!(manager.profile_for_owned_id("legacy").unwrap(), "current");

        manager.replace_cached_profile_sessions("current", &[]).unwrap();

        let sessions = manager.cached_sessions();
        assert_eq!(sessions.len(), 1);
        assert_eq!(sessions[0].owned_id, "keep");
        assert!(!manager.owns("legacy"));
        assert!(manager.owns("keep"));
    }

    #[tokio::test]
    async fn disconnect_keeps_profile_and_session_ownership_but_stops_transport() {
        let manager = RemoteConnectionManager::from_environment(Arc::new(|_| {}), Arc::new(|_| {}), store()).unwrap();
        manager.register_profile(profile("saved")).unwrap();
        manager.remember("conversation", "saved");
        let task = tokio::spawn(std::future::pending::<()>());
        let (sender, _receiver) = mpsc::channel(1);
        {
            let mut state = manager.client.lock().unwrap();
            let client = state.clients.get_mut("saved").unwrap();
            client.requests = Some(sender);
            client.task = Some(task.abort_handle());
            client.ready.store(true, Ordering::Release);
        }
        assert_eq!(manager.environment().ready_profile_ids, vec!["saved"]);
        manager.disconnect_profile("saved").unwrap();
        assert!(task.await.unwrap_err().is_cancelled());
        assert_eq!(manager.environment().profiles, vec![profile("saved")]);
        assert!(manager.environment().ready_profile_ids.is_empty());
        assert!(manager.owns("conversation"));
        assert!(manager.request_for_profile("saved", RemoteCommand::ListSessions).await.unwrap_err().contains("not connected"));
        let restarted = RemoteConnectionManager::from_environment(Arc::new(|_| {}), Arc::new(|_| {}), store()).unwrap();
        restarted.restore_profiles(&serde_json::to_string(&manager.environment().profiles).unwrap()).unwrap();
        assert_eq!(restarted.environment().profiles, vec![profile("saved")]);
        assert!(restarted.environment().ready_profile_ids.is_empty());
    }

    #[tokio::test]
    async fn restored_profiles_are_dormant_and_cancellation_releases_attempt() {
        let manager = RemoteConnectionManager::from_environment(Arc::new(|_| {}), Arc::new(|_| {}), store()).unwrap();
        manager.restore_profiles(&serde_json::to_string(&vec![profile("original")]).unwrap()).unwrap();
        assert!(!manager.is_configured());
        assert!(manager.environment().ready_profile_ids.is_empty());
        assert!(manager.tunnel_processes.lock().unwrap().is_empty());
        let guard = manager.connection_lock.lock().await;
        let owner = manager.clone();
        let (registered, registration) = oneshot::channel();
        let registered = Mutex::new(Some(registered));
        let task = tokio::spawn(async move {
            owner.connect_profile(profile("original"), "cancel-test".into(), tauri::ipc::Channel::new(move |_| {
                if let Some(reply) = registered.lock().unwrap().take() { let _ = reply.send(()); }
                Ok(())
            })).await
        });
        registration.await.unwrap();
        manager.cancel_connection("cancel-test");
        assert_eq!(task.await.unwrap().err().unwrap(), "Connection cancelled");
        drop(guard);
        assert!(manager.attempts.lock().unwrap().is_empty());
        assert!(manager.tunnel_processes.lock().unwrap().is_empty());
        assert_eq!(manager.environment().profiles[0].id, "original");
    }

    #[tokio::test]
    async fn dropping_candidate_reaps_its_tunnel() {
        let processes = Arc::new(Mutex::new(HashMap::new()));
        let child = Command::new("sleep").arg("30").spawn().unwrap();
        let pid = child.id();
        processes.lock().unwrap().insert(pid, child);
        let tunnel = RemoteTunnel { pid, processes: processes.clone(), local_port: 0, token: String::new() };
        let (started, running) = oneshot::channel();
        let task = tokio::spawn(async move {
            let _tunnel = tunnel;
            let _ = started.send(());
            std::future::pending::<()>().await;
        });
        running.await.unwrap();
        let candidate = RemoteClient { requests: None, profile: None, target_key: None,
            task: Some(task.abort_handle()), ready: Arc::new(AtomicBool::new(false)) };
        drop(candidate);
        assert!(task.await.unwrap_err().is_cancelled());
        assert!(processes.lock().unwrap().is_empty());
    }

    #[tokio::test]
    async fn connection_timeout_releases_attempt() {
        let manager = RemoteConnectionManager::from_environment(Arc::new(|_| {}), Arc::new(|_| {}), store()).unwrap();
        let _guard = manager.connection_lock.lock().await;
        let result = manager.connect_profile(profile("timeout"), "timeout-test".into(), tauri::ipc::Channel::new(|_| Ok(()))).await;
        assert_eq!(result.err().unwrap(), "Connection timed out after 30 seconds");
        assert!(manager.attempts.lock().unwrap().is_empty());
        assert!(manager.tunnel_processes.lock().unwrap().is_empty());
    }

    #[test]
    fn timestamp_payloads_decode_in_snapshots_pages_and_live_events() {
        let event = serde_json::json!({"ownedId":"test", "provider":"codex", "generation":1,
            "sequence":7, "timestampMs":1800000000000_u64,
            "payload":{"kind":"connection", "state":"connected"}});
        let snapshot = serde_json::json!({"connection":{"ownedId":"test", "provider":"codex",
            "generation":1, "state":"connected", "config":{}}, "suspended":true,
            "lastSequence":7, "events":[event.clone()], "hasEarlierTranscript":false,
            "pendingEvents":[], "activeTurnId":null});
        for frame in [serde_json::json!({"type":"event", "event":event.clone()}),
            serde_json::json!({"type":"response", "id":1, "response":{"result":"snapshot", "value":snapshot}}),
            serde_json::json!({"type":"response", "id":2, "response":{"result":"eventPage", "value":{"events":[event], "hasMore":false}}})] {
            assert!(parse_server_frame(TungsteniteMessage::Text(frame.to_string().into())).unwrap().is_some());
        }
        assert!(parse_server_frame(TungsteniteMessage::Text("{invalid".into())).unwrap_err().contains("incompatible"));
    }

    #[tokio::test]
    async fn connection_state_reports_connected_reconnecting_and_disconnected() {
        let reports: Arc<Mutex<Vec<RemoteConnectionStatus>>> = Arc::new(Mutex::new(Vec::new()));
        let sink = reports.clone();
        let manager = RemoteConnectionManager::from_environment(
            Arc::new(|_| {}),
            Arc::new(move |status| sink.lock().unwrap().push(status)),
            store(),
        )
        .unwrap();
        manager.register_profile(profile("saved")).unwrap();
        // A saved machine nobody connected is disconnected, not reconnecting.
        assert_eq!(manager.connection_state("saved"), RemoteConnectionState::Disconnected);
        assert_eq!(manager.connection_state("never-saved"), RemoteConnectionState::Disconnected);

        let (sender, _receiver) = mpsc::channel(1);
        {
            let mut state = manager.client.lock().unwrap();
            let client = state.clients.get_mut("saved").unwrap();
            client.requests = Some(sender);
        }
        // A live client that has not answered the readiness list is attempting.
        assert_eq!(manager.connection_state("saved"), RemoteConnectionState::Reconnecting);
        manager.client.lock().unwrap().clients["saved"].ready.store(true, Ordering::Release);
        assert_eq!(manager.connection_state("saved"), RemoteConnectionState::Connected);

        manager.disconnect_profile("saved").unwrap();
        assert_eq!(manager.connection_state("saved"), RemoteConnectionState::Disconnected);
        {
            let attempt = ConnectingAttempt::start(&manager, "saved");
            assert_eq!(manager.connection_state("saved"), RemoteConnectionState::Reconnecting);
            drop(attempt);
        }
        assert_eq!(manager.connection_state("saved"), RemoteConnectionState::Disconnected);
        let sent = reports.lock().unwrap().clone();
        assert_eq!(
            sent,
            vec![
                RemoteConnectionStatus { profile_id: "saved".into(), state: RemoteConnectionState::Disconnected },
                RemoteConnectionStatus { profile_id: "saved".into(), state: RemoteConnectionState::Reconnecting },
                RemoteConnectionStatus { profile_id: "saved".into(), state: RemoteConnectionState::Disconnected },
            ]
        );
    }

    #[tokio::test]
    async fn removing_a_profile_mid_attempt_disconnects_and_refuses_the_stale_client() {
        let reports: Arc<Mutex<Vec<RemoteConnectionStatus>>> = Arc::new(Mutex::new(Vec::new()));
        let sink = reports.clone();
        let manager = RemoteConnectionManager::from_environment(
            Arc::new(|_| {}),
            Arc::new(move |status| sink.lock().unwrap().push(status)),
            store(),
        )
        .unwrap();
        manager.register_profile(profile("saved")).unwrap();
        manager.history.lock().unwrap().snapshot(
            "saved", 0, None, &mut history_snapshot(vec![history_event(1)], 1),
        ).unwrap();
        let stale_history_sink = manager.history_event_sink("saved", true);
        let attempt = ConnectingAttempt::start(&manager, "saved");
        assert_eq!(manager.connection_state("saved"), RemoteConnectionState::Reconnecting);

        // Removal during the attempt answers disconnected immediately.
        manager.remove_profile("saved").unwrap();
        assert_eq!(manager.connection_state("saved"), RemoteConnectionState::Disconnected);
        let history = manager.history.lock().unwrap();
        let epoch = history.epoch("saved");
        assert_eq!(sequences(&history.read_snapshot("saved", epoch, "large-history").unwrap().events), vec![1]);
        drop(history);
        assert!(stale_history_sink(vec![history_event(2)]).is_err());

        // The pre-removal attempt finishes late: its client is refused, its own
        // actor is aborted, and the removed profile is not resurrected.
        let stale_actor = tokio::spawn(std::future::pending::<()>());
        let (stale_sender, _stale_receiver) = mpsc::channel(1);
        let stale = RemoteClient {
            requests: Some(stale_sender), profile: Some(profile("saved")),
            target_key: Some("owner@box:22".into()), task: Some(stale_actor.abort_handle()),
            ready: Arc::new(AtomicBool::new(true)),
        };
        let refused = manager.adopt_client(&attempt, Some(stale)).unwrap_err();
        assert!(refused.contains("was removed during the connection attempt"), "{refused}");
        assert!(stale_actor.await.unwrap_err().is_cancelled());
        assert!(!manager.client.lock().unwrap().clients.contains_key("saved"));
        assert_eq!(manager.connection_state("saved"), RemoteConnectionState::Disconnected);
        drop(attempt);
        assert_eq!(manager.connection_state("saved"), RemoteConnectionState::Disconnected);

        // The id is not tombstoned: a later add and connect works normally.
        manager.register_profile(profile("saved")).unwrap();
        let next = ConnectingAttempt::start(&manager, "saved");
        assert_eq!(manager.connection_state("saved"), RemoteConnectionState::Reconnecting);
        let (sender, _receiver) = mpsc::channel(1);
        manager
            .adopt_client(&next, Some(RemoteClient {
                requests: Some(sender), profile: Some(profile("saved")), target_key: None,
                task: None, ready: Arc::new(AtomicBool::new(true)),
            }))
            .unwrap();
        drop(next);
        assert_eq!(manager.connection_state("saved"), RemoteConnectionState::Connected);

        let status = |state| RemoteConnectionStatus { profile_id: "saved".into(), state };
        assert_eq!(
            reports.lock().unwrap().clone(),
            vec![
                status(RemoteConnectionState::Reconnecting),
                status(RemoteConnectionState::Disconnected),
                status(RemoteConnectionState::Reconnecting),
                status(RemoteConnectionState::Connected),
            ]
        );
    }

    #[test]
    fn resolved_identity_uses_user_host_and_port() {
        assert_eq!(ssh_target_key("host alias\nuser owner\nhostname BOX.Example\nport 2222\n").unwrap(), "owner@box.example:2222");
        assert!(ssh_target_key("hostname box\n").is_err());
    }

    #[test]
    fn merge_over_500_rows_matches_global_order() {
        use serde_json::json;
        for sort in ["time", "sender", "receiver"] {
            let mut rows: Vec<_> = (0..1200).map(|i| json!({"id":format!("{i:04}"),
                "createdAtMs":i,"senderName":format!("Sender {}", i % 7),"receiver":format!("Receiver {}", i % 11)})).collect();
            rows.sort_by_key(|row| (
                match sort { "sender" => row["senderName"].as_str().unwrap(), "receiver" => row["receiver"].as_str().unwrap(), _ => "" }.to_string(),
                std::cmp::Reverse(row["createdAtMs"].as_i64().unwrap()),
                std::cmp::Reverse(row["id"].as_str().unwrap().to_string())));
            let pages = |offset: usize| (0..2).map(|machine| (machine.to_string(), rows.iter().skip(offset)
                .filter(|r| r["createdAtMs"].as_i64().unwrap() % 2 == machine)
                .take(500).cloned().collect())).collect();
            let first = merge_message_pages(pages(0), 500, sort);
            let next = merge_message_pages(pages(500), 500, sort);
            let ids: Vec<_> = first.iter().chain(next.iter()).map(|r| r["id"].clone()).collect();
            assert_eq!(ids, rows.iter().take(1000).map(|r| r["id"].clone()).collect::<Vec<_>>());
        }
    }

    #[tokio::test]
    async fn messages_changed_survives_a_full_outbound_queue() {
        let directory = std::env::temp_dir().join(format!("assembly-message-pressure-{}", uuid::Uuid::new_v4()));
        std::fs::create_dir(&directory).unwrap();
        let manager = AgentRuntimeManager::open(ProviderRegistry::default(), &directory.join("sessions.db")).unwrap();
        let state = ServerState {
            workflow: crate::workflow::WorkflowEngine::managed(manager.clone(), manager.store_handle()),
            manager, data_dir: directory.clone(), token: Arc::from("test-token"),
            maintenance: Arc::new(tokio::sync::RwLock::new(())), restarting: Arc::new(AtomicBool::new(false)),
            restart_requested: Arc::new(tokio::sync::Notify::new()), restart_flushed: Arc::new(tokio::sync::Notify::new()),
            events: broadcast::channel(64).0, terminals: RemoteTerminals::new(), relay: Arc::default(), pairing: Arc::default(),
        };
        let listener = tokio::net::TcpListener::bind("127.0.0.1:0").await.unwrap();
        let address = listener.local_addr().unwrap();
        let server = tokio::spawn(axum::serve(listener, Router::new().route("/assembly", get(upgrade_remote_socket)).with_state(state.clone())).into_future());
        let mut client = handshake::test_client(&format!("ws://{address}/assembly")).await;
        for sequence in 1..=32 {
            let mut event = large_history_event(4 * 1024 * 1024);
            event.sequence = sequence;
            state.events.send(event).unwrap();
        }
        // Stop reading until the writer and the bounded outbound queue cannot advance.
        tokio::time::timeout(Duration::from_secs(5), async {
            while state.relay.desktops.lock().unwrap()[0].1.capacity() != 0 {
                tokio::time::sleep(Duration::from_millis(10)).await;
            }
        }).await.expect("outbound queue must be full before change");
        for _ in 0..20 { MESSAGE_CHANGES.send_modify(|version| *version += 1); }
        tokio::time::timeout(Duration::from_secs(5), async {
            loop {
                let frame = client.next().await.unwrap().unwrap();
                if let TungsteniteMessage::Text(text) = frame {
                    if matches!(serde_json::from_str::<ServerFrame>(&text).unwrap(), ServerFrame::MessagesChanged) { break; }
                }
            }
        }).await.expect("message notification must survive outbound pressure");
        drop(client);
        server.abort();
        let _ = server.await;
        drop(state);
        std::fs::remove_dir_all(directory).unwrap();
    }

    #[tokio::test]
    async fn readiness_requires_protocol_and_session_response() {
        assert_eq!(PROTOCOL_VERSION, 24);
        for version in [PROTOCOL_VERSION - 1, PROTOCOL_VERSION, PROTOCOL_VERSION + 1] {
            let listener = tokio::net::TcpListener::bind("127.0.0.1:0").await.unwrap();
            let address = listener.local_addr().unwrap();
            let (probe_tx, probe_rx) = oneshot::channel();
            let (release_tx, release_rx) = oneshot::channel();
            let server = tokio::spawn(async move {
                let (stream, _) = listener.accept().await.unwrap();
                let mut socket = handshake::test_backend(stream, version).await;
                if version != PROTOCOL_VERSION { return; }
                let _resume = socket.next().await.unwrap().unwrap();
                let request = socket.next().await.unwrap().unwrap();
                let frame: ClientFrame = serde_json::from_str(request.to_text().unwrap()).unwrap();
                assert!(matches!(frame, ClientFrame::Request { id: READINESS_REQUEST_ID, command: RemoteCommand::ListSessions }));
                probe_tx.send(()).unwrap();
                release_rx.await.unwrap();
                socket.send(TungsteniteMessage::Text(serde_json::to_string(&ServerFrame::Response { id: READINESS_REQUEST_ID, response: RemoteResponse::Sessions(vec![]) }).unwrap().into())).await.unwrap();
                let _ = socket.next().await;
            });
            let (requests, mut receiver) = mpsc::channel(1);
            let (reply, mut result) = oneshot::channel();
            let ready = Arc::new(AtomicBool::new(false));
            let actor_ready = ready.clone();
            let actor = tokio::spawn(async move { client_loop(format!("ws://{address}"), "test-token".into(), &mut receiver, Arc::new(|_| Ok(())), None, Some(1), actor_ready, &mut Some(reply), Arc::new(|| {}), None, client_history()).await });
            if version == PROTOCOL_VERSION {
                probe_rx.await.unwrap();
                assert!(!ready.load(Ordering::Acquire));
                assert!(matches!(result.try_recv(), Err(oneshot::error::TryRecvError::Empty)));
                release_tx.send(()).unwrap();
                assert!(result.await.unwrap().unwrap().is_empty());
                assert!(ready.load(Ordering::Acquire));
            } else {
                let error = result.await.unwrap().unwrap_err();
                if version < PROTOCOL_VERSION {
                    assert!(error.contains("Remote backend needs an update"));
                } else {
                    assert!(error.contains("Assembly client needs an update"));
                }
                assert!(!ready.load(Ordering::Acquire));
            }
            drop(requests);
            actor.abort();
            let _ = actor.await;
            server.await.unwrap();
        }
    }

    #[tokio::test]
    async fn pending_remote_request_receives_readiness_error() {
        let listener = tokio::net::TcpListener::bind("127.0.0.1:0").await.unwrap();
        let address = listener.local_addr().unwrap();
        let server = tokio::spawn(async move {
            let (stream, _) = listener.accept().await.unwrap();
            let mut socket = handshake::test_backend(stream, PROTOCOL_VERSION).await;
            let _resume = socket.next().await.unwrap().unwrap();
            let _readiness = socket.next().await.unwrap().unwrap();
            let request = socket.next().await.unwrap().unwrap();
            let frame: ClientFrame = serde_json::from_str(request.to_text().unwrap()).unwrap();
            assert!(matches!(frame, ClientFrame::Request { command: RemoteCommand::Config { .. }, .. }));
            socket.send(TungsteniteMessage::Text(serde_json::to_string(
                &ServerFrame::Error { id: READINESS_REQUEST_ID, message: "backend rejected readiness".into() }
            ).unwrap().into())).await.unwrap();
        });
        let (requests, mut receiver) = mpsc::channel(2);
        let (ready_reply, ready_answer) = oneshot::channel();
        let actor = tokio::spawn(async move {
            client_loop(format!("ws://{address}"), "test-token".into(), &mut receiver,
                Arc::new(|_| Ok(())), None, Some(1), Arc::new(AtomicBool::new(false)),
                &mut Some(ready_reply), Arc::new(|| {}), None, client_history()).await;
        });
        let (reply, answer) = oneshot::channel();
        requests.send(ClientRequest::Execute {
            id: 1, command: RemoteCommand::Config { owned_id: "proof".into() }, reply, progress: None,
        }).await.unwrap();
        let ready_error = tokio::time::timeout(Duration::from_secs(5), ready_answer)
            .await.unwrap().unwrap().unwrap_err();
        let request_error = tokio::time::timeout(Duration::from_secs(5), answer)
            .await.unwrap().unwrap().unwrap_err();
        assert_eq!(ready_error, "backend rejected readiness");
        assert_eq!(request_error, ready_error);
        drop(requests);
        server.await.unwrap();
        actor.await.unwrap();
    }

    #[tokio::test]
    async fn batched_events_resume_from_applied_cursor_without_ack() {
        let listener = tokio::net::TcpListener::bind("127.0.0.1:0").await.unwrap();
        let address = listener.local_addr().unwrap();
        let server = tokio::spawn(async move {
            let (stream, _) = listener.accept().await.unwrap();
            let mut socket = handshake::test_backend(stream, PROTOCOL_VERSION).await;
            assert!(matches!(serde_json::from_str::<ClientFrame>(socket.next().await.unwrap().unwrap().to_text().unwrap()).unwrap(), ClientFrame::Resume { cursors } if cursors.is_empty()));
            let _readiness = socket.next().await.unwrap().unwrap();
            let mut first = large_history_event(32);
            first.sequence = 1;
            let mut second = first.clone();
            second.sequence = 2;
            let mut other = first.clone();
            other.owned_id = "other-history".into();
            for event in [first, other, second] {
                socket.send(TungsteniteMessage::Text(serde_json::to_string(&ServerFrame::Event { event }).unwrap().into())).await.unwrap();
            }
            socket.send(TungsteniteMessage::Text(serde_json::to_string(
                &ServerFrame::Response { id: READINESS_REQUEST_ID, response: RemoteResponse::Sessions(vec![]) }
            ).unwrap().into())).await.unwrap();
            assert!(tokio::time::timeout(Duration::from_millis(100), socket.next()).await.is_err(), "events must not produce Ack frames");
            socket.close(None).await.unwrap();
            let (stream, _) = listener.accept().await.unwrap();
            let mut socket = handshake::test_backend(stream, PROTOCOL_VERSION).await;
            let resume: ClientFrame = serde_json::from_str(socket.next().await.unwrap().unwrap().to_text().unwrap()).unwrap();
            assert!(matches!(resume, ClientFrame::Resume { cursors }
                if cursors.get("large-history") == Some(&2) && cursors.get("other-history") == Some(&1)));
        });
        let delivered = Arc::new(Mutex::new(Vec::new()));
        let captured = delivered.clone();
        let manager = RemoteConnectionManager::from_environment(
            Arc::new(move |event| captured.lock().unwrap().push(event)), Arc::new(|_| {}), store()).unwrap();
        let sink = manager.history_event_sink("test", true);
        let (requests, mut receiver) = mpsc::channel(1);
        let (ready_reply, ready_answer) = oneshot::channel();
        let actor = tokio::spawn(async move {
            client_loop(format!("ws://{address}"), "test-token".into(), &mut receiver,
                sink, None,
                Some(2), Arc::new(AtomicBool::new(false)), &mut Some(ready_reply), Arc::new(|| {}), None, (manager, "test".into())).await;
        });
        ready_answer.await.unwrap().unwrap();
        server.await.unwrap();
        let sequences: Vec<_> = delivered.lock().unwrap().iter()
            .map(|event: &AgentConversationEvent| (event.owned_id.clone(), event.sequence)).collect();
        assert_eq!(sequences, vec![("large-history".into(), 1), ("other-history".into(), 1), ("large-history".into(), 2)]);
        drop(requests);
        actor.abort();
        let _ = actor.await;
    }

    #[test]
    fn replay_completion_decodes_pending_snapshot_metadata() {
        let mut snapshot = history_snapshot(vec![], 3);
        snapshot.pending_events.push(history_event(3));
        snapshot.active_turn_id = Some("active".into());
        let frame = ServerFrame::ReplayComplete { owned_id: "large-history".into(), head: 3, snapshot };
        let decoded = parse_server_frame(TungsteniteMessage::Text(encode_server_frame(&frame).unwrap().into())).unwrap();
        let Some(ServerFrame::ReplayComplete { snapshot, .. }) = decoded else { panic!("expected completion"); };
        assert_eq!(snapshot.pending_events.len(), 1);
        assert_eq!(snapshot.active_turn_id.as_deref(), Some("active"));
        assert!(snapshot.events.is_empty());
    }

    #[tokio::test]
    async fn rework_deleted_session_does_not_abort_resume() {
        let directory = std::env::temp_dir().join(format!("assembly-deleted-{}", uuid::Uuid::new_v4()));
        std::fs::create_dir(&directory).unwrap();
        let manager = AgentRuntimeManager::open(ProviderRegistry::default(), &directory.join("sessions.db")).unwrap();
        let (events, _) = broadcast::channel(2);
        let state = ServerState {
            workflow: crate::workflow::WorkflowEngine::managed(manager.clone(), manager.store_handle()),
            manager, data_dir: directory.clone(), token: Arc::from("test"),
            maintenance: Arc::new(tokio::sync::RwLock::new(())), restarting: Arc::new(AtomicBool::new(false)),
            restart_requested: Arc::new(tokio::sync::Notify::new()), restart_flushed: Arc::new(tokio::sync::Notify::new()), events,
            terminals: RemoteTerminals::new(), relay: Arc::default(), pairing: Arc::default(),
        };
        let (outbound, mut received) = mpsc::channel(8);
        let mut queued = BTreeMap::from([("deleted".into(), 10)]);
        replay_session(&state, &outbound, &mut queued, "deleted").await.unwrap();
        assert!(received.try_recv().is_err(), "an absent session must not open a replay window");
        drop(state);
        std::fs::remove_dir_all(directory).unwrap();
    }

    #[test]
    fn rework_reconnect_before_completion_publishes_saved_newest_once() {
        let published = Arc::new(Mutex::new(Vec::new()));
        let captured = published.clone();
        let manager = RemoteConnectionManager::from_environment(
            Arc::new(move |event| captured.lock().unwrap().push(event)), Arc::new(|_| {}), store()).unwrap();
        manager.history_event_sink("box", false)(vec![history_event(10)]).unwrap();
        // The first connection ended before completion. Resume at 10 supplies no events.
        assert!(!manager.complete_replay("box", history_snapshot(vec![], 10), None, Some(10)).unwrap());
        assert_eq!(sequences(&published.lock().unwrap()), vec![10]);
    }

    #[tokio::test]
    async fn rework_snapshot_head_before_live_event_keeps_saved_rows() {
        let manager = RemoteConnectionManager::from_environment(Arc::new(|_| {}), Arc::new(|_| {}), store()).unwrap();
        manager.history.lock().unwrap().snapshot("cache-test", 0, None,
            &mut history_snapshot(vec![history_event(10)], 10)).unwrap();
        let mut requests = connect(&manager);
        let client = manager.clone();
        let server = tokio::spawn(async move {
            let ClientRequest::Execute { reply, .. } = requests.recv().await.unwrap() else { panic!("expected snapshot"); };
            client.history_event_sink("cache-test", false)(vec![history_event(11)]).unwrap();
            reply.send(Ok(RemoteResponse::Snapshot(Some(history_snapshot(vec![], 10))))).unwrap();
        });
        assert!(manager.save_newer("cache-test", "large-history", 1, false, None).await.unwrap());
        server.await.unwrap();
        assert_eq!(manager.saved_cursors("cache-test").unwrap()["large-history"], 11);
        assert!(!manager.complete_replay("cache-test", history_snapshot(vec![], 10), None, Some(10)).unwrap());
        assert_eq!(manager.saved_cursors("cache-test").unwrap()["large-history"], 11);
        let rows = manager.store.list_events(&RemoteHistory::key("cache-test", "large-history"), i64::MIN, 10).unwrap();
        assert_eq!(rows.iter().map(|row| row.seq).collect::<Vec<_>>(), vec![10, 11]);
    }

    #[test]
    fn resume_uses_saved_maxima_even_past_coverage() {
        let manager = RemoteConnectionManager::from_environment(Arc::new(|_| {}), Arc::new(|_| {}), store()).unwrap();
        let mut base = history_snapshot(vec![history_event(-5)], -5);
        manager.history.lock().unwrap().snapshot("box", 0, None, &mut base).unwrap();
        manager.history_event_sink("box", true)(vec![history_event(7)]).unwrap();
        assert_eq!(manager.saved_cursors("box").unwrap(), BTreeMap::from([("large-history".into(), 7)]));
    }

    fn quiet_replay_300() {
        let published = Arc::new(Mutex::new(Vec::new()));
        let captured = published.clone();
        let manager = RemoteConnectionManager::from_environment(
            Arc::new(move |event| captured.lock().unwrap().push(event)), Arc::new(|_| {}), store()).unwrap();
        let mut base = history_snapshot(vec![history_event(0)], 0);
        base.active_turn_id = Some("finished".into());
        manager.history.lock().unwrap().snapshot("box", 0, None, &mut base).unwrap();
        let mut events: Vec<_> = (1..=300).map(history_event).collect();
        events[0].payload = super::super::protocol::AgentConversationPayload::Turn {
            turn_id: "finished".into(), state: super::super::protocol::TurnState::Started,
        };
        // Completion metadata is authoritative even when the last event isn't a turn event.
        let newest = events.last().cloned();
        manager.history_event_sink("box", false)(events).unwrap();
        assert!(published.lock().unwrap().is_empty());
        assert!(!manager.complete_replay("box", history_snapshot(vec![], 300), newest, Some(0)).unwrap());
        assert_eq!(sequences(&published.lock().unwrap()), vec![300]);
        let saved = manager.history.lock().unwrap().read_snapshot("box", 0, "large-history").unwrap();
        assert_eq!(saved.events.len(), 301);
        assert!(saved.active_turn_id.is_none());
        assert!(saved.pending_events.is_empty());
    }

    #[test]
    fn resume_replay_300_is_saved_and_published_only_on_completion() { quiet_replay_300(); }

    #[test]
    fn lag_replay_300_is_saved_and_published_only_on_completion() { quiet_replay_300(); }

    #[tokio::test]
    async fn client_saves_300_replay_events_before_resume_and_lag_completion() {
        for lag in [false, true] {
            let listener = tokio::net::TcpListener::bind("127.0.0.1:0").await.unwrap();
            let address = listener.local_addr().unwrap();
            let published = Arc::new(Mutex::new(Vec::new()));
            let captured = published.clone();
            let manager = RemoteConnectionManager::from_environment(
                Arc::new(move |event| captured.lock().unwrap().push(event)), Arc::new(|_| {}), store()).unwrap();
            manager.history.lock().unwrap().snapshot("box", 0, None,
                &mut history_snapshot(vec![history_event(0)], 0)).unwrap();
            let saved = manager.clone();
            let server = tokio::spawn(async move {
                let (stream, _) = listener.accept().await.unwrap();
                let mut socket = handshake::test_backend(stream, PROTOCOL_VERSION).await;
                let resume: ClientFrame = serde_json::from_str(socket.next().await.unwrap().unwrap().to_text().unwrap()).unwrap();
                assert!(matches!(resume, ClientFrame::Resume { cursors } if cursors == BTreeMap::from([("large-history".into(), 0)])));
                let _readiness = socket.next().await.unwrap().unwrap();
                let ready = ServerFrame::Response { id: READINESS_REQUEST_ID, response: RemoteResponse::Sessions(vec![]) };
                if lag { socket.send(TungsteniteMessage::Text(encode_server_frame(&ready).unwrap().into())).await.unwrap(); }
                socket.send(TungsteniteMessage::Text(encode_server_frame(&ServerFrame::ReplayStart { owned_id: "large-history".into() }).unwrap().into())).await.unwrap();
                for sequence in 1..=300 {
                    socket.send(TungsteniteMessage::Text(encode_server_frame(&ServerFrame::Event { event: history_event(sequence) }).unwrap().into())).await.unwrap();
                }
                tokio::time::timeout(Duration::from_secs(5), async {
                    while saved.saved_cursors("box").unwrap().get("large-history") != Some(&300) { tokio::task::yield_now().await; }
                }).await.unwrap();
                assert!(published.lock().unwrap().is_empty());
                let complete = ServerFrame::ReplayComplete { owned_id: "large-history".into(), head: 300,
                    snapshot: history_snapshot(vec![], 300) };
                socket.send(TungsteniteMessage::Text(encode_server_frame(&complete).unwrap().into())).await.unwrap();
                if !lag { socket.send(TungsteniteMessage::Text(encode_server_frame(&ready).unwrap().into())).await.unwrap(); }
                tokio::time::timeout(Duration::from_secs(5), async {
                    while published.lock().unwrap().is_empty() { tokio::task::yield_now().await; }
                }).await.unwrap();
                assert_eq!(sequences(&published.lock().unwrap()), vec![300]);
            });
            let (requests, mut receiver) = mpsc::channel(2);
            let sink = manager.history_event_sink("box", true);
            let actor = tokio::spawn(async move {
                client_loop(format!("ws://{address}"), "test-token".into(), &mut receiver, sink, None, Some(1),
                    Arc::new(AtomicBool::new(false)), &mut None, Arc::new(|| {}), None, (manager, "box".into())).await;
            });
            server.await.unwrap();
            drop(requests);
            actor.abort();
            let _ = actor.await;
        }
    }

    #[test]
    fn shrunk_replay_metadata_forgets_before_top_up() {
        let manager = RemoteConnectionManager::from_environment(Arc::new(|_| {}), Arc::new(|_| {}), store()).unwrap();
        let mut base = history_snapshot(vec![history_event(10)], 10);
        manager.history.lock().unwrap().snapshot("box", 0, None, &mut base).unwrap();
        assert!(manager.complete_replay("box", history_snapshot(vec![], 3), None, Some(10)).unwrap());
        assert!(manager.saved_cursors("box").unwrap().is_empty());
    }

    #[test]
    fn replacement_page_preserves_events_pushed_after_its_head() {
        let manager = RemoteConnectionManager::from_environment(Arc::new(|_| {}), Arc::new(|_| {}), store()).unwrap();
        manager.history_event_sink("box", true)(vec![history_event(4)]).unwrap();
        manager.save_replay_snapshot("box", history_snapshot(vec![history_event(3)], 3), Some(history_event(3))).unwrap();
        assert_eq!(manager.saved_cursors("box").unwrap()["large-history"], 4);
    }

    #[tokio::test]
    async fn replay_complete_shrunk_head_forgets_and_requests_newest_page() {
        let listener = tokio::net::TcpListener::bind("127.0.0.1:0").await.unwrap();
        let address = listener.local_addr().unwrap();
        let manager = RemoteConnectionManager::from_environment(Arc::new(|_| {}), Arc::new(|_| {}), store()).unwrap();
        manager.history.lock().unwrap().snapshot("box", 0, None,
            &mut history_snapshot(vec![history_event(10)], 10)).unwrap();
        let saved = manager.clone();
        let server = tokio::spawn(async move {
            let (stream, _) = listener.accept().await.unwrap();
            let mut socket = handshake::test_backend(stream, PROTOCOL_VERSION).await;
            let resume: ClientFrame = serde_json::from_str(socket.next().await.unwrap().unwrap().to_text().unwrap()).unwrap();
            assert!(matches!(resume, ClientFrame::Resume { cursors } if cursors.get("large-history") == Some(&10)));
            let _readiness = socket.next().await.unwrap().unwrap();
            for frame in [ServerFrame::ReplayStart { owned_id: "large-history".into() },
                ServerFrame::ReplayComplete { owned_id: "large-history".into(), head: 3, snapshot: history_snapshot(vec![], 3) }] {
                socket.send(TungsteniteMessage::Text(encode_server_frame(&frame).unwrap().into())).await.unwrap();
            }
            let request = tokio::time::timeout(Duration::from_secs(5), socket.next()).await.unwrap().unwrap().unwrap();
            let ClientFrame::Request { id, command: RemoteCommand::Snapshot { after_sequence, .. } } =
                serde_json::from_str(request.to_text().unwrap()).unwrap() else { panic!("expected newest page request"); };
            assert_eq!(after_sequence, None);
            assert!(saved.saved_cursors("box").unwrap().is_empty());
            let response = ServerFrame::Response { id, response: RemoteResponse::Snapshot(Some(history_snapshot(vec![history_event(3)], 3))) };
            socket.send(TungsteniteMessage::Text(encode_server_frame(&response).unwrap().into())).await.unwrap();
            tokio::time::timeout(Duration::from_secs(5), async {
                while saved.saved_cursors("box").unwrap().get("large-history") != Some(&3) { tokio::task::yield_now().await; }
            }).await.unwrap();
        });
        let (requests, mut receiver) = mpsc::channel(2);
        let sink = manager.history_event_sink("box", true);
        let actor = tokio::spawn(async move {
            client_loop(format!("ws://{address}"), "test-token".into(), &mut receiver, sink, None, Some(1),
                Arc::new(AtomicBool::new(false)), &mut None, Arc::new(|| {}), None, (manager, "box".into())).await;
        });
        server.await.unwrap();
        drop(requests);
        actor.abort();
        let _ = actor.await;
    }

    #[tokio::test]
    async fn sqlite_replay_fills_queued_gap_and_reports_read_failure() {
        let directory = std::env::temp_dir().join(format!("assembly-replay-test-{}", uuid::Uuid::new_v4()));
        std::fs::create_dir(&directory).unwrap();
        let database = directory.join("sessions.db");
        let manager = AgentRuntimeManager::open(ProviderRegistry::default(), &database).unwrap();
        let store = manager.store();
        store.upsert_session(&mcb_core::session_store::SessionRow {
            owned_id: "large-history".into(), native_session_id: None, provider: "codex".into(),
            model: None, effort: None, cwd: String::new(), worktree: None, branch: None,
            title: None, title_source: None, project: None, project_id: None, state: "ready".into(),
            suspended: false, created_at_ms: 0, last_activity_at_ms: 0, extra_json: super::super::manager::imported_session_extra(AgentConversationProvider::Codex).unwrap(),
        }).unwrap();
        for sequence in 1..=301 {
            let mut event = large_history_event(32);
            event.sequence = sequence;
            store.append_event(&mcb_core::session_store::EventRow {
                owned_id: event.owned_id.clone(), seq: sequence, turn_id: None,
                kind: "test".into(), payload_json: serde_json::to_string(&event).unwrap(),
                created_at_ms: 0,
            }).unwrap();
        }
        let (events, _) = broadcast::channel(2);
        let state = ServerState {
            workflow: crate::workflow::WorkflowEngine::managed(manager.clone(), manager.store_handle()),
            manager, data_dir: directory.clone(), token: Arc::from("test"),
            maintenance: Arc::new(tokio::sync::RwLock::new(())), restarting: Arc::new(AtomicBool::new(false)),
            restart_requested: Arc::new(tokio::sync::Notify::new()), restart_flushed: Arc::new(tokio::sync::Notify::new()), events,
            terminals: RemoteTerminals::new(), relay: Arc::default(), pairing: Arc::default(),
        };
        let (outbound, mut received) = mpsc::channel(512);
        let mut queued = BTreeMap::from([("large-history".to_string(), 1)]);
        replay_session(&state, &outbound, &mut queued, "large-history").await.unwrap();
        assert_eq!(queued["large-history"], 301);
        let start = received.try_recv().unwrap();
        assert_eq!(serde_json::to_value(start).unwrap()["type"], "replayStart");
        let mut sequences = Vec::new();
        for _ in 0..300 {
            let ServerFrame::Event { event } = received.try_recv().unwrap() else { panic!("expected replayed event"); };
            sequences.push(event.sequence);
        }
        assert_eq!(sequences, (2..=301).collect::<Vec<_>>());
        let complete = serde_json::to_value(received.try_recv().unwrap()).unwrap();
        assert_eq!(complete["type"], "replayComplete");
        assert_eq!(complete["head"], 301);
        assert!(complete["snapshot"]["activeTurnId"].is_null());
        let mut new_event = large_history_event(32);
        new_event.sequence = 302;
        state.manager.store().append_event(&mcb_core::session_store::EventRow {
            owned_id: new_event.owned_id.clone(), seq: 302, turn_id: None,
            kind: "test".into(), payload_json: serde_json::to_string(&new_event).unwrap(),
            created_at_ms: 0,
        }).unwrap();
        replay_session(&state, &outbound, &mut queued, "large-history").await.unwrap();
        assert_eq!(serde_json::to_value(received.try_recv().unwrap()).unwrap()["type"], "replayStart");
        assert!(matches!(received.try_recv(), Ok(ServerFrame::Event { event }) if event.sequence == 302));
        assert_eq!(serde_json::to_value(received.try_recv().unwrap()).unwrap()["type"], "replayComplete");
        assert_eq!(queued["large-history"], 302);
        replay_session(&state, &outbound, &mut queued, "large-history").await.unwrap();
        assert_eq!(serde_json::to_value(received.try_recv().unwrap()).unwrap()["type"], "replayStart");
        assert_eq!(serde_json::to_value(received.try_recv().unwrap()).unwrap()["type"], "replayComplete");
        assert!(received.try_recv().is_err());
        rusqlite::Connection::open(&database).unwrap().execute("DROP TABLE events", []).unwrap();
        assert!(replay_session(&state, &outbound, &mut queued, "large-history").await.is_err());
        drop(state);
        std::fs::remove_dir_all(directory).unwrap();
    }

    #[tokio::test]
    async fn lagging_socket_replays_missing_rows_and_stays_open() {
        let directory = std::env::temp_dir().join(format!("assembly-socket-lag-{}", uuid::Uuid::new_v4()));
        std::fs::create_dir(&directory).unwrap();
        let manager = AgentRuntimeManager::open(ProviderRegistry::default(), &directory.join("sessions.db")).unwrap();
        let store = manager.store();
        store.upsert_session(&mcb_core::session_store::SessionRow {
            owned_id: "large-history".into(), native_session_id: None, provider: "codex".into(),
            model: None, effort: None, cwd: String::new(), worktree: None, branch: None,
            title: None, title_source: None, project: None, project_id: None, state: "suspended".into(),
            suspended: true, created_at_ms: 0, last_activity_at_ms: 0,
            extra_json: super::super::manager::imported_session_extra(AgentConversationProvider::Codex).unwrap(),
        }).unwrap();
        store.upsert_session(&mcb_core::session_store::SessionRow {
            owned_id: "unknown-history".into(), native_session_id: None, provider: "codex".into(),
            model: None, effort: None, cwd: String::new(), worktree: None, branch: None,
            title: None, title_source: None, project: None, project_id: None, state: "suspended".into(),
            suspended: true, created_at_ms: 0, last_activity_at_ms: 0,
            extra_json: super::super::manager::imported_session_extra(AgentConversationProvider::Codex).unwrap(),
        }).unwrap();
        let append = |sequence| {
            let mut event = large_history_event(32);
            event.sequence = sequence;
            store.append_event(&mcb_core::session_store::EventRow {
                owned_id: event.owned_id.clone(), seq: sequence, turn_id: None,
                kind: "test".into(), payload_json: serde_json::to_string(&event).unwrap(),
                created_at_ms: 0,
            }).unwrap();
            event
        };
        for sequence in 1..=300 { append(sequence); }
        let mut unknown = large_history_event(32);
        unknown.owned_id = "unknown-history".into();
        unknown.sequence = 1;
        store.append_event(&mcb_core::session_store::EventRow {
            owned_id: unknown.owned_id.clone(), seq: 1, turn_id: None,
            kind: "test".into(), payload_json: serde_json::to_string(&unknown).unwrap(),
            created_at_ms: 0,
        }).unwrap();
        let (events, _) = broadcast::channel(2);
        let state = ServerState {
            workflow: crate::workflow::WorkflowEngine::managed(manager.clone(), manager.store_handle()),
            manager: manager.clone(), data_dir: directory.clone(), token: Arc::from("test-token"),
            maintenance: Arc::new(tokio::sync::RwLock::new(())), restarting: Arc::new(AtomicBool::new(false)),
            restart_requested: Arc::new(tokio::sync::Notify::new()), restart_flushed: Arc::new(tokio::sync::Notify::new()), events: events.clone(),
            terminals: RemoteTerminals::new(), relay: Arc::default(), pairing: Arc::default(),
        };
        let listener = tokio::net::TcpListener::bind("127.0.0.1:0").await.unwrap();
        let address = listener.local_addr().unwrap();
        let server = tokio::spawn(axum::serve(listener, Router::new().route("/assembly", get(upgrade_remote_socket)).with_state(state.clone())).into_future());
        let mut socket = handshake::test_client(&format!("ws://{address}/assembly")).await;
        send_client_frame(&mut socket, &ClientFrame::Resume { cursors: BTreeMap::from([("large-history".into(), 0)]) }).await.unwrap();
        let first = parse_server_frame(socket.next().await.unwrap().unwrap()).unwrap().unwrap();
        assert!(matches!(first, ServerFrame::ReplayStart { .. }));
        let first = parse_server_frame(socket.next().await.unwrap().unwrap()).unwrap().unwrap();
        assert!(matches!(first, ServerFrame::Event { event } if event.sequence == 1));

        // The current-thread runtime cannot poll the server while this burst is sent.
        // Its two-slot receiver must report Lagged after the in-progress replay.
        for sequence in 301..=600 { events.send(append(sequence)).unwrap(); }
        let mut sequences = vec![1];
        while sequences.len() < 600 {
            let frame = tokio::time::timeout(Duration::from_secs(5), socket.next()).await.unwrap().unwrap().unwrap();
            if let Some(ServerFrame::Event { event }) = parse_server_frame(frame).unwrap() {
                assert_eq!(event.owned_id, "large-history", "unknown session must not replay from the start");
                sequences.push(event.sequence);
            }
        }
        assert_eq!(sequences, (1..=600).collect::<Vec<_>>());
        send_client_frame(&mut socket, &ClientFrame::Request { id: 7, command: RemoteCommand::ListSessions }).await.unwrap();
        loop {
            let response = tokio::time::timeout(Duration::from_secs(5), socket.next()).await.unwrap().unwrap().unwrap();
            match parse_server_frame(response).unwrap() {
                Some(ServerFrame::ReplayComplete { .. }) => continue,
                Some(ServerFrame::Response { id: 7, response: RemoteResponse::Sessions(_) }) => break,
                other => panic!("unexpected frame: {other:?}"),
            }
        }
        socket.close(None).await.unwrap();
        server.abort();
        let _ = server.await;
        drop(state);
        std::fs::remove_dir_all(directory).unwrap();
    }

    #[tokio::test]
    async fn pending_remote_request_reports_socket_close_reason() {
        use tokio_tungstenite::tungstenite::protocol::{frame::coding::CloseCode, CloseFrame};

        let listener = tokio::net::TcpListener::bind("127.0.0.1:0").await.unwrap();
        let address = listener.local_addr().unwrap();
        let server = tokio::spawn(async move {
            let (stream, _) = listener.accept().await.unwrap();
            let mut socket = handshake::test_backend(stream, PROTOCOL_VERSION).await;
            let _resume = socket.next().await.unwrap().unwrap();
            let _readiness = socket.next().await.unwrap().unwrap();
            socket.send(TungsteniteMessage::Text(serde_json::to_string(
                &ServerFrame::Response { id: READINESS_REQUEST_ID, response: RemoteResponse::Sessions(vec![]) }
            ).unwrap().into())).await.unwrap();
            let request = socket.next().await.unwrap().unwrap();
            let frame: ClientFrame = serde_json::from_str(request.to_text().unwrap()).unwrap();
            assert!(matches!(frame, ClientFrame::Request { command: RemoteCommand::Config { .. }, .. }));
            socket.send(TungsteniteMessage::Close(Some(CloseFrame {
                code: CloseCode::Error,
                reason: "test backend closed during config".into(),
            }))).await.unwrap();
        });
        let (requests, mut receiver) = mpsc::channel(2);
        let (ready_reply, ready_answer) = oneshot::channel();
        let actor = tokio::spawn(async move {
            client_loop(format!("ws://{address}"), "test-token".into(), &mut receiver,
                Arc::new(|_| Ok(())), None, Some(1), Arc::new(AtomicBool::new(false)),
                &mut Some(ready_reply), Arc::new(|| {}), None, client_history()).await;
        });
        ready_answer.await.unwrap().unwrap();
        let (reply, answer) = oneshot::channel();
        requests.send(ClientRequest::Execute {
            id: 1, command: RemoteCommand::Config { owned_id: "proof".into() }, reply, progress: None,
        }).await.unwrap();
        let error = tokio::time::timeout(Duration::from_secs(5), answer)
            .await.unwrap().unwrap().unwrap_err();
        assert!(error.contains("test backend closed during config"), "{error}");
        drop(requests);
        server.await.unwrap();
        actor.await.unwrap();
    }

    #[tokio::test]
    #[ignore = "read-only proof against the owner's installed backend"]
    async fn live_existing_workbox_connection() {
        let manager = RemoteConnectionManager::from_environment(Arc::new(|_| {}), Arc::new(|_| {}), store()).unwrap();
        let result = manager.connect_profile(profile("stage1-proof"), "live-proof".into(), tauri::ipc::Channel::new(|_| Ok(()))).await.unwrap();
        for session in &result.sessions {
            let workspace = manager.request_for_profile(&result.profile.id, RemoteCommand::Workspace {
                operation: "validate_project_root".into(), args: serde_json::json!({"path": session.cwd})
            }).await.unwrap();
            eprintln!("Remote workspace validation: {:?}", workspace);
            assert!(manager.snapshot(session.owned_id.clone(), 100).await.unwrap().is_some());
            let capabilities = manager.capabilities(session.owned_id.clone(), 101).await.unwrap();
            eprintln!("Remote steering advertised: {}", capabilities.session.steering);
        }
        let again = manager.connect_profile(profile("duplicate"), "live-again".into(), tauri::ipc::Channel::new(|_| Ok(()))).await.unwrap();
        assert_eq!(again.profile.id, "stage1-proof");
        assert_eq!(manager.tunnel_processes.lock().unwrap().len(), 1);
        eprintln!("Read-only connection verified: {} session snapshots; duplicate reused one tunnel", result.sessions.len());
        manager.shutdown();
        tokio::task::yield_now().await;
        assert!(manager.tunnel_processes.lock().unwrap().is_empty());
    }
}

#[tauri::command]
pub async fn remote_workspace(
    remote: tauri::State<'_, RemoteConnectionManager>,
    profile_id: String,
    operation: String,
    args: serde_json::Value,
    request_id: Option<u64>,
) -> Result<serde_json::Value, String> {
    remote.workspace_operation(&profile_id, operation, args, request_id).await
}

#[tauri::command]
pub async fn remote_workflow(
    remote: tauri::State<'_, RemoteConnectionManager>,
    profile_id: String,
    operation: String,
    args: serde_json::Value,
    request_id: Option<u64>,
) -> Result<serde_json::Value, String> {
    remote.workflow_operation(&profile_id, operation, args, request_id).await
}

#[tauri::command]
pub async fn check_remote_provider_updates(
    remote: tauri::State<'_, RemoteConnectionManager>,
    profile_id: String,
) -> Result<super::providers::updates::ProviderUpdateStatus, String> {
    match remote.request_for_profile(&profile_id, RemoteCommand::CheckProviderUpdates).await? {
        RemoteResponse::ProviderUpdates(status) => Ok(status),
        _ => Err("The remote backend does not support provider updates; update its backend first".into()),
    }
}

#[tauri::command]
pub async fn install_remote_provider_updates(
    app: tauri::AppHandle,
    remote: tauri::State<'_, RemoteConnectionManager>,
    profile_id: String,
    provider: String,
) -> Result<super::providers::updates::ProviderUpdateStatus, String> {
    use tauri::Emitter;
    let (sender, mut progress) = mpsc::unbounded_channel();
    let id = remote.next_request_id.fetch_add(1, Ordering::Relaxed);
    let request = remote.request_with_id(&profile_id, id, RemoteCommand::InstallProviderUpdates { provider }, Some(sender));
    tokio::pin!(request);
    let response = loop {
        tokio::select! {
            result = &mut request => break result?,
            Some(mut update) = progress.recv() => {
                update.profile_id = Some(profile_id.clone());
                let _ = app.emit("provider-download-progress", update);
            }
        }
    };
    match response {
        RemoteResponse::ProviderUpdates(status) => Ok(status),
        _ => Err("The remote backend does not support provider updates; update its backend first".into()),
    }
}

#[tauri::command]
pub async fn restart_remote_for_provider_updates(
    remote: tauri::State<'_, RemoteConnectionManager>,
    profile_id: String,
) -> Result<(), String> {
    match remote.request_for_profile(&profile_id, RemoteCommand::RestartForProviderUpdates).await? {
        RemoteResponse::Restarting => Ok(()),
        _ => Err("The remote backend returned an incompatible restart response".into()),
    }
}

#[cfg(test)]
mod provider_restart_tests {
    use super::*;

    #[tokio::test]
    async fn remote_start_run_uses_remote_store_and_worker() {
        use serde_json::json;
        let root = std::env::temp_dir().join(format!("assembly-remote-workflow-{}", uuid::Uuid::new_v4()));
        std::fs::create_dir(&root).unwrap();
        let git = |dir: &std::path::Path, args: &[&str]| {
            let output = std::process::Command::new("git").args(args).current_dir(dir).output().unwrap();
            assert!(output.status.success(), "{}", String::from_utf8_lossy(&output.stderr));
        };
        git(&root, &["init", "--bare", "--initial-branch=main", "origin.git"]);
        git(&root, &["clone", "origin.git", "repo"]);
        let repo = root.join("repo");
        std::fs::create_dir_all(repo.join(".assembly/workflows/prompts")).unwrap();
        std::fs::write(repo.join(".assembly/workflows/prompts/build.md"), "Build it").unwrap();
        std::fs::write(repo.join(".assembly/workflows/demo.yml"), "name: Demo\nsteps:\n  build: { agent: { provider: codex }, prompt: prompts/build.md }\n  checks: { needs: [build], run: [\"true\"] }\n").unwrap();
        git(&repo, &["add", "."]);
        git(&repo, &["-c", "user.name=Test", "-c", "user.email=test@test", "commit", "-m", "demo"]);
        git(&repo, &["push", "origin", "main"]);
        let data_dir = root.join("server");
        std::fs::create_dir(&data_dir).unwrap();
        let log = root.join("acp.jsonl");
        let open = || {
            let manifest = super::super::providers::acp_client::tests::fixture_manifest_named(&log, "multiplex");
            let providers = ProviderRegistry::new([(AgentConversationProvider::Codex, manifest)]).unwrap();
            let manager = AgentRuntimeManager::open(providers, &data_dir.join("sessions.db")).unwrap();
            server_state(manager, data_dir.clone(), Arc::from("test")).unwrap()
        };
        async fn call(state: &ServerState, operation: &str, args: serde_json::Value) -> serde_json::Value {
            let command = RemoteCommand::Workflow { operation: operation.into(), args };
            match execute_server_command(state, &Arc::new(Mutex::new(None)), command, Arc::new(|_| {})).await {
                Ok(RemoteResponse::Workflow(value)) => value,
                other => panic!("{operation}: {other:?}"),
            }
        }
        let state = open();
        assert_eq!((call(&state, "listFiles", json!({"repoRoot": repo})).await, call(&state, "usage", json!({"runId": null})).await), (json!([".assembly/workflows/demo.yml"]), json!([])));
        let run = call(&state, "create", json!({"repoRoot": repo, "workflowPath": ".assembly/workflows/demo.yml",
            "task": {"key": "TSK-9003", "title": "Remote demo", "body": "Prove remote runs"}, "idempotencyKey": "create"})).await;
        let run_id = run["id"].as_str().unwrap().to_string();
        call(&state, "start", json!({"runId": run_id, "idempotencyKey": "start"})).await;
        // The worker's finished turn reaches the engine through the server's emitter.
        let deadline = std::time::Instant::now() + Duration::from_secs(20);
        let run = loop {
            let run = call(&state, "get", json!({"runId": run_id})).await;
            if run["state"] == "completed" { break run; }
            assert!(std::time::Instant::now() < deadline, "run did not finish: {run}");
            tokio::time::sleep(Duration::from_millis(50)).await;
        };
        let worker = run["nodes"].as_array().unwrap().iter().find(|node| node["nodeId"] == "build").unwrap();
        let worker_id = worker["ownedId"].as_str().unwrap();
        assert!(state.manager.store().get_session(worker_id).unwrap().is_some(), "the worker lives in the remote store");
        state.manager.shutdown().await;
        drop(state);

        // A restarted server reads the same run from its own store and prompts nobody again.
        let state = open();
        let runs = call(&state, "list", json!({})).await;
        assert_eq!(runs.as_array().unwrap().len(), 1);
        assert_eq!(runs[0]["id"], run_id);
        assert_eq!(runs[0]["state"], "completed");
        tokio::time::sleep(Duration::from_millis(200)).await;
        let prompts = std::fs::read_to_string(&log).unwrap().matches("\"method\":\"session/prompt\"").count();
        assert_eq!(prompts, 1);
        state.manager.shutdown().await;
        drop(state);
        std::fs::remove_dir_all(root).unwrap();
    }

    #[tokio::test]
    async fn restart_is_accepted_while_conversations_are_running() {
        let directory = std::env::temp_dir().join(format!("assembly-busy-restart-{}", uuid::Uuid::new_v4()));
        std::fs::create_dir(&directory).unwrap();
        let manager = AgentRuntimeManager::open(ProviderRegistry::default(), &directory.join("sessions.db")).unwrap();
        let _busy = manager.begin_test_authentication();
        assert!(manager.has_pending_provider_work());
        let state = ServerState {
            workflow: crate::workflow::WorkflowEngine::managed(manager.clone(), manager.store_handle()),
            manager, data_dir: directory.clone(), token: Arc::from("test"),
            maintenance: Arc::new(tokio::sync::RwLock::new(())), restarting: Arc::new(AtomicBool::new(false)),
            restart_requested: Arc::new(tokio::sync::Notify::new()), restart_flushed: Arc::new(tokio::sync::Notify::new()),
            events: broadcast::channel(2).0,
            terminals: RemoteTerminals::new(), relay: Arc::default(), pairing: Arc::default(),
        };
        let response = execute_server_command(&state, &Arc::new(Mutex::new(None)),
            RemoteCommand::RestartForProviderUpdates, Arc::new(|_| {})).await;
        assert!(matches!(response, Ok(RemoteResponse::Restarting)), "{:?}", response.err());
        tokio::time::timeout(Duration::from_millis(100), state.restart_requested.notified())
            .await.expect("the accepted restart must be requested");
        drop(state);
        std::fs::remove_dir_all(directory).unwrap();
    }

    #[tokio::test]
    async fn server_does_not_restart_without_an_accepted_request() {
        let requested = tokio::sync::Notify::new();
        let flushed = tokio::sync::Notify::new();
        assert!(tokio::time::timeout(Duration::from_millis(20),
            wait_for_provider_restart(&requested, &flushed)).await.is_err());
    }

    #[tokio::test]
    async fn accepted_restart_survives_missing_socket_acknowledgement() {
        let requested = tokio::sync::Notify::new();
        let flushed = tokio::sync::Notify::new();
        requested.notify_one();
        tokio::time::timeout(Duration::from_secs(3), wait_for_provider_restart(&requested, &flushed))
            .await.expect("a disconnected client must not prevent restart");
    }

    #[tokio::test]
    async fn flushed_restart_acknowledgement_does_not_wait_for_deadline() {
        let requested = tokio::sync::Notify::new();
        let flushed = tokio::sync::Notify::new();
        requested.notify_one();
        flushed.notify_one();
        tokio::time::timeout(Duration::from_millis(100), wait_for_provider_restart(&requested, &flushed))
            .await.expect("a flushed response should release restart immediately");
    }

    #[tokio::test]
    async fn install_is_accepted_while_conversations_are_running() {
        // Holding the install lock stops the installer before any network or disk work.
        let _installer = super::super::providers::updates::INSTALL_LOCK.lock().await;
        let directory = std::env::temp_dir().join(format!("assembly-busy-install-{}", uuid::Uuid::new_v4()));
        std::fs::create_dir(&directory).unwrap();
        std::env::set_var("ASSEMBLY_SERVER_DATA_DIR", &directory);
        let manager = AgentRuntimeManager::open(ProviderRegistry::default(), &directory.join("sessions.db")).unwrap();
        let _busy = manager.begin_test_authentication();
        assert!(manager.has_pending_provider_work());
        let response = execute_remote_command(&manager, &directory,
            RemoteCommand::InstallProviderUpdates { provider: "codex".into() }, Arc::new(|_| {})).await;
        let error = response.err().expect("the held install lock must stop the installer");
        assert!(error.contains("already running"), "install must reach the installer, got: {error}");
        drop(manager);
        std::fs::remove_dir_all(directory).unwrap();
    }
}

#[cfg(test)]
mod terminal_tests {
    use super::*;

    fn terminal_state(directory: &std::path::Path) -> ServerState {
        let manager = AgentRuntimeManager::open(ProviderRegistry::default(), &directory.join("sessions.db")).unwrap();
        let workflow = crate::workflow::WorkflowEngine::managed(manager.clone(), manager.store_handle());
        ServerState {
            manager, data_dir: directory.to_path_buf(), token: Arc::from("test"), workflow,
            maintenance: Arc::new(tokio::sync::RwLock::new(())), restarting: Arc::new(AtomicBool::new(false)),
            restart_requested: Arc::new(tokio::sync::Notify::new()), restart_flushed: Arc::new(tokio::sync::Notify::new()),
            events: broadcast::channel(2).0,
            terminals: RemoteTerminals::new(), relay: Arc::default(), pairing: Arc::default(),
        }
    }

    fn start_request(cwd: &std::path::Path) -> TerminalStartRequest {
        TerminalStartRequest {
            cwd: cwd.display().to_string(), shell: Some("/bin/sh".into()), cols: Some(80), rows: Some(24),
            owned_id: Some("remote-session".into()), command: None,
        }
    }

    async fn run(state: &ServerState, command: RemoteCommand) -> Result<RemoteResponse, String> {
        execute_server_command(state, &Arc::new(Mutex::new(None)), command, Arc::new(|_| {})).await
    }

    #[tokio::test]
    async fn remote_shell_runs_on_the_server_and_streams_its_output() {
        let directory = std::env::temp_dir().join(format!("assembly-remote-terminal-{}", uuid::Uuid::new_v4()));
        std::fs::create_dir(&directory).unwrap();
        let state = terminal_state(&directory);
        let mut output = state.terminals.output.subscribe();

        let Ok(RemoteResponse::TerminalSession(info)) = run(&state, RemoteCommand::TerminalStart(start_request(&directory))).await else {
            panic!("the shell must start");
        };
        let Ok(RemoteResponse::TerminalSession(again)) = run(&state, RemoteCommand::TerminalStart(start_request(&directory))).await else {
            panic!("the second start must succeed");
        };
        assert_eq!(again.session_id, info.session_id, "a live shell is reused for the same session");
        let wire = encode_server_frame(&ServerFrame::Response { id: 1, response: RemoteResponse::TerminalSession(info.clone()) }).unwrap();
        assert!(matches!(parse_server_frame(TungsteniteMessage::Text(wire.into())).unwrap(),
            Some(ServerFrame::Response { response: RemoteResponse::TerminalSession(decoded), .. }) if decoded.session_id == info.session_id));

        let write = RemoteCommand::TerminalWrite { session_id: info.session_id.clone(), data: "echo hi-$((1+1))\n".into() };
        assert!(matches!(run(&state, write).await, Ok(RemoteResponse::Bool(true))));
        let mut seen = String::new();
        let frame = tokio::time::timeout(Duration::from_secs(10), async {
            loop {
                let frame = output.recv().await.unwrap();
                seen.push_str(&frame.output.data);
                if seen.contains("hi-2") { return frame; }
            }
        }).await.expect("the shell's output must reach the socket broadcast");
        assert_eq!(frame.output.session_id, info.session_id);

        let wire = encode_server_frame(&ServerFrame::TerminalOutput { frame }).unwrap();
        let Some(ServerFrame::TerminalOutput { frame }) = parse_server_frame(TungsteniteMessage::Text(wire.into())).unwrap() else {
            panic!("terminal output must survive the wire");
        };
        assert_eq!(frame.output.session_id, info.session_id);

        let close = RemoteCommand::TerminalClose { session_id: info.session_id.clone() };
        assert!(matches!(run(&state, close).await, Ok(RemoteResponse::Bool(true))));
        drop(state);
        std::fs::remove_dir_all(directory).unwrap();
    }

    #[tokio::test]
    async fn remote_shell_reports_a_missing_session_folder() {
        let directory = std::env::temp_dir().join(format!("assembly-remote-terminal-{}", uuid::Uuid::new_v4()));
        std::fs::create_dir(&directory).unwrap();
        let state = terminal_state(&directory);
        let error = run(&state, RemoteCommand::TerminalStart(start_request(&directory.join("gone")))).await.unwrap_err();
        assert_eq!(error, format!("This session's folder no longer exists: {}", directory.join("gone").display()));
        drop(state);
        std::fs::remove_dir_all(directory).unwrap();
    }

    #[test]
    fn remote_terminal_and_folder_ids_split_into_machine_and_native_parts() {
        let id = remote_terminal_id("profile-1", "terminal-7");
        assert_eq!(split_remote_terminal_id(&id), Some(("profile-1", "terminal-7")));
        assert_eq!(split_remote_terminal_id("terminal-7"), None);
        assert_eq!(split_remote_workspace_path("assembly-remote://profile-1/home/me/repo"), Some(("profile-1", "/home/me/repo")));
        assert_eq!(split_remote_workspace_path("/home/me/repo"), None);
    }
}
