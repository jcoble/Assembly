#[path = "session_project.rs"] pub mod session_project; mod helper_close; mod otel_session; pub mod tool_call; pub mod turn_usage;
use std::collections::{BTreeMap, HashMap, HashSet};
use std::io::{Read, Seek, SeekFrom};
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::sync::mpsc as std_mpsc;
use std::sync::{Arc, Mutex, Weak};
use std::thread::{self, JoinHandle};
use std::time::{Duration, SystemTime, UNIX_EPOCH};
use mcb_core::session_store::{
    AnnotationRow, EventCoverage, EventRow, ItemPage, ProjectRow, SessionRow, SessionStore,
};
use notify::{RecursiveMode, Watcher};
use serde::{Deserialize, Serialize};
use serde_json::Value;
use tokio::sync::{Mutex as AsyncMutex, OwnedMutexGuard};

use super::broker_status::{status_for, AgentWorkStatus, WorkflowBrokerEvent};
use super::handoff::{
    AgentConversationHandoffDirection, AgentConversationHandoffMode, AgentConversationHandoffPhase,
    AgentConversationHandoffReceipt, AgentConversationHandoffRequest,
};
use super::normalization::{
    is_replay_session_update, is_session_state_update, payload_from_session_update_for_turn,
    session_update_kind, text_from_value, tools,
};
use super::protocol::{
    AgentApprovalDecision, AgentApprovalResponse, AgentCapabilities,
    AgentConfigOptionChoice,
    AgentConversationConfigState, AgentConversationConnection, AgentConversationEvent,
    AgentConversationChildHistorySelection,
    AgentConversationEventCoverage, AgentConversationEventPage, AgentConversationItemDescriptor,
    AgentConversationItemPage, AgentConversationPayload, AgentConversationProvider,
    AgentConversationSelectionSnapshot, AgentConversationTurnFacts,
    AgentConversationSendReceipt, AgentConversationSessionMeta, AgentConversationSessionRecord,
    AgentConversationSnapshot, BackgroundWorkItem, BackgroundWorkKind,
    AgentEvent, AgentEventType, AgentExecutionOwner, AgentImplementation,
    AgentInteractionCapabilities, AgentNativeSessionMode, AgentPromptCapabilities,
    AgentRequestIdentity, AgentRuntimeState, AgentSessionCapabilities, AgentUserInputAction,
    AgentUserInputField, AgentUserInputKind, AgentUserInputResponse,
    AgentWriterLease, AgentWriterLeaseOwner, AgentWriterLeaseTransition, ApprovalState,
    ChangeAgentConversationCheckoutRequest, ConversationConnectionState,
    EnsureAgentConversationRequest, ExecutionEnvironment,
    SetAgentConversationConfigRequest, TerminalProjectionPayload, ToolState,
    UpdateAgentConversationSessionMetaRequest,
};
use super::providers::acp_client::{AcpInbound, AcpTransport};
use super::providers::authentication::{self, Authentications};
use super::providers::process::validated_conversation_cwd;
use super::providers::process::SidecarEnvironment;
use super::providers::{
    AcpRuntimeAdapter, AgentConfigValue, AgentConversationConfigUpdate, AgentPrompt,
    AgentRuntimeAdapter, AgentRuntimeError, GeneratedText, InitializeAgentInput, LoadAgentSession,
    NewAgentSession, PermissionResponse, ProviderRegistry, StructuredRuntimeHandle,
};
use super::history::transcript::{self, CodexChildRollout};
use tokio::sync::mpsc::{self, UnboundedSender};

/// How much of a conversation opening it hands over, in bytes of stored event.
///
/// Bytes rather than a count of events, because an event is anything from a
/// config record to a sixteen-kilobyte tool result: the same count of them is a
/// different amount of reading in every session, and it is the bytes that cost
/// the fetch, the hop to the front end and the parse at the other end.
const SNAPSHOT_WINDOW_BYTES: u32 = 512 * 1024;
/// The most events one catch-up read will hand back after a missed update.
///
/// A bound on a single fetch, not on what a session keeps. Nothing trims a
/// conversation's journal: history costs disk and nothing else, and every read
/// of it is already bounded.
const CATCH_UP_EVENT_CAP: u32 = 10_000;
const SESSION_TITLE_CHAR_CAP: usize = 64;
const BROKER_MESSAGE_TTL_MS: i64 = 30 * 60 * 1000;
const ANTIGRAVITY_CANCEL_GRACE: Duration = Duration::from_secs(2);

/// Where a session's name came from, as it is written to the row. A row with
/// none of these on it was written before the app recorded this, and is read as
/// having come from the prompt.
const TITLE_SOURCE_PROMPT: &str = "prompt";
const TITLE_SOURCE_HELPER: &str = "helper";
const TITLE_SOURCE_USER: &str = "user";

/// How much of the first prompt, and of the reply to it, the utility model is
/// given to name a session by. A name comes from the shape of an exchange, not
/// from all of it, and the call is billed by the token.
const TITLE_INPUT_CHAR_CAP: usize = 1_000;
const CHILD_ROLLOUT_SCAN_INTERVAL: Duration = Duration::from_secs(10);
const MAX_THINKING_TOKENS_ENV: &str = "MAX_THINKING_TOKENS";
pub type ConversationEmitter = std::sync::Arc<dyn Fn(AgentConversationEvent) + Send + Sync>;
pub type BrokerEmitter = std::sync::Arc<dyn Fn(WorkflowBrokerEvent) + Send + Sync>;

/// Asks the utility model to name one session from its first exchange. Set once
/// at launch; with none set the helper is not involved and a session keeps the
/// name taken from its first prompt.
pub type SessionNamer =
    std::sync::Arc<dyn Fn(&str) -> Result<String, crate::helper::HelperError> + Send + Sync>;

/// A permission request waiting on the owner, as the phone's inbox shows it.
pub struct PendingPermissionItem {
    pub request_id: String,
    pub owned_id: String,
    pub generation: u64,
    pub provider: AgentConversationProvider,
    pub session_title: Option<String>,
    pub title: String,
    pub command: Option<String>,
    /// `(option id, label, kind)` as the provider sent them.
    pub options: Vec<(String, String, String)>,
    pub requested_at_ms: u128,
}

/// Told which session has just been given a new name, once that name is saved,
/// so the rail can show it without asking for it.
pub type SessionRenamedListener = std::sync::Arc<dyn Fn(&str, &str) + Send + Sync>;

#[derive(Clone, Debug)]
struct PermissionOption {
    option_id: String,
    kind: String,
    /// The provider's label, such as "Allow once".
    name: String,
}

#[derive(Clone, Debug)]
struct PendingPermission {
    wire_id: Value,
    options: Vec<PermissionOption>,
    summary: String,
    /// The tool call's command, when it runs one.
    command: Option<String>,
    child_scoped: bool,
    event: AgentConversationEvent,
}

#[derive(Clone, Debug, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
struct ClaudeChildSession {
    parent_id: String,
    transcript_id: String,
    label: Option<String>,
    state: String,
}

#[derive(Clone, Debug)]
struct PendingUserInput {
    wire_id: Value,
    response_shape: UserInputResponseShape,
    child_scoped: bool,
    event: AgentConversationEvent,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
enum UserInputResponseShape {
    Legacy,
    Elicitation,
}

enum PermissionSelection {
    Decision(AgentApprovalDecision),
    Option(String),
}

enum OrderedSessionEvent {
    PromptResult {
        turn_id: String,
        result: Result<Value, AgentRuntimeError>,
    },
    ApprovalResolved {
        request_id: String,
        state: ApprovalState,
        summary: String,
    },
    UserInputResolved {
        request_id: String,
        cancelled: bool,
    },
}

#[derive(Clone, Debug, Eq, Hash, PartialEq)]
enum AdapterPoolKey {
    Shared(AgentConversationProvider),
    Isolated(AgentConversationProvider, String),
}

struct AdapterPoolEntry {
    runtime: Arc<AsyncMutex<StructuredRuntimeHandle>>,
    transport: Arc<AcpTransport>,
    capabilities: AgentCapabilities,
    members: HashSet<String>,
}

pub struct ManagedAgentSession {
    pub owned_id: String,
    pub provider: AgentConversationProvider,
    pub provider_instance_id: String,
    pub native_session_id: Option<String>,
    native_session_mode: AgentNativeSessionMode,
    pub generation: u64,
    /// The registry project this session was started in, kept across generations.
    pub project_id: Option<String>,
    /// The session that started this one with `start_session`; each turn this
    /// session finishes is reported back to it as a message.
    helper_of: Option<String>,
    /// A workflow step's guard policy, checked against each command the agent asks to run.
    guard_policy: Option<crate::workflow::policy::GuardPolicy>,
    pub owner: AgentExecutionOwner,
    pub state: AgentRuntimeState,
    pub capabilities: AgentCapabilities,
    pub next_sequence: i64,
    pub active_turn_id: Option<String>,
    cancel_deadline: Option<tokio::task::JoinHandle<()>>,
    prompt_once_active: bool,
    pub runtime: Option<Arc<AsyncMutex<StructuredRuntimeHandle>>>,
    pool_key: Option<AdapterPoolKey>,
    transport: Option<Arc<AcpTransport>>,
    pub writer_lease: AgentWriterLease,
    pub writer_lease_transition: Option<AgentWriterLeaseTransition>,
    permission_requests: HashMap<String, PendingPermission>,
    user_input_requests: HashMap<String, PendingUserInput>,
    next_user_input_id: u64,
    ordered_events: Option<UnboundedSender<OrderedSessionEvent>>,
    cwd: String,
    spawn_reasoning_effort: Option<String>,
    /// Whether the running adapter reported an effort control of its own.
    /// When it did, the effort can be changed at any time; when it did not,
    /// the session keeps the effort it was started with.
    adapter_offers_effort: bool,
    config: AgentConversationConfigState,
    connection: AgentConversationConnection,
    store: Arc<SessionStore>,
    created_at_ms: u128,
    last_activity_ms: u128,
    live_tool_calls: HashMap<String, String>, // with each command's joined terminal output
    /// The reply the agent is streaming: its message id, the text of the
    /// deltas stored for it so far, and the autonomous turn it belongs to.
    streaming_reply: Option<(String, String, Option<String>)>,
    background_work: HashMap<String, BackgroundWorkEntry>,
    /// The journal turn for Claude's own work between two prompts — the
    /// replies it writes when a background sub-agent or command finishes.
    /// It lasts from the first such update until Claude Code reports idle.
    autonomous_turn_id: Option<String>,
    autonomous_turn_started: bool,
    /// The Claude adapter forwards Claude Code's running and idle state.
    claude_reports_state: bool,
    child_rollout_scan: Option<tokio::task::JoinHandle<()>>,
    child_rollout_parent_path: Option<PathBuf>,
    codex_children: HashMap<String, CodexChildRollout>,
    claude_children: HashMap<String, ClaudeChildSession>,
    rail_meta: AgentConversationSessionMeta,
    /// Where the current name came from: the first prompt, the utility model, or
    /// the person. A session recovered from a row written before this was
    /// recorded carries none, and counts as the first prompt.
    title_source: Option<String>,
    suspending: bool,
    turn_usage: turn_usage::TurnUsageState, // When the prompt went out and the latest reported cost total.
}

#[derive(Clone, Debug)]
struct BackgroundWorkEntry {
    kind: BackgroundWorkKind,
    label: String,
    started_at_ms: i64,
}

/// Holds only the in-memory fields that an event is allowed to advance.
///
/// Building this copy first keeps the live session unchanged until SQLite has
/// accepted the matching session row and event row.
struct SessionEventCandidate {
    native_session_id: Option<String>,
    native_session_mode: AgentNativeSessionMode,
    state: AgentRuntimeState,
    owner: AgentExecutionOwner,
    capabilities: AgentCapabilities,
    next_sequence: i64,
    connection: AgentConversationConnection,
    writer_owner: AgentWriterLeaseOwner,
    last_activity_ms: u128,
    live_tool_calls: HashMap<String, String>,
}

#[derive(Clone, Copy)]
struct SessionLifecycleUpdate {
    state: AgentRuntimeState,
    connection_state: ConversationConnectionState,
    owner: AgentExecutionOwner,
    writer_owner: AgentWriterLeaseOwner,
    native_session_mode: Option<AgentNativeSessionMode>,
}

impl SessionEventCandidate {
    /// Copies the small event-owned portion of a live session for a pending write.
    fn from_session(session: &ManagedAgentSession) -> Self {
        Self {
            native_session_id: session.native_session_id.clone(),
            native_session_mode: session.native_session_mode,
            state: session.state,
            owner: session.owner,
            capabilities: session.capabilities.clone(),
            next_sequence: session.next_sequence,
            connection: session.connection.clone(),
            writer_owner: session.writer_lease.owner,
            last_activity_ms: session.last_activity_ms,
            live_tool_calls: session.live_tool_calls.clone(),
        }
    }

    /// Validates and stages a lifecycle transition without changing live memory.
    fn transition_lifecycle(
        &mut self,
        state: AgentRuntimeState,
        connection_state: ConversationConnectionState,
        owner: AgentExecutionOwner,
        writer_owner: AgentWriterLeaseOwner,
    ) -> Result<(), String> {
        let terminal = self.state == AgentRuntimeState::Closed
            || self.connection.state == ConversationConnectionState::Closed;
        if terminal
            && (state != AgentRuntimeState::Closed
                || connection_state != ConversationConnectionState::Closed)
        {
            return Err("Closed conversation sessions cannot be revived".to_string());
        }
        self.state = state;
        self.connection.state = connection_state;
        self.owner = owner;
        self.writer_owner = writer_owner;
        Ok(())
    }

    /// Publishes a committed candidate to the live session in one replacement step.
    /// The live session owns the next unused sequence and advances it only here,
    /// after SQLite accepts the event that used the previous value.
    fn apply(self, session: &mut ManagedAgentSession) {
        session.native_session_id = self.native_session_id;
        session.native_session_mode = self.native_session_mode;
        session.state = self.state;
        session.owner = self.owner;
        session.capabilities = self.capabilities;
        session.next_sequence = self.next_sequence;
        session.connection = self.connection;
        session.writer_lease.owner = self.writer_owner;
        session.last_activity_ms = self.last_activity_ms;
        session.live_tool_calls = self.live_tool_calls;
    }
}

impl ManagedAgentSession {
    fn transition_lifecycle(
        &mut self,
        state: AgentRuntimeState,
        connection_state: ConversationConnectionState,
        owner: AgentExecutionOwner,
        writer_owner: AgentWriterLeaseOwner,
    ) -> Result<(), String> {
        let terminal = self.state == AgentRuntimeState::Closed
            || self.connection.state == ConversationConnectionState::Closed;
        if terminal
            && (state != AgentRuntimeState::Closed
                || connection_state != ConversationConnectionState::Closed)
        {
            return Err("Closed conversation sessions cannot be revived".to_string());
        }
        self.state = state;
        self.connection.state = connection_state;
        self.owner = owner;
        self.writer_lease.owner = writer_owner;
        Ok(())
    }
}

pub(crate) struct HandoffContext {
    pub previous_owner: AgentWriterLeaseOwner,
    pub owner: AgentWriterLeaseOwner,
    pub native_session_id: Option<String>,
}

struct ChildHistoryWatcher {
    request_id: u64,
    watcher: Option<notify::RecommendedWatcher>,
    stop: Arc<AtomicBool>,
    wake: std_mpsc::SyncSender<()>,
    worker: Option<JoinHandle<()>>,
}

impl ChildHistoryWatcher {
    fn stop(mut self) {
        self.stop.store(true, Ordering::Release);
        let _ = self.wake.try_send(());
        self.watcher.take();
        if let Some(worker) = self.worker.take() {
            let _ = worker.join();
        }
    }
}

/// The most of a helper's final reply that is sent back to its parent.
const HELPER_REPLY_LIMIT: usize = 4000;

#[derive(Clone)]
pub struct AgentRuntimeManager {
    /// Live process and request overlay. Durable identity, lifecycle state, and
    /// conversation events are always reconstructed from `store` at startup.
    sessions: Arc<Mutex<HashMap<String, ManagedAgentSession>>>,
    providers: Arc<ProviderRegistry>,
    authentications: Authentications,
    emitter: Arc<Mutex<Option<ConversationEmitter>>>,
    broker_emitter: Arc<Mutex<Option<BrokerEmitter>>>,
    broker_statuses: Arc<Mutex<HashMap<String, AgentWorkStatus>>>,
    /// Sessions a queued message is being sent to, so a turn ending and a new
    /// message arriving cannot send the same message twice.
    delivering: Arc<Mutex<HashMap<String, bool>>>,
    namer: Arc<Mutex<Option<SessionNamer>>>,
    renamed_listener: Arc<Mutex<Option<SessionRenamedListener>>>,
    activation_locks: Arc<Mutex<HashMap<String, Arc<AsyncMutex<()>>>>>,
    latest_snapshot_request: Arc<AtomicU64>,
    /// Cancelled ids at or above the current snapshot read. Ids are shared
    /// with other request kinds, so these may not be snapshot reads at all.
    cancelled_snapshot_requests: Arc<Mutex<Vec<u64>>>,
    store: Arc<SessionStore>,
    adapter_pools: Arc<AsyncMutex<HashMap<AdapterPoolKey, AdapterPoolEntry>>>,
    probe_cancellations: tokio::sync::watch::Sender<u64>,
    child_history_watchers: Arc<Mutex<HashMap<String, ChildHistoryWatcher>>>,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct AgentResourceRoot {
    pub pid: u32,
    pub owned_id: String,
    pub provider: AgentConversationProvider,
    pub cwd: String,
}

#[derive(Debug, Clone, Default, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct AgentRuntimeDiagnostics {
    pub durable_session_rows: usize,
    pub live_session_overlays: usize,
    pub live_runtime_handles: usize,
    pub sidecar_processes: usize,
    pub active_turns: usize,
    pub pending_permissions: usize,
    pub pending_inputs: usize,
    pub live_tool_calls: usize,
    pub background_work: usize,
    pub suspendable_sessions: usize,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub adapter_pools: Option<usize>,
}

impl Default for AgentRuntimeManager {
    fn default() -> Self {
        Self::new(ProviderRegistry::default())
    }
}

impl AgentRuntimeManager {
    pub fn new(providers: ProviderRegistry) -> Self {
        let store = SessionStore::open_in_memory()
            .expect("the in-memory session store must initialize for an unmanaged manager");
        Self::with_store(providers, store).expect("the empty session store must recover")
    }

    pub fn open(providers: ProviderRegistry, database_path: &Path) -> Result<Self, String> {
        let store = SessionStore::open(database_path).map_err(|error| error.to_string())?;
        if let Some(directory) = database_path.parent() {
            super::history::legacy_import::import_if_store_empty(&store, directory)?;
        }
        Self::with_store(providers, store)
    }

    pub fn with_store(providers: ProviderRegistry, store: SessionStore) -> Result<Self, String> {
        let store = Arc::new(store);
        normalize_sessions_in_store(&store)?;
        Ok(Self {
            sessions: Arc::new(Mutex::new(HashMap::new())),
            providers: Arc::new(providers),
            emitter: Arc::new(Mutex::new(None)),
            broker_emitter: Arc::new(Mutex::new(None)),
            broker_statuses: Arc::new(Mutex::new(HashMap::new())),
            delivering: Arc::new(Mutex::new(HashMap::new())),
            namer: Arc::new(Mutex::new(None)),
            renamed_listener: Arc::new(Mutex::new(None)),
            activation_locks: Arc::new(Mutex::new(HashMap::new())),
            latest_snapshot_request: Arc::new(AtomicU64::new(0)),
            cancelled_snapshot_requests: Arc::new(Mutex::new(Vec::new())),
            store,
            adapter_pools: Arc::new(AsyncMutex::new(HashMap::new())),
            probe_cancellations: tokio::sync::watch::channel(0).0,
            child_history_watchers: Arc::new(Mutex::new(HashMap::new())),
            authentications: Authentications::default(),
        })
    }

    /// The durable store, for records that live beside a session but are written
    /// by the module that owns them, such as the conversation attachment index.
    pub(crate) fn store(&self) -> &SessionStore {
        &self.store
    }

    pub(crate) fn store_handle(&self) -> Arc<SessionStore> {
        Arc::clone(&self.store)
    }

    pub fn set_session_draft(&self, owned_id: &str, text: &str) -> Result<(), String> {
        self.store
            .set_draft(owned_id, text)
            .map_err(|error| error.to_string())
    }

    pub fn get_session_draft(&self, owned_id: &str) -> Result<Option<String>, String> {
        self.store
            .get_draft(owned_id)
            .map_err(|error| error.to_string())
    }

    pub fn clear_session_draft(&self, owned_id: &str) -> Result<(), String> {
        self.store
            .clear_draft(owned_id)
            .map_err(|error| error.to_string())
    }

    pub fn write_workspace(&self, owned_id: &str, snapshot_json: &str) -> Result<(), String> {
        self.store
            .upsert_workspace_snapshot(owned_id, snapshot_json)
            .map_err(|error| error.to_string())
    }

    pub fn read_workspace(&self, owned_id: &str) -> Result<Option<String>, String> {
        self.store
            .get_workspace_snapshot(owned_id)
            .map_err(|error| error.to_string())
    }

    pub fn read_workspace_expanded_paths(
        &self,
        owned_id: &str,
        root: &str,
    ) -> Result<Vec<String>, String> {
        self.store
            .get_workspace_expanded_paths(owned_id, root)
            .map_err(|error| error.to_string())
    }

    pub fn write_workspace_expanded_paths(
        &self,
        owned_id: &str,
        root: &str,
        paths: &[String],
    ) -> Result<(), String> {
        self.store
            .set_workspace_expanded_paths(owned_id, root, paths)
            .map_err(|error| error.to_string())
    }

    pub fn delete_workspace(&self, owned_id: &str) -> Result<(), String> {
        self.store
            .delete_workspace_snapshot(owned_id)
            .map_err(|error| error.to_string())
    }

    pub fn clear_workspace_editors(&self) -> Result<(), String> {
        self.store
            .clear_workspace_editor_tabs()
            .map_err(|error| error.to_string())
    }

    pub fn clear_workspace_tabs(&self) -> Result<(), String> {
        self.store
            .clear_workspace_selected_tabs()
            .map_err(|error| error.to_string())
    }

    pub fn write_app_setting(&self, setting_key: &str, value_json: &str) -> Result<(), String> {
        self.store
            .upsert_app_setting(setting_key, value_json)
            .map_err(|error| error.to_string())
    }

    pub fn read_app_setting(&self, setting_key: &str) -> Result<Option<String>, String> {
        self.store
            .get_app_setting(setting_key)
            .map_err(|error| error.to_string())
    }

    pub fn list_projects(&self) -> Result<Vec<ProjectRow>, String> {
        self.store.list_projects().map_err(|error| error.to_string())
    }

    /// Registers the project, or returns the one already registered for that
    /// folder, with whether it was already there.
    pub fn add_project(&self, row: ProjectRow) -> Result<(ProjectRow, bool), String> {
        self.store
            .insert_or_get_project(&row)
            .map_err(|error| error.to_string())
    }

    /// Takes the project off the list. With `delete_sessions` its sessions go
    /// through `delete` first; otherwise they stay without a project. Files on
    /// disk are never touched.
    pub async fn remove_project(&self, id: &str, delete_sessions: bool) -> Result<(), String> {
        if delete_sessions {
            for owned_id in self.store.project_session_ids(id, false).map_err(|error| error.to_string())? {
                self.delete(&owned_id).await?;
            }
        }
        if !self.store.remove_project(id).map_err(|error| error.to_string())? {
            return Err("That project no longer exists.".into());
        }
        Ok(())
    }

    /// Names an imported session without reading a single record of it, so the
    /// rail has something to show while the transcript is still being read.
    /// Also answers whether a session already here was cleared to be read again from its transcript.
    pub async fn begin_import_transcript_session(&self, provider: AgentConversationProvider, native_session_id: &str,
        path: &Path, cwd: &str, title: Option<String>, project_id: Option<String>) -> Result<(String, bool), String> {
        // Activation holds the same guard, so the session cannot start between the check and the read-again.
        let existing = super::history::existing_import_session(&self.store, provider, native_session_id)?;
        let _lifecycle = match &existing { Some(row) => Some(self.lifecycle_guard(&row.owned_id).await?), None => None };
        let open = existing.is_some_and(|row| self.sessions.lock().unwrap_or_else(std::sync::PoisonError::into_inner).contains_key(&row.owned_id));
        let _one_at_a_time = import_reading();
        super::history::begin_import_session(&self.store, provider, native_session_id, path, cwd, title, project_id, open)
    }

    /// Reads the newest page of an imported transcript into a session that has
    /// already been named, and returns how many records it gained.
    pub fn finish_import_transcript_session(&self, owned_id: &str, max_bytes: u64, max_records: usize) -> Result<usize, String> {
        super::history::finish_import_session(&self.store, owned_id, max_bytes, max_records)
    }

    /// Hydrate the disposable runtime overlay only for an operation that needs
    /// mutable ACP state. Reads and rail listing use SQLite directly.
    fn hydrate_overlay_from_store(&self, owned_id: &str) -> Result<bool, String> {
        if self
            .sessions
            .lock()
            .unwrap_or_else(std::sync::PoisonError::into_inner)
            .contains_key(owned_id)
        {
            return Ok(true);
        }
        let Some(row) = self
            .store
            .get_session(owned_id)
            .map_err(|error| error.to_string())?
        else {
            return Ok(false);
        };
        let mut session = recovered_session_from_row(&self.store, row)?;
        let mut sessions = self
            .sessions
            .lock()
            .unwrap_or_else(std::sync::PoisonError::into_inner);
        if !sessions.contains_key(owned_id) {
            disconnect_claude_children(&mut session, &self.emitter);
            sessions.insert(owned_id.to_string(), session);
        }
        Ok(true)
    }

    pub fn extend_imported_session(&self, owned_id: &str, max_bytes: u64, max_records: usize) -> Result<super::history::ExtendedImport, String> {
        let _one_at_a_time = import_reading();
        super::history::extend_session(&self.store, owned_id, max_bytes, max_records)
    }

    pub fn select_child_history(
        &self,
        parent_owned_id: &str,
        child_session_id: &str,
        request_id: u64,
        max_bytes: u32,
        import_max_bytes: u64,
        import_max_records: usize,
    ) -> Result<AgentConversationChildHistorySelection, String> {
        self.reserve_child_history_request(parent_owned_id, request_id);
        self.select_reserved_child_history(
            parent_owned_id,
            child_session_id,
            request_id,
            max_bytes,
            import_max_bytes,
            import_max_records,
        )
    }

    pub(crate) fn reserve_child_history_request(
        &self,
        parent_owned_id: &str,
        request_id: u64,
    ) {
        let (wake, _changes) = std_mpsc::sync_channel(1);
        let previous = self
            .child_history_watchers
            .lock()
            .unwrap_or_else(std::sync::PoisonError::into_inner)
            .insert(
                parent_owned_id.to_string(),
                ChildHistoryWatcher {
                    request_id,
                    watcher: None,
                    stop: Arc::new(AtomicBool::new(false)),
                    wake,
                    worker: None,
                },
            );
        if let Some(previous) = previous {
            previous.stop();
        }
    }

    pub(crate) fn select_reserved_child_history(
        &self,
        parent_owned_id: &str,
        child_session_id: &str,
        request_id: u64,
        max_bytes: u32,
        import_max_bytes: u64,
        import_max_records: usize,
    ) -> Result<AgentConversationChildHistorySelection, String> {
        let (wake, changes) = std_mpsc::sync_channel(1);
        let stop = Arc::new(AtomicBool::new(false));
        let previous = self.replace_reserved_child_history(
            parent_owned_id,
            ChildHistoryWatcher {
                request_id,
                watcher: None,
                stop: stop.clone(),
                wake: wake.clone(),
                worker: None,
            },
        )?;
        if let Some(previous) = previous {
            previous.stop();
        }
        let result = (|| {
        let parent = self
            .store
            .get_session(parent_owned_id)
            .map_err(|error| error.to_string())?
            .ok_or_else(|| format!("Conversation session {parent_owned_id} was not found"))?;
        let provider = transcript::parse_provider(&parent.provider)?;
        let parent_native_id = parent
            .native_session_id
            .as_deref()
            .ok_or_else(|| "The parent conversation has no provider session".to_string())?;
        let child_owned_id =
            super::history::child_owned_id(parent_owned_id, child_session_id)?;
        let saved_page = self
            .store
            .get_session(&child_owned_id)
            .map_err(|error| error.to_string())?
            .map(|_| self.list_items_before(&child_owned_id, i64::MAX, max_bytes))
            .transpose()?;
        let saved_exists = saved_page.is_some();
        let watch_directory = match transcript::child_watch_directory(
            provider,
            parent_native_id,
            child_session_id,
        ) {
            Ok(directory) => directory,
            Err(error) => {
                if let Some(page) = saved_page {
                    crate::debug_log::stderr_log!(
                        "Could not restore selected child transcript watch: {error}"
                    );
                    self.stop_child_history(parent_owned_id, request_id);
                    return Ok(AgentConversationChildHistorySelection {
                        history_owned_id: child_owned_id,
                        page,
                    });
                }
                return Err(error);
            }
        };
        let known_path = transcript::discover_child(provider, parent_native_id, child_session_id)
            .ok()
            .flatten()
            .map(|location| location.canonical_path);

        let callback_wake = wake.clone();
        let watched_child = child_session_id.to_string();
        let mut watcher = notify::recommended_watcher(
            move |result: notify::Result<notify::Event>| {
                let Ok(event) = result else { return; };
                if child_watch_event_relevant(
                    &event,
                    known_path.as_deref(),
                    provider,
                    &watched_child,
                ) {
                    let _ = callback_wake.try_send(());
                }
            },
        )
        .map_err(|error| format!("Could not watch child transcript: {error}"))?;
        if let Err(error) = watcher.watch(&watch_directory, RecursiveMode::Recursive) {
            if let Some(page) = saved_page {
                crate::debug_log::stderr_log!(
                    "Could not restore selected child transcript watch: {error}"
                );
                self.stop_child_history(parent_owned_id, request_id);
                return Ok(AgentConversationChildHistorySelection {
                    history_owned_id: child_owned_id,
                    page,
                });
            }
            return Err(format!("Could not watch child transcript directory: {error}"));
        }

        self.attach_child_history_watcher(parent_owned_id, request_id, watcher)?;
        let initial = import_selected_child(
            &self.store,
            &self.emitter,
            parent_owned_id,
            child_session_id,
            import_max_bytes,
            import_max_records,
            false,
            Some(&stop),
        );
        if let Err(error) = &initial {
            if !saved_exists && !error.contains("was not found") {
                self.stop_child_history(parent_owned_id, request_id);
                return Err(error.clone());
            }
            crate::debug_log::stderr_log!("Could not refresh selected child transcript: {error}");
        }

        let worker_stop = stop.clone();
        let worker_store = self.store.clone();
        let worker_emitter = self.emitter.clone();
        let worker_parent = parent_owned_id.to_string();
        let worker_child = child_session_id.to_string();
        let worker = match thread::Builder::new()
            .name(format!("child-history-{request_id}"))
            .spawn(move || {
                while changes.recv().is_ok() {
                    if worker_stop.load(Ordering::Acquire) {
                        break;
                    }
                    if let Err(error) = import_selected_child(
                        &worker_store,
                        &worker_emitter,
                        &worker_parent,
                        &worker_child,
                        import_max_bytes,
                        import_max_records,
                        true,
                        Some(&worker_stop),
                    ) {
                        crate::debug_log::stderr_log!(
                            "Could not refresh selected child transcript: {error}"
                        );
                    }
                }
            }) {
                Ok(worker) => worker,
                Err(error) => {
                    self.stop_child_history(parent_owned_id, request_id);
                    return Err(format!("Could not start child transcript watcher: {error}"));
                }
            };
        let mut watchers = self
            .child_history_watchers
            .lock()
            .unwrap_or_else(std::sync::PoisonError::into_inner);
        if let Some(current) = watchers
            .get_mut(parent_owned_id)
            .filter(|current| current.request_id == request_id)
        {
            current.worker = Some(worker);
        } else {
            stop.store(true, Ordering::Release);
            let _ = wake.try_send(());
            let _ = worker.join();
        }
        drop(watchers);
        let page = self.list_items_before(&child_owned_id, i64::MAX, max_bytes)?;
        Ok(AgentConversationChildHistorySelection {
            history_owned_id: child_owned_id,
            page,
        })
        })();
        if result.is_err() {
            self.stop_child_history(parent_owned_id, request_id);
        }
        result
    }

    fn replace_reserved_child_history(
        &self,
        parent_owned_id: &str,
        reservation: ChildHistoryWatcher,
    ) -> Result<Option<ChildHistoryWatcher>, String> {
        let mut watchers = self
            .child_history_watchers
            .lock()
            .unwrap_or_else(std::sync::PoisonError::into_inner);
        if !watchers
            .get(parent_owned_id)
            .is_some_and(|current| current.request_id == reservation.request_id)
        {
            return Err("Child transcript selection was replaced".to_string());
        }
        Ok(watchers.insert(parent_owned_id.to_string(), reservation))
    }

    fn attach_child_history_watcher(
        &self,
        parent_owned_id: &str,
        request_id: u64,
        watcher: notify::RecommendedWatcher,
    ) -> Result<(), String> {
        let mut watchers = self
            .child_history_watchers
            .lock()
            .unwrap_or_else(std::sync::PoisonError::into_inner);
        let Some(current) = watchers
            .get_mut(parent_owned_id)
            .filter(|current| current.request_id == request_id)
        else {
            return Err("Child transcript selection was replaced".to_string());
        };
        if current.stop.load(Ordering::Acquire) {
            return Err("Child transcript selection was stopped".to_string());
        }
        current.watcher = Some(watcher);
        Ok(())
    }

    pub fn stop_child_history(&self, parent_owned_id: &str, request_id: u64) -> bool {
        let watcher = {
            let mut watchers = self
                .child_history_watchers
                .lock()
                .unwrap_or_else(std::sync::PoisonError::into_inner);
            if watchers
                .get(parent_owned_id)
                .is_some_and(|watcher| watcher.request_id == request_id)
            {
                watchers.remove(parent_owned_id)
            } else {
                None
            }
        };
        if let Some(watcher) = watcher {
            watcher.stop();
            true
        } else {
            false
        }
    }

    pub fn cancel_child_history_request(&self, request_id: u64) {
        let parent = self
            .child_history_watchers
            .lock()
            .unwrap_or_else(std::sync::PoisonError::into_inner)
            .iter()
            .find_map(|(parent, watcher)| {
                (watcher.request_id == request_id).then(|| parent.clone())
            });
        if let Some(parent) = parent {
            self.stop_child_history(&parent, request_id);
        }
    }

    async fn release_pool_scope(
        &self,
        pool_key: &AdapterPoolKey,
        owned_id: &str,
        native_session_id: Option<&str>,
        close_native_session: bool,
    ) -> Result<(), String> {
        let mut pools = self.adapter_pools.lock().await;
        let Some(pool) = pools.get_mut(pool_key) else {
            return Ok(());
        };
        if close_native_session {
            if let Some(native_session_id) = native_session_id {
                pool.runtime
                    .lock()
                    .await
                    .close_native_session(native_session_id)
                    .await
                    .map_err(|error| error.to_string())?;
            }
        }
        let last_member = pool.members.len() == 1 && pool.members.contains(owned_id);
        if last_member {
            let runtime = Arc::clone(&pool.runtime);
            if close_native_session {
                runtime
                    .lock()
                    .await
                    .close_session()
                    .await
                    .map_err(|error| error.to_string())?;
            } else {
                runtime
                    .lock()
                    .await
                    .detach_session()
                    .await
                    .map_err(|error| error.to_string())?;
            }
        }
        pool.members.remove(owned_id);
        if pool.members.is_empty() {
            pools.remove(pool_key);
        }
        Ok(())
    }

    pub fn providers(&self) -> &ProviderRegistry {
        &self.providers
    }

    /// Read the provider's current choices without creating an Assembly session.
    pub async fn probe_provider_config(
        &self,
        provider: AgentConversationProvider,
        cwd: &str,
    ) -> Result<AgentConversationConfigState, String> {
        let cwd = validated_conversation_cwd(cwd)?;
        let manifest = self.providers.manifest(provider)?;
        let probe = async {
            let mut adapter = AcpRuntimeAdapter::with_environment(
                manifest,
                "provider-catalog".into(),
                cwd.clone(),
                session_spawn_environment(provider).without_assembly_tool_approval(),
            );
            let result = async {
                adapter.initialize(InitializeAgentInput { provider }).await?;
                let started = adapter.new_session(NewAgentSession { cwd, mcp_servers: vec![] }).await?;
                let mut config = started.config;
                // A model's default effort is only reported while it is the
                // session's model, so each other model is selected once here.
                let others = config.model_efforts.iter()
                    .filter(|(model, efforts)| !efforts.is_empty() && Some(model.as_str()) != config.model.as_deref())
                    .map(|(model, _)| model.clone()).collect::<Vec<_>>();
                for model in others {
                    let update = AgentConversationConfigUpdate { model: Some(model), ..Default::default() };
                    if let Ok(switched) = adapter.set_conversation_config_on(&started.native_session_id, &update).await {
                        config.model_default_efforts.extend(switched.model_default_efforts);
                    }
                }
                Ok::<_, AgentRuntimeError>(config)
            }
            .await;
            // session/new may leave a provider-native empty session, but its
            // adapter process never remains resident and no Assembly row is saved.
            let stopped = adapter.detach_session().await;
            let config = result.map_err(|error| authentication::explain(provider, error.to_string()))?;
            stopped.map_err(|error| error.to_string())?;
            Ok(config)
        };
        tokio::time::timeout(Duration::from_secs(15), probe)
            .await
            .map_err(|_| "Provider catalog did not answer within 15 seconds".to_string())?
    }

    pub async fn probe_provider_config_for_request(
        &self,
        provider: AgentConversationProvider,
        cwd: &str,
        request_id: u64,
    ) -> Result<AgentConversationConfigState, String> {
        tokio::select! {
            biased;
            _ = self.wait_for_probe_cancellation(request_id) => Err("Provider catalog request was cancelled".into()),
            result = self.probe_provider_config(provider, cwd) => result,
        }
    }

    pub async fn wait_for_probe_cancellation(&self, request_id: u64) {
        let mut cancellations = self.probe_cancellations.subscribe();
        loop {
            if *cancellations.borrow_and_update() >= request_id { break; }
            if cancellations.changed().await.is_err() { break; }
        }
    }

    pub fn cancel_provider_probe(&self, request_id: u64) {
        self.probe_cancellations.send_modify(|latest| *latest = (*latest).max(request_id));
    }

    pub fn provider_probe_cancelled(&self, request_id: u64) -> bool {
        *self.probe_cancellations.borrow() >= request_id
    }

    pub fn add_session_annotation(
        &self,
        owned_id: &str,
        url: &str,
        rect_json: &str,
        note: &str,
    ) -> Result<AnnotationRow, String> {
        let id = self
            .store
            .add_annotation(owned_id, url, rect_json, note)
            .map_err(|error| error.to_string())?;
        self.store
            .list_annotations(owned_id)
            .map_err(|error| error.to_string())?
            .into_iter()
            .find(|annotation| annotation.id == id)
            .ok_or_else(|| "The saved session annotation was not found".to_string())
    }

    pub fn list_session_annotations(&self, owned_id: &str) -> Result<Vec<AnnotationRow>, String> {
        self.store
            .list_annotations(owned_id)
            .map_err(|error| error.to_string())
    }

    pub fn delete_session_annotation(&self, id: i64) -> Result<(), String> {
        self.store
            .delete_annotation(id)
            .map_err(|error| error.to_string())
    }

    pub fn set_emitter(&self, emitter: ConversationEmitter) {
        let mut current = self
            .emitter
            .lock()
            .unwrap_or_else(std::sync::PoisonError::into_inner);
        *current = Some(emitter);
    }

    pub fn set_broker_emitter(&self, emitter: BrokerEmitter) {
        let mut current = self
            .broker_emitter
            .lock()
            .unwrap_or_else(std::sync::PoisonError::into_inner);
        *current = Some(emitter);
    }

    fn sync_broker_status(
        &self,
        owned_id: &str,
        generation: u64,
        runtime_error: bool,
    ) -> Result<(), String> {
        let (turn_active, awaiting_permission_or_input, session_closed) = {
            let sessions = self
                .sessions
                .lock()
                .unwrap_or_else(std::sync::PoisonError::into_inner);
            let session = current_session(&sessions, owned_id, generation)?;
            (
                session.active_turn_id.is_some(),
                !session.permission_requests.is_empty() || !session.user_input_requests.is_empty(),
                session.state == AgentRuntimeState::Closed,
            )
        };
        let Some((group_id, orchestrator)) = self
            .store
            .open_group_for(owned_id)
            .map_err(|error| error.to_string())?
        else {
            return Ok(());
        };
        let decision_pending = self
            .store
            .decision_pending_for(owned_id)
            .map_err(|error| error.to_string())?;
        let status = status_for(
            turn_active,
            awaiting_permission_or_input,
            decision_pending,
            runtime_error,
            session_closed,
        );
        let mut statuses = self
            .broker_statuses
            .lock()
            .unwrap_or_else(std::sync::PoisonError::into_inner);
        if statuses.get(owned_id) == Some(&status) {
            return Ok(());
        }
        let envelope = self
            .store
            .append_message(
                &group_id,
                owned_id,
                orchestrator.as_deref().unwrap_or("orchestrator"),
                mcb_core::broker::MessageKind::Status,
                status.as_str(),
            )
            .map_err(|error| error.to_string())?;
        statuses.insert(owned_id.to_string(), status);
        let callback = self
            .broker_emitter
            .lock()
            .unwrap_or_else(std::sync::PoisonError::into_inner)
            .clone();
        if let Some(callback) = callback {
            callback(WorkflowBrokerEvent {
                group_id,
                envelope: envelope.into(),
            });
        }
        Ok(())
    }

    /// Starts a helper in `folder` or the parent's folder, filed under that folder's project,
    /// lists it among the parent's agents, and sends it `prompt` as its first
    /// turn. Returns the helper's owned id.
    pub(super) async fn start_helper(
        &self, parent: &str, provider: AgentConversationProvider,
        model: Option<String>, name: &str, prompt: &str, folder: Option<&str>,
    ) -> Result<String, String> {
        let parent_row = self.store.get_session(parent).map_err(|error| error.to_string())?
            .ok_or_else(|| "The calling session was not found".to_string())?;
        let cwd = folder.unwrap_or(&parent_row.cwd);
        let project_id = if let Some(folder) = folder {
            if !std::path::Path::new(folder).is_absolute() || !std::path::Path::new(folder).is_dir() {
                return Err("folder must be an existing folder on this machine".into());
            }
            crate::project_folders::match_local_folders(&self.store, &[folder.to_string()])?
                .into_iter().next().map(|(_, id)| id).or(parent_row.project_id.clone())
        } else { parent_row.project_id.clone() };
        // The helper hears its instructions as its parent's message, never as the owner's.
        let sender = self.store.directory_name(parent).map_err(|error| error.to_string())?
            .map_or_else(|| parent.to_string(), |(handle, title)| format!("{handle} · {title}"));
        self.providers.manifest(provider)?;
        let owned_id = uuid::Uuid::new_v4().to_string();
        let connection = self.ensure_async(EnsureAgentConversationRequest {
            owned_id: owned_id.clone(), provider, cwd: cwd.to_string(), native_session_id: None,
            execution_environment: ExecutionEnvironment::Local, remote_profile_id: None,
            native_session_mode: AgentNativeSessionMode::Resume, reasoning_effort: None,
            project_id: project_id.clone(),
        }).await?;
        self.hydrate_overlay_from_store(&owned_id)?;
        {
            let mut sessions = self.sessions.lock().unwrap_or_else(std::sync::PoisonError::into_inner);
            let session = current_session_mut(&mut sessions, &owned_id, connection.generation)?;
            session.helper_of = Some(parent.to_string());
            session.rail_meta.title = Some(name.to_string());
            session.rail_meta.project = if project_id == parent_row.project_id { parent_row.project.clone() }
                else { None };
            session.title_source = Some(TITLE_SOURCE_USER.to_string());
            persist_session(session)?;
        }
        // The rail adds a session it has not seen when it hears its name.
        let listener = self.renamed_listener.lock().unwrap_or_else(std::sync::PoisonError::into_inner).clone();
        if let Some(listener) = listener {
            listener(&owned_id, name);
        }
        self.show_helper_to_parent(parent, &owned_id, name, "running")?;
        let prompt = AgentPrompt { text: format!("[message from {sender}]\n{prompt}"), images: Vec::new(), attachment_ids: Vec::new() };
        // A helper works unattended, so it starts in Auto whatever mode its parent is in.
        let approval_policy = crate::workflow::worker_permission(provider, None);
        if let Err(error) = self.send_message(&owned_id, connection.generation, prompt, model, approval_policy).await {
            if let Err(show_error) = self.show_helper_to_parent(parent, &owned_id, name, "failed") {
                crate::debug_log::stderr_log!("Could not mark the helper failed: {show_error}");
            }
            return Err(error);
        }
        Ok(owned_id)
    }

    /// Updates the helper's row in its parent's agents list.
    fn show_helper_to_parent(&self, parent: &str, helper: &str, name: &str, state: &str) -> Result<(), String> {
        self.hydrate_overlay_from_store(parent)?;
        let mut sessions = self.sessions.lock().unwrap_or_else(std::sync::PoisonError::into_inner);
        let Some(session) = sessions.get_mut(parent) else { return Ok(()) };
        record_payload_for_session_and_dispatch(session, &self.emitter, AgentConversationPayload::ChildUpdate {
            child_id: helper.to_string(),
            parent_tool_call_id: String::new(),
            parent_id: Some(parent.to_string()),
            transcript_id: None,
            label: Some(name.to_string()),
            state: state.to_string(),
            latest_activity: None,
        }).map(|_| ())
    }

    /// Queues a helper's final reply for the session that started it and
    /// wakes that session. Sessions that are not helpers report nothing.
    fn report_helper_turn(&self, owned_id: &str, turn_id: &str, outcome: &str) -> Result<(), String> {
        let (parent, name) = {
            let sessions = self.sessions.lock().unwrap_or_else(std::sync::PoisonError::into_inner);
            let Some(session) = sessions.get(owned_id) else { return Ok(()) };
            let Some(parent) = session.helper_of.clone() else { return Ok(()) };
            (parent, session.rail_meta.title.clone().unwrap_or_else(|| owned_id.to_string()))
        };
        let reply = self.store.latest_assistant_text_for_turn(owned_id, turn_id)
            .map_err(|error| error.to_string())?.unwrap_or_default();
        // The reply is the helper's own text, so it may not pass as another sender's label.
        let reply = super::messaging::quote_labels(&reply.chars().take(HELPER_REPLY_LIMIT).collect::<String>());
        self.store.append_message("", owned_id, &parent, mcb_core::broker::MessageKind::Message,
            &format!("[{name} {outcome}]\n{reply}")).map_err(|error| error.to_string())?;
        let state = match outcome { "finished" => "finished", "failed" => "failed", _ => "cancelled" };
        self.show_helper_to_parent(&parent, owned_id, &name, state)?;
        let manager = self.clone();
        tauri::async_runtime::spawn(async move { manager.deliver_if_idle(&parent).await });
        Ok(())
    }

    /// Delivers queued messages between turns, or steers an authorized message into a busy helper.
    /// Boxed because activation hands this to the messaging endpoint, and the
    /// send below activates again.
    pub(crate) fn deliver_if_idle<'a>(&'a self, owned_id: &'a str)
        -> std::pin::Pin<Box<dyn std::future::Future<Output = ()> + Send + 'a>> {
        Box::pin(async move {
        let generation = match self.hydrate_overlay_from_store(owned_id) {
            Ok(true) => self.sessions.lock().unwrap_or_else(std::sync::PoisonError::into_inner)
                .get(owned_id).filter(|session| session.state != AgentRuntimeState::Closed)
                .map(|session| session.generation),
            Ok(false) => None,
            Err(error) => {
                crate::debug_log::stderr_log!("Could not load message target {owned_id}: {error}");
                None
            }
        };
        let Some(generation) = generation else { return };
        if let Err(error) = self
            .drain_broker_message_at(owned_id, generation, store_timestamp(timestamp_millis()))
            .await
        {
            crate::debug_log::stderr_log!("Could not deliver broker message: {error}");
        }
        })
    }

    /// The session's generation when it can take a message now: no turn of
    /// any kind running and nothing waiting on the person.
    fn idle_generation(&self, owned_id: &str) -> Option<u64> {
        let sessions = self.sessions.lock().unwrap_or_else(std::sync::PoisonError::into_inner);
        sessions.get(owned_id).filter(|session| {
            session.active_turn_id.is_none()
                && !session.autonomous_turn_started
                && !session.prompt_once_active
                && session.permission_requests.is_empty()
                && session.user_input_requests.is_empty()
                && session.state != AgentRuntimeState::Closed
        }).map(|session| session.generation)
    }

    async fn drain_broker_message_at(
        &self,
        owned_id: &str,
        generation: u64,
        now_ms: i64,
    ) -> Result<bool, String> {
        // A second caller while one is sending marks the session to be read
        // again, so a message queued meanwhile is not left behind.
        {
            let mut delivering = self.delivering.lock().unwrap_or_else(std::sync::PoisonError::into_inner);
            if let Some(again) = delivering.get_mut(owned_id) {
                *again = true;
                return Ok(false);
            }
            delivering.insert(owned_id.to_string(), false);
        }
        let mut delivered_any = false;
        loop {
            let delivered = self.drain_next_message(owned_id, generation, now_ms).await;
            delivered_any |= matches!(delivered, Ok(true));
            let mut delivering = self.delivering.lock().unwrap_or_else(std::sync::PoisonError::into_inner);
            // Steering can leave more messages ready in the same active turn.
            if !matches!(delivered, Ok(true)) && delivering.get(owned_id) != Some(&true) {
                delivering.remove(owned_id);
                return delivered.map(|_| delivered_any);
            }
            delivering.insert(owned_id.to_string(), false);
        }
    }

    async fn drain_next_message(
        &self,
        owned_id: &str,
        generation: u64,
        now_ms: i64,
    ) -> Result<bool, String> {
        let steering_parent = {
            let sessions = self.sessions.lock().unwrap_or_else(std::sync::PoisonError::into_inner);
            sessions.get(owned_id).filter(|session| session.active_turn_id.is_some()
                && session.capabilities.session.steering).and_then(|session| session.helper_of.clone())
        };
        // An unrelated sender must not hold back the helper's parent or siblings.
        // Expired messages fail in order, so the first live one is sent.
        let message = loop {
            let Some(message) = self
                .store
                .pending_for_sender(owned_id, steering_parent.as_deref())
                .map_err(|error| error.to_string())?
            else {
                return Ok(false);
            };
            if now_ms.saturating_sub(message.created_at_ms) <= BROKER_MESSAGE_TTL_MS {
                break message;
            }
            self.store
                .set_receipt(&message.id, mcb_core::broker::Receipt::Failed)
                .map_err(|error| error.to_string())?;
        };

        let sibling_parent = self.store.sibling_parent(&message.from_agent, owned_id)
            .map_err(|error| error.to_string())?;
        let steering = {
            let sessions = self.sessions.lock().unwrap_or_else(std::sync::PoisonError::into_inner);
            sessions.get(owned_id).is_some_and(|session| session.active_turn_id.is_some()
                && session.capabilities.session.steering
                && session.helper_of.is_some()
                && (session.helper_of.as_deref() == Some(message.from_agent.as_str())
                    || session.helper_of == sibling_parent))
        };
        if !steering && self.sessions.lock().unwrap_or_else(std::sync::PoisonError::into_inner).contains_key(owned_id)
            && self.idle_generation(owned_id).is_none()
        {
            return Ok(false);
        }
        let prompt = AgentPrompt {
            text: format!(
                "[message from {}]\n{}",
                message.sender_name, message.body
            ),
            images: Vec::new(),
            attachment_ids: Vec::new(),
        };
        let result = self
            .send_message(owned_id, generation, prompt, None, None)
            .await;
        // A turn that began while this was being sent refuses it; the message
        // stays queued for that turn's end instead of failing.
        let busy = self.sessions.lock().unwrap_or_else(std::sync::PoisonError::into_inner)
            .contains_key(owned_id) && self.idle_generation(owned_id).is_none();
        if result.is_err() && (steering || busy) {
            return Ok(false);
        }
        self.store
            .set_receipt(
                &message.id,
                if result.is_ok() {
                    mcb_core::broker::Receipt::Delivered
                } else {
                    mcb_core::broker::Receipt::Failed
                },
            )
            .map_err(|error| error.to_string())?;
        result.map(|_| true)
    }

    pub fn set_session_namer(&self, namer: SessionNamer) {
        let mut current = self
            .namer
            .lock()
            .unwrap_or_else(std::sync::PoisonError::into_inner);
        *current = Some(namer);
    }

    pub fn set_session_renamed_listener(&self, listener: SessionRenamedListener) {
        let mut current = self
            .renamed_listener
            .lock()
            .unwrap_or_else(std::sync::PoisonError::into_inner);
        *current = Some(listener);
    }

    /// Snapshot the provider processes that this registry currently owns.
    /// The async runtime mutex is intentionally sampled with `try_lock`: a
    /// resource refresh must never block an agent turn. A session that is in a
    /// transition simply appears on the next refresh.
    pub fn resource_roots(&self) -> Vec<AgentResourceRoot> {
        let sessions = self
            .sessions
            .lock()
            .unwrap_or_else(std::sync::PoisonError::into_inner);
        sessions
            .values()
            .filter_map(|session| {
                let runtime = session.runtime.as_ref()?;
                let runtime = runtime.try_lock().ok()?;
                let pid = runtime.process_id()?;
                Some(AgentResourceRoot {
                    pid,
                    owned_id: session.owned_id.clone(),
                    provider: session.provider,
                    cwd: session.cwd.clone(),
                })
            })
            .collect()
    }

    pub fn resource_diagnostics(&self) -> Result<AgentRuntimeDiagnostics, String> {
        let durable_session_rows = self
            .store
            .count_sessions()
            .map_err(|error| error.to_string())?;
        let sessions = self
            .sessions
            .lock()
            .unwrap_or_else(std::sync::PoisonError::into_inner);
        let diagnostics = AgentRuntimeDiagnostics {
            durable_session_rows,
            live_session_overlays: sessions.len(),
            live_runtime_handles: sessions
                .values()
                .filter(|session| session.runtime.is_some())
                .count(),
            sidecar_processes: sessions
                .values()
                .filter_map(|session| session.runtime.as_ref())
                .filter_map(|runtime| runtime.try_lock().ok())
                .filter(|runtime| runtime.process_id().is_some())
                .count(),
            active_turns: sessions
                .values()
                .filter(|session| session.active_turn_id.is_some())
                .count(),
            pending_permissions: sessions
                .values()
                .map(|session| session.permission_requests.len())
                .sum(),
            pending_inputs: sessions
                .values()
                .map(|session| session.user_input_requests.len())
                .sum(),
            live_tool_calls: sessions
                .values()
                .map(|session| session.live_tool_calls.len())
                .sum(),
            background_work: sessions
                .values()
                .map(|session| session.background_work.len())
                .sum(),
            suspendable_sessions: sessions
                .values()
                .filter(|session| session_can_suspend(session))
                .count(),
            adapter_pools: None,
        };
        drop(sessions);
        Ok(AgentRuntimeDiagnostics {
            adapter_pools: self.adapter_pools.try_lock().ok().map(|pools| pools.len()),
            ..diagnostics
        })
    }

    /// Holds a sign-in open so a test can see the manager as busy.
    #[cfg(test)]
    pub(crate) fn begin_test_authentication(&self) -> super::providers::authentication::AuthenticationAttempt {
        self.authentications.begin("test", 0).unwrap()
    }

    pub fn has_pending_provider_work(&self) -> bool {
        if self.authentications.pending() { return true; }
        self.sessions
            .lock()
            .unwrap_or_else(std::sync::PoisonError::into_inner)
            .values()
            .any(|session| session.runtime.is_some() && !session_is_quiescent(session))
    }

    /// Returns the capability snapshot advertised by the active ACP session.
    #[cfg(test)]
    pub fn capabilities(
        &self,
        owned_id: &str,
        generation: u64,
    ) -> Result<AgentCapabilities, String> {
        let sessions = self
            .sessions
            .lock()
            .unwrap_or_else(std::sync::PoisonError::into_inner);
        Ok(current_session(&sessions, owned_id, generation)?
            .capabilities
            .clone())
    }

    /// Return the current capability snapshot for a session-owned identifier.
    /// The native read command is intentionally generation-free because it is a
    /// snapshot read, not a mutating operation; the map contains only the
    /// current generation for each owned session.
    pub fn capabilities_for_owned_id(&self, owned_id: &str) -> Result<AgentCapabilities, String> {
        if let Some(capabilities) = self
            .sessions
            .lock()
            .unwrap_or_else(std::sync::PoisonError::into_inner)
            .get(owned_id)
            .map(|session| session.capabilities.clone())
        {
            return Ok(capabilities);
        }
        let row = self
            .store
            .get_session(owned_id)
            .map_err(|error| error.to_string())?
            .ok_or_else(|| "Conversation session was not found".to_string())?;
        Ok(stored_session_projection(&self.store, &row)?.1.capabilities)
    }

    /// Ensure an app-owned ACP session while serializing it with activation.
    /// Replacing a failed or changed generation detaches the previous transport
    /// before the caller can activate the new one.
    pub async fn ensure_async(
        &self,
        request: EnsureAgentConversationRequest,
    ) -> Result<AgentConversationConnection, String> {
        let owned_id = required_id(&request.owned_id, "Owned session id")?;
        let activation_lock = self.activation_lock(&owned_id)?;
        let _activation = activation_lock.lock().await;
        let (connection, mut prior) = self.ensure_inner(request)?;
        if let Some(previous) = prior.as_ref() {
            if let Some(pool_key) = previous.pool_key.as_ref() {
                let cleanup = self
                    .release_pool_scope(pool_key, &owned_id, None, false)
                    .await;
                if let Err(error) = cleanup {
                    let mut sessions = self
                        .sessions
                        .lock()
                        .unwrap_or_else(std::sync::PoisonError::into_inner);
                    let previous = prior.take().expect("prior session disappeared");
                    sessions.insert(owned_id.clone(), previous);
                    let restored = sessions
                        .get(&owned_id)
                        .expect("prior session was not restored");
                    persist_session(restored)?;
                    return Err(error);
                }
            } else if let Some(transport) = previous.transport.as_ref() {
                transport.stop().await;
            }
            if let Some(task) = prior
                .as_mut()
                .and_then(|session| session.child_rollout_scan.take())
            {
                task.abort();
            }
            if let Some(task) = prior
                .as_mut()
                .and_then(|session| session.cancel_deadline.take())
            {
                task.abort();
            }
        }
        let mut sessions = self
            .sessions
            .lock()
            .unwrap_or_else(std::sync::PoisonError::into_inner);
        let disposable = sessions.get(&owned_id).is_some_and(|session| {
            session.generation == connection.generation
                && session.runtime.is_none()
                && session.transport.is_none()
                && session.ordered_events.is_none()
                && session.active_turn_id.is_none()
                && !session.prompt_once_active
                && session.permission_requests.is_empty()
                && session.user_input_requests.is_empty()
                && session.live_tool_calls.is_empty()
                && session.background_work.is_empty()
                && !session.suspending
        });
        if disposable {
            sessions.remove(&owned_id);
        }
        Ok(connection)
    }

    /// Changes the durable checkout for one session without replacing
    /// its generation or native session. A running adapter is detached only
    /// after the quiescent gate passes; the checkout marker and the resulting
    /// suspended session row are then committed in the same SQLite transaction.
    pub async fn change_checkout(
        &self,
        request: ChangeAgentConversationCheckoutRequest,
    ) -> Result<AgentConversationSessionRecord, String> {
        let owned_id = required_id(&request.owned_id, "Owned session id")?;
        let new_cwd = validated_conversation_cwd(&request.cwd)?
            .display()
            .to_string();
        self.hydrate_overlay_from_store(&owned_id)?;
        let _lifecycle = self.lifecycle_guard(&owned_id).await?;
        let (
            runtime,
            transport,
            ordered_events,
            pool_key,
            native_session_id,
            child_rollout_scan,
            from_cwd,
        ) = {
            let mut sessions = self
                .sessions
                .lock()
                .unwrap_or_else(std::sync::PoisonError::into_inner);
            let session = lifecycle_session_mut(&mut sessions, &owned_id, request.generation)?;
            if !super::capabilities::provider_supports_changing_checkout(&session.capabilities) {
                return Err("This session's agent can't keep its conversation in another checkout.".to_string());
            }
            if session.cwd == new_cwd {
                return Err("Session is already using this checkout".to_string());
            }
            if !session_can_change_checkout(session) {
                return Err("Checkout changes are available only while the session is quiescent".to_string());
            }
            let from_cwd = session.cwd.clone();
            if session.runtime.is_some() {
                session.suspending = true;
            }
            (
                session.runtime.take(),
                session.transport.take(),
                session.ordered_events.take(),
                session.pool_key.take(),
                session.native_session_id.clone(),
                session.child_rollout_scan.take(),
                from_cwd,
            )
        };

        let had_runtime = runtime.is_some();
        if let Some(runtime) = runtime.as_ref() {
            let detach_result = if let Some(pool_key) = &pool_key {
                self.release_pool_scope(pool_key, &owned_id, native_session_id.as_deref(), false)
                    .await
            } else {
                runtime
                    .lock()
                    .await
                    .detach_session()
                    .await
                    .map_err(|error| error.to_string())
            };
            if let Err(error) = detach_result {
                let mut sessions = self
                    .sessions
                    .lock()
                    .unwrap_or_else(std::sync::PoisonError::into_inner);
                if let Ok(session) =
                    lifecycle_session_mut(&mut sessions, &owned_id, request.generation)
                {
                    session.suspending = false;
                    session.runtime = Some(Arc::clone(runtime));
                    session.transport = transport;
                    session.ordered_events = ordered_events;
                    session.pool_key = pool_key;
                    session.child_rollout_scan = child_rollout_scan;
                }
                return Err(error);
            }
        }
        if let Some(task) = child_rollout_scan {
            task.abort();
        }

        let mut sessions = self
            .sessions
            .lock()
            .unwrap_or_else(std::sync::PoisonError::into_inner);
        let session = lifecycle_session_mut(&mut sessions, &owned_id, request.generation)?;
        if had_runtime && (!session.suspending || session.state != AgentRuntimeState::Ready) {
            session.suspending = false;
            session.state = AgentRuntimeState::Suspended;
            session.connection.state = ConversationConnectionState::Disconnected;
            session.owner = AgentExecutionOwner::Stopped;
            session.writer_lease.owner = AgentWriterLeaseOwner::None;
            session.native_session_mode = AgentNativeSessionMode::Resume;
            let _ = persist_session(session);
            return Err("Conversation lifecycle changed while checkout was preparing".to_string());
        }
        let previous_worktree = session.rail_meta.worktree.clone();
        session.suspending = false;
        session.cwd = new_cwd.clone();
        session.rail_meta.worktree = Some(new_cwd.clone());
        let payload = AgentConversationPayload::CheckoutChanged {
            from_cwd: from_cwd.clone(),
            to_cwd: new_cwd,
        };
        let event = if had_runtime {
            record_payload_for_session_and_dispatch_with_lifecycle(
                session,
                &self.emitter,
                payload,
                SessionLifecycleUpdate {
                    state: AgentRuntimeState::Suspended,
                    connection_state: ConversationConnectionState::Disconnected,
                    owner: AgentExecutionOwner::Stopped,
                    writer_owner: AgentWriterLeaseOwner::None,
                    native_session_mode: Some(AgentNativeSessionMode::Resume),
                },
            )
        } else {
            record_payload_for_session_and_dispatch(session, &self.emitter, payload)
        };
        if let Err(error) = event {
            session.cwd = from_cwd;
            session.rail_meta.worktree = previous_worktree;
            if had_runtime {
                session.state = AgentRuntimeState::Suspended;
                session.connection.state = ConversationConnectionState::Disconnected;
                session.owner = AgentExecutionOwner::Stopped;
                session.writer_lease.owner = AgentWriterLeaseOwner::None;
                session.native_session_mode = AgentNativeSessionMode::Resume;
                let _ = persist_session(session);
            }
            return Err(error);
        }
        drop(sessions);
        let record = self
            .list_sessions()?
            .into_iter()
            .find(|session| session.owned_id == owned_id)
            .ok_or_else(|| "Conversation session disappeared after checkout change".to_string())?;
        self.sessions
            .lock()
            .unwrap_or_else(std::sync::PoisonError::into_inner)
            .remove(&owned_id);
        Ok(record)
    }

    fn ensure_inner(
        &self,
        request: EnsureAgentConversationRequest,
    ) -> Result<(AgentConversationConnection, Option<ManagedAgentSession>), String> {
        if request.execution_environment != ExecutionEnvironment::Local {
            return Err(
                "Remote Assembly conversations must be routed through the remote connection manager"
                    .to_string(),
            );
        }
        let owned_id = required_id(&request.owned_id, "Owned session id")?;
        let cwd = validated_conversation_cwd(&request.cwd)?
            .display()
            .to_string();
        let native_session_id = normalized_optional_id(request.native_session_id);
        let reasoning_effort = normalized_optional_id(request.reasoning_effort);
        if request.native_session_mode == AgentNativeSessionMode::Load
            && native_session_id.is_none()
        {
            return Err("A native session id is required to load a stopped session".to_string());
        }
        self.hydrate_overlay_from_store(&owned_id)?;
        let mut sessions = self
            .sessions
            .lock()
            .unwrap_or_else(std::sync::PoisonError::into_inner);
        // A session that has never run is not answered by its own record.
        //
        // Generation zero belongs to one thing only: a conversation imported
        // from a transcript, which has a stored row and an entry here so it can
        // be listed, but has never started an adapter and so has never been
        // told what models it offers, what effort levels it takes, or anything
        // else that arrives with the handshake. Handing that record back leaves
        // a session nothing can be chosen for. It falls through instead and is
        // started once, the same as any first run.
        //
        // A suspended session is different and keeps the behaviour it had: it
        // ran before, its capabilities are on disk, and its adapter is started
        // again only when a turn actually needs one.
        if let Some(current) = sessions.get(&owned_id) {
            if current.generation > 0
                && current.provider == request.provider
                && current.cwd == cwd
                && native_session_id
                    .as_ref()
                    .is_none_or(|native_id| current.native_session_id.as_ref() == Some(native_id))
                && current.native_session_mode == request.native_session_mode
                // An effort asked for here is matched against the one the
                // session is on now, not the one it was created with: a change
                // made while it was running has already written the first, and
                // the next send carries that value back here. Comparing it
                // against the spawn value read the same session as a different
                // one and threw its record away. A request that names no effort
                // asks for nothing and cannot disagree.
                && reasoning_effort.as_ref().is_none_or(|asked_for| {
                    current
                        .config
                        .reasoning_effort
                        .as_ref()
                        .or(current.spawn_reasoning_effort.as_ref())
                        == Some(asked_for)
                })
                && current.connection.state != ConversationConnectionState::Failed
            {
                return Ok((current.connection.clone(), None));
            }
        }
        let mut prior = sessions.remove(&owned_id);
        if let Some(session) = prior.as_mut() {
            disconnect_claude_children(session, &self.emitter);
        }
        let generation = prior
            .as_ref()
            .map(|session| session.generation.saturating_add(1))
            .unwrap_or(1);
        // A replacement generation continues the same owned event journal, so
        // it inherits the prior session's next unused sequence instead of
        // starting again at one.
        let next_sequence = prior.as_ref().map_or(1, |session| session.next_sequence);
        let connection = AgentConversationConnection {
            owned_id: owned_id.clone(),
            provider: request.provider,
            generation,
            native_session_id,
            state: ConversationConnectionState::Connecting,
            config: AgentConversationConfigState { reasoning_effort: reasoning_effort.clone(), ..Default::default() },
        };
        let provider_instance_id = format!("{}-{generation}", request.provider.id());
        let created_at_ms = timestamp_millis();
        sessions.insert(
            owned_id.clone(),
            ManagedAgentSession {
                owned_id: owned_id.clone(),
                provider: request.provider,
                provider_instance_id,
                native_session_id: connection.native_session_id.clone(),
                native_session_mode: request.native_session_mode,
                generation,
                project_id: prior
                    .as_ref()
                    .and_then(|session| session.project_id.clone())
                    .or(request.project_id.clone()),
                helper_of: prior.as_ref().and_then(|session| session.helper_of.clone()),
                guard_policy: prior.as_ref().and_then(|session| session.guard_policy.clone()),
                owner: AgentExecutionOwner::Stopped,
                state: AgentRuntimeState::Starting,
                capabilities: empty_capabilities(request.provider),
                next_sequence,
                active_turn_id: None,
                cancel_deadline: None,
                prompt_once_active: false,
                runtime: None,
                pool_key: None,
                transport: None,
                writer_lease: AgentWriterLease {
                    owned_id,
                    generation,
                    owner: AgentWriterLeaseOwner::None,
                },
                writer_lease_transition: None,
                permission_requests: HashMap::new(),
                user_input_requests: HashMap::new(),
                next_user_input_id: 0,
                ordered_events: None,
                cwd,
                spawn_reasoning_effort: reasoning_effort,
                adapter_offers_effort: false,
                config: connection.config.clone(),
                connection: connection.clone(),
                store: Arc::clone(&self.store),
                created_at_ms,
                last_activity_ms: created_at_ms,
                live_tool_calls: HashMap::new(),
                streaming_reply: None,
                background_work: HashMap::new(),
                autonomous_turn_id: None,
                autonomous_turn_started: false,
                claude_reports_state: false,
                child_rollout_scan: None,
                child_rollout_parent_path: None,
                codex_children: HashMap::new(),
                claude_children: prior
                    .as_ref()
                    .map(|session| session.claude_children.clone())
                    .unwrap_or_default(),
                // A replacement generation is the same conversation, so it keeps
                // the name it was given and where that name came from. Starting
                // these empty wrote a blank over the stored row.
                rail_meta: prior
                    .as_ref()
                    .map(|session| session.rail_meta.clone())
                    .unwrap_or_default(),
                title_source: prior
                    .as_ref()
                    .and_then(|session| session.title_source.clone()),
                suspending: false, turn_usage: Default::default(),
            },
        );
        let session = sessions
            .get(&connection.owned_id)
            .ok_or_else(|| "Conversation session was not inserted".to_string())?;
        persist_session(session)?;
        Ok((connection, prior))
    }

    pub async fn activate(
        &self,
        owned_id: &str,
        generation: u64,
    ) -> Result<AgentConversationConnection, String> {
        let _lifecycle = self.lifecycle_guard(owned_id).await?;
        self.activate_locked(owned_id, generation).await
    }

    pub(crate) async fn activate_locked(
        &self,
        owned_id: &str,
        generation: u64,
    ) -> Result<AgentConversationConnection, String> {
        self.hydrate_overlay_from_store(owned_id)?;
        let (
            provider,
            cwd,
            native_session_id,
            native_session_mode,
            reasoning_effort,
            requested_model,
            requested_approval_policy,
            was_suspended,
        ) = {
            let mut sessions = self
                .sessions
                .lock()
                .unwrap_or_else(std::sync::PoisonError::into_inner);
            let session = lifecycle_session_mut(&mut sessions, owned_id, generation)?;
            session.last_activity_ms = timestamp_millis();
            if session.runtime.is_some() {
                if session.connection.state == ConversationConnectionState::Connected
                    && session.state != AgentRuntimeState::Failed
                {
                    return Ok(session.connection.clone());
                }
                return Err(
                    "The structured session must be ensured again before it can reactivate"
                        .to_string(),
                );
            }
            if !matches!(
                session.state,
                AgentRuntimeState::Starting | AgentRuntimeState::Suspended
            ) {
                return Err("Conversation is not ready to activate".to_string());
            }
            (
                session.provider,
                std::path::PathBuf::from(&session.cwd),
                session.native_session_id.clone(),
                session.native_session_mode,
                // What the session is on now, which a live change has already
                // written. Only a session that has never reported an effort
                // falls back to the one it was created with.
                session.config.reasoning_effort.clone().or_else(|| session.spawn_reasoning_effort.clone()),
                session.config.model.clone(),
                // The policy the session is on now, which a live change has
                // already written. A restarted adapter comes back on its own
                // default, so the policy has to be asked for again.
                session.config.approval_policy.clone(),
                session.state == AgentRuntimeState::Suspended,
            )
        };
        // Before the adapter loads it and writes to the transcript itself.
        if let Err(error) = super::history::follow_imported_length(&self.store, owned_id, true) { crate::debug_log::stderr_log!("Could not follow the imported transcript: {error}"); }
        let mut expected_native_session_id = native_session_id.clone();
        let never_had_a_turn = self
            .store
            .first_user_message_payload(owned_id)
            .map_err(|error| error.to_string())?
            .is_none();
        // A stored native session that cannot be resumed is started fresh when
        // there is nothing to lose by it: this store never recorded a turn for
        // it, or the provider no longer holds one. Asked only once a resume has
        // failed, because the second answer means reading the provider's files.
        let can_start_fresh = || {
            native_session_id
                .as_deref()
                .is_some_and(|native_session_id| {
                    never_had_a_turn || !provider_holds_session(provider, native_session_id)
                })
        };
        let endpoint = super::messaging::endpoint()?;
        let credential = endpoint.issue(owned_id);
        endpoint.bind_store(&credential.token, &self.store, Some(self.clone()));
        let mcp_servers = vec![credential.server()];
        let shared_key = AdapterPoolKey::Shared(provider);
        let mut pools = self.adapter_pools.lock().await;
        let (capabilities, started_result, runtime, transport, inbound, pool_key, started_fresh) =
            if let Some(pool) = pools.get_mut(&shared_key) {
                let runtime = Arc::clone(&pool.runtime);
                let mut started_fresh = false;
                let started = {
                    let mut runtime = runtime.lock().await;
                    let attempted = match native_session_id.as_deref() {
                        Some(native_session_id) => match native_session_mode {
                            AgentNativeSessionMode::Resume => {
                                runtime.resume_session_multi(&cwd, native_session_id, mcp_servers.clone()).await
                            }
                            AgentNativeSessionMode::Load => {
                                runtime.load_session_multi(&cwd, native_session_id, mcp_servers.clone()).await
                            }
                        },
                        None => runtime.new_session_multi(&cwd, mcp_servers.clone()).await,
                    };
                    if attempted.is_err() && can_start_fresh() {
                        crate::debug_log::stderr_log!(
                            "{owned_id}: stored native session holds nothing to resume; starting fresh"
                        );
                        started_fresh = true;
                        runtime.new_session_multi(&cwd, mcp_servers.clone()).await
                    } else {
                        attempted
                    }
                };
                if started.is_ok() {
                    pool.members.insert(owned_id.to_string());
                }
                (
                    pool.capabilities.clone(),
                    started,
                    runtime,
                    Arc::clone(&pool.transport),
                    None,
                    shared_key,
                    started_fresh,
                )
            } else {
                let manifest = self.providers.manifest(provider)?;
                let environment = session_spawn_environment(provider);
                let mut adapter = AcpRuntimeAdapter::with_environment(
                    manifest,
                    format!("provider:{}", provider.id()),
                    cwd.clone(),
                    environment,
                );
                let capabilities = adapter
                    .initialize(InitializeAgentInput { provider })
                    .await
                    .map_err(|error| error.to_string())?;
                let attempted = match native_session_id.as_ref() {
                    Some(native_session_id) => {
                        let input = LoadAgentSession {
                            cwd: cwd.clone(),
                            native_session_id: native_session_id.clone(),
                            mcp_servers: mcp_servers.clone(),
                        };
                        match native_session_mode {
                            AgentNativeSessionMode::Resume => adapter.resume_session(input).await,
                            AgentNativeSessionMode::Load => adapter.load_session(input).await,
                        }
                    }
                    None => {
                        adapter
                            .new_session(NewAgentSession { cwd: cwd.clone(), mcp_servers: mcp_servers.clone() })
                            .await
                    }
                };
                let (started, started_fresh) = if attempted.is_err() && can_start_fresh() {
                    crate::debug_log::stderr_log!(
                        "{owned_id}: stored native session holds nothing to resume; starting fresh"
                    );
                    (
                        adapter
                            .new_session(NewAgentSession { cwd: cwd.clone(), mcp_servers: mcp_servers.clone() })
                            .await,
                        true,
                    )
                } else {
                    (attempted, false)
                };
                let runtime = Arc::new(AsyncMutex::new(StructuredRuntimeHandle::Acp(adapter)));
                let (transport, inbound) = {
                    let mut runtime = runtime.lock().await;
                    let transport = runtime.transport().map_err(|error| error.to_string())?;
                    let inbound = runtime.take_inbound().map_err(|error| error.to_string())?;
                    (transport, inbound)
                };
                let pool_key = if provider_is_multi_session_safe(&capabilities) {
                    shared_key
                } else {
                    AdapterPoolKey::Isolated(provider, owned_id.to_string())
                };
                let members = HashSet::from([owned_id.to_string()]);
                pools.insert(
                    pool_key.clone(),
                    AdapterPoolEntry {
                        runtime: Arc::clone(&runtime),
                        transport: Arc::clone(&transport),
                        capabilities: capabilities.clone(),
                        members,
                    },
                );
                (
                    capabilities,
                    started,
                    runtime,
                    transport,
                    Some(inbound),
                    pool_key,
                    started_fresh,
                )
            };
        drop(pools);
        if started_fresh {
            expected_native_session_id = None;
        }
        let mut started = match started_result {
            Ok(started) => started,
            Err(error) if expected_native_session_id.is_some() => {
                let _ = self
                    .release_pool_scope(&pool_key, owned_id, None, false)
                    .await;
                let mut sessions = self
                    .sessions
                    .lock()
                    .unwrap_or_else(std::sync::PoisonError::into_inner);
                let session = lifecycle_session_mut(&mut sessions, owned_id, generation)?;
                record_payload_for_session_and_dispatch_with_lifecycle(
                    session,
                    &self.emitter,
                    AgentConversationPayload::Error {
                        code: "session-resume-failed".to_string(),
                        // The card names the underlying cause. Without it the
                        // reader is told the resume failed and nothing about
                        // why, which is the one thing they need to act on.
                        message: format!(
                            "The stored provider session could not be resumed: {error}"
                        ),
                        recoverable: true,
                    },
                    SessionLifecycleUpdate {
                        state: AgentRuntimeState::Suspended,
                        connection_state: ConversationConnectionState::Disconnected,
                        owner: AgentExecutionOwner::Stopped,
                        writer_owner: AgentWriterLeaseOwner::None,
                        native_session_mode: None,
                    },
                )?;
                return Err(format!("Stored session could not be resumed: {error}"));
            }
            Err(error) => {
                let _ = self
                    .release_pool_scope(&pool_key, owned_id, None, false)
                    .await;
                return Err(error.to_string());
            }
        };
        // The live session controls apply model before effort. A model change can reset effort,
        // so keep the explicit effort even when it matched the initial model's current value.
        let model_changed = requested_model.as_deref().is_some_and(|model| Some(model) != started.config.model.as_deref());
        let mut requested = AgentConversationConfigUpdate {
            model: requested_model.filter(|_| model_changed),
            reasoning_effort: reasoning_effort.clone().filter(|effort| {
                model_changed || Some(effort.as_str()) != started.config.reasoning_effort.as_deref()
            }),
            approval_policy: requested_approval_policy.filter(|policy| {
                Some(policy.as_str()) != started.config.approval_policy.as_deref()
            }),
        };
        normalize_codex_composite_config_update(provider, &started.config, &mut requested);
        // An obsolete approval id must not prevent valid saved model/effort
        // choices from being restored. Keep the adapter's advertised mode.
        if requested
            .approval_policy
            .as_ref()
            .is_some_and(|policy| !started.config.available_approval_policies.contains(policy))
        {
            requested.approval_policy = None;
        }
        if requested != AgentConversationConfigUpdate::default() {
            let configured = runtime
                .lock()
                .await
                .set_conversation_config_on(&started.native_session_id, &requested)
                .await;
            match configured {
                Ok(config) => started.config = config,
                Err(error) if requested.model.is_some() || requested.reasoning_effort.is_some() => {
                    let _ = self
                        .release_pool_scope(
                            &pool_key,
                            owned_id,
                            Some(&started.native_session_id),
                            false,
                        )
                        .await;
                    return Err(format!(
                        "The selected model or effort could not be applied: {error}"
                    ));
                }
                Err(error) => crate::debug_log::stderr_log!(
                    "{owned_id}: the approval setting was not applied: {error}"
                ),
            }
        }
        // An adapter that named an effort control owns the effort from now on.
        let adapter_offers_effort = !started.config.available_efforts.is_empty();
        if let Some(expected_native_session_id) = &expected_native_session_id {
            if started.native_session_id != *expected_native_session_id {
                let _ = self
                    .release_pool_scope(&pool_key, owned_id, Some(&started.native_session_id), true)
                    .await;
                return Err(
                    "The provider resumed a different native session; handoff was rejected"
                        .to_string(),
                );
            }
        }
        let (ordered_tx, ordered_rx) = mpsc::unbounded_channel();
        let mut sessions = self
            .sessions
            .lock()
            .unwrap_or_else(std::sync::PoisonError::into_inner);
        let session = lifecycle_session_mut(&mut sessions, owned_id, generation)?;
        if session.state != if was_suspended { AgentRuntimeState::Suspended } else { AgentRuntimeState::Starting } {
            return Err("Conversation lifecycle changed while activation was in progress".to_string());
        }
        session.capabilities = capabilities;
        if !started.commands.is_empty() {
            session.capabilities.commands = started.commands.clone();
        }
        session.native_session_id = Some(started.native_session_id.clone());
        session.connection.native_session_id = Some(started.native_session_id);
        session.turn_usage.resumed = expected_native_session_id.is_some();
        session.adapter_offers_effort = adapter_offers_effort;
        session.config = started.config;
        session.connection.config = session.config.clone();
        let restoring_terminal_transition = session.owner
            == AgentExecutionOwner::TransitioningToStructured
            && session
                .writer_lease_transition
                .as_ref()
                .map(|transition| transition.to == AgentWriterLeaseOwner::Structured)
                .unwrap_or(false);
        let (owner, writer_owner) = if restoring_terminal_transition {
            (session.owner, session.writer_lease.owner)
        } else {
            (
                AgentExecutionOwner::Structured,
                AgentWriterLeaseOwner::Structured,
            )
        };
        session.runtime = Some(runtime);
        session.pool_key = Some(pool_key);
        session.transport = Some(Arc::clone(&transport));
        session.ordered_events = Some(ordered_tx);
        record_payload_for_session_and_dispatch_with_lifecycle(
            session,
            &self.emitter,
            AgentConversationPayload::Connection {
                state: ConversationConnectionState::Connected,
                native_session_id: session.native_session_id.clone(),
            },
            SessionLifecycleUpdate {
                state: AgentRuntimeState::Ready,
                connection_state: ConversationConnectionState::Connected,
                owner,
                writer_owner,
                native_session_mode: None,
            },
        )?;
        let connection = session.connection.clone();
        drop(sessions);
        if expected_native_session_id.is_none() {
            let transcript = super::history::transcript::discover(provider, connection.native_session_id.as_deref().unwrap_or_default()).ok().flatten();
            if let Err(error) = super::history::track_new_transcript(&self.store, owned_id, transcript.as_ref().map(|location| location.canonical_path.as_path())) { crate::debug_log::stderr_log!("Could not track the new transcript: {error}"); }
        }
        if let Some(inbound) = inbound {
            spawn_inbound_pump(
                self.clone(),
                Arc::downgrade(&transport),
                inbound,
                ordered_rx,
                owned_id.to_string(),
                generation,
            );
        } else {
            spawn_ordered_pump(
                self.clone(),
                Arc::downgrade(&transport),
                ordered_rx,
                owned_id.to_string(),
                generation,
            );
        }
        Ok(connection)
    }

    /// Sends one message: the adapter is started if it is not running, the
    /// choices that came with the message are applied, and the prompt goes out.
    ///
    /// A message that is refused before its prompt — a model or approval
    /// policy the provider does not offer — leaves the session at rest. The
    /// adapter that activation just started has no turn to run, and nothing
    /// else would ever stop it: the teardown that keeps process trees from
    /// lingering runs when a turn ends, and this turn never began.
    pub async fn send_message(
        &self,
        owned_id: &str,
        generation: u64,
        input: AgentPrompt,
        model: Option<String>,
        approval_policy: Option<String>,
    ) -> Result<AgentConversationSendReceipt, String> {
        // Activation, configuration, and durable prompt acceptance are one
        // lifecycle operation. Checkout switching uses this same guard, so it
        // cannot detach the runtime between any of those steps. The guard is
        // released as soon as `prompt` records the active turn; streaming keeps
        // running independently.
        let mut lifecycle = self.lifecycle_guard(owned_id).await?;
        let activation = self.activate_locked(owned_id, generation).await;
        let activation = if let Err(error) = &activation {
            let (provider, cwd) = {
                let sessions = self.sessions.lock().unwrap_or_else(std::sync::PoisonError::into_inner);
                let session = current_session(&sessions, owned_id, generation)?;
                (session.provider, session.cwd.clone())
            };
            if authentication::required(error) {
                // Register cancellation before releasing the session guard. Stop
                // checks both before and after acquiring it, covering either order.
                let mut attempt = self.authentications.begin(owned_id, generation)?;
                let manifest = self.providers.manifest(provider)?;
                drop(lifecycle);
                let authenticated = attempt.authenticate(&manifest, provider, Path::new(&cwd)).await;
                lifecycle = self.lifecycle_guard(owned_id).await?;
                if attempt.was_cancelled() { return Err("Sign-in cancelled".into()); }
                authenticated?;
                drop(attempt);
                self.activate_locked(owned_id, generation).await
            } else { activation }
        } else { activation };
        let sent = async {
            activation?;
            if model.is_some() || approval_policy.is_some() {
                self.apply_conversation_config(
                    SetAgentConversationConfigRequest {
                        owned_id: owned_id.to_string(),
                        generation,
                        model,
                        reasoning_effort: None,
                        approval_policy,
                    },
                    false,
                )
                .await?;
            }
            self.prompt(owned_id, generation, input).await
        }
        .await;
        drop(lifecycle);
        if sent.is_err() {
            if let Err(error) = self.suspend_if_quiescent(owned_id, generation).await {
                crate::debug_log::stderr_log!(
                    "{owned_id}: could not stop the adapter after a refused send: {error}"
                );
            }
        }
        sent
    }

    pub async fn prompt(
        &self,
        owned_id: &str,
        generation: u64,
        input: AgentPrompt,
    ) -> Result<AgentConversationSendReceipt, String> {
        let runtime = self.runtime(owned_id, generation)?;
        let transport = {
            let runtime = runtime.lock().await;
            runtime.transport().map_err(|error| error.to_string())?
        };
        let steer_target = {
            let sessions = self
                .sessions
                .lock()
                .unwrap_or_else(std::sync::PoisonError::into_inner);
            let session = current_session(&sessions, owned_id, generation)?;
            if let Some(turn_id) = &session.active_turn_id {
                if !session.capabilities.session.steering {
                    return Err("This provider does not support steering an active turn".to_string());
                }
                // Nothing jumps ahead of a waiting request, from any surface (TSK-1592).
                if !session.permission_requests.is_empty() || !session.user_input_requests.is_empty() { return Err("The agent is waiting for an answer. Answer it first, then send your message.".to_string()); }
                if session.writer_lease.owner != AgentWriterLeaseOwner::Structured {
                    return Err("The structured writer is not the current owner".to_string());
                }
                Some((
                    session
                        .native_session_id
                        .clone()
                        .ok_or_else(|| "Structured provider session has not started".to_string())?,
                    turn_id.clone(),
                ))
            } else {
                None
            }
        };
        if let Some((native_session_id, turn_id)) = steer_target {
            let attachment_ids = input.attachment_ids.clone();
            let text = input.text.clone();
            let mut params = prompt_params(native_session_id, input);
            // Keep any new turn owned by session/prompt, even when steering races completion.
            params["_meta"]["steering"]["idleBehavior"] = serde_json::json!("promptRequired");
            let result = transport
                .request("_session/steering", params)
                .await
                .map_err(|error| error.to_string())?;
            match result.get("outcome").and_then(Value::as_str) {
                Some("injected") => {}
                Some("promptRequired") => {
                    return Err("The turn finished before the correction arrived. Your message was not sent; send it again as a new turn.".to_string());
                }
                _ => return Err("The provider did not confirm delivery of the correction. Your draft has been restored; check the conversation before resending.".to_string()),
            }
            let mut sessions = self
                .sessions
                .lock()
                .unwrap_or_else(std::sync::PoisonError::into_inner);
            let session = current_session_mut(&mut sessions, owned_id, generation)?;
            let user_item_id = format!("user-steer-{}", uuid::Uuid::new_v4());
            let admitted = record_payload_for_session_with_lifecycle(
                session,
                AgentConversationPayload::UserMessage {
                    item_id: user_item_id.clone(),
                    text,
                    completed: true,
                    attachment_ids,
                },
                None,
                Some(turn_id.clone()),
            )?;
            dispatch_event(&self.emitter, &admitted);
            return Ok(AgentConversationSendReceipt {
                owned_id: owned_id.to_string(),
                generation,
                turn_id,
                user_item_id,
                admitted_sequence: admitted.sequence,
            });
        }
        let turn_id = format!("turn-{}", uuid::Uuid::new_v4());
        let user_item_id = format!("user-{turn_id}");
        let (native_session_id, ordered_events, admitted_sequence) = {
            let mut sessions = self
                .sessions
                .lock()
                .unwrap_or_else(std::sync::PoisonError::into_inner);
            let session = current_session_mut(&mut sessions, owned_id, generation)?;
            if session.active_turn_id.is_some() {
                return Err("The structured session already has an active turn".to_string());
            }
            if session.prompt_once_active {
                return Err(
                    "The structured session is busy generating a one-shot result".to_string(),
                );
            }
            if session.writer_lease.owner != AgentWriterLeaseOwner::Structured {
                return Err("The structured writer is not the current owner".to_string());
            }
            let native_session_id = session
                .native_session_id
                .clone()
                .ok_or_else(|| "Structured provider session has not started".to_string())?;
            let ordered_events = session
                .ordered_events
                .clone()
                .ok_or_else(|| "Structured event pump has not started".to_string())?;
            finish_autonomous_turn(
                session,
                &self.emitter,
                super::protocol::TurnState::Completed,
                AgentRuntimeState::Ready,
            );
            session.active_turn_id = Some(turn_id.clone()); turn_usage::turn_started(session);
            if session_title_is_empty(session.rail_meta.title.as_deref()) {
                let is_first_user_prompt = session
                    .store
                    .first_user_message_payload(&session.owned_id)
                    .map_err(|error| error.to_string())?
                    .is_none();
                if is_first_user_prompt {
                    session.rail_meta.title = prompt_title(&input.text);
                    session.title_source = Some(TITLE_SOURCE_PROMPT.to_string());
                }
            }
            let lifecycle = lifecycle_update_for_state(
                session,
                AgentRuntimeState::Working,
                session.connection.state,
            );
            record_payload_for_session_and_dispatch_with_lifecycle(
                session,
                &self.emitter,
                AgentConversationPayload::Turn {
                    turn_id: turn_id.clone(),
                    state: super::protocol::TurnState::Started,
                },
                lifecycle,
            )?;
            // Agents are not required to echo the prompt back as a
            // user_message_chunk, so the app records its own copy.
            let admitted = record_payload_for_session_and_dispatch(
                session,
                &self.emitter,
                AgentConversationPayload::UserMessage {
                    item_id: user_item_id.clone(),
                    text: input.text.clone(),
                    completed: true,
                    attachment_ids: input.attachment_ids.clone(),
                },
            )?;
            (native_session_id, ordered_events, admitted.sequence)
        };

        self.start_child_rollout_scan(owned_id, generation)?;
        let params = prompt_params(native_session_id, input);
        spawn_prompt_completion(transport, ordered_events, turn_id.clone(), params).await;
        Ok(AgentConversationSendReceipt {
            owned_id: owned_id.to_string(),
            generation,
            turn_id,
            user_item_id,
            admitted_sequence,
        })
    }

    fn start_child_rollout_scan(&self, owned_id: &str, generation: u64) -> Result<(), String> {
        let mut sessions = self
            .sessions
            .lock()
            .unwrap_or_else(std::sync::PoisonError::into_inner);
        let session = current_session_mut(&mut sessions, owned_id, generation)?;
        if session.provider != AgentConversationProvider::Codex {
            return Ok(());
        }
        if let Some(task) = session.child_rollout_scan.take() {
            task.abort();
        }
        let native_session_id = session
            .native_session_id
            .clone()
            .ok_or_else(|| "Structured provider session has not started".to_string())?;
        let manager = self.clone();
        let owned_id = owned_id.to_string();
        session.child_rollout_scan = Some(tokio::spawn(async move {
            run_child_rollout_scan(manager, owned_id, generation, native_session_id).await;
        }));
        Ok(())
    }

    async fn finish_child_rollout_scan(&self, owned_id: &str, generation: u64, turn_id: &str) {
        let scan = {
            let mut sessions = self
                .sessions
                .lock()
                .unwrap_or_else(std::sync::PoisonError::into_inner);
            let Ok(session) = current_session_mut(&mut sessions, owned_id, generation) else {
                return;
            };
            if session.active_turn_id.as_deref() != Some(turn_id) {
                return;
            }
            session.native_session_id.clone().map(|native_session_id| {
                (native_session_id, session.child_rollout_parent_path.clone())
            })
        };
        let Some((native_session_id, parent_path)) = scan else {
            return;
        };
        let outcome = scan_codex_children_once(
            &Arc::downgrade(&self.sessions),
            &self.emitter,
            owned_id,
            generation,
            &native_session_id,
            parent_path,
        )
        .await;
        if outcome.is_some_and(|outcome| outcome.has_running_children) {
            return;
        }
        let mut sessions = self
            .sessions
            .lock()
            .unwrap_or_else(std::sync::PoisonError::into_inner);
        let Ok(session) = current_session_mut(&mut sessions, owned_id, generation) else {
            return;
        };
        if session.active_turn_id.as_deref() == Some(turn_id) {
            if let Some(task) = session.child_rollout_scan.take() {
                task.abort();
            }
        }
    }

    /// Run one product action through the current structured agent without
    /// writing through the conversation UI. The runtime and generation checks
    /// remain the same as an ordinary conversation prompt.
    pub async fn prompt_once(
        &self,
        owned_id: &str,
        generation: u64,
        input: AgentPrompt,
    ) -> Result<GeneratedText, String> {
        let lifecycle = self.lifecycle_guard(owned_id).await?;
        let runtime = self.runtime(owned_id, generation)?;
        let native_session_id = {
            let mut sessions = self
                .sessions
                .lock()
                .unwrap_or_else(std::sync::PoisonError::into_inner);
            let session = current_session_mut(&mut sessions, owned_id, generation)?;
            if session.active_turn_id.is_some() {
                return Err(
                    "The structured session already has an active turn; one-shot generation is unavailable"
                        .to_string(),
                );
            }
            if session.prompt_once_active {
                return Err("A one-shot generation is already active".to_string());
            }
            if session.writer_lease.owner != AgentWriterLeaseOwner::Structured {
                return Err("The structured writer is not the current owner".to_string());
            }
            let native_session_id = session
                .native_session_id
                .clone()
                .ok_or_else(|| "Structured provider session has not started".to_string())?;
            session.prompt_once_active = true;
            native_session_id
        };
        let result = runtime
            .lock()
            .await
            .prompt_once_on(&native_session_id, input)
            .await;
        {
            let mut sessions = self
                .sessions
                .lock()
                .unwrap_or_else(std::sync::PoisonError::into_inner);
            if let Ok(session) = current_session_mut(&mut sessions, owned_id, generation) {
                session.prompt_once_active = false;
            }
        }
        drop(lifecycle);
        if let Err(error) = self.suspend_if_quiescent(owned_id, generation).await {
            crate::debug_log::stderr_log!(
                "{owned_id}: could not stop the adapter after one-shot generation: {error}"
            );
        }
        result.map_err(|error| error.to_string())
    }

    pub async fn respond_permission(&self, input: PermissionResponse) -> Result<(), String> {
        self.respond_permission_selection(
            &input.identity.owned_id,
            input.identity.generation,
            input.identity.request_id,
            PermissionSelection::Decision(input.decision),
        )
        .await
    }

    pub async fn respond_permission_option(
        &self,
        owned_id: &str,
        generation: u64,
        request_id: String,
        option_id: String,
    ) -> Result<(), String> {
        let option_id = required_id(&option_id, "Permission option id")?;
        self.respond_permission_selection(
            owned_id,
            generation,
            request_id,
            PermissionSelection::Option(option_id),
        )
        .await
    }

    async fn respond_permission_selection(
        &self,
        owned_id: &str,
        generation: u64,
        request_id: String,
        selection: PermissionSelection,
    ) -> Result<(), String> {
        let runtime_scope_is_live = {
            let sessions = self
                .sessions
                .lock()
                .unwrap_or_else(std::sync::PoisonError::into_inner);
            current_session(&sessions, owned_id, generation).is_ok_and(|session| {
                session.runtime.is_some()
                    && !matches!(
                        session.state,
                        AgentRuntimeState::Suspended
                            | AgentRuntimeState::Failed
                            | AgentRuntimeState::Closed
                    )
            })
        };
        if !runtime_scope_is_live {
            self.expire_stale_approval(owned_id, generation, &request_id)?;
            return Err("Stale approval request: the original runtime scope is closed".to_string());
        }
        let runtime = self.runtime(&owned_id, generation)?;
        let transport = {
            let runtime = runtime.lock().await;
            runtime.transport().map_err(|error| error.to_string())?
        };
        let (pending, ordered_events, turn_active) = {
            let mut sessions = self
                .sessions
                .lock()
                .unwrap_or_else(std::sync::PoisonError::into_inner);
            let session = current_session_mut(&mut sessions, &owned_id, generation)?;
            let ordered_events = session
                .ordered_events
                .clone()
                .ok_or_else(|| "Structured event pump has not started".to_string())?;
            let pending = session
                .permission_requests
                .remove(&request_id)
                .ok_or_else(|| "Approval request is no longer pending".to_string())?;
            (pending, ordered_events, session.active_turn_id.is_some())
        };
        let may_answer = turn_active || pending.child_scoped;
        let terminal_state = if may_answer {
            permission_state(&pending.options, &selection)
        } else {
            ApprovalState::Declined
        };
        let response = match if may_answer {
            permission_response_for_selection(&pending.options, &selection)
        } else {
            Ok(serde_json::json!({ "outcome": { "outcome": "cancelled" } }))
        } {
            Ok(response) => response,
            Err(error) => {
                let mut sessions = self
                    .sessions
                    .lock()
                    .unwrap_or_else(std::sync::PoisonError::into_inner);
                if let Ok(session) = current_session_mut(&mut sessions, &owned_id, generation) {
                    session.permission_requests.insert(request_id, pending);
                }
                return Err(error);
            }
        };
        if let Err(error) = transport.respond(pending.wire_id.clone(), response).await {
            let mut sessions = self
                .sessions
                .lock()
                .unwrap_or_else(std::sync::PoisonError::into_inner);
            if let Ok(session) = current_session_mut(&mut sessions, &owned_id, generation) {
                session.permission_requests.insert(request_id, pending);
            }
            return Err(error.to_string());
        }
        ordered_events
            .send(OrderedSessionEvent::ApprovalResolved {
                request_id,
                state: terminal_state,
                summary: pending.summary,
            })
            .map_err(|_| "Structured event pump is no longer running".to_string())?;
        Ok(())
    }

    fn expire_stale_approval(
        &self,
        owned_id: &str,
        generation: u64,
        request_id: &str,
    ) -> Result<(), String> {
        if !self.hydrate_overlay_from_store(owned_id)? {
            return Ok(());
        }
        let mut sessions = self
            .sessions
            .lock()
            .unwrap_or_else(std::sync::PoisonError::into_inner);
        let Ok(session) = current_session_mut(&mut sessions, owned_id, generation) else {
            return Ok(());
        };
        if session.runtime.is_some()
            && !matches!(
                session.state,
                AgentRuntimeState::Suspended | AgentRuntimeState::Failed | AgentRuntimeState::Closed
            )
        {
            return Ok(());
        }
        if let Some(summary) = session
            .store
            .pending_approval_summary(owned_id, generation, request_id)
            .map_err(|error| error.to_string())?
        {
            record_payload_for_session_and_dispatch(
                session,
                &self.emitter,
                AgentConversationPayload::Approval {
                    request_id: request_id.to_string(),
                    state: ApprovalState::Expired,
                    summary, options: Vec::new(),
                },
            )?;
            session.permission_requests.remove(request_id);
        }
        Ok(())
    }

    pub async fn respond_user_input(&self, input: AgentUserInputResponse) -> Result<(), String> {
        let owned_id = input.identity.owned_id.clone();
        let generation = input.identity.generation;
        let request_id = input.identity.request_id.clone();
        {
            let sessions = self
                .sessions
                .lock()
                .unwrap_or_else(std::sync::PoisonError::into_inner);
            let session = current_session(&sessions, &owned_id, generation)?;
            if session.runtime.is_none()
                || matches!(
                    session.state,
                    AgentRuntimeState::Suspended
                        | AgentRuntimeState::Failed
                        | AgentRuntimeState::Closed
                )
            {
                return Err("Stale input request: the original runtime scope is closed".to_string());
            }
        }
        let runtime = self.runtime(&owned_id, generation)?;
        let transport = {
            let runtime = runtime.lock().await;
            runtime.transport().map_err(|error| error.to_string())?
        };
        let (pending, ordered_events) = {
            let mut sessions = self
                .sessions
                .lock()
                .unwrap_or_else(std::sync::PoisonError::into_inner);
            let session = current_session_mut(&mut sessions, &owned_id, generation)?;
            let ordered_events = session
                .ordered_events
                .clone()
                .ok_or_else(|| "Structured event pump has not started".to_string())?;
            let pending = session
                .user_input_requests
                .remove(&request_id)
                .ok_or_else(|| "User input request is no longer pending".to_string())?;
            (pending, ordered_events)
        };
        let response = match user_input_response(pending.response_shape, input.action, input.content)
        {
            Ok(response) => response,
            Err(error) => {
                let mut sessions = self
                    .sessions
                    .lock()
                    .unwrap_or_else(std::sync::PoisonError::into_inner);
                if let Ok(session) = current_session_mut(&mut sessions, &owned_id, generation) {
                    if session
                        .runtime
                        .as_ref()
                        .is_some_and(|current| Arc::ptr_eq(current, &runtime))
                    {
                        session.user_input_requests.insert(request_id, pending);
                    }
                }
                return Err(error);
            }
        };
        if let Err(error) = transport.respond(pending.wire_id.clone(), response).await {
            let mut sessions = self
                .sessions
                .lock()
                .unwrap_or_else(std::sync::PoisonError::into_inner);
            if let Ok(session) = current_session_mut(&mut sessions, &owned_id, generation) {
                if session
                    .runtime
                    .as_ref()
                    .is_some_and(|current| Arc::ptr_eq(current, &runtime))
                {
                    session.user_input_requests.insert(request_id, pending);
                }
            }
            return Err(error.to_string());
        }
        ordered_events
            .send(OrderedSessionEvent::UserInputResolved {
                request_id,
                cancelled: input.action == AgentUserInputAction::Cancel,
            })
            .map_err(|_| "Structured event pump is no longer running".to_string())?;
        Ok(())
    }

    pub async fn set_config(
        &self,
        owned_id: &str,
        generation: u64,
        option_id: &str,
        value: AgentConfigValue,
    ) -> Result<Vec<super::protocol::AgentConfigOption>, String> {
        let native_session_id = {
            let sessions = self
                .sessions
                .lock()
                .unwrap_or_else(std::sync::PoisonError::into_inner);
            let session = current_session(&sessions, owned_id, generation)?;
            let option = session
                .capabilities
                .config_options
                .iter()
                .find(|option| option.id == option_id)
                .ok_or_else(|| {
                    "Config option was not advertised by the current provider".to_string()
                })?;
            super::capabilities::validate_config_value(option, &value)?;
            session
                .native_session_id
                .clone()
                .ok_or_else(|| "Structured provider session has not started".to_string())?
        };
        let runtime = self.runtime(owned_id, generation)?;
        let replacement = runtime
            .lock()
            .await
            .set_config_on(&native_session_id, option_id, value)
            .await
            .map_err(|error| error.to_string())?;
        let mut sessions = self
            .sessions
            .lock()
            .unwrap_or_else(std::sync::PoisonError::into_inner);
        let session = current_session_mut(&mut sessions, owned_id, generation)?;
        super::capabilities::replace_config_options(
            &mut session.capabilities,
            replacement.clone(),
        )?;
        persist_session(session)?;
        Ok(replacement)
    }

    pub fn conversation_config(
        &self,
        owned_id: &str,
    ) -> Result<AgentConversationConfigState, String> {
        if let Some(config) = self
            .sessions
            .lock()
            .unwrap_or_else(std::sync::PoisonError::into_inner)
            .get(owned_id)
            .map(|session| session.config.clone())
        {
            return Ok(config);
        }
        let row = self
            .store
            .get_session(owned_id)
            .map_err(|error| error.to_string())?
            .ok_or_else(|| "Conversation session was not found".to_string())?;
        Ok(stored_session_projection(&self.store, &row)?.1.config)
    }

    pub async fn set_conversation_config(
        &self,
        request: SetAgentConversationConfigRequest,
    ) -> Result<AgentConversationConfigState, String> {
        let owned_id = request.owned_id.clone();
        let generation = request.generation;
        let config = self.apply_conversation_config(request, true).await?;
        // An accepted change ends the same way a refused one does: the adapter
        // was started only to be asked, no turn is running, and the teardown
        // that keeps process trees from lingering runs when a turn ends. A
        // send applies its settings through apply_conversation_config instead,
        // because its prompt is about to need the adapter it just started. A
        // session that is mid-turn is left alone — quiescence is the guard.
        if let Err(suspend_error) = self.suspend_if_quiescent(&owned_id, generation).await {
            crate::debug_log::stderr_log!(
                "{owned_id}: could not stop the adapter after a settings change: {suspend_error}"
            );
        }
        Ok(config)
    }

    pub(crate) async fn apply_conversation_config(
        &self,
        request: SetAgentConversationConfigRequest,
        suspend_on_error: bool,
    ) -> Result<AgentConversationConfigState, String> {
        self.hydrate_overlay_from_store(&request.owned_id)?;
        let mut update = AgentConversationConfigUpdate {
            model: normalized_optional_id(request.model),
            reasoning_effort: normalized_optional_id(request.reasoning_effort),
            approval_policy: crate::proof_mode::requested_mode(normalized_optional_id(request.approval_policy)),
        };
        let native_session_id = {
            let mut sessions = self
                .sessions
                .lock()
                .unwrap_or_else(std::sync::PoisonError::into_inner);
            let session =
                lifecycle_session_mut(&mut sessions, &request.owned_id, request.generation)?;
            let started = session.native_session_id.is_some();
            // Effort mutability comes from the adapter's control, for every provider.
            if started
                && update.reasoning_effort.is_some()
                && !session.adapter_offers_effort
            {
                return Err(
                    "Effort is set when the session starts. Start a new session to change it."
                        .to_string(),
                );
            }
            if update == AgentConversationConfigUpdate::default() {
                return Ok(session.config.clone());
            }
            match session.native_session_id.clone() {
                Some(native_session_id) => native_session_id,
                None => {
                    // The adapter only starts when the first turn is sent, so a
                    // choice made now has no session to carry it. Keep it on the
                    // record instead: the composer reads it back, and the start
                    // asks the adapter for it when there is finally one.
                    let effort = update.reasoning_effort.clone();
                    if let Some(model) = update.model.clone() {
                        session.config.model = Some(model);
                    }
                    if let Some(effort) = effort {
                        session.config.reasoning_effort = Some(effort.clone());
                        session.spawn_reasoning_effort = Some(effort);
                    }
                    if let Some(approval_policy) = update.approval_policy.clone() {
                        session.config.approval_policy = Some(approval_policy);
                    }
                    session.connection.config = session.config.clone();
                    persist_session(session)?;
                    return Ok(session.config.clone());
                }
            }
        };
        let runtime = self
            .runtime_or_activate(&request.owned_id, request.generation)
            .await?;
        let applied = async {
            // What a session offers is the running adapter's answer, and the
            // activation above has just refreshed it. Judging the request any
            // earlier weighs it against the snapshot an adapter that is no
            // longer running left behind, which refuses a choice the live one
            // offers.
            {
                let sessions = self
                    .sessions
                    .lock()
                    .unwrap_or_else(std::sync::PoisonError::into_inner);
                let session = current_session(&sessions, &request.owned_id, request.generation)?;
                normalize_codex_composite_config_update(
                    session.provider,
                    &session.config,
                    &mut update,
                );
                validate_conversation_config_update(&session.config, &update)?;
            }
            runtime
                .lock()
                .await
                .set_conversation_config_on(&native_session_id, &update)
                .await
                .map_err(|error| error.to_string())
        }
        .await;
        // Asking the adapter meant starting one, and a refused change gives it
        // no turn to run: the teardown that keeps process trees from lingering
        // runs when a turn ends, and this session never began one.
        let config = match applied {
            Ok(config) => config,
            Err(error) => {
                if suspend_on_error {
                    if let Err(suspend_error) = self
                        .suspend_if_quiescent(&request.owned_id, request.generation)
                        .await
                    {
                        let owned_id = &request.owned_id;
                        crate::debug_log::stderr_log!(
                            "{owned_id}: could not stop the adapter after a refused settings change: {suspend_error}"
                        );
                    }
                }
                return Err(error);
            }
        };
        let config = {
            let mut sessions = self
                .sessions
                .lock()
                .unwrap_or_else(std::sync::PoisonError::into_inner);
            let session =
                lifecycle_session_mut(&mut sessions, &request.owned_id, request.generation)?;
            session.adapter_offers_effort = !config.available_efforts.is_empty();
            session.config = config.clone();
            session.connection.config = config.clone();
            persist_session(session)?;
            config
        };
        Ok(config)
    }

    pub async fn respond_legacy_approval(
        &self,
        owned_id: &str,
        generation: u64,
        request_id: String,
        decision: super::protocol::ApprovalDecision,
    ) -> Result<(), String> {
        let decision = match decision {
            super::protocol::ApprovalDecision::Accept => AgentApprovalDecision::Accept,
            super::protocol::ApprovalDecision::Decline => AgentApprovalDecision::Decline,
        };
        self.respond_permission(AgentApprovalResponse {
            identity: AgentRequestIdentity {
                owned_id: owned_id.to_string(),
                generation,
                request_id,
                turn_id: None,
                item_id: None,
            },
            decision,
        })
        .await
    }

    pub async fn cancel_turn(&self, owned_id: &str, generation: u64) -> Result<(), String> {
        if self.authentications.cancel(owned_id, generation) { return Ok(()); }
        // A send may still be activating the adapter. Inspect its turn only after
        // that lifecycle operation has recorded the accepted prompt.
        let lifecycle = self.lifecycle_guard(owned_id).await?;
        if self.authentications.cancel(owned_id, generation) { return Ok(()); }
        let runtime = self.runtime(owned_id, generation)?;
        let transport = {
            let runtime = runtime.lock().await;
            runtime.transport().map_err(|error| error.to_string())?
        };
        let (native_session_id, turn_id, bounded_cancel) = {
            let mut sessions = self
                .sessions
                .lock()
                .unwrap_or_else(std::sync::PoisonError::into_inner);
            let session = current_session_mut(&mut sessions, owned_id, generation)?;
            if let Some(turn_id) = session.active_turn_id.clone() {
                session.state = AgentRuntimeState::Interrupting;
                // Antigravity 1.2.1 server.py:3089–3090 only signals a started SDK; it provides no prompt-completion deadline.
                let bounded_cancel = session.provider == AgentConversationProvider::Antigravity
                    && session.cancel_deadline.is_none()
                    && matches!(
                        session.pool_key.as_ref(),
                        Some(AdapterPoolKey::Isolated(AgentConversationProvider::Antigravity, _))
                    );
                (
                    session
                        .native_session_id
                        .clone()
                        .ok_or_else(|| "Structured provider session has not started".to_string())?,
                    Some(turn_id),
                    bounded_cancel,
                )
            } else if !session.background_work.is_empty() {
                (session.native_session_id.clone().ok_or_else(||
                    "Structured provider session has not started".to_string())?, None, false)
            } else {
                (String::new(), None, false)
            }
        };
        if native_session_id.is_empty() {
            drop(lifecycle);
            self.suspend_if_quiescent(owned_id, generation).await?;
            return Ok(());
        }
        transport
            .notify(
                "session/cancel",
                serde_json::json!({ "sessionId": native_session_id }),
            )
            .await
            .map_err(|error| error.to_string())?;
        let claude = {
            let mut sessions = self.sessions.lock().unwrap_or_else(std::sync::PoisonError::into_inner);
            let session = current_session_mut(&mut sessions, owned_id, generation)?;
            // Claude ACP 0.88/0.93 cancelTurns can wait its 30s forceCancelGraceMs; release permissions/children locally now.
            if session.provider == AgentConversationProvider::Claude || turn_id.is_none() {
                for (request_id, pending) in session.permission_requests.drain().collect::<Vec<_>>() {
                    record_payload_for_session_and_dispatch(session, &self.emitter,
                        AgentConversationPayload::Approval { request_id,
                            state: ApprovalState::Expired, summary: pending.summary, options: Vec::new() })?;
                }
                for (request_id, _) in session.user_input_requests.drain().collect::<Vec<_>>() {
                    record_payload_for_session_and_dispatch(session, &self.emitter,
                        AgentConversationPayload::UserInputResolved { request_id, cancelled: true })?;
                }
                disconnect_claude_children(session, &self.emitter);
                session.background_work.clear();
                session.live_tool_calls.clear();
                finish_autonomous_turn(session, &self.emitter,
                    super::protocol::TurnState::Interrupted, AgentRuntimeState::Ready);
                publish_background_work_state(session, &self.emitter)?;
            }
            session.provider == AgentConversationProvider::Claude
        };
        if claude || turn_id.is_none() {
            drop(lifecycle);
            if let Some(turn_id) = turn_id {
                handle_ordered_session_event(OrderedSessionEvent::PromptResult {
                    turn_id, result: Ok(serde_json::json!({ "stopReason": "cancelled" })),
                }, self, &Arc::downgrade(&transport), owned_id, generation).await;
            } else {
                self.suspend_if_quiescent(owned_id, generation).await?;
            }
            return Ok(());
        }
        let turn_id = turn_id.expect("active cancellation turn");
        if bounded_cancel {
            let manager = self.clone();
            let owned_id = owned_id.to_string();
            let task_owned_id = owned_id.clone();
            let expected_transport = Arc::clone(&transport);
            let expected_turn_id = turn_id.clone();
            let task = tokio::spawn(async move {
                tokio::time::sleep(ANTIGRAVITY_CANCEL_GRACE).await;
                let ordered_events = {
                    let sessions = manager
                        .sessions
                        .lock()
                        .unwrap_or_else(std::sync::PoisonError::into_inner);
                    sessions.get(&task_owned_id).and_then(|session| {
                        (session.generation == generation
                            && session.state == AgentRuntimeState::Interrupting
                            && session.active_turn_id.as_deref()
                                == Some(expected_turn_id.as_str())
                            && session.transport.as_ref().is_some_and(|transport| {
                                Arc::ptr_eq(transport, &expected_transport)
                            })
                            && matches!(
                                session.pool_key.as_ref(),
                                Some(AdapterPoolKey::Isolated(
                                    AgentConversationProvider::Antigravity,
                                    _
                                ))
                            ))
                        .then(|| session.ordered_events.clone())
                        .flatten()
                    })
                };
                if let Some(ordered_events) = ordered_events {
                    let _ = ordered_events.send(OrderedSessionEvent::PromptResult {
                        turn_id: expected_turn_id,
                        result: Ok(serde_json::json!({ "stopReason": "cancelled" })),
                    });
                }
            });
            let mut sessions = self
                .sessions
                .lock()
                .unwrap_or_else(std::sync::PoisonError::into_inner);
            let session = current_session_mut(&mut sessions, &owned_id, generation)?;
            if session.active_turn_id.as_deref() == Some(turn_id.as_str())
                && session.state == AgentRuntimeState::Interrupting
                && session
                    .transport
                    .as_ref()
                    .is_some_and(|current| Arc::ptr_eq(current, &transport))
            {
                if let Some(previous) = session.cancel_deadline.replace(task) {
                    previous.abort();
                }
            } else {
                task.abort();
            }
        }
        Ok(())
    }

    pub fn snapshot(&self, owned_id: &str) -> Result<Option<AgentConversationSnapshot>, String> {
        self.snapshot_for_request(owned_id, None, None)
    }

    pub fn snapshot_since(
        &self,
        owned_id: &str,
        after: Option<i64>,
    ) -> Result<Option<AgentConversationSnapshot>, String> {
        self.snapshot_for_request(owned_id, None, after)
    }

    fn snapshot_for_request(
        &self,
        owned_id: &str,
        request_id: Option<u64>,
        after: Option<i64>,
    ) -> Result<Option<AgentConversationSnapshot>, String> {
        let Some((connection, suspended, last_sequence, active_turn_id, pending_events)) =
            self.snapshot_state(owned_id)?
        else {
            return Ok(None);
        };
        if request_id.is_some_and(|request_id| {
            self.latest_snapshot_request.load(Ordering::Acquire) != request_id
        }) {
            return Ok(None);
        }
        let rows = if let Some(after) = after {
            self.store
                .list_events_after(owned_id, after, 512 * 1024, last_sequence)
        } else {
            self.store.list_events_before(
                owned_id,
                last_sequence.saturating_add(1),
                SNAPSHOT_WINDOW_BYTES,
                i64::MIN,
            )
        }
        .map_err(|error| error.to_string())?;
        let events = rows
            .events
            .into_iter()
            .map(stored_event)
            .collect::<Result<Vec<_>, _>>()?;
        Ok(Some(AgentConversationSnapshot {
            connection,
            suspended,
            last_sequence,
            events,
            has_earlier_transcript: self.has_earlier_transcript(owned_id)?,
            pending_events,
            active_turn_id,
        }))
    }

    pub fn latest_selection_snapshot(
        &self,
        owned_id: &str,
        request_id: u64,
        max_bytes: u32,
    ) -> Result<Option<AgentConversationSelectionSnapshot>, String> {
        if !self.advance_snapshot_request(request_id) {
            return Ok(None);
        }
        let Some((connection, suspended, head, active_turn_id, pending_events)) =
            self.snapshot_state(owned_id)?
        else {
            return Ok(None);
        };
        let page = self
            .store
            .list_items_before(
                owned_id,
                i64::MAX,
                max_bytes,
                Some(EventCoverage {
                    low: i64::MIN,
                    high: head,
                    start_complete: true,
                }),
            )
            .map_err(|error| error.to_string())?;
        if self.latest_snapshot_request.load(Ordering::Acquire) != request_id
            || self
                .cancelled_snapshot_requests
                .lock()
                .unwrap_or_else(std::sync::PoisonError::into_inner)
                .contains(&request_id)
        {
            return Ok(None);
        }
        let mut page = selected_item_page(page)?;
        page.has_earlier_transcript = self.has_earlier_transcript(owned_id)?;
        // Local history has no synchronization boundary. The explicit bound
        // above freezes this read at `head`; it is not an external coverage gap.
        page.coverage = None;
        Ok(Some(AgentConversationSelectionSnapshot {
            connection,
            suspended,
            page,
            pending_events,
            pending_sequence: head,
            active_turn_id,
        }))
    }

    fn snapshot_state(
        &self,
        owned_id: &str,
    ) -> Result<
        Option<(
            AgentConversationConnection,
            bool,
            i64,
            Option<String>,
            Vec<AgentConversationEvent>,
        )>,
        String,
    > {
        let live = {
            let sessions = self
                .sessions
                .lock()
                .unwrap_or_else(std::sync::PoisonError::into_inner);
            sessions.get(owned_id).map(|session| {
                let mut pending_events = session
                    .permission_requests
                    .values()
                    .map(|pending| pending.event.clone())
                    .chain(
                        session
                            .user_input_requests
                            .values()
                            .map(|pending| pending.event.clone()),
                    )
                    .collect::<Vec<_>>();
                pending_events.sort_by_key(|event| event.sequence);
                (
                    session.connection.clone(),
                    session.state == AgentRuntimeState::Suspended,
                    session.next_sequence.saturating_sub(1),
                    session.active_turn_id.clone(),
                    pending_events,
                )
            })
        };
        let mut state = match live {
            Some(state) => Some(state),
            None => {
                let Some(row) = self
                    .store
                    .get_session(owned_id)
                    .map_err(|error| error.to_string())?
                else {
                    return Ok(None);
                };
                let (provider, stored, state) = stored_session_projection(&self.store, &row)?;
                let head = self
                    .store
                    .latest_seq(owned_id)
                    .map_err(|error| error.to_string())?;
                Some((
                    AgentConversationConnection {
                        owned_id: row.owned_id,
                        provider,
                        generation: stored.generation,
                        native_session_id: row.native_session_id,
                        state: connection_state_for_runtime_state(state),
                        config: stored.config,
                    },
                    state == AgentRuntimeState::Suspended,
                    head,
                    None,
                    Vec::new(),
                ))
            }
        };
        if let Some((_, _, head, _, pending_events)) = &mut state {
            pending_events.extend(
                self.store
                    .latest_child_events(owned_id, *head)
                    .map_err(|error| error.to_string())?
                    .into_iter()
                    .map(stored_event)
                    .collect::<Result<Vec<_>, _>>()?,
            );
            pending_events.sort_by_key(|event| event.sequence);
        }
        Ok(state)
    }

    fn has_earlier_transcript(&self, owned_id: &str) -> Result<bool, String> {
        let Some(row) = self
            .store
            .get_session(owned_id)
            .map_err(|error| error.to_string())?
        else {
            return Ok(false);
        };
        let extra: serde_json::Value = serde_json::from_str(&row.extra_json)
            .map_err(|error| format!("Could not decode stored session metadata: {error}"))?;
        Ok(extra
            .get("import")
            .and_then(|import| import.get("reachedStart"))
            .and_then(serde_json::Value::as_bool)
            == Some(false))
    }

    /// Releases the frontend owner even when no replacement session snapshot
    /// is about to start. The request identity makes a delayed cancel harmless
    /// if a newer read has already taken ownership.
    /// A cancel is only remembered, never raises the current read's id: that
    /// would empty a snapshot read in flight when the cancelled id belongs to
    /// some other kind of request, or empty the next read if it gets the next id.
    pub fn cancel_snapshot(&self, request_id: u64) {
        let mut cancelled = self
            .cancelled_snapshot_requests
            .lock()
            .unwrap_or_else(std::sync::PoisonError::into_inner);
        let latest = self.latest_snapshot_request.load(Ordering::Acquire);
        if request_id >= latest {
            cancelled.push(request_id);
        }
        if request_id == latest {
            self.store.cancel_recent_events_read();
        }
    }

    fn advance_snapshot_request(&self, request_id: u64) -> bool {
        let mut cancelled = self
            .cancelled_snapshot_requests
            .lock()
            .unwrap_or_else(std::sync::PoisonError::into_inner);
        let cancelled_early = cancelled.contains(&request_id);
        cancelled.retain(|id| *id > request_id);
        let previous = self
            .latest_snapshot_request
            .fetch_max(request_id, Ordering::AcqRel);
        if request_id <= previous {
            return false;
        }
        if previous != 0 {
            self.store.cancel_recent_events_read();
        }
        !cancelled_early
    }

    pub fn list_sessions(&self) -> Result<Vec<AgentConversationSessionRecord>, String> {
        let rows = self
            .store
            .list_sessions_with_handles()
            .map_err(|error| error.to_string())?;
        let main_orchestrator = self.store.main_orchestrator().map_err(|error| error.to_string())?;
        let sessions = self
            .sessions
            .lock()
            .unwrap_or_else(std::sync::PoisonError::into_inner);
        rows.into_iter()
            .map(|listed| {
                let mcb_core::session_store::ListedSession { row, handle, helper_of, run_id, run_label, run_lead } = listed;
                let live = sessions.get(&row.owned_id);
                let (
                    provider,
                    state,
                    active_turn_id,
                    pending_permission,
                    pending_input,
                    background_work,
                    native_session_id, can_change_checkout,
                    mut meta,
                ) = if let Some(session) = live {
                    (
                        session.provider,
                        session.state,
                        session.active_turn_id.clone(),
                        !session.permission_requests.is_empty(),
                        !session.user_input_requests.is_empty(),
                        projected_background_work(session),
                        session.native_session_id.clone(),
                        super::capabilities::provider_supports_changing_checkout(&session.capabilities),
                        session.rail_meta.clone(),
                    )
                } else {
                    let (provider, stored, state) = stored_session_projection(&self.store, &row)?;
                    (
                        provider,
                        state,
                        None,
                        false,
                        false,
                        Vec::new(),
                        row.native_session_id.clone(),
                        super::capabilities::provider_supports_changing_checkout(&stored.capabilities),
                        stored.rail_meta,
                    )
                };
                meta.worktree.clone_from(&row.worktree);
                meta.branch.clone_from(&row.branch);
                meta.title.clone_from(&row.title);
                meta.project.clone_from(&row.project);
                let orchestrator_marked_at_ms = main_orchestrator.as_ref()
                    .filter(|(owned_id, _)| *owned_id == row.owned_id).map(|(_, marked_at)| *marked_at);
                Ok(AgentConversationSessionRecord {
                    owned_id: row.owned_id,
                    execution_environment: ExecutionEnvironment::Local,
                    remote_profile_id: None,
                    provider, can_change_checkout,
                    model: row.model,
                    effort: row.effort,
                    cwd: row.cwd,
                    state,
                    suspended: state == AgentRuntimeState::Suspended,
                    created_at_ms: row.created_at_ms,
                    last_activity_at_ms: row.last_activity_at_ms,
                    active_turn_id, pending_permission, pending_input, background_work,
                    numbered_after: super::history::optional_import_cursor(&row.extra_json)?.map(|cursor| cursor.numbered_after).filter(|head| *head > 0),
                    native_session_id,
                    project_id: row.project_id,
                    handle,
                    // Filled by the list command, in one SQL statement for the whole list.
                    project_group_key: String::new(),
                    project_group_label: String::new(),
                    orchestrator_marked_at_ms,
                    helper_of,
                    run_id,
                    run_label,
                    run_lead,
                    meta,
                })
            })
            .collect()
    }

    pub fn list_events(
        &self,
        owned_id: &str,
        from_sequence: i64,
    ) -> Result<Vec<AgentConversationEvent>, String> {
        self.store
            .list_events(owned_id, from_sequence, CATCH_UP_EVENT_CAP)
            .map_err(|error| error.to_string())?
            .into_iter()
            .map(stored_event)
            .collect()
    }

    pub fn list_items_before(
        &self,
        owned_id: &str,
        before_sequence: i64,
        max_bytes: u32,
    ) -> Result<AgentConversationItemPage, String> {
        let mut page = selected_item_page(
            self.store
                .list_items_before(owned_id, before_sequence, max_bytes, None)
                .map_err(|error| error.to_string())?,
        )?;
        page.has_earlier_transcript = self.has_earlier_transcript(owned_id)?;
        Ok(page)
    }

    pub fn list_items_after(
        &self,
        owned_id: &str,
        after_sequence: i64,
        max_bytes: u32,
    ) -> Result<AgentConversationItemPage, String> {
        let mut page = selected_item_page(
            self.store
                .list_items_after(owned_id, after_sequence, max_bytes, None)
                .map_err(|error| error.to_string())?,
        )?;
        page.has_earlier_transcript = self.has_earlier_transcript(owned_id)?;
        Ok(page)
    }

    /// Every permission request waiting on the owner in this manager's sessions.
    pub fn pending_permissions(&self) -> Vec<PendingPermissionItem> {
        let sessions = self.sessions.lock().unwrap_or_else(std::sync::PoisonError::into_inner);
        sessions.iter().flat_map(|(owned_id, session)| session.permission_requests.iter().map(|(request_id, pending)| PendingPermissionItem {
            request_id: request_id.clone(), owned_id: owned_id.clone(), generation: session.generation,
            provider: session.provider, session_title: session.rail_meta.title.clone(), title: pending.summary.clone(),
            command: pending.command.clone(), requested_at_ms: pending.event.timestamp_ms,
            options: pending.options.iter().map(|option| (option.option_id.clone(), option.name.clone(), option.kind.clone())).collect(),
        })).collect()
    }

    /// Whether `owned_id` has a turn running now.
    pub fn turn_running(&self, owned_id: &str) -> bool {
        let sessions = self.sessions.lock().unwrap_or_else(std::sync::PoisonError::into_inner);
        sessions.get(owned_id).is_some_and(|session| session.active_turn_id.is_some())
    }

    /// The page of transcript events just older than `before_sequence`.
    ///
    /// Opening a conversation ships one screen of history; scrolling up calls
    /// this for the previous page until `has_more` says there is nothing older.
    pub fn list_events_before(
        &self,
        owned_id: &str,
        before_sequence: i64,
        max_bytes: u32,
    ) -> Result<AgentConversationEventPage, String> {
        let page = self
            .store
            .list_events_before(owned_id, before_sequence, max_bytes, i64::MIN)
            .map_err(|error| error.to_string())?;
        let events = page
            .events
            .into_iter()
            .map(stored_event)
            .collect::<Result<Vec<AgentConversationEvent>, String>>()?;
        Ok(AgentConversationEventPage {
            events,
            has_more: page.has_more,
        })
    }

    /// The page of transcript events just newer than `after_sequence`.
    pub fn list_events_after(
        &self,
        owned_id: &str,
        after_sequence: i64,
        max_bytes: u32,
    ) -> Result<AgentConversationEventPage, String> {
        let page = self
            .store
            .list_events_after(owned_id, after_sequence, max_bytes, i64::MAX)
            .map_err(|error| error.to_string())?;
        let events = page
            .events
            .into_iter()
            .map(stored_event)
            .collect::<Result<Vec<AgentConversationEvent>, String>>()?;
        Ok(AgentConversationEventPage {
            events,
            has_more: page.has_more,
        })
    }

    pub fn submit_terminal_projection(
        &self,
        owned_id: &str,
        projection: TerminalProjectionPayload,
    ) -> Result<AgentConversationEvent, String> {
        let mut sessions = self
            .sessions
            .lock()
            .unwrap_or_else(std::sync::PoisonError::into_inner);
        let session = sessions
            .get_mut(owned_id)
            .ok_or_else(|| "Agent conversation session was not found".to_string())?;
        record_payload_for_session_and_dispatch(
            session,
            &self.emitter,
            AgentConversationPayload::TerminalProjection(projection),
        )
    }

    pub fn update_session_meta(
        &self,
        request: UpdateAgentConversationSessionMetaRequest,
    ) -> Result<AgentConversationSessionRecord, String> {
        let owned_id = required_id(&request.owned_id, "Owned session id")?;
        let apply = |session: &mut ManagedAgentSession| -> Result<(), String> {
            // The rail row this comes from is a copy that a fresh session has
            // not filled in yet, and it is saved right after the first send.
            // Taking its empty model or effort wrote a blank over the choice
            // every time.
            if let Some(model) = request.model.as_ref() {
                session.config.model = Some(model.clone());
            }
            if let Some(effort) = request.effort.as_ref() {
                session.spawn_reasoning_effort.clone_from(&request.effort);
                session.config.reasoning_effort = Some(effort.clone());
            }
            session.connection.config = session.config.clone();
            // A name typed here is the person's own, and nothing overwrites it
            // afterwards. Everything else this request carries — model, effort,
            // the rail's own copy of the row — arrives with the name unchanged,
            // so only a different one counts as a rename.
            let renamed = request
                .meta
                .title
                .as_deref()
                .is_some_and(|title| !title.trim().is_empty())
                && request.meta.title != session.rail_meta.title;
            session.rail_meta = request.meta.clone();
            if renamed {
                session.title_source = Some(TITLE_SOURCE_USER.to_string());
            }
            persist_session(session)
        };
        let updated_live = {
            let mut sessions = self
                .sessions
                .lock()
                .unwrap_or_else(std::sync::PoisonError::into_inner);
            if let Some(session) = sessions.get_mut(&owned_id) {
                apply(session)?;
                true
            } else {
                false
            }
        };
        if !updated_live {
            let row = self
                .store
                .get_session(&owned_id)
                .map_err(|error| error.to_string())?
                .ok_or_else(|| "Conversation session was not found".to_string())?;
            let mut session = recovered_session_from_row(&self.store, row)?;
            apply(&mut session)?;
        }
        self.list_sessions()?
            .into_iter()
            .find(|session| session.owned_id == owned_id)
            .ok_or_else(|| "Conversation session was not found after metadata update".to_string())
    }

    /// Puts a workflow step's guard policy on its session.
    pub(crate) fn set_guard_policy(&self, owned_id: &str, policy: crate::workflow::policy::GuardPolicy) -> Result<(), String> {
        let mut sessions = self.sessions.lock().unwrap_or_else(std::sync::PoisonError::into_inner);
        sessions.get_mut(owned_id).ok_or_else(|| format!("No session {owned_id}"))?.guard_policy = Some(policy);
        Ok(())
    }

    pub fn set_workflow_session_title(&self, owned_id: &str, title: &str) -> Result<(), String> {
        let set_title = |session: &mut ManagedAgentSession| {
            if !session_title_is_empty(session.rail_meta.title.as_deref()) {
                return Ok(false);
            }
            session.rail_meta.title = Some(title.to_string());
            session.title_source = Some(TITLE_SOURCE_USER.to_string());
            persist_session(session).map(|()| true)
        };
        let live = {
            let mut sessions = self.sessions.lock().unwrap_or_else(std::sync::PoisonError::into_inner);
            sessions.get_mut(owned_id).map(|session| set_title(session))
        };
        let named = if let Some(named) = live {
            named?
        } else {
            let row = self.store.get_session(owned_id).map_err(|error| error.to_string())?
                .ok_or_else(|| "Workflow agent session was not found".to_string())?;
            set_title(&mut recovered_session_from_row(&self.store, row)?)?
        };
        // The rail adds a session it has not seen when it hears its name.
        let listener = self.renamed_listener.lock().unwrap_or_else(std::sync::PoisonError::into_inner).clone();
        if let (true, Some(listener)) = (named, listener) {
            listener(owned_id, title);
        }
        Ok(())
    }

    /// Replaces the provisional name of a session with one the utility model
    /// writes from the first exchange, in the background.
    ///
    /// The provisional name is the first line the person typed, which is rarely
    /// what the session turns out to be about. Once a turn has finished there
    /// is enough to summarise, so the helper is asked for a short one. Nothing
    /// waits on the answer: a send has already finished by the time this runs,
    /// and a helper that is off, slow, or unreachable simply leaves the name
    /// alone. A name the person typed is never touched, and neither is one the
    /// helper has already written, so this happens once per session.
    fn name_session_after_turn(&self, owned_id: &str, generation: u64, turn_id: &str) {
        let namer = {
            let namer = self
                .namer
                .lock()
                .unwrap_or_else(std::sync::PoisonError::into_inner);
            let Some(namer) = namer.as_ref() else {
                return;
            };
            Arc::clone(namer)
        };
        let store = {
            let sessions = self
                .sessions
                .lock()
                .unwrap_or_else(std::sync::PoisonError::into_inner);
            let Ok(session) = current_session(&sessions, owned_id, generation) else {
                return;
            };
            if !title_can_be_replaced(session.title_source.as_deref()) {
                return;
            }
            Arc::clone(&session.store)
        };
        let sessions = Arc::clone(&self.sessions);
        let renamed_listener = Arc::clone(&self.renamed_listener);
        let owned_id = owned_id.to_string();
        let turn_id = turn_id.to_string();
        // The helper's transport blocks, and so do the two reads that gather
        // what it is given, so neither runs on the thread carrying the session's
        // events. This is the last thing a finished turn does, and the pump
        // moves on without it.
        tokio::task::spawn_blocking(move || {
            let Some(input) = title_input(&store, &owned_id, &turn_id) else {
                return;
            };
            let answer = match namer(&input) {
                Ok(answer) => answer,
                // No key stored means the helper is off, which is a choice
                // rather than a fault: the session keeps its prompt name.
                Err(crate::helper::HelperError::NoKey) => return,
                Err(error) => {
                    crate::debug_log::stderr_log!(
                        "Could not name the session: {}",
                        error.sentence()
                    );
                    return;
                }
            };
            let Some(title) = helper_title(&answer) else {
                return;
            };
            let updated_live = {
                let mut sessions = sessions
                    .lock()
                    .unwrap_or_else(std::sync::PoisonError::into_inner);
                match sessions.get_mut(&owned_id) {
                    Some(session) if session.generation == generation => {
                        // The person may have renamed the session while the
                        // helper was being asked, and their name wins.
                        if !title_can_be_replaced(session.title_source.as_deref()) {
                            return;
                        }
                        session.rail_meta.title = Some(title.clone());
                        session.title_source = Some(TITLE_SOURCE_HELPER.to_string());
                        if let Err(error) = persist_session(session) {
                            crate::debug_log::stderr_log!(
                                "Could not save the session name: {error}"
                            );
                            return;
                        }
                        true
                    }
                    Some(_) => return,
                    None => false,
                }
            };
            if !updated_live {
                let Some(mut row) = store
                    .get_session(&owned_id)
                    .map_err(|error| error.to_string())
                    .unwrap_or_else(|error| {
                        crate::debug_log::stderr_log!("Could not read the session name: {error}");
                        None
                    })
                else {
                    return;
                };
                let Ok(mut stored) = serde_json::from_str::<StoredSessionExtra>(&row.extra_json)
                else {
                    return;
                };
                if stored.generation != generation
                    || !title_can_be_replaced(row.title_source.as_deref())
                {
                    return;
                }
                row.title = Some(title.clone());
                row.title_source = Some(TITLE_SOURCE_HELPER.to_string());
                stored.rail_meta.title = Some(title.clone());
                let Ok(extra_json) = serde_json::to_string(&stored) else {
                    return;
                };
                row.extra_json = extra_json;
                if let Err(error) = store.upsert_session(&row) {
                    crate::debug_log::stderr_log!("Could not save the session name: {error}");
                    return;
                }
            }
            let listener = renamed_listener
                .lock()
                .unwrap_or_else(std::sync::PoisonError::into_inner)
                .clone();
            if let Some(listener) = listener {
                listener(&owned_id, &title);
            }
        });
    }

    /// Stop local adapter transports and persist interrupted work before app exit.
    /// Runtime locks can be held by prompts, so shutdown uses transports directly.
    pub async fn shutdown(&self) {
        let mut transports: Vec<_> = self
            .adapter_pools
            .lock()
            .await
            .values()
            .map(|pool| Arc::clone(&pool.transport))
            .collect();
        {
            let sessions = self
                .sessions
                .lock()
                .unwrap_or_else(std::sync::PoisonError::into_inner);
            for transport in sessions.values().filter_map(|session| session.transport.as_ref()) {
                if !transports.iter().any(|current| Arc::ptr_eq(current, transport)) {
                    transports.push(Arc::clone(transport));
                }
            }
        }
        // Capture ownership before stop takes the process handles out of transports.
        let processes: Vec<_> = transports.iter().filter_map(|transport| transport.process_id())
            .map(|pid| (pid, super::reaper::detached_descendant_identities(pid))).collect();
        futures_util::future::join_all(transports.iter().map(|transport| transport.stop())).await;
        let deadline = tokio::time::Instant::now() + super::providers::process::STOP_GRACE;
        while processes.iter().any(|(pid, _)| unsafe { libc::kill(-(*pid as i32), 0) } == 0)
            && tokio::time::Instant::now() < deadline
        {
            tokio::time::sleep(Duration::from_millis(10)).await;
        }
        // Exit can abandon the normal detached watchdog; finish its cleanup here.
        for (pid, detached) in processes {
            super::reaper::signal_process_identities(&detached, libc::SIGKILL);
            let group = -(pid as i32);
            if unsafe { libc::kill(group, 0) } == 0 {
                let _ = unsafe { libc::kill(group, libc::SIGKILL) };
            }
        }
        for transport in transports {
            // Do not leave terminal journal writes to tasks the exiting runtime
            // may abandon. The existing disconnect handler preserves native IDs.
            settle_closed_transport(self, &transport, "Assembly exited").await;
        }
    }

    pub async fn suspend_if_quiescent(
        &self,
        owned_id: &str,
        generation: u64,
    ) -> Result<bool, String> {
        if !self.hydrate_overlay_from_store(owned_id)? {
            return Ok(false);
        }
        let _lifecycle = self.lifecycle_guard(owned_id).await?;
        let (runtime, transport, ordered_events, pool_key, native_session_id) = {
            let mut sessions = self
                .sessions
                .lock()
                .unwrap_or_else(std::sync::PoisonError::into_inner);
            let session = lifecycle_session_mut(&mut sessions, owned_id, generation)?;
            if !session_can_suspend(session) {
                return Ok(false);
            }
            let Some(runtime) = session.runtime.take() else {
                return Ok(false);
            };
            session.suspending = true;
            (
                runtime,
                session.transport.take(),
                session.ordered_events.take(),
                session.pool_key.take(),
                session.native_session_id.clone(),
            )
        };

        let detach_result = if let Some(pool_key) = &pool_key {
            self.release_pool_scope(pool_key, owned_id, native_session_id.as_deref(), false)
                .await
        } else {
            runtime
                .lock()
                .await
                .detach_session()
                .await
                .map_err(|error| error.to_string())
        };
        if let Err(error) = detach_result {
            let mut sessions = self
                .sessions
                .lock()
                .unwrap_or_else(std::sync::PoisonError::into_inner);
            if let Ok(session) = lifecycle_session_mut(&mut sessions, owned_id, generation) {
                session.suspending = false;
                session.runtime = Some(runtime);
                session.transport = transport;
                session.ordered_events = ordered_events;
                session.pool_key = pool_key;
            }
            return Err(error);
        }
        if let Err(error) = super::history::follow_imported_length(&self.store, owned_id, false) { crate::debug_log::stderr_log!("Could not follow the imported transcript: {error}"); }
        let mut sessions = self
            .sessions
            .lock()
            .unwrap_or_else(std::sync::PoisonError::into_inner);
        let session = lifecycle_session_mut(&mut sessions, owned_id, generation)?;
        if !session.suspending || session.state != AgentRuntimeState::Ready {
            return Err(
                "Conversation lifecycle changed while suspension was in progress".to_string(),
            );
        }
        session.suspending = false;
        record_payload_for_session_and_dispatch_with_lifecycle(
            session,
            &self.emitter,
            AgentConversationPayload::Connection {
                state: ConversationConnectionState::Disconnected,
                native_session_id: session.native_session_id.clone(),
            },
            SessionLifecycleUpdate {
                state: AgentRuntimeState::Suspended,
                connection_state: ConversationConnectionState::Disconnected,
                owner: AgentExecutionOwner::Stopped,
                writer_owner: AgentWriterLeaseOwner::None,
                native_session_mode: Some(AgentNativeSessionMode::Resume),
            },
        )?;
        if let Some(mut session) = sessions.remove(owned_id) {
            if let Some(task) = session.child_rollout_scan.take() {
                task.abort();
            }
        }
        Ok(true)
    }

    /// Start this conversation's adapter, keep what its handshake answered, and
    /// stop the process again.
    ///
    /// A conversation imported from a past transcript has never run, so nothing
    /// has told it which models it offers, which effort levels it takes or which
    /// approval policies it understands. Those answers arrive only with an
    /// adapter's handshake, and the adapter is otherwise started by a turn —
    /// which is why a resumed conversation opened saying its agent had no
    /// settings. Asking once, here, is what fills the composer in.
    ///
    /// The process is STOPPED afterwards rather than suspended, and the
    /// difference is the whole reason an earlier attempt at this was taken out
    /// again: suspending detaches the session and keeps the adapter warm for the
    /// next turn, so one was left running per resume, hundreds of megabytes
    /// each. What activation learnt is on disk by then, so nothing is lost by
    /// stopping. The session is left Suspended, which is the state a session
    /// between turns is already in, and the next send resumes it the ordinary
    /// way.
    pub async fn warm_conversation_config(
        &self,
        owned_id: &str,
        generation: u64,
    ) -> Result<AgentConversationConfigState, String> {
        self.hydrate_overlay_from_store(owned_id)?;
        let _lifecycle = self.lifecycle_guard(owned_id).await?;
        self.activate_locked(owned_id, generation).await?;
        let (runtime, transport, ordered_events, pool_key, native_session_id) = {
            let mut sessions = self
                .sessions
                .lock()
                .unwrap_or_else(std::sync::PoisonError::into_inner);
            let session = lifecycle_session_mut(&mut sessions, owned_id, generation)?;
            // A send can reach `prompt` while this is running — that path takes
            // the sessions lock but not the lifecycle guard — and stopping an
            // adapter mid-turn leaves the turn id set against a process that has
            // been killed, which refuses every later send as "already has an
            // active turn". Someone talking to the conversation outranks warming
            // it: the answers are already stored, so leaving the adapter up is
            // the harmless half of the choice. Quiescence rather than
            // `session_can_suspend`, whose `capabilities.session.resume`
            // requirement would skip the very providers this exists for.
            if !session_is_quiescent(session) {
                return Ok(session.config.clone());
            }
            let Some(runtime) = session.runtime.take() else {
                // Activation started nothing, so there is nothing to stop and
                // whatever is known is already stored.
                return Ok(session.config.clone());
            };
            session.suspending = true;
            (
                runtime,
                session.transport.take(),
                session.ordered_events.take(),
                session.pool_key.take(),
                session.native_session_id.clone(),
            )
        };

        // Both branches stop the process — `close` and `detach` alike end in
        // `transport.stop()` — and they differ only in whether `session/close`
        // is sent first. Warming asks for neither: the session has to stay
        // resumable, so closing it would be wrong even where it works, and it
        // does not always work. Claude's adapter answers `session/close` with
        // "Method not found" (-32601), which turned a successful warm into a
        // failed one. The leak this design guards against was never detach; it
        // was `suspend_if_quiescent` bailing before any teardown at all.
        let stop_result = if let Some(pool_key) = &pool_key {
            self.release_pool_scope(pool_key, owned_id, native_session_id.as_deref(), false)
                .await
        } else {
            runtime
                .lock()
                .await
                .detach_session()
                .await
                .map_err(|error| error.to_string())
        };
        if let Err(error) = stop_result {
            let mut sessions = self
                .sessions
                .lock()
                .unwrap_or_else(std::sync::PoisonError::into_inner);
            if let Ok(session) = lifecycle_session_mut(&mut sessions, owned_id, generation) {
                session.suspending = false;
                session.runtime = Some(runtime);
                session.transport = transport;
                session.ordered_events = ordered_events;
                session.pool_key = pool_key;
            }
            return Err(error);
        }
        if let Err(error) = super::history::follow_imported_length(&self.store, owned_id, false) { crate::debug_log::stderr_log!("Could not follow the imported transcript: {error}"); }
        let mut sessions = self
            .sessions
            .lock()
            .unwrap_or_else(std::sync::PoisonError::into_inner);
        let session = lifecycle_session_mut(&mut sessions, owned_id, generation)?;
        // The same race again, on the other side of the stop: writing Suspended
        // over a session that has meanwhile started a turn would claim a state
        // that is not true. The adapter is already gone by here, so this reports
        // rather than pretends.
        if !session.suspending || session.state != AgentRuntimeState::Ready {
            session.suspending = false;
            return Err(
                "Conversation lifecycle changed while its settings were being read".to_string(),
            );
        }
        session.suspending = false;
        record_payload_for_session_and_dispatch_with_lifecycle(
            session,
            &self.emitter,
            AgentConversationPayload::Connection {
                state: ConversationConnectionState::Disconnected,
                native_session_id: session.native_session_id.clone(),
            },
            SessionLifecycleUpdate {
                state: AgentRuntimeState::Suspended,
                connection_state: ConversationConnectionState::Disconnected,
                owner: AgentExecutionOwner::Stopped,
                writer_owner: AgentWriterLeaseOwner::None,
                native_session_mode: Some(AgentNativeSessionMode::Resume),
            },
        )?;
        let config = session.config.clone();
        sessions.remove(owned_id);
        Ok(config)
    }

    pub async fn close(&self, owned_id: &str, generation: u64) -> Result<bool, String> {
        self.authentications.cancel(owned_id, generation);
        if !self.hydrate_overlay_from_store(owned_id)? {
            return Ok(false);
        }
        let _lifecycle = self.lifecycle_guard(owned_id).await?;
        self.authentications.cancel(owned_id, generation);
        let (runtime, transport, pool_key, native_session_id, pending_permissions, pending_inputs) = {
            let mut sessions = self
                .sessions
                .lock()
                .unwrap_or_else(std::sync::PoisonError::into_inner);
            let Some(session) = sessions.get_mut(owned_id) else {
                return Ok(false);
            };
            if session.generation != generation {
                return Err(
                    "Conversation connection changed; retry on the current session".to_string(),
                );
            }
            let transport = session.transport.take();
            if let Some(task) = session.child_rollout_scan.take() {
                task.abort();
            }
            abort_cancel_deadline(session);
            session.ordered_events = None;
            let pending_permission_rows = session.permission_requests.drain().collect::<Vec<_>>();
            let mut pending_permissions = Vec::with_capacity(pending_permission_rows.len());
            for (request_id, pending) in pending_permission_rows {
                let _ = record_payload_for_session_and_dispatch(
                    session,
                    &self.emitter,
                    AgentConversationPayload::Approval {
                        request_id,
                        state: ApprovalState::Expired,
                        summary: pending.summary, options: Vec::new(),
                    },
                );
                pending_permissions.push(pending.wire_id);
            }
            let pending_input_rows = session.user_input_requests.drain().collect::<Vec<_>>();
            let mut pending_inputs = Vec::with_capacity(pending_input_rows.len());
            for (request_id, pending) in pending_input_rows {
                let _ = record_payload_for_session_and_dispatch(
                    session,
                    &self.emitter,
                    AgentConversationPayload::UserInputResolved {
                        request_id,
                        cancelled: true,
                    },
                );
                pending_inputs.push(pending);
            }
            finish_autonomous_turn(
                session,
                &self.emitter,
                super::protocol::TurnState::Interrupted,
                AgentRuntimeState::Ready,
            );
            record_payload_for_session_and_dispatch_with_lifecycle(
                session,
                &self.emitter,
                AgentConversationPayload::Connection {
                    state: ConversationConnectionState::Closed,
                    native_session_id: session.native_session_id.clone(),
                },
                SessionLifecycleUpdate {
                    state: AgentRuntimeState::Closed,
                    connection_state: ConversationConnectionState::Closed,
                    owner: AgentExecutionOwner::Stopped,
                    writer_owner: AgentWriterLeaseOwner::None,
                    native_session_mode: None,
                },
            )?;
            (
                session.runtime.take(),
                transport,
                session.pool_key.take(),
                session.native_session_id.clone(),
                pending_permissions,
                pending_inputs,
            )
        };
        if let Some(transport) = transport {
            for wire_id in pending_permissions {
                let _ = transport
                    .respond(
                        wire_id,
                        serde_json::json!({ "outcome": { "outcome": "cancelled" } }),
                    )
                    .await;
            }
            for pending in pending_inputs {
                let response = cancelled_user_input_response(&pending);
                let _ = transport
                    .respond(pending.wire_id, response)
                    .await;
            }
        }
        if let Some(pool_key) = pool_key {
            self.release_pool_scope(&pool_key, owned_id, native_session_id.as_deref(), true)
                .await?;
        } else if let Some(runtime) = runtime {
            runtime
                .lock()
                .await
                .close_session()
                .await
                .map_err(|error| error.to_string())?;
        }
        let mut sessions = self
            .sessions
            .lock()
            .unwrap_or_else(std::sync::PoisonError::into_inner);
        let session = current_session(&sessions, owned_id, generation)?;
        if session.state != AgentRuntimeState::Closed
            || session.connection.state != ConversationConnectionState::Closed
        {
            return Err("Conversation lifecycle changed while close was in progress".to_string());
        }
        sessions.remove(owned_id);
        Ok(true)
    }

    /// Take a conversation out of the store for good.
    ///
    /// Stops it first if anything is running it, then drops the row — and with
    /// it, through the store's cascades, its events, draft, annotations and
    /// attachment records — and forgets it here, so nothing rebuilds it at the
    /// next launch. The provider's own transcript on disk is not touched.
    /// Answers whether there was anything to delete.
    pub async fn delete(&self, owned_id: &str) -> Result<bool, String> {
        let owned_id = required_id(owned_id, "Owned session id")?;
        let live = {
            let sessions = self
                .sessions
                .lock()
                .unwrap_or_else(std::sync::PoisonError::into_inner);
            sessions.get(&owned_id).map(|session| {
                (
                    session.generation,
                    session.state != AgentRuntimeState::Closed,
                )
            })
        };
        if let Some((generation, open)) = live {
            if open {
                self.close(&owned_id, generation).await?;
            }
        } else if self
            .store
            .get_session(&owned_id)
            .map_err(|error| error.to_string())?
            .is_none()
        {
            return Ok(false);
        }
        let _lifecycle = self.lifecycle_guard(&owned_id).await?;
        self.sessions
            .lock()
            .unwrap_or_else(std::sync::PoisonError::into_inner)
            .remove(&owned_id);
        self.activation_locks
            .lock()
            .unwrap_or_else(std::sync::PoisonError::into_inner)
            .remove(&owned_id);
        self.broker_statuses
            .lock()
            .unwrap_or_else(std::sync::PoisonError::into_inner)
            .remove(&owned_id);
        self.store
            .delete_session(&owned_id)
            .map_err(|error| error.to_string())?;
        super::messaging::endpoint()?.revoke(&owned_id);
        Ok(true)
    }

    pub(crate) fn handoff_prepare(
        &self,
        request: &AgentConversationHandoffRequest,
    ) -> Result<HandoffContext, String> {
        let mut sessions = self
            .sessions
            .lock()
            .unwrap_or_else(std::sync::PoisonError::into_inner);
        let session = current_session_mut(&mut sessions, &request.owned_id, request.generation)?;
        let expected = request
            .expected_owner
            .unwrap_or_else(|| match request.direction {
                AgentConversationHandoffDirection::StructuredToTerminal => {
                    AgentWriterLeaseOwner::Structured
                }
                AgentConversationHandoffDirection::TerminalToStructured => {
                    AgentWriterLeaseOwner::Terminal
                }
            });
        if session.writer_lease.owner != expected {
            return Err("The current writer owner does not match the handoff request".to_string());
        }
        if session.writer_lease_transition.is_some() {
            return Err("Another handoff is already in progress".to_string());
        }
        if request.mode == AgentConversationHandoffMode::Fork {
            if request.direction != AgentConversationHandoffDirection::StructuredToTerminal {
                return Err("Only structured conversations can fork to a terminal".to_string());
            }
            if !session.capabilities.session.fork {
                return Err("The current provider did not advertise fork support".to_string());
            }
            let target = request
                .target_owned_id
                .as_deref()
                .ok_or_else(|| "Fork handoff requires a distinct target owned id".to_string())?;
            if target.trim().is_empty() || target == request.owned_id {
                return Err("Fork handoff requires a distinct target owned id".to_string());
            }
        }
        let to = match request.direction {
            AgentConversationHandoffDirection::StructuredToTerminal => {
                AgentWriterLeaseOwner::Terminal
            }
            AgentConversationHandoffDirection::TerminalToStructured => {
                AgentWriterLeaseOwner::Structured
            }
        };
        let previous_owner = session.writer_lease.owner;
        let owner = match request.direction {
            AgentConversationHandoffDirection::StructuredToTerminal => {
                AgentExecutionOwner::TransitioningToTerminal
            }
            AgentConversationHandoffDirection::TerminalToStructured => {
                AgentExecutionOwner::TransitioningToStructured
            }
        };
        session.transition_lifecycle(
            session.state,
            session.connection.state,
            owner,
            session.writer_lease.owner,
        )?;
        session.writer_lease_transition = Some(AgentWriterLeaseTransition {
            owned_id: request.owned_id.clone(),
            generation: request.generation,
            from: previous_owner,
            to,
            state: super::protocol::AgentWriterLeaseTransitionState::Requested,
            error: None,
        });
        Ok(HandoffContext {
            previous_owner,
            owner: previous_owner,
            native_session_id: session.native_session_id.clone(),
        })
    }

    pub(crate) async fn detach_structured_runtime(
        &self,
        owned_id: &str,
        generation: u64,
    ) -> Result<(), String> {
        let runtime = {
            let mut sessions = self
                .sessions
                .lock()
                .unwrap_or_else(std::sync::PoisonError::into_inner);
            let session = lifecycle_session_mut(&mut sessions, owned_id, generation)?;
            if session.owner != AgentExecutionOwner::TransitioningToTerminal {
                return Err("Structured runtime is not in a terminal handoff".to_string());
            }
            session.runtime.take()
        };
        let Some(runtime) = runtime else {
            return Ok(());
        };
        let result = runtime.lock().await.detach_session().await;
        if let Err(error) = result {
            let mut sessions = self
                .sessions
                .lock()
                .unwrap_or_else(std::sync::PoisonError::into_inner);
            if let Ok(session) = lifecycle_session_mut(&mut sessions, owned_id, generation) {
                session.runtime = Some(runtime);
            }
            return Err(error.to_string());
        }
        let mut sessions = self
            .sessions
            .lock()
            .unwrap_or_else(std::sync::PoisonError::into_inner);
        let session = lifecycle_session_mut(&mut sessions, owned_id, generation)?;
        if session.owner != AgentExecutionOwner::TransitioningToTerminal {
            return Err("Conversation lifecycle changed while handoff was in progress".to_string());
        }
        Ok(())
    }

    pub(crate) fn validate_handoff_history(
        &self,
        request: &AgentConversationHandoffRequest,
    ) -> Result<(), String> {
        let sessions = self
            .sessions
            .lock()
            .unwrap_or_else(std::sync::PoisonError::into_inner);
        let session = current_session(&sessions, &request.owned_id, request.generation)?;
        let boundary = &request.history_boundary;
        if request.native_session_id != session.native_session_id {
            return Err("Handoff native session does not match the current session".to_string());
        }
        if boundary.native_session_id != session.native_session_id {
            return Err(
                "Handoff history native session does not match the current session".to_string(),
            );
        }
        if session.native_session_id.is_none() {
            return Err(
                "A native session id is required to resume the structured session".to_string(),
            );
        }
        let last_sequence = session.next_sequence.saturating_sub(1);
        if boundary.first_sequence > boundary.last_sequence
            || boundary.last_sequence > last_sequence
        {
            return Err("Handoff history boundary is outside the stored conversation".to_string());
        }
        if boundary.reconciled_sequence != Some(last_sequence) {
            return Err("Handoff history was not reconciled to the current sequence".to_string());
        }
        Ok(())
    }

    pub(crate) fn validate_handoff_identity(
        &self,
        request: &AgentConversationHandoffRequest,
    ) -> Result<(), String> {
        let sessions = self
            .sessions
            .lock()
            .unwrap_or_else(std::sync::PoisonError::into_inner);
        let session = current_session(&sessions, &request.owned_id, request.generation)?;
        if request.native_session_id != session.native_session_id {
            return Err("Handoff native session does not match the current session".to_string());
        }
        if request.history_boundary.native_session_id != request.native_session_id {
            return Err("Handoff history native session does not match the request".to_string());
        }
        Ok(())
    }

    pub(crate) async fn handoff_commit(
        &self,
        request: &AgentConversationHandoffRequest,
    ) -> Result<AgentConversationHandoffReceipt, String> {
        let mut sessions = self
            .sessions
            .lock()
            .unwrap_or_else(std::sync::PoisonError::into_inner);
        let session = lifecycle_session_mut(&mut sessions, &request.owned_id, request.generation)?;
        let transition = session
            .writer_lease_transition
            .clone()
            .ok_or_else(|| "Handoff commit has no prepared transition".to_string())?;
        let expected_to = match request.direction {
            AgentConversationHandoffDirection::StructuredToTerminal => {
                AgentWriterLeaseOwner::Terminal
            }
            AgentConversationHandoffDirection::TerminalToStructured => {
                AgentWriterLeaseOwner::Structured
            }
        };
        if transition.to != expected_to || transition.from != session.writer_lease.owner {
            return Err("Handoff commit does not match the prepared transition".to_string());
        }
        let previous_owner = transition.from;
        let owner = if request.mode == AgentConversationHandoffMode::Fork {
            previous_owner
        } else {
            expected_to
        };
        session.writer_lease_transition = None;
        let execution_owner = match owner {
            AgentWriterLeaseOwner::Structured => AgentExecutionOwner::Structured,
            AgentWriterLeaseOwner::Terminal => AgentExecutionOwner::Terminal,
            AgentWriterLeaseOwner::None => AgentExecutionOwner::Stopped,
        };
        session.transition_lifecycle(
            session.state,
            session.connection.state,
            execution_owner,
            owner,
        )?;
        Ok(AgentConversationHandoffReceipt {
            owned_id: request
                .target_owned_id
                .clone()
                .filter(|_| request.mode == AgentConversationHandoffMode::Fork)
                .unwrap_or_else(|| request.owned_id.clone()),
            generation: request.generation,
            direction: request.direction,
            mode: request.mode,
            phase: AgentConversationHandoffPhase::Commit,
            previous_owner,
            owner,
            native_session_id: session.native_session_id.clone(),
            pty_session_id: request
                .pty_session_id
                .clone()
                .or_else(|| request.process_tree.pty_session_id.clone()),
            history_boundary: request.history_boundary.clone(),
            process_tree: request.process_tree.clone(),
            rollback_available: false,
            message: "Handoff committed without closing the terminal scrollback".to_string(),
        })
    }

    pub(crate) fn handoff_rollback(
        &self,
        request: &AgentConversationHandoffRequest,
    ) -> Result<AgentConversationHandoffReceipt, String> {
        let mut sessions = self
            .sessions
            .lock()
            .unwrap_or_else(std::sync::PoisonError::into_inner);
        let session = lifecycle_session_mut(&mut sessions, &request.owned_id, request.generation)?;
        let transition = session
            .writer_lease_transition
            .take()
            .ok_or_else(|| "Handoff rollback has no prepared transition".to_string())?;
        let owner = match transition.from {
            AgentWriterLeaseOwner::Structured => AgentExecutionOwner::Structured,
            AgentWriterLeaseOwner::Terminal => AgentExecutionOwner::Terminal,
            AgentWriterLeaseOwner::None => AgentExecutionOwner::Stopped,
        };
        session.transition_lifecycle(
            session.state,
            session.connection.state,
            owner,
            transition.from,
        )?;
        Ok(AgentConversationHandoffReceipt {
            owned_id: request.owned_id.clone(),
            generation: request.generation,
            direction: request.direction,
            mode: request.mode,
            phase: AgentConversationHandoffPhase::Rollback,
            previous_owner: transition.from,
            owner: transition.from,
            native_session_id: session.native_session_id.clone(),
            pty_session_id: request
                .pty_session_id
                .clone()
                .or_else(|| request.process_tree.pty_session_id.clone()),
            history_boundary: request.history_boundary.clone(),
            process_tree: request.process_tree.clone(),
            rollback_available: false,
            message: "Handoff rolled back; existing terminal scrollback was preserved".to_string(),
        })
    }

    pub(crate) async fn lifecycle_guard(
        &self,
        owned_id: &str,
    ) -> Result<OwnedMutexGuard<()>, String> {
        Ok(self.activation_lock(owned_id)?.lock_owned().await)
    }

    fn activation_lock(&self, owned_id: &str) -> Result<Arc<AsyncMutex<()>>, String> {
        let mut locks = self
            .activation_locks
            .lock()
            .unwrap_or_else(std::sync::PoisonError::into_inner);
        locks.retain(|_, lock| Arc::strong_count(lock) > 1);
        Ok(locks
            .entry(owned_id.to_string())
            .or_insert_with(|| Arc::new(AsyncMutex::new(())))
            .clone())
    }

    fn runtime(
        &self,
        owned_id: &str,
        generation: u64,
    ) -> Result<Arc<AsyncMutex<StructuredRuntimeHandle>>, String> {
        let sessions = self
            .sessions
            .lock()
            .unwrap_or_else(std::sync::PoisonError::into_inner);
        let session = current_session(&sessions, owned_id, generation)?;
        if session.owner != AgentExecutionOwner::Structured {
            return Err("The structured writer is not the current owner".to_string());
        }
        session
            .runtime
            .clone()
            .ok_or_else(|| "Structured provider is still connecting".to_string())
    }

    /// The runtime for a session, waking a suspended one first.
    ///
    /// A session restored from the database has no runtime until something
    /// activates it. Sending a message does that on the way through, so a
    /// session a person has been talking to is awake; changing a setting used
    /// to be refused outright instead, which read as "still connecting" on a
    /// session that was demonstrably live.
    async fn runtime_or_activate(
        &self,
        owned_id: &str,
        generation: u64,
    ) -> Result<Arc<AsyncMutex<StructuredRuntimeHandle>>, String> {
        match self.runtime(owned_id, generation) {
            Ok(runtime) => Ok(runtime),
            Err(error) => {
                if !self.session_is_suspended(owned_id, generation) {
                    return Err(error);
                }
                self.activate(owned_id, generation).await?;
                self.runtime(owned_id, generation)
            }
        }
    }

    /// True when a session is stored and recoverable but has no runtime yet.
    fn session_is_suspended(&self, owned_id: &str, generation: u64) -> bool {
        {
            let sessions = self
                .sessions
                .lock()
                .unwrap_or_else(std::sync::PoisonError::into_inner);
            if let Ok(session) = current_session(&sessions, owned_id, generation) {
                return session.runtime.is_none() && session.state == AgentRuntimeState::Suspended;
            }
        }

        self.store
            .get_session(owned_id)
            .ok()
            .flatten()
            .and_then(|row| stored_session_projection(&self.store, &row).ok())
            .is_some_and(|(_, stored, state)| {
                stored.generation == generation && state == AgentRuntimeState::Suspended
            })
    }

    #[cfg(test)]
    pub(crate) fn session_terminal_ownership(
        &self,
        owned_id: &str,
    ) -> Option<AgentWriterLeaseOwner> {
        self.sessions
            .lock()
            .unwrap_or_else(std::sync::PoisonError::into_inner)
            .get(owned_id)
            .map(|session| session.writer_lease.owner)
    }
}
// Codex watches shared .codex/sessions; Claude watches <parent>/subagents and matches JSON agentId (transcript/codex.rs:47–54, claude.rs:346–364).
fn child_watch_event_relevant(
    event: &notify::Event,
    known_path: Option<&Path>,
    provider: AgentConversationProvider,
    child_session_id: &str,
) -> bool {
    let writes_source = matches!(
        event.kind,
        notify::EventKind::Create(_)
            | notify::EventKind::Modify(notify::event::ModifyKind::Data(_))
            | notify::EventKind::Modify(notify::event::ModifyKind::Name(_))
            | notify::EventKind::Access(notify::event::AccessKind::Close(
                notify::event::AccessMode::Write
            ))
    );
    writes_source
        && known_path.map_or_else(
            || {
                provider != AgentConversationProvider::Codex
                    || event.paths.iter().any(|path| {
                        path.file_name()
                            .and_then(|value| value.to_str())
                            .is_some_and(|name| name.contains(child_session_id))
                    })
            },
            |path| event.paths.iter().any(|changed| changed == path),
        )
}

impl Drop for AgentRuntimeManager {
    fn drop(&mut self) {
        if Arc::strong_count(&self.child_history_watchers) == 1 {
            let watchers = std::mem::take(
                &mut *self
                    .child_history_watchers
                    .lock()
                    .unwrap_or_else(std::sync::PoisonError::into_inner),
            );
            for (_, watcher) in watchers {
                watcher.stop();
            }
        }
        if Arc::strong_count(&self.sessions) == 1 {
            self.sessions
                .lock()
                .unwrap_or_else(std::sync::PoisonError::into_inner)
                .clear();
        }
    }
}

#[derive(Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
struct StoredSessionExtra {
    generation: u64,
    native_session_mode: AgentNativeSessionMode,
    owner: AgentExecutionOwner,
    config: AgentConversationConfigState,
    capabilities: AgentCapabilities,
    #[serde(default)]
    rail_meta: AgentConversationSessionMeta,
    #[serde(default)]
    claude_children: HashMap<String, ClaudeChildSession>,
    #[serde(default)]
    helper_of: Option<String>,
}

/// The stored metadata an imported conversation starts life with.
///
/// An import writes a session row without starting an adapter, so it has no
/// generation, no capabilities and no configuration of its own to record. It
/// cannot leave them out: every stored row is decoded together at launch, and
/// one row missing any of these fields fails that decode and takes the whole
/// launch with it. So an import writes what a session holds before its first
/// run — nothing is live, and the fields are all present.
pub(super) fn imported_session_extra(
    provider: AgentConversationProvider,
) -> Result<String, String> {
    serde_json::to_string(&StoredSessionExtra {
        generation: 0,
        native_session_mode: AgentNativeSessionMode::Resume,
        owner: AgentExecutionOwner::Stopped,
        config: AgentConversationConfigState::default(),
        capabilities: empty_capabilities(provider),
        rail_meta: AgentConversationSessionMeta::default(),
        claude_children: HashMap::new(),
        helper_of: None,
    })
    .map_err(|error| format!("Could not encode imported session metadata: {error}"))
}

/// Builds the durable row from the current live session for metadata-only writes.
fn persisted_session_row(session: &ManagedAgentSession) -> Result<SessionRow, String> {
    let candidate = SessionEventCandidate::from_session(session);
    persisted_session_row_for_candidate(session, &candidate)
}

/// Builds the session row that must become visible with a pending event.
fn persisted_session_row_for_candidate(
    session: &ManagedAgentSession,
    candidate: &SessionEventCandidate,
) -> Result<SessionRow, String> {
    let extra = StoredSessionExtra {
        generation: session.generation,
        native_session_mode: candidate.native_session_mode,
        owner: candidate.owner,
        config: session.config.clone(),
        capabilities: candidate.capabilities.clone(),
        rail_meta: session.rail_meta.clone(),
        claude_children: session.claude_children.clone(),
        helper_of: session.helper_of.clone(),
    };
    Ok(SessionRow {
        owned_id: session.owned_id.clone(),
        native_session_id: candidate.native_session_id.clone(),
        provider: enum_storage_value(session.provider)?,
        model: session.config.model.clone(),
        effort: session
            .config
            .reasoning_effort
            .clone()
            .or_else(|| session.spawn_reasoning_effort.clone()),
        cwd: session.cwd.clone(),
        worktree: session
            .rail_meta
            .worktree
            .clone()
            .or_else(|| Some(session.cwd.clone())),
        branch: session.rail_meta.branch.clone(),
        title: session.rail_meta.title.clone(),
        title_source: session.title_source.clone(),
        project: session.rail_meta.project.clone(),
        project_id: session.project_id.clone(),
        state: enum_storage_value(candidate.state)?,
        suspended: candidate.state == AgentRuntimeState::Suspended,
        created_at_ms: store_timestamp(session.created_at_ms),
        last_activity_at_ms: store_timestamp(candidate.last_activity_ms),
        extra_json: serde_json::to_string(&extra)
            .map_err(|error| format!("Could not encode stored session metadata: {error}"))?,
    })
}

/// Persists rail-only metadata when no event needs to share the transaction.
fn persist_session(session: &ManagedAgentSession) -> Result<(), String> {
    let row = persisted_session_row(session)?;
    session
        .store
        .upsert_session(&row)
        .map_err(|error| error.to_string())
}

fn normalize_sessions_in_store(store: &Arc<SessionStore>) -> Result<(), String> {
    for row in store.list_sessions().map_err(|error| error.to_string())? {
        let session = recovered_session_from_row(store, row)?;
        persist_session(&session)?;
    }
    Ok(())
}

fn stored_session_projection(
    store: &Arc<SessionStore>,
    row: &SessionRow,
) -> Result<
    (
        AgentConversationProvider,
        StoredSessionExtra,
        AgentRuntimeState,
    ),
    String,
> {
    let provider: AgentConversationProvider = enum_from_storage(&row.provider)?;
    let mut stored: StoredSessionExtra = serde_json::from_str(&row.extra_json)
        .map_err(|error| format!("Could not decode stored session metadata: {error}"))?;
    stored.rail_meta.worktree.clone_from(&row.worktree);
    stored.rail_meta.branch.clone_from(&row.branch);
    stored.rail_meta.title.clone_from(&row.title);
    stored.rail_meta.project.clone_from(&row.project);
    if session_title_is_empty(stored.rail_meta.title.as_deref()) {
        stored.rail_meta.title = store
            .first_user_message_payload(&row.owned_id)
            .map_err(|error| error.to_string())?
            .map(|payload| {
                serde_json::from_str::<AgentConversationEvent>(&payload)
                    .map_err(|error| format!("Could not decode stored first user message: {error}"))
            })
            .transpose()?
            .and_then(|event| match event.payload {
                AgentConversationPayload::UserMessage { text, .. } => prompt_title(&text),
                _ => None,
            });
    }
    let persisted_state: AgentRuntimeState = enum_from_storage(&row.state)?;
    let state = if row.native_session_id.is_some() && persisted_state != AgentRuntimeState::Closed {
        AgentRuntimeState::Suspended
    } else {
        persisted_state
    };
    Ok((provider, stored, state))
}

fn connection_state_for_runtime_state(state: AgentRuntimeState) -> ConversationConnectionState {
    match state {
        AgentRuntimeState::Failed => ConversationConnectionState::Failed,
        _ => ConversationConnectionState::Disconnected,
    }
}

/// Older pages read into an import, and a session cleared to be read again, go one at a time: two downloads would read
/// the same page in twice, and a page read across a clearing would land in the journal that replaced it.
fn import_reading() -> std::sync::MutexGuard<'static, ()> {
    static READING: std::sync::Mutex<()> = std::sync::Mutex::new(());
    READING.lock().unwrap_or_else(std::sync::PoisonError::into_inner)
}

/// Rebuilds the disposable runtime overlay only when a mutating ACP operation
/// needs one. Rail listing and conversation reads project directly from SQLite.
fn recovered_session_from_row(
    store: &Arc<SessionStore>,
    row: SessionRow,
) -> Result<ManagedAgentSession, String> {
    let (provider, stored, state) = stored_session_projection(store, &row)?;
    let next_sequence = store
        .latest_seq(&row.owned_id)
        .map_err(|error| error.to_string())?
        .saturating_add(1);
    let connection = AgentConversationConnection {
        owned_id: row.owned_id.clone(),
        provider,
        generation: stored.generation,
        native_session_id: row.native_session_id.clone(),
        state: connection_state_for_runtime_state(state),
        config: stored.config.clone(),
    };
    let session = ManagedAgentSession {
        owned_id: row.owned_id.clone(),
        provider,
        provider_instance_id: format!("{}-{}", provider.id(), stored.generation),
        native_session_id: row.native_session_id,
        native_session_mode: stored.native_session_mode,
        generation: stored.generation,
        project_id: row.project_id,
        helper_of: stored.helper_of.clone(),
        guard_policy: None,
        owner: stored.owner,
        state,
        capabilities: stored.capabilities,
        next_sequence,
        active_turn_id: None,
        cancel_deadline: None,
        prompt_once_active: false,
        runtime: None,
        pool_key: None,
        transport: None,
        writer_lease: AgentWriterLease {
            owned_id: row.owned_id.clone(),
            generation: stored.generation,
            owner: AgentWriterLeaseOwner::Structured,
        },
        writer_lease_transition: None,
        permission_requests: HashMap::new(),
        user_input_requests: HashMap::new(),
        next_user_input_id: 0,
        ordered_events: None,
        cwd: row.cwd,
        spawn_reasoning_effort: row.effort,
        adapter_offers_effort: !stored.config.available_efforts.is_empty(),
        config: stored.config,
        connection,
        store: Arc::clone(store),
        created_at_ms: row.created_at_ms.max(0) as u128,
        last_activity_ms: row.last_activity_at_ms.max(0) as u128,
        live_tool_calls: HashMap::new(),
        streaming_reply: None,
        background_work: HashMap::new(),
        autonomous_turn_id: None,
        autonomous_turn_started: false,
        claude_reports_state: false,
        child_rollout_scan: None,
        child_rollout_parent_path: None,
        codex_children: HashMap::new(),
        claude_children: stored.claude_children,
        rail_meta: stored.rail_meta,
        title_source: row.title_source,
        suspending: false, turn_usage: Default::default(),
    };
    Ok(session)
}

fn enum_storage_value<T: Serialize>(value: T) -> Result<String, String> {
    let encoded = serde_json::to_value(value)
        .map_err(|error| format!("Could not encode stored enum value: {error}"))?;
    encoded
        .as_str()
        .map(str::to_string)
        .ok_or_else(|| "Stored enum value was not a string".to_string())
}

fn enum_from_storage<T: for<'de> Deserialize<'de>>(value: &str) -> Result<T, String> {
    serde_json::from_value(Value::String(value.to_string()))
        .map_err(|error| format!("Could not decode stored enum value: {error}"))
}

fn store_timestamp(value: u128) -> i64 {
    i64::try_from(value).unwrap_or(i64::MAX)
}

fn session_title_is_empty(title: Option<&str>) -> bool {
    title.is_none_or(|title| title.trim().is_empty())
}

fn prompt_title(prompt: &str) -> Option<String> {
    prompt
        .lines()
        .next()
        .map(str::trim)
        .filter(|line| !line.is_empty())
        .map(|line| line.chars().take(SESSION_TITLE_CHAR_CAP).collect())
}

/// Whether a name may still be replaced by one the helper writes. A name the
/// person typed is theirs to keep, and one the helper has already written is
/// the name of the session.
fn title_can_be_replaced(source: Option<&str>) -> bool {
    !matches!(source, Some(TITLE_SOURCE_USER) | Some(TITLE_SOURCE_HELPER))
}

/// Reads the helper's answer as a name. It is asked for bare words and usually
/// gives them, but a model that wraps a name in quotation marks anyway must not
/// put those on the rail. Only its first line is the name.
fn helper_title(answer: &str) -> Option<String> {
    let first_line = answer.lines().map(str::trim).find(|line| !line.is_empty())?;
    let trimmed = first_line
        .trim_matches(|character| matches!(character, '"' | '\'' | '\u{201c}' | '\u{201d}'))
        .trim();
    if trimmed.is_empty() {
        return None;
    }
    Some(trimmed.chars().take(SESSION_TITLE_CHAR_CAP).collect())
}

/// What the helper is given to name a session by: what the person first asked
/// for, and what came back in the turn that just finished. Every reply ends as
/// a whole message, whether it was streamed here or read in from elsewhere, so
/// the deltas it was streamed as are not read again.
fn title_input(store: &SessionStore, owned_id: &str, turn_id: &str) -> Option<String> {
    let prompt = store
        .first_user_message_payload(owned_id)
        .ok()
        .flatten()
        .and_then(|payload| serde_json::from_str::<AgentConversationEvent>(&payload).ok())
        .and_then(|event| match event.payload {
            AgentConversationPayload::UserMessage { text, .. } => Some(text),
            _ => None,
        })?;
    let mut reply = String::new();
    for row in store
        .list_recent_events(owned_id, SNAPSHOT_WINDOW_BYTES)
        .ok()?
    {
        if row.turn_id.as_deref() != Some(turn_id) || reply.chars().count() >= TITLE_INPUT_CHAR_CAP
        {
            continue;
        }
        let Ok(event) = serde_json::from_str::<AgentConversationEvent>(&row.payload_json) else {
            continue;
        };
        if let AgentConversationPayload::AssistantMessage { text, .. } = event.payload {
            reply.push_str(&text);
        }
    }
    let prompt: String = prompt.chars().take(TITLE_INPUT_CHAR_CAP).collect();
    let reply: String = reply.chars().take(TITLE_INPUT_CHAR_CAP).collect();
    Some(format!("{prompt}\n\n{reply}"))
}

fn current_session<'a>(
    sessions: &'a HashMap<String, ManagedAgentSession>,
    owned_id: &str,
    generation: u64,
) -> Result<&'a ManagedAgentSession, String> {
    let session = sessions
        .get(owned_id)
        .ok_or_else(|| "Conversation session was not found".to_string())?;
    if session.generation != generation {
        return Err("Conversation connection changed; retry on the current session".to_string());
    }
    Ok(session)
}

fn current_session_mut<'a>(
    sessions: &'a mut HashMap<String, ManagedAgentSession>,
    owned_id: &str,
    generation: u64,
) -> Result<&'a mut ManagedAgentSession, String> {
    let session = sessions
        .get_mut(owned_id)
        .ok_or_else(|| "Conversation session was not found".to_string())?;
    if session.generation != generation {
        return Err("Conversation connection changed; retry on the current session".to_string());
    }
    Ok(session)
}

fn lifecycle_session_mut<'a>(
    sessions: &'a mut HashMap<String, ManagedAgentSession>,
    owned_id: &str,
    generation: u64,
) -> Result<&'a mut ManagedAgentSession, String> {
    let session = current_session_mut(sessions, owned_id, generation)?;
    if session.state == AgentRuntimeState::Closed
        || session.connection.state == ConversationConnectionState::Closed
    {
        return Err("Closed conversation sessions cannot be changed".to_string());
    }
    Ok(session)
}

/// Records one payload while leaving live memory untouched if SQLite rejects it.
fn record_payload_for_session(
    session: &mut ManagedAgentSession,
    payload: AgentConversationPayload,
) -> Result<AgentConversationEvent, String> {
    let lifecycle = (matches!(payload, AgentConversationPayload::ChildUpdate { .. }
        | AgentConversationPayload::Tool { .. }) && session.active_turn_id.is_none()
        && !session.autonomous_turn_started
        && matches!(session.state, AgentRuntimeState::Ready | AgentRuntimeState::Working))
        .then(|| lifecycle_update_for_state(session, AgentRuntimeState::Ready, session.connection.state));
    record_payload_for_session_with_lifecycle(session, payload, lifecycle, None)
}

/// Claude needs a native turn and Codex a rollout to resume (TSK-1508 empty-resume probes).
/// Trust unreadable history; Antigravity precreates its trajectory (1.2.1 server.py:3011–3015).
fn provider_holds_session(provider: AgentConversationProvider, native_session_id: &str) -> bool {
    match provider {
        AgentConversationProvider::Claude => transcript::claude_transcript_holds_a_turn(native_session_id).unwrap_or(true),
        AgentConversationProvider::Codex => transcript::codex_transcript_holds_session(native_session_id).unwrap_or(true),
        _ => true,
    }
}

/// Commits an event and an optional lifecycle change before publishing either in memory.
fn record_payload_for_session_with_lifecycle(
    session: &mut ManagedAgentSession,
    mut payload: AgentConversationPayload,
    lifecycle: Option<SessionLifecycleUpdate>,
    event_turn_id: Option<String>,
) -> Result<AgentConversationEvent, String> {
    if let AgentConversationPayload::AssistantMessage { text, blocks, .. } = &mut payload {
        if blocks.is_none() && !std::env::args().any(|argument| argument == "--assembly-server") {
            *blocks = Some(super::safe_markdown::parse_safe_markdown(text));
        }
    }
    let mut candidate = SessionEventCandidate::from_session(session);
    if let Some(lifecycle) = lifecycle {
        candidate.transition_lifecycle(
            lifecycle.state,
            lifecycle.connection_state,
            lifecycle.owner,
            lifecycle.writer_owner,
        )?;
        if let Some(native_session_mode) = lifecycle.native_session_mode {
            candidate.native_session_mode = native_session_mode;
        }
    }
    let sequence = candidate.next_sequence;
    candidate.next_sequence = candidate.next_sequence.saturating_add(1);
    if let AgentConversationPayload::Connection {
        state,
        native_session_id,
    } = &payload
    {
        candidate.connection.state = *state;
        if native_session_id.is_some() {
            candidate.connection.native_session_id = native_session_id.clone();
            candidate.native_session_id = native_session_id.clone();
        }
    }
    if let AgentConversationPayload::AvailableCommandsUpdate { available_commands } = &payload {
        candidate.capabilities.commands = available_commands.clone();
    }
    let timestamp_ms = timestamp_millis();
    candidate.last_activity_ms = timestamp_ms;
    let mut canonical = canonical_event(
        session,
        candidate.native_session_id.clone(),
        sequence,
        timestamp_ms,
        &payload,
    )?;
    if let Some(turn_id) = event_turn_id {
        canonical.turn_id = Some(turn_id);
    }
    let frontend_event = AgentConversationEvent {
        runtime_state: Some(candidate.state),
        owned_id: session.owned_id.clone(),
        provider: session.provider,
        generation: session.generation,
        sequence,
        timestamp_ms,
        turn_id: canonical.turn_id.clone(),
        payload: payload.clone(),
    };
    match &payload {
        AgentConversationPayload::Tool { item_id, state, .. } => match state {
            ToolState::Started | ToolState::Updated => {
                candidate.live_tool_calls.entry(item_id.clone()).or_default();
            }
            ToolState::Completed | ToolState::Failed => {
                candidate.live_tool_calls.remove(item_id);
            }
        },
        AgentConversationPayload::Turn { state, .. }
            if matches!(
                state,
                super::protocol::TurnState::Completed
                    | super::protocol::TurnState::Interrupted
                    | super::protocol::TurnState::Failed
            ) =>
        {
            candidate.live_tool_calls.clear();
        }
        _ => {}
    }
    let payload_json = serde_json::to_string(&frontend_event)
        .map_err(|error| format!("Could not encode the stored conversation event: {error}"))?;
    let kind = enum_storage_value(canonical.event_type)?;
    let row = persisted_session_row_for_candidate(session, &candidate)?;
    let event_row = EventRow {
        owned_id: session.owned_id.clone(),
        seq: i64::try_from(sequence)
            .map_err(|_| "Conversation event sequence exceeded the store limit".to_string())?,
        turn_id: canonical.turn_id.clone(),
        kind,
        payload_json,
        created_at_ms: store_timestamp(timestamp_ms),
    };
    let result = if matches!(&payload, AgentConversationPayload::CheckoutChanged { .. }) {
        session
            .store
            .upsert_session_with_event_and_clear_workspace(&row, Some(&event_row))
    } else {
        session
            .store
            .upsert_session_with_event(&row, Some(&event_row))
    };
    result.map_err(|error| error.to_string())?;
    candidate.apply(session);
    if matches!(&payload, AgentConversationPayload::Tool { state: ToolState::Completed, .. }) {
        let stored = session.store.list_events(&session.owned_id, event_row.seq, 1)
            .map_err(|error| error.to_string())?
            .into_iter().next()
            .filter(|event| event.seq == event_row.seq)
            .ok_or_else(|| "Committed tool event is missing from the journal".to_string())?;
        return stored_event(stored);
    }
    Ok(frontend_event)
}

/// Reads one stored row back as the event it holds.
///
/// The row's `seq` column is the authority on where the event sits in the
/// journal; the copy inside the payload is only a copy, and it has been wrong.
/// Importing older history writes descending sequences, and a build that could
/// not represent a negative one wrote zero into every payload it touched. Those
/// rows are still in the database. Taking the position from the column repairs
/// them as they are read, and keeps the two from ever disagreeing again. The
/// `turn_id` column is taken the same way, so rows written before the event
/// carried it still say which turn they belong to.
pub(super) fn stored_event(row: EventRow) -> Result<AgentConversationEvent, String> {
    let mut event: AgentConversationEvent = serde_json::from_str(&row.payload_json)
        .map_err(|error| format!("Could not decode stored conversation event: {error}"))?;
    event.sequence = row.seq;
    event.turn_id = row.turn_id;
    Ok(event)
}

pub(super) fn selected_item_page(page: ItemPage) -> Result<AgentConversationItemPage, String> {
    Ok(AgentConversationItemPage {
        items: page
            .items
            .into_iter()
            .map(|item| AgentConversationItemDescriptor {
                stable_id: item.stable_id,
                item_id: item.item_id,
                selection_mode: item.record_mode,
                first_sequence: item.first_sequence,
                first_timestamp_ms: item.first_timestamp_ms,
                last_sequence: item.last_sequence,
                authority_seq: item.authority_sequence,
                turn_id: item.turn_id,
                completed: item.completed,
                prefix_complete: item.prefix_complete,
                position_known: item.position_known,
                required_bytes: item.required_bytes,
            })
            .collect(),
        events: page
            .events
            .into_iter()
            .map(|row| stored_event(row).map(tool_call::without_details))
            .collect::<Result<Vec<_>, _>>()?,
        turns: page
            .turns
            .into_iter()
            .map(|turn| AgentConversationTurnFacts {
                turn_id: turn.turn_id,
                started_at_ms: turn.started_at_ms,
                ended_at_ms: turn.ended_at_ms,
                terminal_state: turn.terminal_state,
                final_assistant_item_id: turn.final_assistant_item_id,
            })
            .collect(),
        before_cursor: page.before_cursor,
        after_cursor: page.after_cursor,
        has_before: page.has_before,
        has_earlier_transcript: false,
        has_after: page.has_after,
        watermark: page.watermark,
        transfer_bytes: page.transfer_bytes,
        oversized: page.oversized,
        coverage: page.coverage.map(|coverage| AgentConversationEventCoverage {
            low: coverage.low,
            high: coverage.high,
            start_complete: coverage.start_complete,
        }),
    })
}

fn import_selected_child(
    store: &SessionStore,
    emitter: &Arc<Mutex<Option<ConversationEmitter>>>,
    parent_owned_id: &str,
    child_session_id: &str,
    max_bytes: u64,
    max_records: usize,
    publish: bool,
    stop: Option<&AtomicBool>,
) -> Result<String, String> {
    let child_owned_id = super::history::ensure_child_import(
        store,
        parent_owned_id,
        None,
        child_session_id,
    )?;
    let mut published_after = store
        .latest_seq(&child_owned_id)
        .map_err(|error| error.to_string())?;
    loop {
        if stop.is_some_and(|stop| stop.load(Ordering::Acquire)) {
            return Ok(child_owned_id);
        }
        let progress = super::history::refresh_child_import(
            store,
            parent_owned_id,
            None,
            &child_owned_id,
            max_bytes,
            max_records,
        )?;
        if publish {
            loop {
                if stop.is_some_and(|stop| stop.load(Ordering::Acquire)) {
                    break;
                }
                let page = store
                    .list_events_after(
                        &child_owned_id,
                        published_after,
                        SNAPSHOT_WINDOW_BYTES,
                        i64::MAX,
                    )
                    .map_err(|error| error.to_string())?;
                let has_more = page.has_more;
                if page.events.is_empty() {
                    break;
                }
                for row in page.events {
                    let event = stored_event(row)?;
                    published_after = event.sequence;
                    dispatch_event(emitter, &event);
                }
                if !has_more {
                    break;
                }
            }
        }
        if !progress.has_more_source {
            break;
        }
    }
    Ok(child_owned_id)
}

/// Records durably, then emits while the session lock still preserves event order.
fn record_payload_for_session_and_dispatch(
    session: &mut ManagedAgentSession,
    emitter: &Arc<Mutex<Option<ConversationEmitter>>>,
    payload: AgentConversationPayload,
) -> Result<AgentConversationEvent, String> {
    let event = record_payload_for_session(session, payload)?;
    // The session mutex is the emit-order lock. Keep it held through the
    // callback so sequence allocation and frontend dispatch are one atomic
    // operation for this session.
    dispatch_event(emitter, &event);
    Ok(event)
}

/// Records a lifecycle event transactionally, then emits the committed event.
fn record_payload_for_session_and_dispatch_with_lifecycle(
    session: &mut ManagedAgentSession,
    emitter: &Arc<Mutex<Option<ConversationEmitter>>>,
    payload: AgentConversationPayload,
    lifecycle: SessionLifecycleUpdate,
) -> Result<AgentConversationEvent, String> {
    let event = record_payload_for_session_with_lifecycle(session, payload, Some(lifecycle), None)?;
    dispatch_event(emitter, &event);
    Ok(event)
}

/// Stores the reply the agent just finished streaming as one whole message.
/// Its deltas are already stored and drawn; this row carries all of its text,
/// with the Markdown converted once as it is recorded, so nothing that draws
/// the conversation later has to parse it.
fn record_finished_reply(
    session: &mut ManagedAgentSession,
    emitter: &Arc<Mutex<Option<ConversationEmitter>>>,
) {
    let Some((item_id, text, autonomous_turn_id)) = session
        .streaming_reply
        .take()
        .filter(|(_, text, _)| !text.is_empty())
    else {
        return;
    };
    match record_payload_for_session_with_lifecycle(
        session,
        AgentConversationPayload::AssistantMessage {
            item_id,
            text,
            completed: true,
            blocks: None,
        },
        None,
        autonomous_turn_id,
    ) {
        Ok(event) => dispatch_event(emitter, &event),
        Err(error) => {
            crate::debug_log::stderr_log!("Could not record the finished reply: {error}")
        }
    }
}

fn finish_autonomous_turn(
    session: &mut ManagedAgentSession,
    emitter: &Arc<Mutex<Option<ConversationEmitter>>>,
    state: super::protocol::TurnState,
    runtime_state: AgentRuntimeState,
) {
    record_finished_reply(session, emitter);
    let turn_id = match (session.autonomous_turn_started, session.autonomous_turn_id.clone()) {
        (true, Some(turn_id)) => turn_id,
        _ => {
            session.autonomous_turn_id = None;
            session.autonomous_turn_started = false;
            return;
        }
    };
    let lifecycle = lifecycle_update_for_state(session, runtime_state, session.connection.state);
    // Clear the turn only once its end is stored, so a failed write is retried at the next end.
    match record_payload_for_session_and_dispatch_with_lifecycle(
        session,
        emitter,
        AgentConversationPayload::Turn { turn_id, state },
        lifecycle,
    ) {
        Ok(_) => {
            session.autonomous_turn_id = None;
            session.autonomous_turn_started = false;
        }
        Err(error) => crate::debug_log::stderr_log!("Could not finish autonomous Claude turn: {error}"),
    }
}

/// Describes a state-only lifecycle change while preserving current ownership.
fn lifecycle_update_for_state(
    session: &ManagedAgentSession,
    state: AgentRuntimeState,
    connection_state: ConversationConnectionState,
) -> SessionLifecycleUpdate {
    SessionLifecycleUpdate {
        state: if state == AgentRuntimeState::Ready && !session.background_work.is_empty() {
            AgentRuntimeState::Working
        } else { state },
        connection_state,
        owner: session.owner,
        writer_owner: session.writer_lease.owner,
        native_session_mode: None,
    }
}

fn publish_background_work_state(session: &mut ManagedAgentSession,
    emitter: &Arc<Mutex<Option<ConversationEmitter>>>) -> Result<(), String> {
    let state = if session.active_turn_id.is_none() && !session.autonomous_turn_started
        && session.permission_requests.is_empty() && session.user_input_requests.is_empty()
        && matches!(session.state, AgentRuntimeState::Ready | AgentRuntimeState::Working
            | AgentRuntimeState::WaitingInput | AgentRuntimeState::WaitingApproval) {
        AgentRuntimeState::Ready
    } else { session.state };
    let lifecycle = lifecycle_update_for_state(session, state, session.connection.state);
    record_payload_for_session_and_dispatch_with_lifecycle(session, emitter,
        AgentConversationPayload::Connection { state: session.connection.state,
            native_session_id: session.native_session_id.clone() }, lifecycle)?;
    Ok(())
}

fn dispatch_event(
    emitter: &Arc<Mutex<Option<ConversationEmitter>>>,
    event: &AgentConversationEvent,
) {
    let callback = emitter
        .lock()
        .unwrap_or_else(std::sync::PoisonError::into_inner)
        .clone();
    if let Some(callback) = callback {
        if std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| callback(event.clone())))
            .is_err()
        {
            crate::debug_log::stderr_log!(
                "Agent conversation event emitter panicked; sequence {} remains available in the session snapshot",
                event.sequence
            );
        }
    }
}

pub(crate) fn prompt_params(native_session_id: String, input: AgentPrompt) -> Value {
    let mut prompt = Vec::new();
    if !input.text.is_empty() {
        prompt.push(serde_json::json!({ "type": "text", "text": input.text }));
    }
    prompt.extend(input.images.into_iter().map(|image| {
        serde_json::json!({
            "type": "image",
            "data": image.data,
            "mimeType": image.mime_type
        })
    }));
    serde_json::json!({ "sessionId": native_session_id, "prompt": prompt })
}

/// Writes the prompt before returning, while the caller still holds the
/// session lock, so a cancel that waits on that lock reaches the agent after
/// the prompt. Only the wait for the result runs on its own.
async fn spawn_prompt_completion(
    transport: Arc<AcpTransport>,
    ordered_events: UnboundedSender<OrderedSessionEvent>,
    turn_id: String,
    params: Value,
) {
    let response = transport.send_request("session/prompt", params).await;
    tokio::spawn(async move {
        let result = match response {
            Ok(response) => response.await,
            Err(error) => Err(error),
        };
        let _ = ordered_events.send(OrderedSessionEvent::PromptResult { turn_id, result });
    });
}

fn spawn_inbound_pump(
    manager: AgentRuntimeManager,
    transport: Weak<AcpTransport>,
    inbound: mpsc::UnboundedReceiver<AcpInbound>,
    ordered_events: mpsc::UnboundedReceiver<OrderedSessionEvent>,
    owned_id: String,
    generation: u64,
) {
    tokio::spawn(pump_inbound(
        manager,
        transport,
        inbound,
        ordered_events,
        owned_id,
        generation,
    ));
}

fn spawn_ordered_pump(
    manager: AgentRuntimeManager,
    transport: Weak<AcpTransport>,
    mut ordered_events: mpsc::UnboundedReceiver<OrderedSessionEvent>,
    owned_id: String,
    generation: u64,
) {
    tokio::spawn(async move {
        while let Some(ordered) = ordered_events.recv().await {
            if !handle_ordered_session_event(ordered, &manager, &transport, &owned_id, generation)
                .await
            {
                return;
            }
        }
    });
}

async fn pump_inbound(
    manager: AgentRuntimeManager,
    transport: Weak<AcpTransport>,
    mut inbound: mpsc::UnboundedReceiver<AcpInbound>,
    mut ordered_events: mpsc::UnboundedReceiver<OrderedSessionEvent>,
    owned_id: String,
    generation: u64,
) {
    let mut ordered_open = true;
    loop {
        let inbound = if ordered_open {
            tokio::select! {
                biased;
                inbound = inbound.recv() => inbound,
                ordered = ordered_events.recv() => {
                    match ordered {
                        Some(ordered) => {
                            if !handle_ordered_session_event(
                                ordered,
                                &manager,
                                &transport,
                                &owned_id,
                                generation,
                            ).await {
                                return;
                            }
                        }
                        None => ordered_open = false,
                    }
                    continue;
                }
            }
        } else {
            inbound.recv().await
        };

        let Some(inbound) = inbound else {
            return;
        };
        let Some(transport_runtime) = transport.upgrade() else {
            return;
        };
        if let AcpInbound::TransportClosed { reason } = &inbound {
            settle_closed_transport(&manager, &transport_runtime, reason).await;
            return;
        }
        // The agent withdrew a permission request: expire its card, since no answer can reach it.
        if let AcpInbound::RequestCancelled { wire_id } = &inbound {
            let mut sessions = manager
                .sessions
                .lock()
                .unwrap_or_else(std::sync::PoisonError::into_inner);
            for session in sessions.values_mut().filter(|session| {
                session
                    .transport
                    .as_ref()
                    .is_some_and(|current| Arc::ptr_eq(current, &transport_runtime))
            }) {
                let withdrawn = session
                    .permission_requests
                    .iter()
                    .find(|(_, pending)| &pending.wire_id == wire_id)
                    .map(|(request_id, _)| request_id.clone());
                let Some((request_id, pending)) =
                    withdrawn.and_then(|id| session.permission_requests.remove_entry(&id))
                else {
                    continue;
                };
                if let Some(ordered_events) = &session.ordered_events {
                    let _ = ordered_events.send(OrderedSessionEvent::ApprovalResolved {
                        request_id,
                        state: ApprovalState::Expired,
                        summary: pending.summary,
                    });
                }
            }
            continue;
        }
        let target = {
            let sessions = manager
                .sessions
                .lock()
                .unwrap_or_else(std::sync::PoisonError::into_inner);
            routed_session_for_inbound(&sessions, &transport_runtime, &inbound)
        };
        let Some((target_owned_id, target_generation)) = target else {
            crate::debug_log::stderr_log!(
                "Dropping an adapter event that did not identify one live session"
            );
            continue;
        };
        let owned_id = target_owned_id;
        let generation = target_generation;
        let sessions = Arc::clone(&manager.sessions);
        let emitter = Arc::clone(&manager.emitter);
        match inbound {
            AcpInbound::SessionUpdate(params) => {
                let mut runtime_error = raw_update_failed(&params);
                let reached_quiescence = 'update: {
                    let mut sessions = sessions
                        .lock()
                        .unwrap_or_else(std::sync::PoisonError::into_inner);
                    let Ok(session) = current_session_mut(&mut sessions, &owned_id, generation)
                    else {
                        return;
                    };
                    // Claude is completed locally by cancel_turn; cancelTurns can still emit updates (TSK-1508 cancel probes).
                    if session.suspending || (session.provider == AgentConversationProvider::Claude
                        && session.state == AgentRuntimeState::Interrupting) { break 'update false; }
                    let child_content = is_known_claude_child_inbound(session, &params);
                    if child_content
                        || matches!(
                            session_update_kind(&params),
                            Some("subagent_spawned" | "subagent_state_update")
                        )
                    {
                        runtime_error = false;
                    }
                    if let Some(payload) = claude_native_child_payload(session, &params) {
                        let reached_quiescence = session_is_quiescent(session);
                        if session.writer_lease.owner == AgentWriterLeaseOwner::Structured {
                            if let Err(error) =
                                record_payload_for_session_and_dispatch(session, &emitter, payload)
                            {
                                crate::debug_log::stderr_log!(
                                    "Could not record Claude child lifecycle: {error}"
                                );
                            }
                        } else if let Err(error) = persist_session(session) {
                            crate::debug_log::stderr_log!(
                                "Could not persist Claude child lifecycle: {error}"
                            );
                        }
                        break 'update reached_quiescence;
                    }
                    // Native child content belongs to the child's provider transcript.
                    // The parent journal carries only spawn/state metadata.
                    if child_content
                        && !session_update_kind(&params)
                            .is_some_and(|kind| kind.starts_with("async_task_"))
                    {
                        break 'update false;
                    }
                    if session.provider == AgentConversationProvider::Claude { // Claude ACP 0.93 extNotification publishes backgroundTaskCount (runtime byte 92464637).
                        if let Some(count) = params
                            .pointer("/update/_meta/jetbrains/air/backgroundTaskCount")
                            .and_then(Value::as_u64)
                        {
                            if count > 0 {
                                insert_background_work(&mut session.background_work,
                                    CLAUDE_BACKGROUND_TASKS.to_string(), BackgroundWorkKind::Command,
                                    String::new());
                            } else if session.background_work.remove(CLAUDE_BACKGROUND_TASKS).is_some() {
                                expect_claude_wake(session);
                            }
                            if let Err(error) = publish_background_work_state(session, &emitter) {
                                crate::debug_log::stderr_log!("Could not publish background work: {error}");
                            }
                            break 'update session_is_quiescent(session);
                        }
                    }
                    if let Some(state) = claude_session_state(session, &params) {
                        session.claude_reports_state = true;
                        if state == "idle" {
                            session.background_work.remove(CLAUDE_SESSION_RUNNING);
                            finish_autonomous_turn(
                                session,
                                &emitter,
                                super::protocol::TurnState::Completed,
                                AgentRuntimeState::Ready,
                            );
                            if let Err(error) = publish_background_work_state(session, &emitter) {
                                crate::debug_log::stderr_log!("Could not publish idle background work: {error}");
                            }
                        } else {
                            if state == "running"
                                && session.active_turn_id.is_none()
                                && !session.autonomous_turn_started
                                && session.writer_lease.owner == AgentWriterLeaseOwner::Structured
                            {
                                let turn_id = session
                                    .autonomous_turn_id
                                    .get_or_insert_with(|| format!("turn-{}", uuid::Uuid::new_v4()))
                                    .clone();
                                let lifecycle = lifecycle_update_for_state(
                                    session,
                                    AgentRuntimeState::Working,
                                    session.connection.state,
                                );
                                match record_payload_for_session_and_dispatch_with_lifecycle(
                                    session,
                                    &emitter,
                                    AgentConversationPayload::Turn {
                                        turn_id: turn_id.clone(),
                                        state: super::protocol::TurnState::Started,
                                    },
                                    lifecycle,
                                ) {
                                    Ok(_) => {
                                        session.autonomous_turn_id = Some(turn_id);
                                        session.autonomous_turn_started = true;
                                    }
                                    Err(error) => crate::debug_log::stderr_log!(
                                        "Could not start autonomous Claude turn: {error}"
                                    ),
                                }
                            }
                            insert_background_work(
                                &mut session.background_work,
                                CLAUDE_SESSION_RUNNING.to_string(),
                                BackgroundWorkKind::Command,
                                String::new(),
                            );
                        }
                        break 'update session_is_quiescent(session);
                    }
                    let prior_output = tools::joined_output_so_far(&session.live_tool_calls, &params);
                    let mut reached_quiescence = update_raw_liveness(session, &params); turn_usage::note_reported_cost(session, &params);
                    if let Some(payload) = stopped_background_task_payload(&mut session.background_work, &params) {
                        expect_claude_wake(session);
                        reached_quiescence |= session_is_quiescent(session);
                        if session.writer_lease.owner == AgentWriterLeaseOwner::Structured {
                            if let Err(error) = record_payload_for_session_and_dispatch(session, &emitter, payload) {
                                crate::debug_log::stderr_log!("Could not record stopped background task: {error}");
                            }
                        }
                    }
                    if let Err(error) = persist_session(session) {
                        crate::debug_log::stderr_log!(
                            "Could not persist adapter liveness state: {error}"
                        );
                    }
                    if let Some(mode_id) = current_mode_update(&params) {
                        session.config.approval_policy = Some(mode_id.to_string());
                        session.connection.config = session.config.clone();
                        if let Err(error) = persist_session(session) {
                            crate::debug_log::stderr_log!(
                                "Could not persist adapter configuration: {error}"
                            );
                        }
                        break 'update reached_quiescence;
                    }
                    if is_session_state_update(&params) {
                        if session.writer_lease.owner != AgentWriterLeaseOwner::Structured {
                            break 'update reached_quiescence;
                        }
                        let Some(payload) = payload_from_session_update_for_turn(
                            &params,
                            session.active_turn_id.as_deref(),
                        ) else {
                            break 'update reached_quiescence;
                        };
                        if let Err(error) =
                            record_payload_for_session_and_dispatch(session, &emitter, payload)
                        {
                            crate::debug_log::stderr_log!(
                                "Could not record ACP session state update: {error}"
                            );
                        }
                        break 'update reached_quiescence;
                    }
                    if let Some(payload) = background_task_payload(&params) {
                        if session.writer_lease.owner == AgentWriterLeaseOwner::Structured {
                            if let Err(error) = record_payload_for_session_and_dispatch(session, &emitter, payload) {
                                crate::debug_log::stderr_log!("Could not record background task update: {error}");
                            }
                        }
                        break 'update reached_quiescence || session_is_quiescent(session);
                    }
                    let replay = is_replay_session_update(&params);
                    if replay
                        && session.native_session_mode == AgentNativeSessionMode::Resume
                        && session
                            .store
                            .has_display_events(&session.owned_id)
                            .unwrap_or_else(|error| {
                                crate::debug_log::stderr_log!(
                                    "Could not inspect existing conversation history: {error}"
                                );
                                false
                            })
                    {
                        break 'update reached_quiescence;
                    }
                    // Claude ACP 0.93 sessionUpdate publishes outOfTurn (byte 92463882); 0.88 defines xq (byte 82537574).
                    // Journal this between-prompt output under the autonomous turn; audited Codex/Antigravity have no such field.
                    let out_of_turn = session.provider == AgentConversationProvider::Claude
                        && params.pointer("/update/_meta/jetbrains/air/outOfTurn")
                            == Some(&Value::Bool(true));
                    if session.active_turn_id.is_none() && !replay && !out_of_turn {
                        crate::debug_log::stderr_log!(
                            "[debug] Dropping ACP session update without an active conversation turn"
                        );
                        break 'update reached_quiescence;
                    }
                    if session.writer_lease.owner != AgentWriterLeaseOwner::Structured {
                        break 'update reached_quiescence;
                    }
                    let autonomous_turn_id = out_of_turn.then(|| {
                        session
                            .autonomous_turn_id
                            .get_or_insert_with(|| format!("turn-{}", uuid::Uuid::new_v4()))
                            .clone()
                    });
                    let turn_id = autonomous_turn_id.as_deref().or(session.active_turn_id.as_deref());
                    let Some(mut payload) = payload_from_session_update_for_turn(&params, turn_id) else {
                        break 'update reached_quiescence;
                    };
                    tools::join_terminal_output(&mut session.live_tool_calls, prior_output, &params, &mut payload);
                    // A delta for another message means the agent finished the
                    // one it was streaming.
                    if let AgentConversationPayload::AssistantDelta { item_id, .. } = &payload {
                        if session
                            .streaming_reply
                            .as_ref()
                            .is_some_and(|(streaming, _, _)| streaming != item_id)
                        {
                            record_finished_reply(session, &emitter);
                        }
                    }
                    match record_payload_for_session_with_lifecycle(
                        session,
                        payload,
                        None,
                        autonomous_turn_id.clone(),
                    ) {
                        Ok(event) => {
                            dispatch_event(&emitter, &event);
                            if let AgentConversationPayload::AssistantDelta { item_id, delta } =
                                event.payload
                            {
                                session
                                    .streaming_reply
                                    .get_or_insert_with(|| {
                                        (item_id, String::new(), autonomous_turn_id)
                                    })
                                    .1
                                    .push_str(&delta);
                            }
                        }
                        Err(error) => crate::debug_log::stderr_log!(
                            "Could not record ACP session update: {error}"
                        ),
                    }
                    reached_quiescence
                };
                if let Err(error) = manager.sync_broker_status(&owned_id, generation, runtime_error)
                {
                    crate::debug_log::stderr_log!("Could not publish broker status: {error}");
                }
                if reached_quiescence {
                    if let Err(error) = manager
                        .drain_broker_message_at(&owned_id, generation, store_timestamp(timestamp_millis()))
                        .await
                    {
                        crate::debug_log::stderr_log!("Could not drain broker message: {error}");
                    }
                    if let Err(error) = manager.suspend_if_quiescent(&owned_id, generation).await {
                        crate::debug_log::stderr_log!(
                            "Could not tear down quiescent runtime: {error}"
                        );
                    }
                }
            }
            AcpInbound::AgentRequest {
                wire_id,
                method,
                params,
            } => {
                if method != "session/request_permission"
                    && method != "session/request_user_input"
                    && method != "session/request_input"
                    && method != "elicitation/create"
                {
                    crate::debug_log::stderr_log!(
                        "Ignoring unsupported ACP agent request: {method}"
                    );
                    continue;
                }
                if method == "session/request_user_input"
                    || method == "session/request_input"
                    || method == "elicitation/create"
                {
                    let elicitation = method == "elicitation/create";
                    let parsed = if elicitation {
                        parse_elicitation_form(&params)
                    } else {
                        Ok((
                            params
                                .get("title")
                                .and_then(Value::as_str)
                                .filter(|value| !value.trim().is_empty())
                                .unwrap_or("Input requested")
                                .to_string(),
                            params
                                .get("description")
                                .and_then(Value::as_str)
                                .map(str::to_string),
                            params
                                .get("fields")
                                .cloned()
                                .and_then(|value| serde_json::from_value(value).ok())
                                .unwrap_or_default(),
                        ))
                    };
                    let (title, description, fields) = match parsed {
                        Ok(parsed) => parsed,
                        Err(error) => {
                            crate::debug_log::stderr_log!(
                                "Declining unsupported ACP elicitation: {error}"
                            );
                            if let Err(error) = transport_runtime
                                .respond(wire_id, serde_json::json!({ "action": "decline" }))
                                .await
                            {
                                crate::debug_log::stderr_log!(
                                    "Could not decline unsupported ACP elicitation: {error}"
                                );
                            }
                            continue;
                        }
                    };
                    let mut sessions = sessions
                        .lock()
                        .unwrap_or_else(std::sync::PoisonError::into_inner);
                    let Ok(session) = current_session_mut(&mut sessions, &owned_id, generation)
                    else {
                        return;
                    };
                    if session.writer_lease.owner != AgentWriterLeaseOwner::Structured {
                        continue;
                    }
                    let child_scoped = is_known_claude_child_inbound(session, &params);
                    session.next_user_input_id = session.next_user_input_id.saturating_add(1);
                    let request_id = format!("input-{}", session.next_user_input_id);
                    let lifecycle = lifecycle_update_for_state(
                        session,
                        AgentRuntimeState::WaitingInput,
                        session.connection.state,
                    );
                    match record_payload_for_session_and_dispatch_with_lifecycle(
                        session,
                        &emitter,
                        AgentConversationPayload::UserInputRequested {
                            request_id: request_id.clone(),
                            title,
                            description,
                            fields,
                            can_decline: elicitation,
                        },
                        lifecycle,
                    ) {
                        Ok(event) => {
                            session.user_input_requests.insert(
                                request_id,
                                PendingUserInput {
                                    wire_id,
                                    response_shape: if elicitation {
                                        UserInputResponseShape::Elicitation
                                    } else {
                                        UserInputResponseShape::Legacy
                                    },
                                    child_scoped,
                                    event,
                                },
                            );
                        }
                        Err(error) => crate::debug_log::stderr_log!(
                            "Could not record ACP user input request: {error}"
                        ),
                    }
                    continue;
                }
                let summary = permission_summary(&params);
                let options = permission_options(&params);
                let command = params.pointer("/toolCall/rawInput/command").and_then(|command| command.as_str().map(str::to_owned)
                    .or_else(|| command.as_array().map(|parts| parts.iter().filter_map(Value::as_str).collect::<Vec<_>>().join(" "))));
                // The descriptor's permission-handler pre-approval answers the agent's Assembly tools here (TSK-1522).
                let answer = sessions.lock().unwrap_or_else(std::sync::PoisonError::into_inner).get(&owned_id).filter(|session| session.generation == generation
                    && session.writer_lease.owner == AgentWriterLeaseOwner::Structured).and_then(|session| super::providers::descriptors::handler_answer(session.provider, &params));
                if let Some(answer) = answer {
                    if let Err(error) = transport_runtime.respond(wire_id, answer).await { crate::debug_log::stderr_log!("Could not pre-approve an Assembly tool: {error}"); }
                    continue;
                }
                // A workflow step's policy answers what it names; `ask`, or an allow with no allow-once option, reaches a person.
                let decided = sessions.lock().unwrap_or_else(std::sync::PoisonError::into_inner).get(&owned_id)
                    .and_then(|session| Some(session.guard_policy.as_ref()?.decide(command.as_deref()?)))
                    .and_then(|decision| Some((decision, policy_answer(decision, &options)?)));
                if let Some((decision, response)) = decided {
                    let allowed = decision == crate::workflow::policy::Decision::Allow;
                    if let Err(error) = transport_runtime.respond(wire_id, response).await {
                        crate::debug_log::stderr_log!("Could not answer a permission request from the policy: {error}");
                    }
                    if let Ok(session) = current_session_mut(&mut sessions.lock().unwrap_or_else(std::sync::PoisonError::into_inner), &owned_id, generation) {
                        let (state, summary) = if allowed { (ApprovalState::Accepted, summary) } else { (ApprovalState::Declined, format!("Blocked by the workflow policy: {summary}")) };
                        let _ = record_payload_for_session_and_dispatch(session, &emitter, AgentConversationPayload::Approval { request_id: format!("perm-{}", uuid::Uuid::new_v4()), state, summary, options: Vec::new() });
                    }
                    continue;
                }
                let mut sessions = sessions
                    .lock()
                    .unwrap_or_else(std::sync::PoisonError::into_inner);
                let Ok(session) = current_session_mut(&mut sessions, &owned_id, generation) else {
                    return;
                };
                if session.writer_lease.owner != AgentWriterLeaseOwner::Structured {
                    continue;
                }
                let request_id = format!("perm-{}", uuid::Uuid::new_v4());
                let child_scoped = is_known_claude_child_inbound(session, &params);
                let lifecycle = lifecycle_update_for_state(
                    session,
                    AgentRuntimeState::WaitingApproval,
                    session.connection.state,
                );
                match record_payload_for_session_and_dispatch_with_lifecycle(
                    session,
                    &emitter,
                    AgentConversationPayload::Approval {
                        request_id: request_id.clone(),
                        state: ApprovalState::Requested,
                        summary: summary.clone(), options: options.iter().map(|option| (option.option_id.clone(), option.name.clone(), option.kind.clone())).collect(),
                    },
                    lifecycle,
                ) {
                    Ok(event) => {
                        session.permission_requests.insert(
                            request_id,
                            PendingPermission {
                                wire_id,
                                options,
                                summary,
                                command,
                                child_scoped,
                                event,
                            },
                        );
                    }
                    Err(error) => crate::debug_log::stderr_log!(
                        "Could not record ACP permission request: {error}"
                    ),
                }
            }
            AcpInbound::RequestCancelled { .. } | AcpInbound::TransportClosed { .. } => {
                unreachable!("handled before session routing")
            }
        }
    }
}

async fn settle_closed_transport(
    manager: &AgentRuntimeManager,
    transport: &Arc<AcpTransport>,
    reason: &str,
) {
    let emitter = Arc::clone(&manager.emitter);
    let mut failed_turns = Vec::new();
    {
        let mut sessions = manager
            .sessions
            .lock()
            .unwrap_or_else(std::sync::PoisonError::into_inner);
        for session in sessions.values_mut().filter(|session| {
            session
                .transport
                .as_ref()
                .is_some_and(|current| Arc::ptr_eq(current, transport))
        }) {
            if let Some(task) = session.child_rollout_scan.take() {
                task.abort();
            }
            abort_cancel_deadline(session);
            session.runtime = None;
            session.transport = None;
            session.ordered_events = None;
            session.pool_key = None;
            if session.suspending {
                continue;
            }
            for (request_id, pending) in session.permission_requests.drain().collect::<Vec<_>>() {
                let _ = record_payload_for_session_and_dispatch(
                    session,
                    &emitter,
                    AgentConversationPayload::Approval {
                        request_id,
                        state: ApprovalState::Expired,
                        summary: pending.summary, options: Vec::new(),
                    },
                );
            }
            for (request_id, _) in session.user_input_requests.drain().collect::<Vec<_>>() {
                let _ = record_payload_for_session_and_dispatch(
                    session,
                    &emitter,
                    AgentConversationPayload::UserInputResolved {
                        request_id,
                        cancelled: true,
                    },
                );
            }
            disconnect_claude_children(session, &emitter);
            for (item_id, _) in session.background_work.drain().collect::<Vec<_>>() {
                if item_id.starts_with("background-task:") {
                    let _ = record_payload_for_session_and_dispatch(
                        session,
                        &emitter,
                        AgentConversationPayload::Tool {
                            item_id,
                            name: String::new(),
                            state: ToolState::Failed,
                            summary: Some(
                                "Background command stopped when the adapter disconnected".into(),
                            ),
                            output: None,
                            path: None,
                            diff: None, has_detail: false, added: None, removed: None,
                        },
                    );
                }
            }
            finish_autonomous_turn(
                session,
                &emitter,
                super::protocol::TurnState::Failed,
                AgentRuntimeState::Failed,
            );
            session.claude_reports_state = false;
            if let Some(turn_id) = session.active_turn_id.take() {
                failed_turns.push((session.owned_id.clone(), turn_id.clone()));
                let _ = record_payload_for_session_and_dispatch(
                    session,
                    &emitter,
                    AgentConversationPayload::Turn {
                        turn_id,
                        state: super::protocol::TurnState::Failed,
                    },
                );
            }
            session.prompt_once_active = false;
            let lifecycle = lifecycle_update_for_state(
                session,
                AgentRuntimeState::Failed,
                ConversationConnectionState::Failed,
            );
            let _ = record_payload_for_session_and_dispatch_with_lifecycle(
                session,
                &emitter,
                AgentConversationPayload::Connection {
                    state: ConversationConnectionState::Failed,
                    native_session_id: session.native_session_id.clone(),
                },
                lifecycle,
            );
            let _ = record_payload_for_session_and_dispatch(
                session,
                &emitter,
                AgentConversationPayload::Error {
                    code: "acp-transport".to_string(),
                    message: reason.to_string(),
                    recoverable: true,
                },
            );
        }
    }
    for (owned_id, turn_id) in failed_turns {
        if let Err(error) = manager.report_helper_turn(&owned_id, &turn_id, "failed") {
            crate::debug_log::stderr_log!("Could not report the helper's turn: {error}");
        }
    }
    manager
        .adapter_pools
        .lock()
        .await
        .retain(|_, pool| !Arc::ptr_eq(&pool.transport, transport));
}

fn routed_session_for_inbound(
    sessions: &HashMap<String, ManagedAgentSession>,
    transport: &Arc<AcpTransport>,
    inbound: &AcpInbound,
) -> Option<(String, u64)> {
    let params = match inbound {
        AcpInbound::SessionUpdate(params) | AcpInbound::AgentRequest { params, .. } => Some(params),
        AcpInbound::RequestCancelled { .. } | AcpInbound::TransportClosed { .. } => None,
    };
    let native_session_id = params.and_then(inbound_native_session_id);
    let matching_transport = |session: &&ManagedAgentSession| {
        session
            .transport
            .as_ref()
            .is_some_and(|current| Arc::ptr_eq(current, transport))
    };
    if let Some(native_session_id) = native_session_id {
        return sessions
            .values()
            .filter(matching_transport)
            .find(|session| {
                session.native_session_id.as_deref() == Some(native_session_id)
                    || session.claude_children.contains_key(native_session_id)
                    || is_claude_replay_session_id(session, native_session_id)
            })
            .map(|session| (session.owned_id.clone(), session.generation));
    }
    let matching = sessions
        .values()
        .filter(matching_transport)
        .collect::<Vec<_>>();
    if let [session] = matching.as_slice() {
        return Some((session.owned_id.clone(), session.generation));
    }
    let mut active = matching
        .into_iter()
        .filter(|session| session.active_turn_id.is_some());
    let session = active.next()?;
    active
        .next()
        .is_none()
        .then(|| (session.owned_id.clone(), session.generation))
}

fn inbound_native_session_id(params: &Value) -> Option<&str> {
    params
        .get("sessionId")
        .or_else(|| params.get("session_id"))
        .or_else(|| params.pointer("/update/sessionId"))
        .or_else(|| params.pointer("/update/session_id"))
        .and_then(Value::as_str)
}

fn is_known_claude_child_inbound(session: &ManagedAgentSession, params: &Value) -> bool {
    inbound_native_session_id(params)
        .is_some_and(|session_id| {
            session.claude_children.contains_key(session_id)
                || is_claude_replay_session_id(session, session_id)
        })
}

fn parse_elicitation_form(
    params: &Value,
) -> Result<(String, Option<String>, Vec<AgentUserInputField>), String> {
    if params.get("mode").and_then(Value::as_str) != Some("form") {
        return Err("only form elicitation is supported".to_string());
    }
    let title = params
        .get("message")
        .and_then(Value::as_str)
        .filter(|message| !message.trim().is_empty())
        .ok_or_else(|| "elicitation message is missing".to_string())?
        .to_string();
    let schema = params
        .get("requestedSchema")
        .and_then(Value::as_object)
        .ok_or_else(|| "elicitation schema is missing".to_string())?;
    if schema
        .keys()
        .any(|key| !matches!(key.as_str(), "type" | "properties" | "required"))
        || schema.get("type").and_then(Value::as_str) != Some("object")
    {
        return Err("elicitation object schema contains unsupported constraints".to_string());
    }
    let properties = schema
        .get("properties")
        .and_then(Value::as_object)
        .filter(|properties| !properties.is_empty())
        .ok_or_else(|| "elicitation form has no fields".to_string())?;
    let required = match schema.get("required") {
        None => HashSet::new(),
        Some(Value::Array(values)) => values
            .iter()
            .map(|value| {
                value
                    .as_str()
                    .map(str::to_string)
                    .ok_or_else(|| "elicitation required entries must be field ids".to_string())
            })
            .collect::<Result<HashSet<_>, _>>()?,
        Some(_) => return Err("elicitation required must be an array".to_string()),
    };
    if required.iter().any(|field| !properties.contains_key(field)) {
        return Err("elicitation required names an unknown field".to_string());
    }
    let mut fields = Vec::with_capacity(properties.len());
    for (id, value) in properties {
        let property = value
            .as_object()
            .ok_or_else(|| format!("elicitation field {id} is not an object"))?;
        let field_type = property
            .get("type")
            .and_then(Value::as_str)
            .ok_or_else(|| format!("elicitation field {id} has no type"))?;
        let label = match property.get("title") {
            Some(Value::String(label)) if !label.trim().is_empty() => label.clone(),
            None => id.clone(),
            _ => return Err(format!("elicitation field {id} has an invalid title")),
        };
        let description = match property.get("description") {
            Some(Value::String(description)) => Some(description.clone()),
            None => None,
            _ => return Err(format!("elicitation field {id} has an invalid description")),
        };
        let (kind, choices, allowed): (_, _, &[&str]) = match field_type {
            "string" if property.contains_key("oneOf") => (
                AgentUserInputKind::Select,
                Some(parse_elicitation_choices(
                    property.get("oneOf").unwrap(),
                    id,
                )?),
                &["type", "title", "description", "oneOf", "_meta"],
            ),
            "string" => (
                AgentUserInputKind::Text,
                None,
                &["type", "title", "description", "_meta"],
            ),
            "array" => {
                let items = property
                    .get("items")
                    .and_then(Value::as_object)
                    .ok_or_else(|| format!("elicitation field {id} has invalid array items"))?;
                if items
                    .keys()
                    .any(|key| !matches!(key.as_str(), "anyOf" | "_meta"))
                {
                    return Err(format!(
                        "elicitation field {id} has unsupported item constraints"
                    ));
                }
                (
                    AgentUserInputKind::MultiSelect,
                    Some(parse_elicitation_choices(
                        items
                            .get("anyOf")
                            .ok_or_else(|| format!("elicitation field {id} has no choices"))?,
                        id,
                    )?),
                    &["type", "title", "description", "items", "_meta"],
                )
            }
            _ => return Err(format!("elicitation field {id} has an unsupported type")),
        };
        if property
            .keys()
            .any(|key| !allowed.contains(&key.as_str()))
        {
            return Err(format!(
                "elicitation field {id} contains unsupported constraints"
            ));
        }
        fields.push(AgentUserInputField {
            id: id.clone(),
            label,
            description,
            required: required.contains(id),
            kind,
            choices,
        });
    }
    Ok((title, None, fields))
}

fn parse_elicitation_choices(
    value: &Value,
    field_id: &str,
) -> Result<Vec<AgentConfigOptionChoice>, String> {
    let values = value
        .as_array()
        .filter(|values| !values.is_empty())
        .ok_or_else(|| format!("elicitation field {field_id} has no choices"))?;
    values
        .iter()
        .map(|value| {
            let choice = value
                .as_object()
                .ok_or_else(|| format!("elicitation field {field_id} has an invalid choice"))?;
            if choice.keys().any(|key| {
                !matches!(key.as_str(), "const" | "title" | "description" | "_meta")
            }) {
                return Err(format!(
                    "elicitation field {field_id} has unsupported choice constraints"
                ));
            }
            let value = choice
                .get("const")
                .cloned()
                .ok_or_else(|| format!("elicitation field {field_id} choice has no value"))?;
            let label = match choice.get("title") {
                Some(Value::String(label)) if !label.trim().is_empty() => label.clone(),
                None => match &value {
                    Value::String(value) => value.clone(),
                    _ => value.to_string(),
                },
                _ => {
                    return Err(format!(
                        "elicitation field {field_id} choice has an invalid title"
                    ))
                }
            };
            let description = match choice.get("description") {
                Some(Value::String(description)) => Some(description.clone()),
                None => None,
                _ => {
                    return Err(format!(
                        "elicitation field {field_id} choice has an invalid description"
                    ))
                }
            };
            Ok(AgentConfigOptionChoice {
                value,
                label,
                description,
            })
        })
        .collect()
}
// Claude ACP 0.88/0.93 replaySessionHistory synthesizes these IDs (runtime bytes 82758328/92413277); they are not native child IDs.
fn is_claude_replay_session_id(session: &ManagedAgentSession, session_id: &str) -> bool {
    session.provider == AgentConversationProvider::Claude
        && session.native_session_id.as_deref().is_some_and(|root_id| {
            session_id.starts_with(&format!("{root_id}:replay-subagent:"))
        })
}

fn claude_native_child_payload(
    session: &mut ManagedAgentSession,
    params: &Value,
) -> Option<AgentConversationPayload> {
    if session.provider != AgentConversationProvider::Claude {
        return None;
    }
    let update = params.get("update").unwrap_or(params);
    let kind = session_update_kind(params)?;
    if !matches!(kind, "subagent_spawned" | "subagent_state_update") {
        return None;
    }
    let child_id = update
        .get("subagentSessionId")
        .or_else(|| update.get("subagent_session_id"))
        .and_then(Value::as_str)?
        .to_string();
    if is_claude_replay_session_id(session, &child_id) {
        return None;
    }
    let parent_id = inbound_native_session_id(params)?.to_string();
    let existing = session.claude_children.get(&child_id);
    let transcript_id = existing
        .map(|child| child.transcript_id.clone())
        .unwrap_or_else(|| durable_claude_agent_id(&child_id).to_string());
    let label = update
        .get("name")
        .and_then(Value::as_str)
        .map(str::trim)
        .filter(|value| !value.is_empty())
        .map(str::to_string)
        .or_else(|| existing.and_then(|child| child.label.clone()));
    let state = if kind == "subagent_spawned" {
        "running".to_string()
    } else {
        match update
            .get("state")
            .and_then(Value::as_str)
            .unwrap_or("running")
        {
            "completed" => "finished".to_string(),
            other => other.to_string(),
        }
    };
    let terminal = matches!(
        state.as_str(),
        "finished" | "failed" | "cancelled" | "disconnected"
    );
    let work_id = format!("claude-child:{child_id}");
    if terminal {
        if session.background_work.remove(&work_id).is_some() {
            expect_claude_wake(session);
        }
    } else {
        insert_background_work(
            &mut session.background_work,
            work_id,
            BackgroundWorkKind::Subagent,
            label.clone().unwrap_or_default(),
        );
    }
    session.claude_children.insert(
        child_id.clone(),
        ClaudeChildSession {
            parent_id: parent_id.clone(),
            transcript_id: transcript_id.clone(),
            label: label.clone(),
            state: state.clone(),
        },
    );
    Some(AgentConversationPayload::ChildUpdate {
        child_id,
        parent_tool_call_id: parent_id.clone(),
        parent_id: Some(parent_id),
        transcript_id: Some(transcript_id),
        label,
        state,
        latest_activity: update
            .get("task")
            .and_then(Value::as_str)
            .map(|value| value.chars().take(160).collect()),
    })
}

fn durable_claude_agent_id(session_id: &str) -> &str {
    session_id
        .rsplit_once(":generation:")
        .filter(|(_, generation)| {
            !generation.is_empty() && generation.chars().all(|ch| ch.is_ascii_digit())
        })
        .map(|(agent_id, _)| agent_id)
        .unwrap_or(session_id)
}

fn disconnect_claude_children(
    session: &mut ManagedAgentSession,
    emitter: &Arc<Mutex<Option<ConversationEmitter>>>,
) {
    let active = session
        .claude_children
        .iter()
        .filter(|(_, child)| {
            !matches!(
                child.state.as_str(),
                "finished" | "failed" | "cancelled" | "disconnected"
            )
        })
        .map(|(child_id, child)| (child_id.clone(), child.clone()))
        .collect::<Vec<_>>();
    for (child_id, child) in active {
        if let Some(stored) = session.claude_children.get_mut(&child_id) {
            stored.state = "disconnected".to_string();
        }
        session.background_work.remove(&format!("claude-child:{child_id}"));
        let _ = record_payload_for_session_and_dispatch(
            session,
            emitter,
            AgentConversationPayload::ChildUpdate {
                child_id,
                parent_tool_call_id: child.parent_id.clone(),
                parent_id: Some(child.parent_id),
                transcript_id: Some(child.transcript_id),
                label: child.label,
                state: "disconnected".to_string(),
                latest_activity: None,
            },
        );
    }
}

fn update_raw_liveness(session: &mut ManagedAgentSession, params: &Value) -> bool {
    let update = params.get("update").unwrap_or(params);
    let kind = update
        .get("sessionUpdate")
        .or_else(|| update.get("session_update"))
        .or_else(|| update.get("type"))
        .and_then(Value::as_str)
        .unwrap_or_default()
        .to_ascii_lowercase();
    let state = update
        .get("status")
        .or_else(|| update.get("state"))
        .and_then(Value::as_str)
        .unwrap_or_default()
        .to_ascii_lowercase();
    let terminal = matches!(
        state.as_str(),
        "completed" | "failed" | "cancelled" | "canceled" | "stopped" | "done"
    );
    let identifier = update
        .get("asyncTaskId")
        .or_else(|| update.get("async_task_id"))
        .or_else(|| update.get("toolCallId"))
        .or_else(|| update.get("tool_call_id"))
        .or_else(|| update.get("taskId"))
        .or_else(|| update.get("task_id"))
        .or_else(|| update.get("childSessionId"))
        .or_else(|| update.get("child_session_id"))
        .or_else(|| update.get("id"))
        .and_then(Value::as_str)
        .map(str::to_string);
    let Some(identifier) = identifier else {
        return false;
    };
    let identifier = if kind.starts_with("async_task_") {
        format!("background-task:{identifier}")
    } else {
        identifier
    };
    if kind.contains("tool") {
        if terminal {
            session.live_tool_calls.remove(&identifier);
        } else {
            session.live_tool_calls.entry(identifier).or_default();
        }
        return session_is_quiescent(session);
    }
    if !(kind.contains("task")
        || kind.contains("child")
        || kind.contains("subagent")
        || kind.contains("background"))
    {
        return false;
    }
    if !terminal {
        let label = update
            .get("name")
            .and_then(Value::as_str)
            .unwrap_or_default()
            .to_string();
        insert_background_work(
            &mut session.background_work,
            identifier,
            BackgroundWorkKind::Command,
            label,
        );
        return false;
    }
    if session.background_work.remove(&identifier).is_some() {
        expect_claude_wake(session);
    }
    session_is_quiescent(session)
}

fn stopped_background_task_payload(
    background_work: &mut HashMap<String, BackgroundWorkEntry>,
    params: &Value,
) -> Option<AgentConversationPayload> {
    let update = params.get("update").unwrap_or(params);
    if session_update_kind(params) != Some("tool_call_update")
        || update.get("status").and_then(Value::as_str) != Some("completed")
        || update.pointer("/_meta/claudeCode/toolName").and_then(Value::as_str) != Some("TaskStop")
    {
        return None;
    }
    let result = update.get("rawOutput").and_then(text_from_value)?;
    let result: Value = serde_json::from_str(&result).ok()?;
    let task_id = result.get("task_id")?.as_str()?;
    let success = format!("Successfully stopped task: {task_id}");
    if !result.get("message")?.as_str()?.starts_with(&success) {
        return None;
    }
    let item_id = format!("background-task:{task_id}");
    if background_work.remove(&item_id).is_none() {
        return None;
    }
    Some(AgentConversationPayload::Tool {
        item_id,
        name: "Background command".into(),
        state: ToolState::Failed,
        summary: Some("Stopped".into()),
        output: None,
        path: None,
        diff: None, has_detail: false, added: None, removed: None,
    })
}

fn background_task_payload(params: &Value) -> Option<AgentConversationPayload> {
    let update = params.get("update").unwrap_or(params);
    let kind = session_update_kind(params)?;
    let exit_code = if update.get("state").and_then(Value::as_str) == Some("stopped") {
        update.get("outputFilePath").and_then(Value::as_str).and_then(background_command_exit_code)
    } else {
        None
    };
    let state = match kind {
        "async_task_spawned" => ToolState::Started,
        "async_task_progress" => ToolState::Updated,
        "async_task_state_update" => match update.get("state")?.as_str()? {
            "completed" => ToolState::Completed,
            "failed" | "cancelled" | "canceled" => ToolState::Failed,
            "stopped" if exit_code == Some(0) => ToolState::Completed,
            "stopped" => ToolState::Failed,
            "running" | "paused" => ToolState::Updated,
            _ => return None,
        },
        _ => return None,
    };
    let task_id = update.get("asyncTaskId")?.as_str()?;
    let summary = update.get("summary")
        .or_else(|| update.get("description"))
        .and_then(Value::as_str)
        .map(str::to_string)
        .or_else(|| match update.get("state").and_then(Value::as_str) {
            Some("completed") => Some("Completed".into()),
            Some("failed") => Some("Failed".into()),
            Some("stopped") => Some(match exit_code {
                Some(0) => "Completed".into(),
                Some(code) => format!("Exited with code {code}"),
                None => "Stopped".into(),
            }),
            Some("cancelled" | "canceled") => Some("Cancelled".into()),
            _ => None,
        });
    Some(AgentConversationPayload::Tool {
        item_id: format!("background-task:{task_id}"),
        name: update.get("name").and_then(Value::as_str)
            .unwrap_or(if kind == "async_task_spawned" { "Background command" } else { "" })
            .to_string(),
        state,
        summary,
        output: None,
        path: None,
        diff: None, has_detail: false, added: None, removed: None,
    })
}

fn background_command_exit_code(path: &str) -> Option<i32> {
    let mut file = std::fs::File::open(path).ok()?;
    let start = file.metadata().ok()?.len().saturating_sub(128);
    file.seek(SeekFrom::Start(start)).ok()?;
    let mut tail = Vec::new();
    file.read_to_end(&mut tail).ok()?;
    String::from_utf8_lossy(&tail).lines().last()?.trim().strip_prefix("[exited with code ")?
        .strip_suffix(']')?.parse().ok()
}

fn raw_update_failed(params: &Value) -> bool {
    if session_update_kind(params).is_some_and(|kind| kind.starts_with("async_task_")) {
        return false;
    }
    let update = params.get("update").unwrap_or(params);
    update
        .get("status")
        .or_else(|| update.get("state"))
        .and_then(Value::as_str)
        == Some("failed")
}

async fn run_child_rollout_scan(
    manager: AgentRuntimeManager,
    owned_id: String,
    generation: u64,
    native_session_id: String,
) {
    let sessions = Arc::downgrade(&manager.sessions);
    let mut parent_path = None;
    loop {
        let Some(outcome) = scan_codex_children_once(
            &sessions,
            &manager.emitter,
            &owned_id,
            generation,
            &native_session_id,
            parent_path,
        )
        .await
        else {
            return;
        };
        parent_path = Some(outcome.parent_path);
        // After the turn ends, keep polling only while a child is still working.
        if !outcome.keep_scanning {
            // The last child finished after the turn did, so the idle process
            // may stop now. Detach this task first so stopping does not abort it.
            if let Ok(session) = current_session_mut(
                &mut manager.sessions.lock().unwrap_or_else(std::sync::PoisonError::into_inner),
                &owned_id,
                generation,
            ) {
                session.child_rollout_scan.take();
            }
            if let Err(error) = manager.suspend_if_quiescent(&owned_id, generation).await {
                crate::debug_log::stderr_log!("Could not tear down quiescent runtime: {error}");
            }
            return;
        }
        tokio::time::sleep(CHILD_ROLLOUT_SCAN_INTERVAL).await;
    }
}

struct ChildRolloutScanOutcome {
    parent_path: PathBuf,
    has_running_children: bool,
    keep_scanning: bool,
}

async fn scan_codex_children_once(
    sessions: &Weak<Mutex<HashMap<String, ManagedAgentSession>>>,
    emitter: &Arc<Mutex<Option<ConversationEmitter>>>,
    owned_id: &str,
    generation: u64,
    native_session_id: &str,
    parent_path: Option<PathBuf>,
) -> Option<ChildRolloutScanOutcome> {
    let native_id = native_session_id.to_string();
    let scan_parent_id = native_id.clone();
    let scan = tokio::task::spawn_blocking(move || {
        let path = match parent_path {
            Some(path) => path,
            None => {
                transcript::discover(AgentConversationProvider::Codex, &native_id)
                    .ok()
                    .flatten()?
                    .canonical_path
            }
        };
        transcript::scan_codex_child_rollouts(&path, &scan_parent_id)
            .ok()
            .map(|children| (path, children))
    })
    .await
    .ok()
    .flatten()?;
    let (parent_path, children) = scan;
    let sessions = sessions.upgrade()?;
    let mut sessions = sessions
        .lock()
        .unwrap_or_else(std::sync::PoisonError::into_inner);
    let session = current_session_mut(&mut sessions, owned_id, generation).ok()?;
    let turn_active = session.active_turn_id.is_some();
    let had_running_children = session
        .codex_children
        .values()
        .any(|child| child.state == "running");
    if session.provider != AgentConversationProvider::Codex
        || session.state == AgentRuntimeState::Closed
        // Once the turn ends, only a previously running child earns another scan.
        || (!turn_active && !had_running_children)
    {
        return None;
    }
    session.child_rollout_parent_path = Some(parent_path.clone());
    let (updates, has_running_children, keep_scanning) = child_rollout_scan_updates(
        native_session_id,
        &mut session.codex_children,
        children,
        turn_active,
    );
    for payload in updates {
        if let Err(error) = record_payload_for_session_and_dispatch(session, emitter, payload) {
            crate::debug_log::stderr_log!("Could not record child session update: {error}");
            break;
        }
    }
    Some(ChildRolloutScanOutcome {
        parent_path,
        has_running_children,
        keep_scanning,
    })
}

fn child_rollout_scan_updates(
    parent_id: &str,
    known: &mut HashMap<String, CodexChildRollout>,
    children: Vec<CodexChildRollout>,
    turn_active: bool,
) -> (Vec<AgentConversationPayload>, bool, bool) {
    let has_running_children = children.iter().any(|child| child.state == "running");
    (
        changed_child_updates(parent_id, known, children),
        has_running_children,
        turn_active || has_running_children,
    )
}

fn changed_child_updates(
    parent_id: &str,
    known: &mut HashMap<String, CodexChildRollout>,
    children: Vec<CodexChildRollout>,
) -> Vec<AgentConversationPayload> {
    let mut updates = Vec::new();
    for child in children {
        if known.get(&child.child_id) == Some(&child) {
            continue;
        }
        updates.push(AgentConversationPayload::ChildUpdate {
            child_id: child.child_id.clone(),
            parent_tool_call_id: parent_id.to_string(),
            parent_id: None,
            transcript_id: None,
            label: Some(child.label.clone()),
            state: child.state.clone(),
            latest_activity: Some(child.latest_activity.clone()),
        });
        known.insert(child.child_id.clone(), child);
    }
    updates
}

async fn handle_ordered_session_event(
    ordered: OrderedSessionEvent,
    manager: &AgentRuntimeManager,
    transport: &Weak<AcpTransport>,
    owned_id: &str,
    generation: u64,
) -> bool {
    let sessions = Arc::clone(&manager.sessions);
    let emitter = Arc::clone(&manager.emitter);
    match ordered {
        OrderedSessionEvent::ApprovalResolved {
            request_id,
            state,
            summary,
        } => {
            let mut sessions = sessions
                .lock()
                .unwrap_or_else(std::sync::PoisonError::into_inner);
            let Ok(session) = current_session_mut(&mut sessions, owned_id, generation) else {
                return false;
            };
            if session.writer_lease.owner != AgentWriterLeaseOwner::Structured {
                return true;
            }
            let next_state = if session.active_turn_id.is_some()
                || session.autonomous_turn_started
            {
                AgentRuntimeState::Working
            } else {
                AgentRuntimeState::Ready
            };
            let lifecycle =
                lifecycle_update_for_state(session, next_state, session.connection.state);
            if let Err(error) = record_payload_for_session_and_dispatch_with_lifecycle(
                session,
                &emitter,
                AgentConversationPayload::Approval {
                    request_id,
                    state,
                    summary, options: Vec::new(),
                },
                lifecycle,
            ) {
                crate::debug_log::stderr_log!(
                    "Could not record ACP permission resolution: {error}"
                );
                return false;
            }
            true
        }
        OrderedSessionEvent::UserInputResolved {
            request_id,
            cancelled,
        } => {
            let mut sessions = sessions
                .lock()
                .unwrap_or_else(std::sync::PoisonError::into_inner);
            let Ok(session) = current_session_mut(&mut sessions, owned_id, generation) else {
                return false;
            };
            if session.writer_lease.owner != AgentWriterLeaseOwner::Structured {
                return true;
            }
            let next_state = if session.active_turn_id.is_some()
                || session.autonomous_turn_started
            {
                AgentRuntimeState::Working
            } else {
                AgentRuntimeState::Ready
            };
            let lifecycle =
                lifecycle_update_for_state(session, next_state, session.connection.state);
            if let Err(error) = record_payload_for_session_and_dispatch_with_lifecycle(
                session,
                &emitter,
                AgentConversationPayload::UserInputResolved {
                    request_id,
                    cancelled,
                },
                lifecycle,
            ) {
                crate::debug_log::stderr_log!(
                    "Could not record ACP user input resolution: {error}"
                );
                return false;
            }
            true
        }
        OrderedSessionEvent::PromptResult { turn_id, result } => {
            manager.finish_child_rollout_scan(owned_id, generation, &turn_id).await;
            if let Err(error) = super::history::follow_imported_length(&manager.store, owned_id, false) { crate::debug_log::stderr_log!("Could not follow the imported transcript: {error}"); }
            let cancelled = result
                .as_ref()
                .ok()
                .and_then(stop_reason)
                .is_some_and(|reason| reason == "cancelled");
            let error_details = result
                .as_ref()
                .err()
                .map(|error| (error.code.to_string(), error.message.clone()));
            let runtime_error = error_details.is_some();
            // The same reading of the result that decides the turn payload
            // below. A turn that was interrupted or that failed has nothing
            // worth naming a session after.
            let turn_completed = result.is_ok() && !cancelled;

            // Approval draining and terminal event emission are one atomic
            // session-lock operation. If transport closure wins this lock,
            // it emits the same terminal approval/turn events and this handler
            // returns without duplicating them.
            let (pending_permissions, pending_inputs) = {
                let mut sessions = sessions
                    .lock()
                    .unwrap_or_else(std::sync::PoisonError::into_inner);
                let Ok(session) = current_session_mut(&mut sessions, owned_id, generation) else {
                    return false;
                };
                if session.active_turn_id.as_deref() != Some(turn_id.as_str())
                    || session.writer_lease.owner != AgentWriterLeaseOwner::Structured
                {
                    return true;
                }
                abort_cancel_deadline(session);
                // Claude cancelTurns retires native children (0.88 finishAll / 0.93 parentCancelled); Codex cancel only interrupts the turn.
                if cancelled && session.provider == AgentConversationProvider::Claude { session.background_work.clear(); }
                let pending_permissions =
                    drain_root_permissions(&mut session.permission_requests);
                for (request_id, pending) in &pending_permissions {
                    if let Err(error) = record_payload_for_session_and_dispatch(
                        session,
                        &emitter,
                        AgentConversationPayload::Approval {
                            request_id: request_id.clone(),
                            state: if cancelled {
                                ApprovalState::Declined
                            } else {
                                ApprovalState::Expired
                            },
                            summary: pending.summary.clone(), options: Vec::new(),
                        },
                    ) {
                        crate::debug_log::stderr_log!(
                            "Could not expire ACP permission request: {error}"
                        );
                        return false;
                    }
                }
                let pending_inputs = drain_root_inputs(&mut session.user_input_requests);
                for (request_id, _pending) in &pending_inputs {
                    if let Err(error) = record_payload_for_session_and_dispatch(
                        session,
                        &emitter,
                        AgentConversationPayload::UserInputResolved {
                            request_id: request_id.clone(),
                            cancelled: true,
                        },
                    ) {
                        crate::debug_log::stderr_log!(
                            "Could not expire ACP user input request: {error}"
                        );
                        return false;
                    }
                }
                record_finished_reply(session, &emitter); turn_usage::report_turn_usage(session, &turn_id, result.as_ref().unwrap_or(&Value::Null));
                let payload = match result {
                    Ok(_) if cancelled => AgentConversationPayload::Turn {
                        turn_id: turn_id.clone(),
                        state: super::protocol::TurnState::Interrupted,
                    },
                    Ok(_) => AgentConversationPayload::Turn {
                        turn_id: turn_id.clone(),
                        state: super::protocol::TurnState::Completed,
                    },
                    Err(_) => AgentConversationPayload::Turn {
                        turn_id: turn_id.clone(),
                        state: super::protocol::TurnState::Failed,
                    },
                };
                let lifecycle = lifecycle_update_for_state(
                    session,
                    AgentRuntimeState::Ready,
                    session.connection.state,
                );
                if let Err(error) = record_payload_for_session_and_dispatch_with_lifecycle(
                    session, &emitter, payload, lifecycle,
                ) {
                    crate::debug_log::stderr_log!("Could not record ACP turn completion: {error}");
                    return false;
                }
                if let Some((code, message)) = error_details {
                    if let Err(error) = record_payload_for_session_and_dispatch(
                        session,
                        &emitter,
                        AgentConversationPayload::Error {
                            code,
                            message: authentication::explain(session.provider, message),
                            recoverable: true,
                        },
                    ) {
                        crate::debug_log::stderr_log!("Could not record ACP prompt error: {error}");
                        return false;
                    }
                }
                session.active_turn_id = None;
                session.prompt_once_active = false;
                (pending_permissions, pending_inputs)
            };

            // The wire responses are transport I/O and must not hold the
            // session lock. Lifecycle emission above is complete regardless of
            // whether the transport is still able to accept these responses.
            if let Some(transport) = transport.upgrade() {
                for (_, pending) in pending_permissions {
                    if let Err(error) = transport
                        .respond(
                            pending.wire_id,
                            serde_json::json!({ "outcome": { "outcome": "cancelled" } }),
                        )
                        .await
                    {
                        crate::debug_log::stderr_log!(
                            "Could not expire ACP permission request: {error}"
                        );
                    }
                }
                for (_, pending) in pending_inputs {
                    let response = cancelled_user_input_response(&pending);
                    if let Err(error) = transport
                        .respond(pending.wire_id, response)
                        .await
                    {
                        crate::debug_log::stderr_log!(
                            "Could not expire ACP user input request: {error}"
                        );
                    }
                }
            }
            if turn_completed {
                manager.name_session_after_turn(owned_id, generation, &turn_id);
            }
            let outcome = if cancelled { "stopped" } else if runtime_error { "failed" } else { "finished" };
            if let Err(error) = manager.report_helper_turn(owned_id, &turn_id, outcome) {
                crate::debug_log::stderr_log!("Could not report the helper's turn: {error}");
            }
            if let Err(error) = manager
                .drain_broker_message_at(owned_id, generation, store_timestamp(timestamp_millis()))
                .await
            {
                crate::debug_log::stderr_log!("Could not drain broker message: {error}");
            }
            // A finished turn stops the adapter process as soon as no prompt,
            // approval, input request, or tool work remains. The stored native
            // session survives for resume on the next send; only the idle
            // process tree is removed because those trees otherwise retain
            // hundreds of megabytes between turns.
            if let Err(error) = manager.sync_broker_status(owned_id, generation, runtime_error) {
                crate::debug_log::stderr_log!("Could not publish broker status: {error}");
            }
            if let Err(error) = manager.suspend_if_quiescent(owned_id, generation).await {
                crate::debug_log::stderr_log!("Could not tear down quiescent runtime: {error}");
            }
            true
        }
    }
}

fn stop_reason(response: &Value) -> Option<&str> {
    response
        .get("stopReason")
        .or_else(|| response.get("stop_reason"))
        .and_then(Value::as_str)
}

fn current_mode_update(params: &Value) -> Option<&str> {
    let update = params
        .get("update")
        .or_else(|| params.get("sessionUpdate"))
        .unwrap_or(params);
    update
        .get("sessionUpdate")
        .or_else(|| update.get("session_update"))
        .or_else(|| update.get("type"))
        .and_then(Value::as_str)
        .filter(|kind| *kind == "current_mode_update")?;
    update
        .get("currentModeId")
        .or_else(|| update.get("current_mode_id"))
        .and_then(Value::as_str)
}

fn permission_summary(params: &Value) -> String {
    params
        .get("toolCall")
        .and_then(|tool| tool.get("title").or_else(|| tool.get("name")))
        .or_else(|| params.get("title"))
        .and_then(Value::as_str)
        .filter(|summary| !summary.is_empty())
        .unwrap_or("Permission requested")
        .to_string()
}

fn permission_options(params: &Value) -> Vec<PermissionOption> {
    params
        .get("options")
        .and_then(Value::as_array)
        .into_iter()
        .flatten()
        .filter_map(|option| {
            let option_id = option
                .get("optionId")
                .or_else(|| option.get("option_id"))
                .and_then(Value::as_str)
                .filter(|option_id| !option_id.is_empty())?
                .to_string();
            Some(PermissionOption {
                option_id,
                kind: option
                    .get("kind")
                    .and_then(Value::as_str)
                    .unwrap_or_default()
                    .to_string(),
                name: option.get("name").and_then(Value::as_str).unwrap_or_default().to_string(),
            })
        })
        .collect()
}

fn permission_response(
    options: &[PermissionOption],
    decision: AgentApprovalDecision,
) -> Result<Value, String> {
    match decision {
        AgentApprovalDecision::Cancel => Ok(serde_json::json!({
            "outcome": { "outcome": "cancelled" }
        })),
        AgentApprovalDecision::Accept => {
            let option_id = options
                .iter()
                .find(|option| is_allow_kind(&option.kind))
                .or_else(|| options.first())
                .map(|option| option.option_id.clone())
                .ok_or_else(|| "ACP permission request did not provide an option".to_string())?;
            Ok(serde_json::json!({
                "outcome": { "outcome": "selected", "optionId": option_id }
            }))
        }
        AgentApprovalDecision::Decline => {
            let option_id = options
                .iter()
                .find(|option| is_reject_kind(&option.kind))
                .map(|option| option.option_id.clone())
                .ok_or_else(|| {
                    "ACP permission request did not provide a reject option".to_string()
                })?;
            Ok(serde_json::json!({
                "outcome": { "outcome": "selected", "optionId": option_id }
            }))
        }
    }
}

/// What a workflow policy answers for the agent. Allow picks the allow-once
/// option, never one that allows for good; with none offered, or on ask, a
/// person is asked instead.
fn policy_answer(decision: crate::workflow::policy::Decision, options: &[PermissionOption]) -> Option<Value> {
    use crate::workflow::policy::Decision;
    let selected = |option: &PermissionOption| serde_json::json!({ "outcome": { "outcome": "selected", "optionId": option.option_id } });
    match decision {
        Decision::Allow => options.iter().find(|option| option.kind == "allow_once").map(selected),
        Decision::Deny => Some(options.iter().find(|option| is_reject_kind(&option.kind)).map_or_else(|| serde_json::json!({ "outcome": { "outcome": "cancelled" } }), selected)),
        Decision::Ask => None,
    }
}

fn permission_response_for_selection(
    options: &[PermissionOption],
    selection: &PermissionSelection,
) -> Result<Value, String> {
    match selection {
        PermissionSelection::Decision(decision) => permission_response(options, *decision),
        PermissionSelection::Option(option_id) => {
            if options.iter().any(|option| option.option_id == *option_id) {
                Ok(serde_json::json!({
                    "outcome": { "outcome": "selected", "optionId": option_id }
                }))
            } else {
                Err("ACP permission option is not part of the pending request".to_string())
            }
        }
    }
}

fn permission_state(
    options: &[PermissionOption],
    selection: &PermissionSelection,
) -> ApprovalState {
    match selection {
        PermissionSelection::Decision(AgentApprovalDecision::Accept) => ApprovalState::Accepted,
        PermissionSelection::Decision(
            AgentApprovalDecision::Decline | AgentApprovalDecision::Cancel,
        ) => ApprovalState::Declined,
        PermissionSelection::Option(option_id) => options
            .iter()
            .find(|option| option.option_id == *option_id)
            .filter(|option| !is_reject_kind(&option.kind))
            .map(|_| ApprovalState::Accepted)
            .unwrap_or(ApprovalState::Declined),
    }
}

fn user_input_response(
    shape: UserInputResponseShape,
    action: AgentUserInputAction,
    content: BTreeMap<String, Value>,
) -> Result<Value, String> {
    match (shape, action) {
        (UserInputResponseShape::Legacy, AgentUserInputAction::Accept) => {
            Ok(serde_json::json!({ "values": content, "cancelled": false }))
        }
        (UserInputResponseShape::Legacy, AgentUserInputAction::Cancel) => {
            Ok(serde_json::json!({ "values": {}, "cancelled": true }))
        }
        (UserInputResponseShape::Legacy, AgentUserInputAction::Decline) => {
            Err("This input request does not support decline".to_string())
        }
        (UserInputResponseShape::Elicitation, AgentUserInputAction::Accept) => {
            Ok(serde_json::json!({ "action": "accept", "content": content }))
        }
        (UserInputResponseShape::Elicitation, AgentUserInputAction::Decline) => {
            Ok(serde_json::json!({ "action": "decline" }))
        }
        (UserInputResponseShape::Elicitation, AgentUserInputAction::Cancel) => {
            Ok(serde_json::json!({ "action": "cancel" }))
        }
    }
}

fn drain_root_permissions(
    permissions: &mut HashMap<String, PendingPermission>,
) -> Vec<(String, PendingPermission)> {
    let mut drained = Vec::new();
    permissions.retain(|request_id, pending| {
        if pending.child_scoped {
            true
        } else {
            drained.push((request_id.clone(), pending.clone()));
            false
        }
    });
    drained
}

fn drain_root_inputs(
    inputs: &mut HashMap<String, PendingUserInput>,
) -> Vec<(String, PendingUserInput)> {
    let mut drained = Vec::new();
    inputs.retain(|request_id, pending| {
        if pending.child_scoped {
            true
        } else {
            drained.push((request_id.clone(), pending.clone()));
            false
        }
    });
    drained
}

fn cancelled_user_input_response(pending: &PendingUserInput) -> Value {
    user_input_response(
        pending.response_shape,
        AgentUserInputAction::Cancel,
        BTreeMap::new(),
    )
    .expect("cancel is valid for every user input response shape")
}

fn is_allow_kind(kind: &str) -> bool {
    kind.eq_ignore_ascii_case("allow") || kind.to_ascii_lowercase().starts_with("allow_")
}

fn is_reject_kind(kind: &str) -> bool {
    kind.eq_ignore_ascii_case("reject") || kind.to_ascii_lowercase().starts_with("reject_")
}

fn canonical_event(
    session: &ManagedAgentSession,
    native_session_id: Option<String>,
    sequence: i64,
    timestamp_ms: u128,
    payload: &AgentConversationPayload,
) -> Result<AgentEvent, String> {
    let event_type = match payload {
        AgentConversationPayload::Connection {
            state: ConversationConnectionState::Closed,
            ..
        } => AgentEventType::SessionClosed,
        AgentConversationPayload::Connection { .. } | AgentConversationPayload::SessionCompleted { .. } => AgentEventType::SessionStateChanged,
        AgentConversationPayload::UserMessage { .. } => AgentEventType::ItemCompleted,
        AgentConversationPayload::AssistantDelta { .. } => AgentEventType::ContentDelta,
        AgentConversationPayload::AssistantMessage { .. } => AgentEventType::ItemCompleted,
        AgentConversationPayload::Tool { .. } => AgentEventType::ItemUpdated,
        AgentConversationPayload::ChildUpdate { .. } => AgentEventType::ChildrenUpdated,
        AgentConversationPayload::Approval { .. } => AgentEventType::ApprovalRequested,
        AgentConversationPayload::UserInputRequested { .. } => AgentEventType::UserInputRequested,
        AgentConversationPayload::UserInputResolved { .. } => AgentEventType::UserInputResolved,
        AgentConversationPayload::Plan { .. } => AgentEventType::PlanUpdated,
        AgentConversationPayload::AvailableCommandsUpdate { .. } => AgentEventType::CommandsUpdated,
        AgentConversationPayload::Turn {
            state: super::protocol::TurnState::Started, ..
        } => AgentEventType::TurnStarted,
        AgentConversationPayload::Turn {
            state: super::protocol::TurnState::Interrupted,
            ..
        } => AgentEventType::TurnInterrupted,
        AgentConversationPayload::Turn { .. } => AgentEventType::TurnCompleted,
        AgentConversationPayload::Usage { .. } => AgentEventType::UsageUpdated,
        AgentConversationPayload::ContextCompaction { .. } => AgentEventType::ItemCompleted,
        AgentConversationPayload::CheckoutChanged { .. } => AgentEventType::RuntimeWarning,
        AgentConversationPayload::TerminalProjection(projection) => projection.event_type,
        AgentConversationPayload::Error { .. } => AgentEventType::RuntimeError,
    };
    if let AgentConversationPayload::TerminalProjection(projection) = payload {
        return Ok(AgentEvent {
            event_type,
            owned_id: session.owned_id.clone(),
            provider: session.provider,
            provider_instance_id: projection.provider_instance_id.clone(),
            generation: session.generation,
            sequence,
            timestamp_ms: projection
                .timestamp_ms
                .map(u128::from)
                .unwrap_or(timestamp_ms),
            native_session_id: Some(projection.native_session_id.clone()),
            turn_id: None,
            item_id: projection.item_id.clone(),
            request_id: None,
            payload: projection.payload.clone(),
            provider_metadata: Some(projection.provider_metadata.clone()),
            raw_frame_reference: Some(projection.raw_frame_reference.clone()),
        });
    }
    let payload_value = serde_json::to_value(payload).map_err(|error| error.to_string())?;
    let payload = payload_value
        .as_object()
        .map(|object| {
            object
                .iter()
                .map(|(key, value)| (key.clone(), value.clone()))
                .collect()
        })
        .unwrap_or_else(BTreeMap::new);
    Ok(AgentEvent {
        event_type,
        owned_id: session.owned_id.clone(),
        provider: session.provider,
        provider_instance_id: session.provider_instance_id.clone(),
        generation: session.generation,
        sequence,
        timestamp_ms,
        native_session_id,
        turn_id: session.active_turn_id.clone(),
        item_id: None,
        request_id: None,
        payload,
        provider_metadata: None,
        raw_frame_reference: None,
    })
}

fn empty_capabilities(provider: AgentConversationProvider) -> AgentCapabilities {
    AgentCapabilities {
        revision: 1,
        provider,
        implementation: AgentImplementation {
            name: "not-initialized".into(),
            version: "0".into(),
        },
        session: AgentSessionCapabilities {
            multi_session: false,
            list: false,
            load: false,
            resume: false,
            close: false,
            steering: false,
            fork: false,
        },
        prompt: AgentPromptCapabilities {
            text: true,
            image: false,
            embedded_context: false,
            resource_links: false,
        },
        interaction: AgentInteractionCapabilities {
            permissions: false,
            structured_user_input: false,
            tool_terminals: false,
            plans: false,
            tasks: false,
            subagents: false,
        },
        config_options: Vec::new(),
        commands: Vec::new(),
    }
}

fn provider_is_multi_session_safe(capabilities: &AgentCapabilities) -> bool {
    capabilities.session.multi_session
}

fn timestamp_millis() -> u128 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|duration| duration.as_millis())
        .unwrap_or(0)
}

fn session_can_suspend(session: &ManagedAgentSession) -> bool {
    session.runtime.is_some()
        && !session.suspending
        && session.native_session_id.is_some()
        && session.capabilities.session.resume
        && session_is_quiescent(session)
}

fn session_can_change_checkout(session: &ManagedAgentSession) -> bool {
    matches!(
        session.owner,
        AgentExecutionOwner::Structured | AgentExecutionOwner::Stopped
    ) && !session.suspending
        && session.native_session_id.is_some()
        && super::capabilities::provider_supports_changing_checkout(&session.capabilities)
        && (session.runtime.is_some() || session.state == AgentRuntimeState::Suspended)
        && session_is_checkout_quiescent(session)
}

fn session_is_checkout_quiescent(session: &ManagedAgentSession) -> bool {
    matches!(
        session.state,
        AgentRuntimeState::Ready | AgentRuntimeState::Suspended
    ) && session.active_turn_id.is_none()
        && !session.prompt_once_active
        && session.permission_requests.is_empty()
        && session.user_input_requests.is_empty()
        && session.writer_lease_transition.is_none()
        && session.live_tool_calls.is_empty()
        && session.background_work.is_empty()
}

/// Background work held while Claude Code reports it is running.
const CLAUDE_SESSION_RUNNING: &str = "claude-session:running";
const CLAUDE_BACKGROUND_TASKS: &str = "claude-session:background-tasks";

fn insert_background_work(
    background_work: &mut HashMap<String, BackgroundWorkEntry>,
    id: String,
    kind: BackgroundWorkKind,
    label: String,
) {
    background_work
        .entry(id)
        .and_modify(|entry| {
            entry.kind = kind;
            if !label.is_empty() {
                entry.label.clone_from(&label);
            }
        })
        .or_insert_with(|| BackgroundWorkEntry {
            kind,
            label,
            started_at_ms: store_timestamp(timestamp_millis()),
        });
}

fn projected_background_work(session: &ManagedAgentSession) -> Vec<BackgroundWorkItem> {
    let mut work = session
        .background_work
        .iter()
        .filter_map(|(id, entry)| {
            let visible = id.starts_with("claude-child:") || id.starts_with("background-task:");
            visible.then(|| BackgroundWorkItem {
                id: id.clone(),
                kind: entry.kind,
                label: id
                    .strip_prefix("claude-child:")
                    .and_then(|child_id| session.claude_children.get(child_id))
                    .and_then(|child| child.label.clone())
                    .unwrap_or_else(|| entry.label.clone()),
                started_at_ms: entry.started_at_ms,
            })
        })
        .collect::<Vec<_>>();
    work.sort_by(|left, right| {
        left.started_at_ms
            .cmp(&right.started_at_ms)
            .then_with(|| left.id.cmp(&right.id))
    });
    work
}

/// Claude Code wakes the agent that owned finished background work — a shell,
/// or a sub-agent — to report it, even after it reported idle. Until its next
/// idle the session still has work in hand.
fn expect_claude_wake(session: &mut ManagedAgentSession) {
    if session.claude_reports_state {
        insert_background_work(
            &mut session.background_work,
            CLAUDE_SESSION_RUNNING.to_string(),
            BackgroundWorkKind::Command,
            String::new(),
        );
    }
}

/// Claude ACP 0.93 foreground hooks publish sessionState running/requires_action/idle
/// (byte 92464749); 0.88 defines the field beside backgroundSubagents (byte 82537556).
/// Idle ends the current cycle; background commands and monitors can remain.
fn claude_session_state<'a>(session: &ManagedAgentSession, params: &'a Value) -> Option<&'a str> {
    if session.provider != AgentConversationProvider::Claude
        || session_update_kind(params) != Some("session_info_update")
    {
        return None;
    }
    params
        .pointer("/update/_meta/jetbrains/air/sessionState")
        .and_then(Value::as_str)
}

fn session_is_quiescent(session: &ManagedAgentSession) -> bool {
    session.state == AgentRuntimeState::Ready
        && session.active_turn_id.is_none()
        && !session.prompt_once_active
        && session.permission_requests.is_empty()
        && session.user_input_requests.is_empty()
        && session.writer_lease_transition.is_none()
        && session.live_tool_calls.is_empty()
        && session.background_work.is_empty()
        // Stopping the process would stop a Codex sub-agent still at work.
        && !session.codex_children.values().any(|child| child.state == "running")
}

fn abort_cancel_deadline(session: &mut ManagedAgentSession) {
    if let Some(task) = session.cancel_deadline.take() {
        task.abort();
    }
}
fn required_id(value: &str, label: &str) -> Result<String, String> {
    let value = value.trim();
    if value.is_empty() {
        Err(format!("{label} is required"))
    } else {
        Ok(value.to_string())
    }
}
fn normalized_optional_id(value: Option<String>) -> Option<String> {
    value.and_then(|value| {
        let value = value.trim();
        (!value.is_empty()).then(|| value.to_string())
    })
}

/// Claude ACP 0.90.0 resolveThinkingConfig maps MAX_THINKING_TOKENS to an SDK budget.
/// Clear that inherited override so the mutable effort control owns thinking.
/// Source: claude-0.90.0-thinking-source.txt in the TSK-1508 model-effort receipts.
fn session_spawn_environment(provider: AgentConversationProvider) -> SidecarEnvironment {
    if provider != AgentConversationProvider::Claude {
        return SidecarEnvironment::default();
    }
    crate::usage_history::otel::claude_telemetry(SidecarEnvironment::default().remove(MAX_THINKING_TOKENS_ENV))
}

fn validate_conversation_config_update(
    current: &AgentConversationConfigState,
    update: &AgentConversationConfigUpdate,
) -> Result<(), String> {
    for (value, available, label) in [
        (&update.model, &current.available_models, "model"),
        (
            &update.reasoning_effort,
            &current.available_efforts,
            "reasoning effort",
        ),
        (
            &update.approval_policy,
            &current.available_approval_policies,
            "approval policy",
        ),
    ] {
        if value
            .as_ref()
            .is_some_and(|value| !available.contains(value))
        {
            return Err(format!(
                "The requested {label} is not available for this session"
            ));
        }
    }
    Ok(())
}

/// Older suspended snapshots may submit Codex's composite model shape once.
/// Codex ACP 1.13.1/2.2.2 still emit it (codex-1.13.1-isolated.jsonl:6).
/// Translate to the live controls negotiated during activation before validating.
fn normalize_codex_composite_config_update(
    provider: AgentConversationProvider,
    current: &AgentConversationConfigState,
    update: &mut AgentConversationConfigUpdate,
) {
    if !provider.descriptor().effort_in_model_id || current.available_efforts.is_empty() {
        return;
    }
    let Some(model) = update.model.as_deref() else {
        return;
    };
    let Some((base, effort)) = model
        .strip_suffix(']')
        .and_then(|value| value.rsplit_once('['))
    else {
        return;
    };
    let replacement = (current.available_models.iter().any(|value| value == base)
        && current
            .available_efforts
            .iter()
            .any(|value| value == effort))
    .then(|| (base.to_string(), effort.to_string()));
    if let Some((model, effort)) = replacement {
        update.model = Some(model);
        update.reasoning_effort = Some(effort);
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::agent_conversation::normalization::tools::{
        tool_details, unified_diff, unwrap_console_block,
    };
    use crate::agent_conversation::protocol::AgentCommandDescriptor;
    mod helper_messages;
    mod checkout;
    mod model_effort; mod permissions;
    mod codex_resume; mod import_length; mod turn_answers; mod rail_meta; mod helper_folder; mod helper_close; mod agui_steer; mod lazy_tool_rows; mod terminal_output;
    use serde_json::json;
    use std::fs;
    use std::time::{Duration, Instant};
    use crate::agent_conversation::handoff::{
        AgentConversationHistoryBoundary, AgentConversationProcessTreeAssertion,
    };

    fn pending_test_event(
        owned_id: &str,
        generation: u64,
        payload: AgentConversationPayload,
    ) -> AgentConversationEvent {
        AgentConversationEvent {
            runtime_state: None,
            owned_id: owned_id.into(),
            provider: AgentConversationProvider::Codex,
            generation,
            sequence: 1,
            timestamp_ms: 1,
            turn_id: Some("turn-pending".into()),
            payload,
        }
    }

    #[test]
    fn claude_form_maps_installed_text_single_and_multi_fields() {
        let (title, description, fields) = parse_elicitation_form(&json!({
            "mode": "form",
            "message": "Choose release details",
            "requestedSchema": {
                "type": "object",
                "required": ["summary", "channel"],
                "properties": {
                    "summary": {
                        "type": "string",
                        "title": "Summary",
                        "description": "What changed?",
                        "_meta": { "source": "claude" }
                    },
                    "channel": {
                        "type": "string",
                        "oneOf": [
                            { "const": "stable", "title": "Stable" },
                            { "const": "other", "title": "Other", "description": "Enter another value" }
                        ]
                    },
                    "reviewers": {
                        "type": "array",
                        "items": {
                            "anyOf": [
                                { "const": "ada", "title": "Ada" },
                                { "const": "lin", "title": "Lin" }
                            ]
                        }
                    }
                }
            }
        }))
        .unwrap();

        assert_eq!(title, "Choose release details");
        assert_eq!(description, None);
        assert_eq!(fields.len(), 3);
        let by_id = fields
            .into_iter()
            .map(|field| (field.id.clone(), field))
            .collect::<HashMap<_, _>>();
        assert!(by_id["summary"].required);
        assert_eq!(by_id["summary"].description.as_deref(), Some("What changed?"));
        assert_eq!(by_id["channel"].kind, AgentUserInputKind::Select);
        assert_eq!(by_id["channel"].choices.as_ref().unwrap()[1].label, "Other");
        assert_eq!(by_id["reviewers"].kind, AgentUserInputKind::MultiSelect);
        assert!(!by_id["reviewers"].required);
    }

    #[test]
    fn claude_form_declines_unsupported_constraints() {
        let error = parse_elicitation_form(&json!({
            "mode": "form",
            "message": "Name",
            "requestedSchema": {
                "type": "object",
                "properties": {
                    "name": { "type": "string", "minLength": 2 }
                }
            }
        }))
        .unwrap_err();
        assert!(error.contains("unsupported constraints"));
    }

    #[test]
    fn stale_codex_composite_choice_becomes_live_model_and_effort_controls() {
        let current = AgentConversationConfigState {
            model_labels: Default::default(),
            model_efforts: Default::default(),
            model_default_efforts: Default::default(),
            model: Some("gpt-6-astra".into()),
            available_models: vec!["gpt-6-astra".into(), "gpt-5.6-sol".into()],
            reasoning_effort: Some("medium".into()),
            available_efforts: vec!["low".into(), "medium".into(), "high".into()],
            approval_policy: None,
            available_approval_policies: Vec::new(),
        };
        let mut update = AgentConversationConfigUpdate {
            model: Some("gpt-5.6-sol[medium]".into()),
            reasoning_effort: None,
            approval_policy: None,
        };

        normalize_codex_composite_config_update(
            AgentConversationProvider::Codex,
            &current,
            &mut update,
        );

        assert_eq!(update.model.as_deref(), Some("gpt-5.6-sol"));
        assert_eq!(update.reasoning_effort.as_deref(), Some("medium"));
    }

    /// A provider that hands over a whole `git diff` as text still gets a
    /// file-change row rather than a wall of raw patch lines.
    #[test]
    fn a_patch_sent_as_text_is_read_as_a_change() {
        let patch = "diff --git a/src/app.ts b/src/app.ts\n\
                     index 1111111..2222222 100644\n\
                     --- a/src/app.ts\n\
                     +++ b/src/app.ts\n\
                     @@ -1,2 +1,2 @@\n\
                     -let total = 1;\n\
                     +let total = 2;\n\
                      export { total };";
        let details = tool_details(&json!({
            "content": [{ "type": "content", "content": { "type": "text", "text": patch } }]
        }));
        let diff = details.diff.expect("the patch should be read as a change");
        assert!(
            diff.starts_with("@@ -1,2 +1,2 @@"),
            "hunks only, got: {diff}"
        );
        assert!(
            !diff.contains("diff --git"),
            "the git header should be dropped"
        );
        assert!(
            !diff.contains("index 1111111"),
            "the index line should be dropped"
        );
        assert_eq!(details.path.as_deref(), Some("src/app.ts"));
        assert!(details.output.is_none(), "a patch is not also tool output");
    }

    /// A deletion names its file on the side that is not `/dev/null`.
    #[test]
    fn a_deletion_is_named_by_the_file_that_went_away() {
        let patch = "diff --git a/old.txt b/old.txt\n\
                     --- a/old.txt\n\
                     +++ /dev/null\n\
                     @@ -1 +0,0 @@\n\
                     -gone";
        let details = tool_details(&json!({
            "content": [{ "type": "content", "content": { "type": "text", "text": patch } }]
        }));
        assert_eq!(details.path.as_deref(), Some("old.txt"));
    }

    /// Ordinary output stays output. Prose that happens to quote a rule of
    /// dashes, or a command that printed one, must not become a file change.
    #[test]
    fn ordinary_output_is_not_mistaken_for_a_patch() {
        for text in [
            "Building...\n--- done ---\nok",
            "--- a/only-a-header",
            "@@ a hunk with no file lines @@",
            "",
        ] {
            let details = tool_details(&json!({
                "content": [{ "type": "content", "content": { "type": "text", "text": text } }]
            }));
            assert!(
                details.diff.is_none(),
                "{text:?} should not read as a change"
            );
        }
    }

    fn request(
        root: &str,
        owned_id: &str,
        provider: AgentConversationProvider,
    ) -> EnsureAgentConversationRequest {
        EnsureAgentConversationRequest {
            owned_id: owned_id.into(),
            execution_environment: ExecutionEnvironment::Local,
            remote_profile_id: None,
            provider,
            cwd: root.into(),
            native_session_id: None,
            native_session_mode: AgentNativeSessionMode::Resume,
            reasoning_effort: None,
            project_id: None,
        }
    }

    #[test]
    fn remote_environment_never_launches_through_the_local_manager() {
        let manager = AgentRuntimeManager::new(ProviderRegistry::default());
        let mut request = request(
            "/remote/path/that/does/not/exist/here",
            "owned-remote",
            AgentConversationProvider::Codex,
        );
        request.execution_environment = ExecutionEnvironment::Remote;

        let error = manager
            .ensure_inner(request)
            .err()
            .expect("the local manager must reject a remote environment");
        assert_eq!(
            error,
            "Remote Assembly conversations must be routed through the remote connection manager"
        );
    }

    fn temp_root() -> std::path::PathBuf {
        let path = std::env::temp_dir().join(format!("mcb-runtime-{}", uuid::Uuid::new_v4()));
        fs::create_dir_all(&path).unwrap();
        path
    }

    #[test]
    fn child_rollout_scan_emits_once_until_metadata_changes() {
        let root = temp_root();
        let parent_directory = root.join("2026/08/13");
        let today_directory = root.join(chrono::Local::now().format("%Y/%m/%d").to_string());
        fs::create_dir_all(&parent_directory).unwrap();
        fs::create_dir_all(&today_directory).unwrap();
        let parent = parent_directory.join("rollout-parent.jsonl");
        fs::write(
            &parent,
            "{\"type\":\"session_meta\",\"payload\":{\"id\":\"parent\"}}\n",
        )
        .unwrap();
        fs::write(
            today_directory.join("rollout-child.jsonl"),
            concat!(
                "{\"type\":\"session_meta\",\"payload\":{",
                "\"id\":\"child-1\",\"parent_thread_id\":\"parent\",",
                "\"thread_source\":\"subagent\",",
                "\"source\":{\"subagent\":{\"thread_spawn\":{",
                "\"agent_path\":\"/root/probe\",\"agent_role\":\"explore\"}}}}}\n",
                "{\"type\":\"response_item\",\"payload\":{\"text\":\"ignored\"}}\n"
            ),
        )
        .unwrap();
        let children = transcript::scan_codex_child_rollouts(&parent, "parent").unwrap();
        let mut known = HashMap::new();

        assert_eq!(
            changed_child_updates("parent", &mut known, children.clone()),
            vec![AgentConversationPayload::ChildUpdate {
                child_id: "child-1".into(),
                parent_tool_call_id: "parent".into(),
                parent_id: None,
                transcript_id: None,
                label: Some("probe (explore)".into()),
                state: "running".into(),
                latest_activity: Some("Running".into()),
            }]
        );
        assert!(changed_child_updates("parent", &mut known, children).is_empty());
        // The child's own transcript says when its turn ends, however long it was quiet before.
        let child = today_directory.join("rollout-child.jsonl");
        let append = |line: &str| {
            use std::io::Write;
            writeln!(fs::OpenOptions::new().append(true).open(&child).unwrap(), "{line}").unwrap();
        };
        let state = || transcript::scan_codex_child_rollouts(&parent, "parent").unwrap()[0].state.clone();
        append(r#"{"type":"event_msg","payload":{"type":"task_started"}}"#);
        assert_eq!(state(), "running");
        append(r#"{"type":"event_msg","payload":{"type":"task_complete"}}"#);
        assert_eq!(state(), "finished");
        append(r#"{"type":"event_msg","payload":{"type":"task_started"}}"#);
        assert_eq!(state(), "running");
        append(r#"{"type":"event_msg","payload":{"type":"turn_aborted"}}"#);
        assert_eq!(state(), "finished");
        // A child that died without its closing record stops counting once it has been silent too long.
        append(r#"{"type":"event_msg","payload":{"type":"task_started"}}"#);
        assert_eq!(state(), "running");
        let silent_since = std::time::SystemTime::now() - transcript::CHILD_SILENCE_LIMIT - Duration::from_secs(60);
        fs::OpenOptions::new().append(true).open(&child).unwrap().set_modified(silent_since).unwrap();
        assert_eq!(state(), "finished");
        fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn child_rollout_scan_outlives_turn_until_running_children_finish() {
        let running = CodexChildRollout {
            child_id: "child-1".into(),
            label: "probe (explore)".into(),
            state: "running".into(),
            latest_activity: "Running".into(),
        };
        let finished = CodexChildRollout {
            state: "finished".into(),
            latest_activity: "Finished".into(),
            ..running.clone()
        };
        let mut known = HashMap::new();

        let (updates, _, keep_scanning) =
            child_rollout_scan_updates("parent", &mut known, vec![running], true);
        assert!(keep_scanning);
        assert_eq!(
            updates,
            vec![AgentConversationPayload::ChildUpdate {
                child_id: "child-1".into(),
                parent_tool_call_id: "parent".into(),
                parent_id: None,
                transcript_id: None,
                label: Some("probe (explore)".into()),
                state: "running".into(),
                latest_activity: Some("Running".into()),
            }]
        );

        let (updates, _, keep_scanning) =
            child_rollout_scan_updates("parent", &mut known, vec![finished], false);
        assert!(!keep_scanning);
        assert_eq!(
            updates,
            vec![AgentConversationPayload::ChildUpdate {
                child_id: "child-1".into(),
                parent_tool_call_id: "parent".into(),
                parent_id: None,
                transcript_id: None,
                label: Some("probe (explore)".into()),
                state: "finished".into(),
                latest_activity: Some("Finished".into()),
            }]
        );
    }

    /// The stored name of a fixture session, read back the way the rail reads it.
    fn stored_title(fixture: &FixtureManager) -> Option<String> {
        fixture
            .manager
            .store()
            .get_session(&fixture.owned_id)
            .expect("read the session row")
            .and_then(|row| row.title)
    }

    fn stored_title_source(fixture: &FixtureManager) -> Option<String> {
        fixture
            .manager
            .store()
            .get_session(&fixture.owned_id)
            .expect("read the session row")
            .and_then(|row| row.title_source)
    }

    /// A namer that answers from memory instead of from a utility model, and
    /// counts how many times it was asked.
    fn counting_namer(calls: &Arc<Mutex<usize>>, answer: &'static str) -> SessionNamer {
        let calls = Arc::clone(calls);
        Arc::new(move |_input: &str| {
            *calls.lock().unwrap() += 1;
            Ok(answer.to_string())
        })
    }

    fn renamed_meta(title: &str) -> AgentConversationSessionMeta {
        AgentConversationSessionMeta {
            title: Some(title.to_string()),
            ..AgentConversationSessionMeta::default()
        }
    }

    #[tokio::test(flavor = "current_thread")]
    async fn a_helper_title_replaces_the_prompt_title_once() {
        let fixture = fixture_manager_with_acp_session("helper_title").await;
        let calls: Arc<Mutex<usize>> = Default::default();
        fixture
            .manager
            .set_session_namer(counting_namer(&calls, "Fix rail titles"));
        let seen: Arc<Mutex<Vec<AgentConversationEvent>>> = Default::default();
        let sink = Arc::clone(&seen);
        fixture
            .manager
            .set_emitter(Arc::new(move |event| sink.lock().unwrap().push(event)));

        fixture
            .manager
            .prompt(
                &fixture.owned_id,
                fixture.generation,
                test_prompt("make the rail titles readable"),
            )
            .await
            .expect("prompt starts");
        wait_until(|| stored_title(&fixture).as_deref() == Some("Fix rail titles")).await;

        assert_eq!(stored_title_source(&fixture).as_deref(), Some("helper"));
        assert_eq!(*calls.lock().unwrap(), 1);

        fixture
            .manager
            .prompt(
                &fixture.owned_id,
                fixture.generation,
                test_prompt("and one more thing"),
            )
            .await
            .expect("second prompt starts");
        wait_until(|| completed_turns(&seen) == 2).await;

        assert_eq!(*calls.lock().unwrap(), 1);
        assert_eq!(stored_title(&fixture).as_deref(), Some("Fix rail titles"));
        fixture
            .manager
            .close(&fixture.owned_id, fixture.generation)
            .await
            .unwrap();
        fs::remove_dir_all(fixture.root).unwrap();
    }

    /// The rail saves a new session's row back within seconds of the send,
    /// carrying the same provisional name the prompt path has just written.
    /// That is not a rename, and reading it as one would stop the session from
    /// ever being given a better name after its first turn.
    #[tokio::test(flavor = "current_thread")]
    async fn saving_the_row_back_unchanged_is_not_a_rename() {
        let fixture = fixture_manager_with_acp_session("saved_back_title").await;
        let prompt = "n".repeat(100);

        fixture
            .manager
            .prompt(&fixture.owned_id, fixture.generation, test_prompt(&prompt))
            .await
            .expect("prompt starts");
        wait_until(|| stored_title(&fixture).is_some()).await;

        let provisional = stored_title(&fixture).expect("the prompt names the session");
        assert_eq!(provisional.chars().count(), SESSION_TITLE_CHAR_CAP);
        assert_eq!(stored_title_source(&fixture).as_deref(), Some("prompt"));

        fixture
            .manager
            .update_session_meta(UpdateAgentConversationSessionMetaRequest {
                owned_id: fixture.owned_id.clone(),
                model: None,
                effort: None,
                meta: renamed_meta(&provisional),
            })
            .expect("save the row back");

        assert_eq!(stored_title_source(&fixture).as_deref(), Some("prompt"));
        assert_eq!(stored_title(&fixture), Some(provisional));
        fixture
            .manager
            .close(&fixture.owned_id, fixture.generation)
            .await
            .unwrap();
        fs::remove_dir_all(fixture.root).unwrap();
    }

    #[tokio::test(flavor = "current_thread")]
    async fn a_user_title_is_never_overwritten() {
        let fixture = fixture_manager_with_acp_session("user_title").await;
        let calls: Arc<Mutex<usize>> = Default::default();
        fixture
            .manager
            .set_session_namer(counting_namer(&calls, "Fix rail titles"));
        let seen: Arc<Mutex<Vec<AgentConversationEvent>>> = Default::default();
        let sink = Arc::clone(&seen);
        fixture
            .manager
            .set_emitter(Arc::new(move |event| sink.lock().unwrap().push(event)));
        fixture
            .manager
            .update_session_meta(UpdateAgentConversationSessionMetaRequest {
                owned_id: fixture.owned_id.clone(),
                model: None,
                effort: None,
                meta: renamed_meta("Rail work"),
            })
            .expect("rename the session");

        fixture
            .manager
            .prompt(
                &fixture.owned_id,
                fixture.generation,
                test_prompt("make the rail titles readable"),
            )
            .await
            .expect("prompt starts");
        wait_until(|| completed_turns(&seen) == 1).await;

        assert_eq!(stored_title(&fixture).as_deref(), Some("Rail work"));
        assert_eq!(stored_title_source(&fixture).as_deref(), Some("user"));
        assert_eq!(*calls.lock().unwrap(), 0);
        fixture
            .manager
            .close(&fixture.owned_id, fixture.generation)
            .await
            .unwrap();
        fs::remove_dir_all(fixture.root).unwrap();
    }

    fn completed_turns(seen: &Arc<Mutex<Vec<AgentConversationEvent>>>) -> usize {
        seen.lock()
            .unwrap()
            .iter()
            .filter(|event| {
                matches!(
                    event.payload,
                    AgentConversationPayload::Turn {
                        state: super::super::protocol::TurnState::Completed,
                        ..
                    }
                )
            })
            .count()
    }

    struct FixtureManager {
        manager: AgentRuntimeManager,
        owned_id: String,
        generation: u64,
        root: std::path::PathBuf,
    }

    async fn fixture_manager_with_acp_session(fixture: &str) -> FixtureManager {
        fixture_manager_with_provider(fixture, AgentConversationProvider::Codex, None).await
    }

    async fn fixture_manager_with_provider(
        fixture: &str,
        provider: AgentConversationProvider,
        reasoning_effort: Option<&str>,
    ) -> FixtureManager {
        let root = temp_root();
        let log = root.join(format!("{fixture}.jsonl"));
        let manifest =
            super::super::providers::acp_client::tests::fixture_manifest_named(&log, fixture);
        let providers = ProviderRegistry::new([(provider, manifest)]).expect("fixture provider");
        let manager = AgentRuntimeManager::new(providers);
        let owned_id = format!("owned-{fixture}");
        let mut ensure_request = request(root.to_str().unwrap(), &owned_id, provider);
        ensure_request.reasoning_effort = reasoning_effort.map(str::to_string);
        let connection = manager.ensure_inner(ensure_request).expect("ensure").0;
        manager
            .activate(&owned_id, connection.generation)
            .await
            .expect("activate");
        FixtureManager {
            manager,
            owned_id,
            generation: connection.generation,
            root,
        }
    }

    fn test_prompt(text: &str) -> AgentPrompt {
        AgentPrompt {
            text: text.to_string(),
            images: Vec::new(),
            attachment_ids: Vec::new(),
        }
    }

    async fn wait_until(mut condition: impl FnMut() -> bool) {
        let deadline = Instant::now() + Duration::from_secs(2);
        while !condition() {
            assert!(Instant::now() < deadline, "condition timed out");
            tokio::time::sleep(Duration::from_millis(10)).await;
        }
    }

    fn payload_kind(payload: &AgentConversationPayload) -> &'static str {
        match payload {
            AgentConversationPayload::Connection { .. } => "connection",
            AgentConversationPayload::UserMessage { .. } => "userMessage",
            AgentConversationPayload::AssistantDelta { .. } => "assistantDelta",
            AgentConversationPayload::AssistantMessage { .. } => "assistantMessage",
            AgentConversationPayload::Tool { .. } => "tool",
            AgentConversationPayload::ChildUpdate { .. } => "childUpdate",
            AgentConversationPayload::Approval { .. } => "approval",
            AgentConversationPayload::UserInputRequested { .. } => "userInputRequested",
            AgentConversationPayload::UserInputResolved { .. } => "userInputResolved",
            AgentConversationPayload::Plan { .. } => "plan",
            AgentConversationPayload::Turn { .. } => "turn",
            AgentConversationPayload::AvailableCommandsUpdate { .. } => "availableCommandsUpdate",
            AgentConversationPayload::Usage { .. } => "usage",
            AgentConversationPayload::ContextCompaction { .. } => "contextCompaction",
            AgentConversationPayload::CheckoutChanged { .. } => "checkoutChanged",
            AgentConversationPayload::SessionCompleted { .. } => "sessionCompleted",
            AgentConversationPayload::TerminalProjection(_) => "terminalProjection",
            AgentConversationPayload::Error { .. } => "error",
        }
    }

    fn terminal_handoff(
        owned_id: &str,
        generation: u64,
        native_session_id: String,
    ) -> AgentConversationHandoffRequest {
        AgentConversationHandoffRequest {
            owned_id: owned_id.to_string(),
            generation,
            direction: AgentConversationHandoffDirection::StructuredToTerminal,
            mode: AgentConversationHandoffMode::SameSession,
            phase: AgentConversationHandoffPhase::Commit,
            expected_owner: Some(AgentWriterLeaseOwner::Structured),
            target_owned_id: None,
            native_session_id: Some(native_session_id.clone()),
            pty_session_id: Some("fixture-pty".to_string()),
            history_boundary: AgentConversationHistoryBoundary {
                native_session_id: Some(native_session_id),
                first_sequence: 0,
                last_sequence: 0,
                reconciled_sequence: Some(0),
            },
            process_tree: AgentConversationProcessTreeAssertion {
                checked: true,
                tui_live: true,
                tui_released: false,
                writer_count: 1,
                user_pty_count: 1,
                sidecar_count: 1,
                pty_session_id: Some("fixture-pty".to_string()),
            },
        }
    }

    fn process_is_alive(pid: u32) -> bool {
        unsafe { libc::kill(pid as i32, 0) == 0 }
    }

    #[test]
    fn session_updates_map_to_conversation_payloads() {
        let chunk = json!({ "sessionId": "s", "update": {
            "sessionUpdate": "agent_message_chunk",
            "content": { "type": "text", "text": "Hi" }, "messageId": "m1" } });
        assert_eq!(
            payload_from_session_update_for_turn(&chunk, None),
            Some(AgentConversationPayload::AssistantDelta {
                item_id: "m1".into(),
                delta: "Hi".into(),
            })
        );
        let replayed_chunk = json!({ "sessionId": "s", "update": {
            "sessionUpdate": "agent_message_chunk",
            "content": { "type": "text", "text": "Restored" },
            "messageId": "m-replay",
            "_meta": { "replay": true }
        } });
        assert_eq!(
            payload_from_session_update_for_turn(&replayed_chunk, None),
            Some(AgentConversationPayload::AssistantMessage {
                item_id: "m-replay".into(),
                text: "Restored".into(),
                completed: true,
                blocks: None,
            })
        );
        let tool = json!({ "sessionId": "s", "update": {
            "sessionUpdate": "tool_call", "toolCallId": "t1", "title": "Read file",
            "status": "in_progress" } });
        match payload_from_session_update_for_turn(&tool, None) {
            Some(AgentConversationPayload::Tool {
                item_id,
                name,
                state,
                ..
            }) => {
                assert_eq!(item_id, "t1");
                assert_eq!(name, "Read file");
                assert_eq!(state, super::super::protocol::ToolState::Started);
            }
            other => panic!("expected Tool, got {other:?}"),
        }
        let child_started = json!({ "sessionId": "s", "update": {
            "sessionUpdate": "tool_call", "toolCallId": "child-tool-1", "kind": "subagent",
            "childSessionId": "child-session-1", "parentToolCallId": "parent-tool-1",
            "label": "Review the change" } });
        assert_eq!(
            payload_from_session_update_for_turn(&child_started, None),
            Some(AgentConversationPayload::ChildUpdate {
                child_id: "child-session-1".into(),
                parent_tool_call_id: "parent-tool-1".into(),
                parent_id: None,
                transcript_id: None,
                label: Some("Review the change".into()),
                state: "running".into(),
                latest_activity: Some("Review the change".into()),
            })
        );
        let child_finished = json!({ "sessionId": "s", "update": {
            "sessionUpdate": "tool_call_update", "toolCallId": "child-tool-1", "kind": "subagent",
            "status": "completed", "childSessionId": "child-session-1",
            "parentToolCallId": "parent-tool-1",
            "content": [{ "type": "text", "text": "Review complete" }] } });
        assert_eq!(
            payload_from_session_update_for_turn(&child_finished, None),
            Some(AgentConversationPayload::ChildUpdate {
                child_id: "child-session-1".into(),
                parent_tool_call_id: "parent-tool-1".into(),
                parent_id: None,
                transcript_id: None,
                label: None,
                state: "finished".into(),
                latest_activity: Some("Review complete".into()),
            })
        );
        let plan = json!({ "sessionId": "s", "update": {
            "sessionUpdate": "plan", "entries": [
                { "content": "step one", "status": "pending" }
            ] } });
        match payload_from_session_update_for_turn(&plan, None) {
            Some(AgentConversationPayload::Plan { items }) => {
                assert_eq!(items.len(), 1);
                assert_eq!(items[0].text, "step one");
            }
            other => panic!("expected Plan, got {other:?}"),
        }

        let user = json!({ "update": {
            "sessionUpdate": "user_message_chunk",
            "content": { "type": "text", "text": "Question" },
            "turnId": "turn-2"
        } });
        assert_eq!(
            payload_from_session_update_for_turn(&user, None),
            Some(AgentConversationPayload::UserMessage {
                item_id: "user-turn-2".into(),
                text: "Question".into(),
                completed: false,
                attachment_ids: Vec::new(),
            })
        );
        let replayed_user = json!({ "update": {
            "sessionUpdate": "user_message_chunk",
            "content": { "type": "text", "text": "Restored question" },
            "messageId": "user-replay",
            "turnId": "turn-2",
            "_meta": { "replay": true }
        } });
        assert_eq!(
            payload_from_session_update_for_turn(&replayed_user, None),
            Some(AgentConversationPayload::UserMessage {
                item_id: "user-replay".into(),
                text: "Restored question".into(),
                completed: true,
                attachment_ids: Vec::new(),
            })
        );

        for (status, expected) in [
            ("in_progress", ToolState::Updated),
            ("completed", ToolState::Completed),
            ("failed", ToolState::Failed),
        ] {
            let update = json!({ "update": {
                "sessionUpdate": "tool_call_update",
                "toolCallId": "t1",
                "title": "Read file",
                "status": status,
                "content": [{ "type": "text", "text": "detail" }],
                "locations": [{ "path": "src/main.rs" }]
            } });
            match payload_from_session_update_for_turn(&update, None) {
                Some(AgentConversationPayload::Tool {
                    state,
                    summary,
                    output,
                    path,
                    ..
                }) => {
                    assert_eq!(state, expected);
                    assert_eq!(summary, None);
                    assert_eq!(output.as_deref(), Some("detail"));
                    assert_eq!(path.as_deref(), Some("src/main.rs"));
                }
                other => panic!("expected Tool update, got {other:?}"),
            }
        }

        assert_eq!(
            payload_from_session_update_for_turn(
                &json!({ "update": {
                "sessionUpdate": "agent_thought_chunk",
                "content": { "type": "text", "text": "private" }
            } }),
                None
            ),
            None
        );
        let commands = json!({ "update": {
            "sessionUpdate": "available_commands_update",
            "availableCommands": [
                { "name": "review", "description": "Review the change", "input": { "hint": "path" } },
                { "id": "status", "label": "Status" }
            ]
        }});
        assert_eq!(
            payload_from_session_update_for_turn(&commands, None),
            Some(AgentConversationPayload::AvailableCommandsUpdate {
                available_commands: vec![
                    AgentCommandDescriptor {
                        id: "review".into(),
                        label: "review".into(),
                        description: Some("Review the change".into()),
                        input_hint: Some("path".into()),
                        provider_metadata: None,
                    },
                    AgentCommandDescriptor {
                        id: "status".into(),
                        label: "Status".into(),
                        description: None,
                        input_hint: None,
                        provider_metadata: None,
                    }
                ]
            })
        );
        // How full the window is, not how much the session has spent getting
        // here. A report carrying both used to hand over the running total,
        // which passes the window early in a day's work and pins the meter at
        // full for everything after it.
        let session_totals = json!({ "update": {
            "sessionUpdate": "token_count",
            "info": {
                "model_context_window": 237_500,
                "total_token_usage": { "total_tokens": 15_908_466 },
                "last_token_usage": { "total_tokens": 41_233 }
            }
        }});
        assert_eq!(
            payload_from_session_update_for_turn(&session_totals, None),
            Some(AgentConversationPayload::Usage {
                input_tokens: None,
                output_tokens: None,
                used_tokens: Some(41_233),
                context_window: Some(237_500),
                total_tokens: Some(15_908_466),
            })
        );

        let usage = json!({ "update": {
            "sessionUpdate": "usage_update", "used": 120, "size": 4096,
            "inputTokens": 100, "outputTokens": 20
        }});
        assert_eq!(
            payload_from_session_update_for_turn(&usage, None),
            Some(AgentConversationPayload::Usage {
                input_tokens: Some(100),
                output_tokens: Some(20),
                used_tokens: Some(120),
                context_window: Some(4096),
                total_tokens: None,
            })
        );
        // The bridge reports a compaction outright, because nothing else in a
        // Codex session shows one: no occupancy is reported at all, so the drop
        // that gives a compaction away elsewhere cannot be seen here.
        let compaction = json!({ "update": {
            "sessionUpdate": "context_compaction", "trigger": "auto",
            "preTokens": 351_238, "postTokens": 22_202
        }});
        assert_eq!(
            payload_from_session_update_for_turn(&compaction, None),
            Some(AgentConversationPayload::ContextCompaction {
                trigger: Some("auto".into()),
                pre_tokens: Some(351_238),
                post_tokens: Some(22_202),
            })
        );
        assert_eq!(
            payload_from_session_update_for_turn(
                &json!({ "update": { "sessionUpdate": "context_compaction" } }),
                None
            ),
            Some(AgentConversationPayload::ContextCompaction {
                trigger: None,
                pre_tokens: None,
                post_tokens: None,
            })
        );
        assert_eq!(
            payload_from_session_update_for_turn(
                &json!({ "update": {
                "sessionUpdate": "future_update"
            } }),
                None
            ),
            None
        );
    }

    #[tokio::test(flavor = "current_thread")]
    async fn claude_native_children_keep_routing_and_transcript_identities_separate() {
        let fixture = fixture_manager_with_provider(
            "native_claude_children",
            AgentConversationProvider::Claude,
            None,
        )
        .await;
        let (transport, replay_id) = {
            let mut sessions = fixture.manager.sessions.lock().unwrap();
            let session = sessions.get_mut(&fixture.owned_id).unwrap();
            let root_session_id = session.native_session_id.clone().unwrap();
            let replay_id = format!("{root_session_id}:replay-subagent:tool-1");
            let replayed = json!({
                "sessionId": root_session_id,
                "update": {
                    "sessionUpdate": "subagent_spawned",
                    "subagentSessionId": replay_id,
                    "name": "Imported child"
                }
            });
            assert!(claude_native_child_payload(session, &replayed).is_none());
            assert!(session.claude_children.is_empty());
            assert!(is_known_claude_child_inbound(
                session,
                &json!({
                    "sessionId": replay_id,
                    "update": { "sessionUpdate": "agent_message_chunk" }
                })
            ));
            let spawned = json!({
                "sessionId": root_session_id,
                "update": {
                    "sessionUpdate": "subagent_spawned",
                    "subagentSessionId": "agent-a",
                    "name": "Reviewer",
                    "task": "Review the patch"
                }
            });
            assert_eq!(
                claude_native_child_payload(session, &spawned),
                Some(AgentConversationPayload::ChildUpdate {
                    child_id: "agent-a".into(),
                    parent_tool_call_id: root_session_id.clone(),
                    parent_id: Some(root_session_id.clone()),
                    transcript_id: Some("agent-a".into()),
                    label: Some("Reviewer".into()),
                    state: "running".into(),
                    latest_activity: Some("Review the patch".into()),
                })
            );
            let nested = json!({
                "sessionId": "agent-a",
                "update": {
                    "sessionUpdate": "subagent_spawned",
                    "subagentSessionId": "agent-b:generation:2",
                    "name": "Researcher"
                }
            });
            let nested_payload = claude_native_child_payload(session, &nested).unwrap();
            assert!(matches!(
                nested_payload,
                AgentConversationPayload::ChildUpdate {
                    child_id,
                    parent_id: Some(parent_id),
                    transcript_id: Some(transcript_id),
                    ..
                } if child_id == "agent-b:generation:2"
                    && parent_id == "agent-a"
                    && transcript_id == "agent-b"
            ));
            assert_eq!(session.background_work.len(), 2);
            let child_content = json!({
                "sessionId": "agent-b:generation:2",
                "update": { "sessionUpdate": "agent_message_chunk", "content": { "type": "text", "text": "private child content" } }
            });
            assert!(is_known_claude_child_inbound(session, &child_content));
            assert!(claude_native_child_payload(session, &child_content).is_none());
            for (parent, child) in [
                ("agent-a", "agent-b:generation:2"),
                (root_session_id.as_str(), "agent-a"),
            ] {
                let completed = json!({
                    "sessionId": parent,
                    "update": {
                        "sessionUpdate": "subagent_state_update",
                        "subagentSessionId": child,
                        "state": "completed"
                    }
                });
                assert!(claude_native_child_payload(session, &completed).is_some());
            }
            assert!(session.background_work.is_empty());
            let still_running = json!({
                "sessionId": root_session_id,
                "update": {
                    "sessionUpdate": "subagent_spawned",
                    "subagentSessionId": "agent-c",
                    "name": "Late reviewer"
                }
            });
            assert!(claude_native_child_payload(session, &still_running).is_some());
            disconnect_claude_children(session, &fixture.manager.emitter);
            assert_eq!(session.claude_children["agent-c"].state, "disconnected");
            assert!(session.background_work.is_empty());
            (session.transport.clone().unwrap(), replay_id)
        };

        let sessions = fixture.manager.sessions.lock().unwrap();
        assert_eq!(
            routed_session_for_inbound(
                &sessions,
                &transport,
                &AcpInbound::SessionUpdate(json!({
                    "sessionId": "agent-b:generation:2",
                    "update": { "sessionUpdate": "agent_message_chunk" }
                }))
            ),
            Some((fixture.owned_id.clone(), fixture.generation))
        );
        assert_eq!(
            routed_session_for_inbound(
                &sessions,
                &transport,
                &AcpInbound::SessionUpdate(json!({
                    "sessionId": replay_id,
                    "update": { "sessionUpdate": "agent_message_chunk" }
                }))
            ),
            Some((fixture.owned_id.clone(), fixture.generation))
        );
        drop(sessions);
        fixture
            .manager
            .close(&fixture.owned_id, fixture.generation)
            .await
            .unwrap();
        fs::remove_dir_all(fixture.root).unwrap();
    }

    #[tokio::test(flavor = "current_thread")]
    async fn child_permission_uses_original_wire_id_after_parent_completion() {
        let fixture = fixture_manager_with_provider(
            "child_permission_after_parent",
            AgentConversationProvider::Claude,
            None,
        )
        .await;
        let seen: Arc<Mutex<Vec<AgentConversationEvent>>> = Default::default();
        let sink = Arc::clone(&seen);
        fixture
            .manager
            .set_emitter(Arc::new(move |event| sink.lock().unwrap().push(event)));
        fixture
            .manager
            .prompt(&fixture.owned_id, fixture.generation, test_prompt("hello"))
            .await
            .expect("prompt starts");
        wait_until(|| {
            let events = seen.lock().unwrap();
            events.iter().any(|event| {
                matches!(
                    event.payload,
                    AgentConversationPayload::Turn {
                        state: super::super::protocol::TurnState::Completed,
                        ..
                    }
                )
            }) && events.iter().any(|event| {
                matches!(
                    event.payload,
                    AgentConversationPayload::Approval {
                        state: ApprovalState::Requested,
                        ..
                    }
                )
            })
        })
        .await;
        {
            let events = seen.lock().unwrap();
            let requested_sequence = events
                .iter()
                .find(|event| {
                    matches!(
                        event.payload,
                        AgentConversationPayload::Approval {
                            state: ApprovalState::Requested,
                            ..
                        }
                    )
                })
                .unwrap()
                .sequence;
            let completed_sequence = events
                .iter()
                .find(|event| {
                    matches!(
                        event.payload,
                        AgentConversationPayload::Turn {
                            state: super::super::protocol::TurnState::Completed,
                            ..
                        }
                    )
                })
                .unwrap()
                .sequence;
            assert!(requested_sequence < completed_sequence);
        }
        let request_id = seen
            .lock()
            .unwrap()
            .iter()
            .find_map(|event| match &event.payload {
                AgentConversationPayload::Approval {
                    request_id,
                    state: ApprovalState::Requested,
                    ..
                } => Some(request_id.clone()),
                _ => None,
            })
            .unwrap();
        assert!(fixture
            .manager
            .sessions
            .lock()
            .unwrap()[&fixture.owned_id]
            .active_turn_id
            .is_none());
        fixture
            .manager
            .respond_legacy_approval(
                &fixture.owned_id,
                fixture.generation,
                request_id,
                super::super::protocol::ApprovalDecision::Accept,
            )
            .await
            .expect("child permission response");
        wait_until(|| {
            fs::read_to_string(fixture.root.join("child_permission_after_parent.jsonl"))
                .is_ok_and(|log| {
                    log.contains(r#""id":77"#)
                        && log.contains(r#""outcome":{"outcome":"selected","optionId":"allow""#)
                })
        })
        .await;

        fixture
            .manager
            .close(&fixture.owned_id, fixture.generation)
            .await
            .unwrap();
        fs::remove_dir_all(fixture.root).unwrap();
    }

    #[tokio::test(flavor = "current_thread")]
    async fn child_elicitation_survives_parent_completion_and_answers_original_wire() {
        let (fixture, seen) =
            feed_fixture("child_input_after_parent", AgentConversationProvider::Claude).await;
        fixture
            .manager
            .prompt(&fixture.owned_id, fixture.generation, test_prompt("hello"))
            .await
            .expect("prompt starts");
        wait_until(|| {
            let sessions = fixture.manager.sessions.lock().unwrap();
            let session = &sessions[&fixture.owned_id];
            session.active_turn_id.is_none() && session.user_input_requests.len() == 1
        })
        .await;
        let request_id = {
            let sessions = fixture.manager.sessions.lock().unwrap();
            let session = &sessions[&fixture.owned_id];
            let (request_id, pending) = session.user_input_requests.iter().next().unwrap();
            assert_eq!(pending.wire_id, json!(77));
            assert_eq!(pending.response_shape, UserInputResponseShape::Elicitation);
            assert!(pending.child_scoped);
            request_id.clone()
        };

        fixture
            .manager
            .respond_user_input(AgentUserInputResponse {
                identity: AgentRequestIdentity {
                    owned_id: fixture.owned_id.clone(),
                    generation: fixture.generation,
                    request_id,
                    turn_id: None,
                    item_id: None,
                },
                action: AgentUserInputAction::Accept,
                content: BTreeMap::from([("question_0_custom".into(), json!("Canary"))]),
            })
            .await
            .expect("child elicitation response");
        wait_until(|| {
            seen.lock().unwrap().iter().any(|event| {
                matches!(event.payload, AgentConversationPayload::UserInputResolved { .. })
            })
        })
        .await;
        {
            let sessions = fixture.manager.sessions.lock().unwrap();
            let session = &sessions[&fixture.owned_id];
            assert!(session.user_input_requests.is_empty());
            assert!(!session.background_work.is_empty(), "the sub-agent still runs");
            // The child keeps Working and Stop available after the parent ends.
            assert_eq!(session.state, AgentRuntimeState::Working);
        }
        let log_path = fixture.root.join("child_input_after_parent.jsonl");
        wait_until(|| fs::read_to_string(&log_path).is_ok_and(|log| log.contains(r#""id":77"#))).await;
        let log = fs::read_to_string(&log_path).unwrap();
        assert!(log.contains(r#""action":"accept""#));
        assert!(log.contains(r#""question_0_custom":"Canary""#));

        fixture
            .manager
            .close(&fixture.owned_id, fixture.generation)
            .await
            .unwrap();
        fs::remove_dir_all(fixture.root).unwrap();
    }

    #[tokio::test(flavor = "current_thread")]
    async fn failed_input_response_restores_exact_pending_request_and_legacy_shape() {
        let fixture = fixture_manager_with_acp_session("legacy_input").await;
        fixture
            .manager
            .prompt(&fixture.owned_id, fixture.generation, test_prompt("hello"))
            .await
            .expect("prompt starts");
        wait_until(|| {
            fixture.manager.sessions.lock().unwrap()[&fixture.owned_id]
                .user_input_requests
                .len()
                == 1
        })
        .await;
        let (request_id, wire_id, event, transport) = {
            let sessions = fixture.manager.sessions.lock().unwrap();
            let session = &sessions[&fixture.owned_id];
            let (request_id, pending) = session.user_input_requests.iter().next().unwrap();
            assert_eq!(pending.response_shape, UserInputResponseShape::Legacy);
            (
                request_id.clone(),
                pending.wire_id.clone(),
                pending.event.clone(),
                session.transport.clone().unwrap(),
            )
        };
        let response = || AgentUserInputResponse {
            identity: AgentRequestIdentity {
                owned_id: fixture.owned_id.clone(),
                generation: fixture.generation,
                request_id: request_id.clone(),
                turn_id: None,
                item_id: None,
            },
            action: AgentUserInputAction::Accept,
            content: BTreeMap::from([("channel".into(), json!("stable"))]),
        };
        transport.fail_next_write_for_test();
        assert!(fixture.manager.respond_user_input(response()).await.is_err());
        {
            let sessions = fixture.manager.sessions.lock().unwrap();
            let restored = &sessions[&fixture.owned_id].user_input_requests[&request_id];
            assert_eq!(restored.wire_id, wire_id);
            assert_eq!(restored.response_shape, UserInputResponseShape::Legacy);
            assert_eq!(restored.event, event);
        }

        fixture
            .manager
            .respond_user_input(response())
            .await
            .expect("retried legacy response");
        wait_until(|| {
            fs::read_to_string(fixture.root.join("legacy_input.jsonl"))
                .is_ok_and(|log| log.contains(r#""id":78"#) && log.contains(r#""cancelled":false"#)
                    && log.contains(r#""values":{"channel":"stable"}"#))
        })
        .await;
        fixture
            .manager
            .close(&fixture.owned_id, fixture.generation)
            .await
            .unwrap();
        fs::remove_dir_all(fixture.root).unwrap();
    }

    #[tokio::test(flavor = "current_thread")]
    async fn transport_close_disconnects_running_claude_children() {
        let fixture = fixture_manager_with_provider(
            "child_then_dies",
            AgentConversationProvider::Claude,
            None,
        )
        .await;
        let seen: Arc<Mutex<Vec<AgentConversationEvent>>> = Default::default();
        let sink = Arc::clone(&seen);
        fixture
            .manager
            .set_emitter(Arc::new(move |event| sink.lock().unwrap().push(event)));
        fixture
            .manager
            .prompt(&fixture.owned_id, fixture.generation, test_prompt("hello"))
            .await
            .expect("prompt starts");
        wait_until(|| {
            seen.lock().unwrap().iter().any(|event| {
                matches!(
                    &event.payload,
                    AgentConversationPayload::ChildUpdate {
                        child_id,
                        state,
                        ..
                    } if child_id == "child-agent" && state == "disconnected"
                )
            })
        })
        .await;
        {
            let sessions = fixture.manager.sessions.lock().unwrap();
            let session = &sessions[&fixture.owned_id];
            assert_eq!(session.claude_children["child-agent"].state, "disconnected");
            assert!(session.background_work.is_empty());
        }

        fixture
            .manager
            .close(&fixture.owned_id, fixture.generation)
            .await
            .unwrap();
        fs::remove_dir_all(fixture.root).unwrap();
    }

    // Claude adapter markers for background sub-agents (TSK-1394). The
    // `*_feed` fixture writes every line the test appends to its feed file to
    // the client, so these drive the real update loop.
    fn feed(fixture: &FixtureManager, frames: &[Value]) {
        use std::io::Write as _;
        let name = fixture.owned_id.trim_start_matches("owned-");
        let mut file = fs::OpenOptions::new()
            .append(true)
            .open(fixture.root.join(format!("{name}.jsonl.feed")))
            .unwrap();
        for frame in frames {
            writeln!(file, "{frame}").unwrap();
        }
    }

    fn root_update(update: Value) -> Value {
        json!({"jsonrpc": "2.0", "method": "session/update",
            "params": {"sessionId": "new-session", "update": update}})
    }

    fn claude_state(state: &str) -> Value {
        root_update(json!({"sessionUpdate": "session_info_update",
            "_meta": {"jetbrains": {"air": {"sessionState": state}}}}))
    }

    fn reply_chunk(message_id: &str, text: &str, out_of_turn: bool) -> Value {
        let mut update = json!({"sessionUpdate": "agent_message_chunk", "messageId": message_id,
            "content": {"type": "text", "text": text}});
        if out_of_turn {
            update["_meta"] = json!({"jetbrains": {"air": {"outOfTurn": true}}});
        }
        root_update(update)
    }

    fn child_spawned(child: &str) -> Value {
        root_update(json!({"sessionUpdate": "subagent_spawned",
            "subagentSessionId": child, "name": "Reviewer"}))
    }

    fn child_state(child: &str, state: &str) -> Value {
        root_update(json!({"sessionUpdate": "subagent_state_update",
            "subagentSessionId": child, "state": state}))
    }

    async fn prompt_result(fixture: &FixtureManager) -> Value {
        let name = fixture.owned_id.trim_start_matches("owned-");
        let log = fixture.root.join(format!("{name}.jsonl"));
        wait_until(|| {
            fs::read_to_string(&log).is_ok_and(|log| log.contains(r#""method":"session/prompt""#))
        })
        .await;
        let prompt: Value = fs::read_to_string(&log)
            .unwrap()
            .lines()
            .filter(|line| line.contains(r#""method":"session/prompt""#))
            .last()
            .map(|line| serde_json::from_str(line).unwrap())
            .unwrap();
        json!({"jsonrpc": "2.0", "id": prompt["id"],
            "result": {"turnId": prompt["params"]["turnId"], "stopReason": "end_turn"}})
    }

    type SeenEvents = Arc<Mutex<Vec<AgentConversationEvent>>>;

    async fn feed_fixture(
        name: &str,
        provider: AgentConversationProvider,
    ) -> (FixtureManager, SeenEvents) {
        let fixture = fixture_manager_with_provider(name, provider, None).await;
        let seen: SeenEvents = Default::default();
        let sink = Arc::clone(&seen);
        fixture
            .manager
            .set_emitter(Arc::new(move |event| sink.lock().unwrap().push(event)));
        (fixture, seen)
    }

    /// The journal turn of each streamed piece of `text`.
    fn delta_turns(seen: &SeenEvents, text: &str) -> Vec<Option<String>> {
        seen.lock()
            .unwrap()
            .iter()
            .filter(|event| {
                matches!(&event.payload, AgentConversationPayload::AssistantDelta { delta, .. } if delta == text)
            })
            .map(|event| event.turn_id.clone())
            .collect()
    }

    /// The journal turn of the finished reply whose text is `text`.
    fn finished_turn(seen: &SeenEvents, text: &str) -> Option<Option<String>> {
        seen.lock()
            .unwrap()
            .iter()
            .find(|event| {
                matches!(&event.payload, AgentConversationPayload::AssistantMessage { text: t, completed: true, .. } if t == text)
            })
            .map(|event| event.turn_id.clone())
    }

    fn autonomous_turn(fixture: &FixtureManager) -> Option<String> {
        fixture.manager.sessions.lock().unwrap()[&fixture.owned_id]
            .autonomous_turn_id
            .clone()
    }

    fn turn_states(seen: &SeenEvents, turn_id: &str) -> Vec<super::super::protocol::TurnState> {
        seen.lock().unwrap().iter().filter_map(|event| match &event.payload {
            AgentConversationPayload::Turn { turn_id: id, state } if id == turn_id => Some(*state),
            _ => None,
        }).collect::<Vec<_>>()
    }

    fn claude_running(fixture: &FixtureManager) -> bool {
        fixture.manager.sessions.lock().unwrap()[&fixture.owned_id]
            .background_work
            .contains_key("claude-session:running")
    }

    /// The runtime is gone. The suspended session then leaves memory, so its
    /// fields are not read after this.
    fn suspended(fixture: &FixtureManager) -> bool {
        fixture.manager.resource_roots().is_empty()
    }

    /// Gives the update loop time to handle what was fed.
    async fn settle() {
        tokio::time::sleep(Duration::from_millis(150)).await;
    }

    async fn finish(fixture: FixtureManager) {
        fixture
            .manager
            .close(&fixture.owned_id, fixture.generation)
            .await
            .unwrap();
        fs::remove_dir_all(fixture.root).unwrap();
    }

    #[tokio::test(flavor = "current_thread")]
    async fn claude_session_running_blocks_suspend_until_idle() {
        let (fixture, _) = feed_fixture("suspend_claude_feed", AgentConversationProvider::Claude).await;
        feed(&fixture, &[claude_state("running")]);
        wait_until(|| claude_running(&fixture)).await;
        assert!(!fixture
            .manager
            .suspend_if_quiescent(&fixture.owned_id, fixture.generation)
            .await
            .unwrap());
        assert!(!suspended(&fixture));
        feed(&fixture, &[claude_state("idle")]);
        wait_until(|| suspended(&fixture)).await;
        finish(fixture).await;
    }

    #[tokio::test(flavor = "current_thread")]
    async fn a_running_codex_sub_agent_blocks_suspend() {
        let (fixture, _) = feed_fixture("suspend_codex_child", AgentConversationProvider::Codex).await;
        fixture.manager.sessions.lock().unwrap().get_mut(&fixture.owned_id).unwrap()
            .codex_children.insert("child-1".into(), CodexChildRollout {
                child_id: "child-1".into(), label: "probe".into(),
                state: "running".into(), latest_activity: "Running".into(),
            });
        assert!(!fixture
            .manager
            .suspend_if_quiescent(&fixture.owned_id, fixture.generation)
            .await
            .unwrap());
        assert!(!suspended(&fixture));
        finish(fixture).await;
    }

    #[tokio::test(flavor = "current_thread")]
    async fn child_terminal_while_cli_running_does_not_suspend() {
        let (fixture, _) = feed_fixture("suspend_claude_feed", AgentConversationProvider::Claude).await;
        feed(
            &fixture,
            &[
                claude_state("running"),
                child_spawned("agent-a"),
                child_state("agent-a", "completed"),
            ],
        );
        wait_until(|| {
            fixture.manager.sessions.lock().unwrap()[&fixture.owned_id]
                .claude_children
                .get("agent-a")
                .is_some_and(|child| child.state == "finished")
        })
        .await;
        settle().await;
        assert!(!suspended(&fixture), "Claude Code still runs the follow-up");
        feed(&fixture, &[claude_state("idle")]);
        wait_until(|| suspended(&fixture)).await;
        finish(fixture).await;
    }

    #[tokio::test(flavor = "current_thread")]
    async fn out_of_turn_tagged_update_journals_under_autonomous_turn() {
        let (fixture, seen) = feed_fixture("suspend_claude_feed", AgentConversationProvider::Claude).await;
        feed(
            &fixture,
            &[
                child_spawned("keeper"),
                claude_state("running"),
            ],
        );
        wait_until(|| completed_turns(&seen) == 0 && autonomous_turn(&fixture).is_some()).await;
        assert_eq!(fixture.manager.sessions.lock().unwrap()[&fixture.owned_id].state,
            AgentRuntimeState::Working);
        feed(
            &fixture,
            &[
                reply_chunk("m-auto", "late summary", true),
                claude_state("idle"),
            ],
        );
        wait_until(|| completed_turns(&seen) == 1).await;
        assert_eq!(fixture.manager.sessions.lock().unwrap()[&fixture.owned_id].state,
            AgentRuntimeState::Working);
        let turns = delta_turns(&seen, "late summary");
        assert_eq!(turns.len(), 1);
        let autonomous = turns[0].clone().expect("the summary has a journal turn");
        assert!(autonomous.starts_with("turn-"));
        assert_eq!(finished_turn(&seen, "late summary"), Some(Some(autonomous.clone())));
        let seen = seen.lock().unwrap();
        let order = seen.iter().filter_map(|event| match &event.payload {
            AgentConversationPayload::Turn { turn_id, state: super::super::protocol::TurnState::Started }
                if turn_id == &autonomous => Some("started"),
            AgentConversationPayload::AssistantDelta { delta, .. } if delta == "late summary" => Some("delta"),
            AgentConversationPayload::AssistantMessage { text, .. } if text == "late summary" => Some("message"),
            AgentConversationPayload::Turn { turn_id, state: super::super::protocol::TurnState::Completed }
                if turn_id == &autonomous => Some("completed"),
            _ => None,
        }).collect::<Vec<_>>();
        assert_eq!(order, vec!["started", "delta", "message", "completed"]);
        drop(seen);
        finish(fixture).await;
    }

    #[tokio::test(flavor = "current_thread")]
    async fn autonomous_update_during_active_user_turn_keeps_both_turns_separate() {
        let (fixture, seen) = feed_fixture("suspend_claude_feed", AgentConversationProvider::Claude).await;
        fixture
            .manager
            .prompt(&fixture.owned_id, fixture.generation, test_prompt("hello"))
            .await
            .expect("prompt starts");
        let user_turn = fixture.manager.sessions.lock().unwrap()[&fixture.owned_id]
            .active_turn_id
            .clone()
            .expect("the prompt opens a turn");
        let result = prompt_result(&fixture).await;
        feed(
            &fixture,
            &[
                claude_state("running"),
                reply_chunk("m-user", "answer", false),
                reply_chunk("m-auto", "summary", true),
                result,
            ],
        );
        wait_until(|| completed_turns(&seen) == 1).await;
        assert_eq!(delta_turns(&seen, "answer"), vec![Some(user_turn.clone())]);
        let autonomous = delta_turns(&seen, "summary");
        assert_eq!(autonomous.len(), 1);
        let autonomous = autonomous[0].clone().expect("the summary has a journal turn");
        assert_ne!(autonomous, user_turn);
        assert_eq!(finished_turn(&seen, "answer"), Some(Some(user_turn)));
        assert_eq!(finished_turn(&seen, "summary"), Some(Some(autonomous.clone())));
        feed(&fixture, &[claude_state("running"), claude_state("idle")]);
        wait_until(|| turn_states(&seen, &autonomous).len() == 2).await;
        assert_eq!(turn_states(&seen, &autonomous), vec![super::super::protocol::TurnState::Started, super::super::protocol::TurnState::Completed]);
        finish(fixture).await;
    }

    #[tokio::test(flavor = "current_thread")]
    async fn idle_closes_autonomous_turn_and_next_bracket_gets_new_id() {
        let (fixture, seen) = feed_fixture("suspend_claude_feed", AgentConversationProvider::Claude).await;
        // A child still running keeps the runtime up across the idle.
        feed(
            &fixture,
            &[
                child_spawned("keeper"),
                claude_state("running"),
                reply_chunk("m-1", "first result", true),
                reply_chunk("m-2", "second result", true),
                claude_state("idle"),
                claude_state("running"),
                reply_chunk("m-3", "next bracket", true),
            ],
        );
        wait_until(|| delta_turns(&seen, "next bracket").len() == 1).await;
        let first = delta_turns(&seen, "first result")[0].clone().unwrap();
        assert_eq!(delta_turns(&seen, "second result"), vec![Some(first.clone())]);
        assert_eq!(finished_turn(&seen, "second result"), Some(Some(first.clone())));
        let next = delta_turns(&seen, "next bracket")[0].clone().unwrap();
        assert_ne!(next, first);
        assert_eq!(autonomous_turn(&fixture), Some(next));
        finish(fixture).await;
    }

    #[tokio::test(flavor = "current_thread")]
    async fn send_completes_autonomous_turn() {
        let (fixture, seen) = feed_fixture("suspend_claude_feed", AgentConversationProvider::Claude).await;
        feed(
            &fixture,
            &[claude_state("running"), reply_chunk("m-auto", "summary", true)],
        );
        wait_until(|| delta_turns(&seen, "summary").len() == 1).await;
        let autonomous = delta_turns(&seen, "summary")[0].clone().unwrap();
        fixture
            .manager
            .prompt(&fixture.owned_id, fixture.generation, test_prompt("next"))
            .await
            .expect("send while Claude works on its own");
        let user_turn = fixture.manager.sessions.lock().unwrap()[&fixture.owned_id]
            .active_turn_id
            .clone()
            .expect("the prompt opens its own turn");
        assert_ne!(user_turn, autonomous);
        assert_eq!(autonomous_turn(&fixture), None);
        assert_eq!(turn_states(&seen, &autonomous), vec![super::super::protocol::TurnState::Started, super::super::protocol::TurnState::Completed]);
        finish(fixture).await;
    }

    #[tokio::test(flavor = "current_thread")]
    async fn codex_out_of_turn_update_still_dropped() {
        let (fixture, seen) = feed_fixture("suspend_codex_feed", AgentConversationProvider::Codex).await;
        feed(
            &fixture,
            &[claude_state("running"), reply_chunk("m-auto", "summary", true)],
        );
        settle().await;
        assert!(delta_turns(&seen, "summary").is_empty());
        assert!(!claude_running(&fixture));
        assert_eq!(autonomous_turn(&fixture), None);
        finish(fixture).await;
    }

    #[tokio::test(flavor = "current_thread")]
    async fn two_children_and_duplicate_terminal_suspend_only_at_idle() {
        let (fixture, _) = feed_fixture("suspend_claude_feed", AgentConversationProvider::Claude).await;
        feed(
            &fixture,
            &[
                claude_state("running"),
                child_spawned("agent-a"),
                child_spawned("agent-b"),
                child_state("agent-a", "completed"),
                child_state("agent-a", "completed"),
                child_state("agent-b", "completed"),
            ],
        );
        wait_until(|| {
            fixture.manager.sessions.lock().unwrap()[&fixture.owned_id]
                .claude_children
                .get("agent-b")
                .is_some_and(|child| child.state == "finished")
        })
        .await;
        settle().await;
        assert!(!suspended(&fixture));
        feed(&fixture, &[claude_state("idle")]);
        wait_until(|| suspended(&fixture)).await;
        finish(fixture).await;
    }

    #[tokio::test(flavor = "current_thread")]
    async fn cancelled_child_then_idle_suspends() {
        let (fixture, _) = feed_fixture("suspend_claude_feed", AgentConversationProvider::Claude).await;
        feed(
            &fixture,
            &[
                claude_state("running"),
                child_spawned("agent-a"),
                child_state("agent-a", "cancelled"),
            ],
        );
        wait_until(|| {
            fixture.manager.sessions.lock().unwrap()[&fixture.owned_id]
                .claude_children
                .get("agent-a")
                .is_some_and(|child| child.state == "cancelled")
        })
        .await;
        settle().await;
        assert!(!suspended(&fixture));
        feed(&fixture, &[claude_state("idle")]);
        wait_until(|| suspended(&fixture)).await;
        finish(fixture).await;
    }

    #[tokio::test(flavor = "current_thread")]
    async fn autonomous_requires_action_keeps_runtime_and_group() {
        let (fixture, seen) = feed_fixture("suspend_claude_feed", AgentConversationProvider::Claude).await;
        feed(
            &fixture,
            &[claude_state("running"), reply_chunk("m-1", "before approval", true)],
        );
        wait_until(|| delta_turns(&seen, "before approval").len() == 1).await;
        let autonomous = delta_turns(&seen, "before approval")[0].clone().unwrap();
        feed(&fixture, &[claude_state("requires_action")]);
        settle().await;
        assert!(!suspended(&fixture));
        feed(
            &fixture,
            &[reply_chunk("m-2", "after approval", true), claude_state("idle")],
        );
        wait_until(|| suspended(&fixture)).await;
        assert_eq!(delta_turns(&seen, "after approval"), vec![Some(autonomous.clone())]);
        assert_eq!(finished_turn(&seen, "before approval"), Some(Some(autonomous.clone())));
        assert_eq!(finished_turn(&seen, "after approval"), Some(Some(autonomous)));
        finish(fixture).await;
    }

    #[tokio::test(flavor = "current_thread")]
    async fn autonomous_error_then_idle_clears_group_and_suspends() {
        let (fixture, seen) = feed_fixture("suspend_claude_feed", AgentConversationProvider::Claude).await;
        feed(
            &fixture,
            &[
                claude_state("running"),
                reply_chunk("m-err", "API Error: overloaded", true),
            ],
        );
        wait_until(|| autonomous_turn(&fixture).is_some()).await;
        let autonomous = autonomous_turn(&fixture);
        feed(&fixture, &[claude_state("idle")]);
        wait_until(|| suspended(&fixture)).await;
        assert_eq!(finished_turn(&seen, "API Error: overloaded"), Some(autonomous));
        finish(fixture).await;
    }

    #[tokio::test(flavor = "current_thread")]
    async fn stop_clears_background_work_without_idle_and_allows_suspension() {
        let (fixture, _) = feed_fixture("suspend_claude_feed", AgentConversationProvider::Claude).await;
        feed(&fixture, &[claude_state("running"), child_spawned("child-agent"), claude_state("idle"),
            json!({"jsonrpc": "2.0", "id": 77, "method": "session/request_user_input",
                "params": {"sessionId": "child-agent", "title": "Choose", "fields": []}})]);
        wait_until(|| { let sessions = fixture.manager.sessions.lock().unwrap();
            sessions[&fixture.owned_id].active_turn_id.is_none()
                && !sessions[&fixture.owned_id].user_input_requests.is_empty() }).await;
        fixture.manager.sessions.lock().unwrap().get_mut(&fixture.owned_id).unwrap()
            .capabilities.session.resume = false;
        fixture
            .manager
            .cancel_turn(&fixture.owned_id, fixture.generation)
            .await
            .expect("Stop with no prompt turn");
        {
            let mut sessions = fixture.manager.sessions.lock().unwrap();
            let session = sessions.get_mut(&fixture.owned_id).unwrap();
            assert_eq!(session.state, AgentRuntimeState::Ready);
            assert!(session.background_work.is_empty());
            assert!(session_is_quiescent(session));
            session.active_turn_id = Some("stopping-foreground".into());
            session.state = AgentRuntimeState::Interrupting;
        }
        feed(&fixture, &[root_update(json!({"sessionUpdate": "session_info_update",
            "_meta": {"jetbrains": {"air": {"backgroundTaskCount": 2}}}}))]);
        settle().await;
        { let mut sessions = fixture.manager.sessions.lock().unwrap();
            let session = sessions.get_mut(&fixture.owned_id).unwrap();
            assert!(session.background_work.is_empty());
            session.state = AgentRuntimeState::Ready; session.capabilities.session.resume = true; }
        fixture.manager.cancel_turn(&fixture.owned_id, fixture.generation).await.unwrap();
        assert!(suspended(&fixture));
        let log = fs::read_to_string(fixture.root.join("suspend_claude_feed.jsonl")).unwrap();
        assert!(log.contains(r#""method":"session/cancel""#));
        finish(fixture).await;
    }

    #[tokio::test(flavor = "current_thread")]
    async fn transport_closed_ends_autonomous_turn_and_running_marker() {
        let (fixture, seen) = feed_fixture("suspend_claude_feed", AgentConversationProvider::Claude).await;
        feed(
            &fixture,
            &[claude_state("running"), reply_chunk("m-auto", "summary", true)],
        );
        wait_until(|| claude_running(&fixture) && autonomous_turn(&fixture).is_some()).await;
        let autonomous = autonomous_turn(&fixture).unwrap();
        fixture.manager.sessions.lock().unwrap().get_mut(&fixture.owned_id).unwrap()
            .state = AgentRuntimeState::WaitingApproval;
        let transport = fixture.manager.sessions.lock().unwrap()[&fixture.owned_id]
            .transport.clone().unwrap();
        settle_closed_transport(&fixture.manager, &transport, "fixture exit").await;
        assert_eq!(autonomous_turn(&fixture), None);
        assert!(!claude_running(&fixture));
        assert_eq!(turn_states(&seen, &autonomous), vec![super::super::protocol::TurnState::Started, super::super::protocol::TurnState::Failed]);
        finish(fixture).await;
    }

    fn async_task(session_id: &str, task: &str, state: Option<&str>) -> Value {
        let update = match state {
            None => json!({"sessionUpdate": "async_task_spawned", "asyncTaskId": task,
                "name": "sleep 60", "taskType": "shell"}),
            Some(state) => json!({"sessionUpdate": "async_task_state_update",
                "asyncTaskId": task, "state": state}),
        };
        json!({"jsonrpc": "2.0", "method": "session/update",
            "params": {"sessionId": session_id, "update": update}})
    }

    fn holds_work(fixture: &FixtureManager, id: &str) -> bool {
        fixture.manager.sessions.lock().unwrap()[&fixture.owned_id]
            .background_work
            .contains_key(id)
    }

    /// The child is finished, or the session already left memory because it
    /// suspended; the assertion after the wait tells the two apart.
    fn child_finished(fixture: &FixtureManager, child: &str) -> bool {
        fixture.manager.sessions.lock().unwrap().get(&fixture.owned_id).map_or(true, |session| {
            session.claude_children.get(child).is_some_and(|child| child.state == "finished")
        })
    }

    // A sub-agent can finish while a shell it started keeps running. Claude Code
    // reports idle then, and wakes the sub-agent and the parent when the shell
    // ends; the runtime has to outlive both.
    #[tokio::test(flavor = "current_thread")]
    async fn child_owned_shell_keeps_runtime_until_the_wake_idle() {
        let (fixture, seen) =
            feed_fixture("suspend_claude_feed", AgentConversationProvider::Claude).await;
        feed(
            &fixture,
            &[
                claude_state("running"),
                child_spawned("agent-a"),
                async_task("agent-a", "shell-1", None),
                child_state("agent-a", "completed"),
                claude_state("idle"),
            ],
        );
        wait_until(|| child_finished(&fixture, "agent-a")).await;
        settle().await;
        assert!(!suspended(&fixture), "the sub-agent's shell still runs");
        assert!(holds_work(&fixture, "background-task:shell-1"));
        assert!(seen.lock().unwrap().iter().any(|event| matches!(&event.payload,
            AgentConversationPayload::Tool { item_id, state: ToolState::Started, .. } if item_id == "background-task:shell-1")));
        feed(
            &fixture,
            &[
                async_task("agent-a", "shell-1", Some("completed")),
                child_spawned("agent-a:generation:2"),
                child_state("agent-a:generation:2", "completed"),
            ],
        );
        wait_until(|| child_finished(&fixture, "agent-a:generation:2")).await;
        settle().await;
        assert!(!suspended(&fixture), "Claude Code still owes the parent its report");
        assert!(seen.lock().unwrap().iter().any(|event| matches!(&event.payload,
            AgentConversationPayload::Tool { item_id, state: ToolState::Completed, .. } if item_id == "background-task:shell-1")));
        feed(&fixture, &[claude_state("running"), claude_state("idle")]);
        wait_until(|| suspended(&fixture)).await;
        finish(fixture).await;
    }

    #[tokio::test(flavor = "current_thread")]
    async fn root_shell_end_while_idle_waits_for_the_wake() {
        let (fixture, _) =
            feed_fixture("suspend_claude_feed", AgentConversationProvider::Claude).await;
        feed(
            &fixture,
            &[
                claude_state("running"),
                async_task("new-session", "shell-1", None),
                claude_state("idle"),
            ],
        );
        wait_until(|| holds_work(&fixture, "background-task:shell-1")).await;
        settle().await;
        assert!(!suspended(&fixture), "the shell still runs");
        assert_eq!(fixture.manager.sessions.lock().unwrap()[&fixture.owned_id].state,
            AgentRuntimeState::Working);
        feed(&fixture, &[async_task("new-session", "shell-1", Some("completed"))]);
        settle().await;
        assert!(!suspended(&fixture), "Claude Code still owes the report of the shell");
        feed(&fixture, &[claude_state("running"), claude_state("idle")]);
        wait_until(|| suspended(&fixture)).await;
        finish(fixture).await;
    }

    #[tokio::test(flavor = "current_thread")]
    async fn claude_session_with_running_monitor_is_not_suspended() {
        let (fixture, _) =
            feed_fixture("suspend_claude_feed", AgentConversationProvider::Claude).await;
        let background_count = |count| root_update(json!({
            "sessionUpdate": "session_info_update",
            "_meta": {"jetbrains": {"air": {"backgroundTaskCount": count}}}
        }));
        feed(&fixture, &[
            claude_state("running"), background_count(1), claude_state("idle"),
        ]);
        settle().await;
        assert!(!suspended(&fixture), "the Monitor still runs after foreground idle");
        assert_eq!(fixture.manager.sessions.lock().unwrap()[&fixture.owned_id].state,
            AgentRuntimeState::Working);
        assert!(!fixture.manager.suspend_if_quiescent(&fixture.owned_id, fixture.generation)
            .await.unwrap());
        feed(&fixture, &[background_count(0)]);
        settle().await;
        assert!(!suspended(&fixture), "the Monitor completion still owes a wake");
        feed(&fixture, &[claude_state("running"), claude_state("idle")]);
        wait_until(|| suspended(&fixture)).await;
        finish(fixture).await;
    }

    #[tokio::test(flavor = "current_thread")]
    async fn codex_background_task_end_owes_no_wake() {
        let (fixture, _) =
            feed_fixture("suspend_codex_feed", AgentConversationProvider::Codex).await;
        feed(
            &fixture,
            &[
                claude_state("running"),
                async_task("new-session", "task-1", None),
            ],
        );
        wait_until(|| holds_work(&fixture, "background-task:task-1")).await;
        feed(&fixture, &[async_task("new-session", "task-1", Some("completed"))]);
        wait_until(|| suspended(&fixture)).await;
        finish(fixture).await;
    }

    #[test]
    fn ordinary_think_tool_call_does_not_map_to_child() {
        let thought = json!({ "sessionId": "s", "update": {
            "sessionUpdate": "tool_call",
            "toolCallId": "think-tool-1",
            "title": "Think",
            "kind": "think",
            "status": "pending"
        }});

        assert!(matches!(
            payload_from_session_update_for_turn(&thought, None),
            Some(AgentConversationPayload::Tool { .. })
        ));
    }

    /// Antigravity's adapter fixes its working folder when its process starts
    /// and never reads the folder named in `session/new`, so every session has
    /// to keep a process of its own. It advertises no multi-session support,
    /// and the app pools sessions onto one process only when a provider does.
    /// Pinning the two together keeps anyone from turning pooling on later
    /// without also teaching the adapter about per-session folders — every
    /// pooled session would silently run in the first one's folder.
    #[tokio::test(flavor = "current_thread")]
    async fn antigravity_sessions_keep_a_process_each() {
        let fixture =
            fixture_manager_with_provider("agy", AgentConversationProvider::Antigravity, None)
                .await;
        let capabilities = fixture
            .manager
            .capabilities(&fixture.owned_id, fixture.generation)
            .expect("capabilities");
        assert!(!provider_is_multi_session_safe(&capabilities));
        fs::remove_dir_all(fixture.root).unwrap();
    }

    #[tokio::test(flavor = "current_thread")]
    async fn captures_session_commands_and_usage_without_an_active_turn() {
        let fixture = fixture_manager_with_acp_session("command_capture").await;
        wait_until(|| {
            fixture
                .manager
                .capabilities(&fixture.owned_id, fixture.generation)
                .map(|capabilities| {
                    capabilities
                        .commands
                        .iter()
                        .any(|command| command.id == "updated")
                })
                .unwrap_or(false)
        })
        .await;
        let capabilities = fixture
            .manager
            .capabilities(&fixture.owned_id, fixture.generation)
            .expect("capabilities");
        assert_eq!(capabilities.commands[0].id, "updated");
        let snapshot = fixture
            .manager
            .snapshot(&fixture.owned_id)
            .expect("snapshot")
            .expect("active snapshot");
        assert!(snapshot.events.iter().any(|event| matches!(
            event.payload,
            AgentConversationPayload::AvailableCommandsUpdate { .. }
        )));
        assert!(snapshot.events.iter().any(|event| matches!(
            event.payload,
            AgentConversationPayload::Usage {
                used_tokens: Some(120),
                context_window: Some(4096),
                ..
            }
        )));
        fixture
            .manager
            .close(&fixture.owned_id, fixture.generation)
            .await
            .expect("close");
        fs::remove_dir_all(fixture.root).unwrap();
    }

    #[tokio::test(flavor = "current_thread")]
    async fn a_prompt_streams_updates_and_lifecycle_through_the_emitter() {
        let fixture = fixture_manager_with_acp_session("prompt_with_update").await;
        let ready = fixture.manager.snapshot(&fixture.owned_id).unwrap().unwrap();
        assert_eq!(ready.events[0].sequence, 1);
        assert!(matches!(
            ready.events[0].payload,
            AgentConversationPayload::Connection {
                state: ConversationConnectionState::Connected,
                ..
            }
        ));
        let seen: Arc<Mutex<Vec<AgentConversationEvent>>> = Default::default();
        let sink = Arc::clone(&seen);
        fixture
            .manager
            .set_emitter(Arc::new(move |event| sink.lock().unwrap().push(event)));

        let receipt = fixture
            .manager
            .send_message(&fixture.owned_id, fixture.generation, test_prompt("hello"), None, None)
            .await
            .expect("prompt");
        wait_until(|| {
            seen.lock().unwrap().iter().any(|event| {
                matches!(
                    event.payload,
                    AgentConversationPayload::Turn {
                        state: super::super::protocol::TurnState::Completed,
                        ..
                    }
                )
            })
        })
        .await;

        {
            let seen = seen.lock().unwrap();
            let kinds = seen
                .iter()
                .map(|event| payload_kind(&event.payload))
                .collect::<Vec<_>>();
            assert!(
                kinds.starts_with(&["turn"]),
                "Turn Started must be first, got {kinds:?}"
            );
            assert!(
                kinds.contains(&"assistantDelta"),
                "streamed delta missing: {kinds:?}"
            );
            assert_eq!(seen[0].sequence, ready.events[0].sequence + 1);
            assert!(seen
                .windows(2)
                .all(|events| events[1].sequence == events[0].sequence + 1));
            let turn_ids = seen
                .iter()
                .filter_map(|event| match &event.payload {
                    AgentConversationPayload::Turn { turn_id, .. } => Some(turn_id.clone()),
                    _ => None,
                })
                .collect::<Vec<_>>();
            assert_eq!(turn_ids.len(), 2);
            assert_eq!(
                turn_ids[0], turn_ids[1],
                "app-minted id correlates Started and Completed"
            );
            assert!(turn_ids[0].starts_with("turn-"));
            assert_eq!(receipt.owned_id, fixture.owned_id);
            assert_eq!(receipt.generation, fixture.generation);
            assert_eq!(receipt.turn_id, turn_ids[0]);
            assert_eq!(receipt.user_item_id, format!("user-{}", receipt.turn_id));
            let admitted = seen.iter().find(|event| event.sequence == receipt.admitted_sequence).unwrap();
            assert!(matches!(&admitted.payload,
                AgentConversationPayload::UserMessage { item_id, text, .. }
                    if item_id == &receipt.user_item_id && text == "hello"));
            let stored = fixture.manager.list_events(&fixture.owned_id, receipt.admitted_sequence).unwrap();
            assert_eq!(stored.first(), Some(admitted));
            let assistant_item_id = seen.iter().find_map(|event| match &event.payload {
                AgentConversationPayload::AssistantDelta { item_id, .. } => Some(item_id),
                _ => None,
            });
            assert_eq!(
                assistant_item_id,
                Some(&format!("assistant-{}", turn_ids[0])),
                "fallback item identity must use the app-minted turn id"
            );
        }

        fixture
            .manager
            .close(&fixture.owned_id, fixture.generation)
            .await
            .unwrap();
        fs::remove_dir_all(fixture.root).unwrap();
    }

    #[tokio::test(flavor = "current_thread")]
    async fn prompt_after_failed_session_replacement_continues_the_event_sequence() {
        let fixture = fixture_manager_with_acp_session("prompt_with_update").await;

        fixture
            .manager
            .prompt(&fixture.owned_id, fixture.generation, test_prompt("first"))
            .await
            .expect("first prompt");
        wait_until(|| {
            fixture
                .manager
                .sessions
                .lock()
                .unwrap()
                .get(&fixture.owned_id)
                .is_some_and(|session| session.active_turn_id.is_none())
        })
        .await;
        let first_last_sequence = fixture
            .manager
            .snapshot(&fixture.owned_id)
            .unwrap()
            .unwrap()
            .last_sequence;

        {
            let mut sessions = fixture.manager.sessions.lock().unwrap();
            let session = sessions.get_mut(&fixture.owned_id).unwrap();
            session.state = AgentRuntimeState::Failed;
            session.connection.state = ConversationConnectionState::Failed;
        }
        let replacement = fixture
            .manager
            .ensure_async(request(
                fixture.root.to_str().unwrap(),
                &fixture.owned_id,
                AgentConversationProvider::Codex,
            ))
            .await
            .expect("replace failed session");
        fixture
            .manager
            .activate(&fixture.owned_id, replacement.generation)
            .await
            .expect("activate replacement");
        fixture
            .manager
            .prompt(
                &fixture.owned_id,
                replacement.generation,
                test_prompt("second"),
            )
            .await
            .expect("second prompt after replacement");

        let events = fixture.manager.list_events(&fixture.owned_id, 0).unwrap();
        let second_start = events
            .iter()
            .find(|event| event.sequence > first_last_sequence)
            .expect("replacement prompt event");
        assert_eq!(second_start.sequence, first_last_sequence + 1);

        let _ = fixture
            .manager
            .close(&fixture.owned_id, replacement.generation)
            .await;
        fs::remove_dir_all(fixture.root).unwrap();
    }

    #[tokio::test(flavor = "current_thread")]
    async fn first_prompt_titles_once_and_explicit_metadata_wins() {
        let fixture = fixture_manager_with_acp_session("prompt_with_update").await;

        fixture
            .manager
            .prompt(
                &fixture.owned_id,
                fixture.generation,
                test_prompt("  First prompt title  \nignored second line"),
            )
            .await
            .expect("first prompt");
        wait_until(|| {
            fixture
                .manager
                .list_sessions()
                .expect("list after first prompt")[0]
                .active_turn_id
                .is_none()
        })
        .await;
        assert_eq!(
            fixture.manager.list_sessions().unwrap()[0]
                .meta
                .title
                .as_deref(),
            Some("First prompt title")
        );

        fixture
            .manager
            .prompt(
                &fixture.owned_id,
                fixture.generation,
                test_prompt("A later prompt must not replace the title"),
            )
            .await
            .expect("second prompt");
        wait_until(|| {
            fixture
                .manager
                .list_sessions()
                .expect("list after second prompt")[0]
                .active_turn_id
                .is_none()
        })
        .await;
        assert_eq!(
            fixture.manager.list_sessions().unwrap()[0]
                .meta
                .title
                .as_deref(),
            Some("First prompt title")
        );

        fixture
            .manager
            .update_session_meta(UpdateAgentConversationSessionMetaRequest {
                owned_id: fixture.owned_id.clone(),
                model: None,
                effort: None,
                meta: AgentConversationSessionMeta {
                    title: Some("Chosen title".into()),
                    ..AgentConversationSessionMeta::default()
                },
            })
            .expect("set explicit title");
        fixture
            .manager
            .prompt(
                &fixture.owned_id,
                fixture.generation,
                test_prompt("Another prompt must preserve the chosen title"),
            )
            .await
            .expect("third prompt");
        wait_until(|| {
            fixture
                .manager
                .list_sessions()
                .expect("list after third prompt")[0]
                .active_turn_id
                .is_none()
        })
        .await;
        assert_eq!(
            fixture.manager.list_sessions().unwrap()[0]
                .meta
                .title
                .as_deref(),
            Some("Chosen title")
        );

        fixture
            .manager
            .close(&fixture.owned_id, fixture.generation)
            .await
            .unwrap();
        fs::remove_dir_all(fixture.root).unwrap();
    }

    #[test]
    fn prompt_title_is_char_boundary_safe_and_capped() {
        let long_title = format!("  {} trailing", "é".repeat(64));
        assert_eq!(prompt_title(&long_title), Some("é".repeat(64)));
        assert_eq!(prompt_title("   \nsecond line"), None);
    }

    #[tokio::test(flavor = "current_thread")]
    async fn activating_the_same_generation_reuses_one_runtime_and_pump() {
        let fixture = fixture_manager_with_acp_session("prompt_with_update").await;
        let seen: Arc<Mutex<Vec<AgentConversationEvent>>> = Default::default();
        let sink = Arc::clone(&seen);
        fixture
            .manager
            .set_emitter(Arc::new(move |event| sink.lock().unwrap().push(event)));

        fixture
            .manager
            .activate(&fixture.owned_id, fixture.generation)
            .await
            .expect("second activation is idempotent");
        assert_eq!(fixture.manager.resource_roots().len(), 1);
        let log = fs::read_to_string(fixture.root.join("prompt_with_update.jsonl"))
            .expect("activation fixture log");
        assert_eq!(log.matches(r#""method":"initialize""#).count(), 1);
        assert_eq!(log.matches(r#""method":"session/new""#).count(), 1);
        assert!(
            seen.lock().unwrap().is_empty(),
            "idempotent activation emits no event"
        );

        fixture
            .manager
            .prompt(&fixture.owned_id, fixture.generation, test_prompt("hello"))
            .await
            .expect("prompt starts");
        wait_until(|| {
            seen.lock().unwrap().iter().any(|event| {
                matches!(
                    event.payload,
                    AgentConversationPayload::Turn {
                        state: super::super::protocol::TurnState::Completed,
                        ..
                    }
                )
            })
        })
        .await;
        assert_eq!(
            seen.lock()
                .unwrap()
                .iter()
                .filter(|event| matches!(
                    event.payload,
                    AgentConversationPayload::AssistantDelta { .. }
                ))
                .count(),
            1,
            "one pump must emit one update"
        );

        fixture
            .manager
            .close(&fixture.owned_id, fixture.generation)
            .await
            .unwrap();
        fs::remove_dir_all(fixture.root).unwrap();
    }

    #[tokio::test(flavor = "current_thread")]
    async fn store_row_updated_on_every_lifecycle_transition() {
        let root = temp_root();
        let database = root.join("sessions.db");
        let log = root.join("store-lifecycle.jsonl");
        let manifest = super::super::providers::acp_client::tests::fixture_manifest_named(
            &log,
            "suspend_store_lifecycle",
        );
        let providers = ProviderRegistry::new([(AgentConversationProvider::Codex, manifest)])
            .expect("fixture provider");
        let manager = AgentRuntimeManager::open(providers, &database).unwrap();
        let connection = manager
            .ensure_async(request(
                root.to_str().unwrap(),
                "owned-store-lifecycle",
                AgentConversationProvider::Codex,
            ))
            .await
            .unwrap();
        assert_eq!(
            manager
                .store
                .get_session(&connection.owned_id)
                .unwrap()
                .unwrap()
                .state,
            "starting"
        );
        manager
            .activate(&connection.owned_id, connection.generation)
            .await
            .unwrap();
        assert_eq!(
            manager
                .store
                .get_session(&connection.owned_id)
                .unwrap()
                .unwrap()
                .state,
            "ready"
        );
        manager
            .suspend_if_quiescent(&connection.owned_id, connection.generation)
            .await
            .unwrap();
        let suspended = manager
            .store
            .get_session(&connection.owned_id)
            .unwrap()
            .unwrap();
        assert_eq!(suspended.state, "suspended");
        assert!(suspended.suspended);

        manager
            .activate(&connection.owned_id, connection.generation)
            .await
            .unwrap();
        assert_eq!(
            manager
                .store
                .get_session(&connection.owned_id)
                .unwrap()
                .unwrap()
                .state,
            "ready"
        );
        manager
            .close(&connection.owned_id, connection.generation)
            .await
            .unwrap();
        assert_eq!(
            manager
                .store
                .get_session(&connection.owned_id)
                .unwrap()
                .unwrap()
                .state,
            "closed"
        );
        fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn lifecycle_transition_updates_all_fields_and_rejects_terminal_revival() {
        let root = temp_root();
        let manager = AgentRuntimeManager::new(ProviderRegistry::default());
        let connection = manager
            .ensure_inner(request(
                root.to_str().unwrap(),
                "owned-lifecycle-transition",
                AgentConversationProvider::Codex,
            ))
            .unwrap()
            .0;
        let mut sessions = manager.sessions.lock().unwrap();
        let session = sessions.get_mut(&connection.owned_id).unwrap();

        session
            .transition_lifecycle(
                AgentRuntimeState::Ready,
                ConversationConnectionState::Connected,
                AgentExecutionOwner::Structured,
                AgentWriterLeaseOwner::Structured,
            )
            .unwrap();
        assert_eq!(session.state, AgentRuntimeState::Ready);
        assert_eq!(
            session.connection.state,
            ConversationConnectionState::Connected
        );
        assert_eq!(session.owner, AgentExecutionOwner::Structured);
        assert_eq!(
            session.writer_lease.owner,
            AgentWriterLeaseOwner::Structured
        );

        session
            .transition_lifecycle(
                AgentRuntimeState::Closed,
                ConversationConnectionState::Closed,
                AgentExecutionOwner::Stopped,
                AgentWriterLeaseOwner::None,
            )
            .unwrap();
        let error = session
            .transition_lifecycle(
                AgentRuntimeState::Ready,
                ConversationConnectionState::Connected,
                AgentExecutionOwner::Structured,
                AgentWriterLeaseOwner::Structured,
            )
            .unwrap_err();
        assert_eq!(error, "Closed conversation sessions cannot be revived");
        assert_eq!(session.state, AgentRuntimeState::Closed);
        assert_eq!(
            session.connection.state,
            ConversationConnectionState::Closed
        );
        assert_eq!(session.owner, AgentExecutionOwner::Stopped);
        assert_eq!(session.writer_lease.owner, AgentWriterLeaseOwner::None);
        drop(sessions);
        fs::remove_dir_all(root).unwrap();
    }

    #[tokio::test(flavor = "current_thread")]
    async fn stale_generation_close_is_refused_without_changing_current_session() {
        let root = temp_root();
        let manager = AgentRuntimeManager::new(ProviderRegistry::default());
        let connection = manager
            .ensure_inner(request(
                root.to_str().unwrap(),
                "owned-stale-close",
                AgentConversationProvider::Codex,
            ))
            .unwrap()
            .0;

        let error = manager
            .close(&connection.owned_id, connection.generation + 1)
            .await
            .unwrap_err();
        assert_eq!(
            error,
            "Conversation connection changed; retry on the current session"
        );
        {
            let sessions = manager.sessions.lock().unwrap();
            let session = sessions.get(&connection.owned_id).unwrap();
            assert_eq!(session.generation, connection.generation);
            assert_eq!(session.state, AgentRuntimeState::Starting);
            assert_eq!(
                session.connection.state,
                ConversationConnectionState::Connecting
            );
            assert_eq!(session.owner, AgentExecutionOwner::Stopped);
            assert_eq!(session.writer_lease.owner, AgentWriterLeaseOwner::None);
        }

        manager
            .close(&connection.owned_id, connection.generation)
            .await
            .unwrap();
        fs::remove_dir_all(root).unwrap();
    }

    #[tokio::test(flavor = "current_thread")]
    async fn close_during_activation_cannot_resurrect_the_session() {
        let root = temp_root();
        let log = root.join("blocked-activation.jsonl");
        let mut manifest =
            super::super::providers::acp_client::tests::fixture_manifest_named(&log, "default");
        let script = manifest.args.get_mut(1).expect("fixture script");
        *script = script.replace(
            "*'\"method\":\"session/new\"'*)",
            "*'\"method\":\"session/new\"'*)\n      sleep 0.15",
        );
        let providers = ProviderRegistry::new([(AgentConversationProvider::Codex, manifest)])
            .expect("fixture provider");
        let manager = AgentRuntimeManager::new(providers);
        let connection = manager
            .ensure_inner(request(
                root.to_str().unwrap(),
                "owned-blocked-activation",
                AgentConversationProvider::Codex,
            ))
            .expect("ensure")
            .0;

        let activating_manager = manager.clone();
        let activating_owned_id = connection.owned_id.clone();
        let activation = tokio::spawn(async move {
            activating_manager
                .activate(&activating_owned_id, connection.generation)
                .await
        });
        wait_until(|| {
            fs::read_to_string(&log)
                .is_ok_and(|requests| requests.contains(r#""method":"session/new""#))
        })
        .await;

        manager
            .close("owned-blocked-activation", connection.generation)
            .await
            .unwrap();
        activation.await.unwrap().unwrap();

        assert!(manager.sessions.lock().unwrap().is_empty());
        assert_eq!(
            manager.list_sessions().unwrap()[0].state,
            AgentRuntimeState::Closed
        );
        fs::remove_dir_all(root).unwrap();
    }

    /// A conversation keeps every event it was given.
    ///
    /// The journal used to be trimmed to its newest ten thousand on every
    /// write, which was reasonable when opening a conversation handed over all
    /// of them. Once a window became a bounded read that trim only did harm: it
    /// deleted the reading a person had just scrolled back to fetch, and it did
    /// it on their next message.
    #[test]
    fn a_conversation_keeps_every_event_it_was_given() {
        let root = temp_root();
        let database = root.join("sessions.db");
        let manager = AgentRuntimeManager::open(ProviderRegistry::default(), &database).unwrap();
        let connection = manager
            .ensure_inner(request(
                root.to_str().unwrap(),
                "owned-event-cap",
                AgentConversationProvider::Codex,
            ))
            .unwrap()
            .0;
        {
            let mut sessions = manager.sessions.lock().unwrap();
            let session = sessions.get_mut(&connection.owned_id).unwrap();
            for index in 0..=CATCH_UP_EVENT_CAP {
                record_payload_for_session(
                    session,
                    AgentConversationPayload::Error {
                        code: format!("event-{index}"),
                        message: "fixture".into(),
                        recoverable: true,
                    },
                )
                .unwrap();
            }
        }
        let written = i64::from(CATCH_UP_EVENT_CAP) + 1;
        assert_eq!(
            manager.store.latest_seq(&connection.owned_id).unwrap(),
            written,
            "nothing older is dropped to make room"
        );
        let events = manager
            .store
            .list_events(&connection.owned_id, 0, CATCH_UP_EVENT_CAP)
            .unwrap();
        // A catch-up read is bounded, but it starts at the first event there
        // ever was rather than at whatever survived a trim.
        assert_eq!(events.len(), CATCH_UP_EVENT_CAP as usize);
        assert_eq!(events.first().unwrap().seq, 1);
        let snapshot = manager.snapshot(&connection.owned_id).unwrap().unwrap();
        // Opening hands over the newest reading, bounded by bytes rather than
        // by a count of events, and ending on the newest event there is.
        assert!(
            snapshot.events.len() < events.len(),
            "a session larger than the window is not handed over whole"
        );
        assert_eq!(snapshot.events.last().unwrap().sequence, written);
        assert!(snapshot
            .events
            .windows(2)
            .all(|pair| pair[1].sequence == pair[0].sequence + 1));
        fs::remove_dir_all(root).unwrap();
    }

    #[tokio::test(flavor = "current_thread")]
    async fn restart_recovers_sessions_from_store() {
        let root = temp_root();
        let database = root.join("sessions.db");
        let log = root.join("restart.jsonl");
        let manifest = super::super::providers::acp_client::tests::fixture_manifest_named(
            &log,
            "suspend_restart",
        );
        let providers =
            ProviderRegistry::new([(AgentConversationProvider::Codex, manifest.clone())]).unwrap();
        let manager = AgentRuntimeManager::open(providers, &database).unwrap();
        let connection = manager
            .ensure_async(request(
                root.to_str().unwrap(),
                "owned-restart",
                AgentConversationProvider::Codex,
            ))
            .await
            .unwrap();
        manager
            .activate(&connection.owned_id, connection.generation)
            .await
            .unwrap();
        drop(manager);

        let providers =
            ProviderRegistry::new([(AgentConversationProvider::Codex, manifest)]).unwrap();
        let recovered = AgentRuntimeManager::open(providers, &database).unwrap();
        assert!(recovered.sessions.lock().unwrap().is_empty());
        let sessions = recovered.list_sessions().unwrap();
        assert_eq!(sessions.len(), 1);
        assert!(sessions[0].suspended);
        assert!(recovered.sessions.lock().unwrap().is_empty());
        recovered
            .activate(&connection.owned_id, connection.generation)
            .await
            .unwrap();
        assert!(
            !recovered
                .snapshot(&connection.owned_id)
                .unwrap()
                .unwrap()
                .suspended
        );
        recovered
            .close(&connection.owned_id, connection.generation)
            .await
            .unwrap();
        fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn startup_recovery_backfills_untitled_session_from_first_user_message() {
        let root = temp_root();
        let database = root.join("sessions.db");
        let manager = AgentRuntimeManager::open(ProviderRegistry::default(), &database).unwrap();
        let connection = manager
            .ensure_inner(request(
                root.to_str().unwrap(),
                "owned-title-backfill",
                AgentConversationProvider::Codex,
            ))
            .unwrap()
            .0;
        {
            let mut sessions = manager.sessions.lock().unwrap();
            let session = sessions.get_mut(&connection.owned_id).unwrap();
            record_payload_for_session(
                session,
                AgentConversationPayload::AssistantMessage {
                    item_id: "earlier-assistant".into(),
                    text: "not a title".into(),
                    completed: true,
                    blocks: None,
                },
            )
            .unwrap();
            record_payload_for_session(
                session,
                AgentConversationPayload::UserMessage {
                    item_id: "first-user".into(),
                    text: "  Recovered title  \nignored second line".into(),
                    completed: true,
                    attachment_ids: Vec::new(),
                },
            )
            .unwrap();
            record_payload_for_session(
                session,
                AgentConversationPayload::UserMessage {
                    item_id: "second-user".into(),
                    text: "Later title".into(),
                    completed: true,
                    attachment_ids: Vec::new(),
                },
            )
            .unwrap();
        }
        drop(manager);

        let recovered = AgentRuntimeManager::open(ProviderRegistry::default(), &database).unwrap();
        assert_eq!(
            recovered.list_sessions().unwrap()[0].meta.title.as_deref(),
            Some("Recovered title")
        );
        assert_eq!(
            recovered
                .store
                .get_session(&connection.owned_id)
                .unwrap()
                .unwrap()
                .title
                .as_deref(),
            Some("Recovered title")
        );
        drop(recovered);
        fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn pinned_session_survives_manager_restart() {
        let root = temp_root();
        let database = root.join("sessions.db");
        let manager = AgentRuntimeManager::open(ProviderRegistry::default(), &database).unwrap();
        let connection = manager
            .ensure_inner(request(
                root.to_str().unwrap(),
                "owned-pinned",
                AgentConversationProvider::Codex,
            ))
            .unwrap()
            .0;
        manager
            .update_session_meta(UpdateAgentConversationSessionMetaRequest {
                owned_id: connection.owned_id.clone(),
                model: None,
                effort: None,
                meta: AgentConversationSessionMeta {
                    pinned_at: Some("2026-10-07T10:00:00.000Z".into()),
                    ..AgentConversationSessionMeta::default()
                },
            })
            .expect("pin the session");
        drop(manager);

        let recovered = AgentRuntimeManager::open(ProviderRegistry::default(), &database).unwrap();
        assert_eq!(
            recovered.list_sessions().unwrap()[0].meta.pinned_at.as_deref(),
            Some("2026-10-07T10:00:00.000Z")
        );
        drop(recovered);
        fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn session_draft_survives_manager_restart_and_clears() {
        let root = temp_root();
        let database = root.join("sessions.db");
        let manager = AgentRuntimeManager::open(ProviderRegistry::default(), &database).unwrap();
        let connection = manager
            .ensure_inner(request(
                root.to_str().unwrap(),
                "owned-draft-restart",
                AgentConversationProvider::Codex,
            ))
            .unwrap()
            .0;
        manager
            .set_session_draft(&connection.owned_id, "unfinished message")
            .unwrap();
        drop(manager);

        let recovered = AgentRuntimeManager::open(ProviderRegistry::default(), &database).unwrap();
        assert_eq!(
            recovered.get_session_draft(&connection.owned_id).unwrap(),
            Some("unfinished message".to_owned())
        );
        recovered.clear_session_draft(&connection.owned_id).unwrap();
        assert_eq!(
            recovered.get_session_draft(&connection.owned_id).unwrap(),
            None
        );
        drop(recovered);
        fs::remove_dir_all(root).unwrap();
    }

    // The rail row is saved right after the first send, before it has copied
    // the model in. Saving it must not blank the model the session was given.
    #[test]
    fn saving_the_rail_row_without_a_model_keeps_the_one_chosen() {
        let root = temp_root();
        let log = root.join("keep-model.jsonl");
        let manifest = super::super::providers::acp_client::tests::fixture_manifest_named(
            &log,
            "prompt_with_update",
        );
        let providers = ProviderRegistry::new([(AgentConversationProvider::Codex, manifest)])
            .expect("fixture provider");
        let manager = AgentRuntimeManager::new(providers);
        let (connection, _) = manager
            .ensure_inner(request(
                root.to_str().unwrap(),
                "owned-keep-model",
                AgentConversationProvider::Codex,
            ))
            .unwrap();
        let row = |model: Option<&str>| UpdateAgentConversationSessionMetaRequest {
            owned_id: connection.owned_id.clone(),
            model: model.map(str::to_owned),
            effort: None,
            meta: AgentConversationSessionMeta::default(),
        };
        manager
            .update_session_meta(row(Some("gpt-5.6-luna")))
            .unwrap();

        let saved = manager.update_session_meta(row(None)).unwrap();

        assert_eq!(saved.model.as_deref(), Some("gpt-5.6-luna"));
        assert_eq!(
            manager.list_sessions().unwrap()[0].model.as_deref(),
            Some("gpt-5.6-luna")
        );
        fs::remove_dir_all(root).unwrap();
    }

    // Removing a row on the rail used to remove nothing here, and the next
    // launch rebuilt the rail from this store — so every removed session came
    // back. Deleting takes the row, and everything hanging off it, out for good.
    #[tokio::test(flavor = "current_thread")]
    async fn deleting_a_conversation_takes_it_out_of_the_store() {
        let root = temp_root();
        let log = root.join("delete-session.jsonl");
        let manifest = super::super::providers::acp_client::tests::fixture_manifest_named(
            &log,
            "prompt_with_update",
        );
        let providers = ProviderRegistry::new([(AgentConversationProvider::Codex, manifest)])
            .expect("fixture provider");
        let manager = AgentRuntimeManager::new(providers);
        let (connection, _) = manager
            .ensure_inner(request(
                root.to_str().unwrap(),
                "owned-to-delete",
                AgentConversationProvider::Codex,
            ))
            .unwrap();
        {
            let mut sessions = manager.sessions.lock().unwrap();
            let session = sessions.get_mut(&connection.owned_id).unwrap();
            record_payload_for_session(
                session,
                AgentConversationPayload::UserMessage {
                    item_id: "message-to-delete".into(),
                    text: "stored".into(),
                    completed: true,
                    attachment_ids: Vec::new(),
                },
            )
            .unwrap();
        }
        assert_eq!(manager.list_sessions().unwrap().len(), 1);

        assert!(manager.delete(&connection.owned_id).await.unwrap());

        assert!(manager.list_sessions().unwrap().is_empty());
        assert!(manager
            .list_events(&connection.owned_id, 0)
            .unwrap()
            .is_empty());
        assert!(manager.snapshot(&connection.owned_id).unwrap().is_none());
        assert!(!manager
            .activation_locks
            .lock()
            .unwrap()
            .contains_key(&connection.owned_id));
        assert!(!manager
            .broker_statuses
            .lock()
            .unwrap()
            .contains_key(&connection.owned_id));
        // Gone is gone: asking again is not an error, just nothing to do.
        assert!(!manager.delete(&connection.owned_id).await.unwrap());
        fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn selecting_and_reading_session_never_spawns_runtime() {
        let root = temp_root();
        let database = root.join("sessions.db");
        let log = root.join("read-only-session.jsonl");
        let manifest = super::super::providers::acp_client::tests::fixture_manifest_named(
            &log,
            "prompt_with_update",
        );
        let providers =
            ProviderRegistry::new([(AgentConversationProvider::Codex, manifest.clone())])
                .expect("fixture provider");
        let manager = AgentRuntimeManager::open(providers, &database).unwrap();
        let (connection, _) = manager
            .ensure_inner(request(
                root.to_str().unwrap(),
                "owned-read-only",
                AgentConversationProvider::Codex,
            ))
            .unwrap();
        {
            let mut sessions = manager.sessions.lock().unwrap();
            let session = sessions.get_mut(&connection.owned_id).unwrap();
            record_payload_for_session(
                session,
                AgentConversationPayload::UserMessage {
                    item_id: "message-read-only".into(),
                    text: "stored".into(),
                    completed: true,
                    attachment_ids: Vec::new(),
                },
            )
            .unwrap();
        }
        manager
            .update_session_meta(UpdateAgentConversationSessionMetaRequest {
                owned_id: connection.owned_id.clone(),
                model: Some("model-a".into()),
                effort: Some("medium".into()),
                meta: AgentConversationSessionMeta {
                    worktree: Some(root.display().to_string()),
                    branch: Some("lane/read-only".into()),
                    title: Some("Read only".into()),
                    project: Some("Project".into()),
                    pty_session_id: Some("pty-1".into()),
                    origin: Some("app".into()),
                    source: Some("fresh".into()),
                    ..AgentConversationSessionMeta::default()
                },
            })
            .unwrap();
        drop(manager);

        let providers = ProviderRegistry::new([(AgentConversationProvider::Codex, manifest)])
            .expect("fixture provider");
        let manager = AgentRuntimeManager::open(providers, &database).unwrap();
        assert!(manager.sessions.lock().unwrap().is_empty());
        let before_reads = manager.resource_diagnostics().unwrap();
        assert_eq!(before_reads.durable_session_rows, 1);
        assert_eq!(before_reads.live_session_overlays, 0);

        let listed = manager.list_sessions().unwrap();
        let snapshot = manager.snapshot(&connection.owned_id).unwrap().unwrap();
        let events = manager.list_events(&connection.owned_id, 0).unwrap();

        assert_eq!(listed.len(), 1);
        assert_eq!(listed[0].meta.title.as_deref(), Some("Read only"));
        assert_eq!(listed[0].meta.pty_session_id.as_deref(), Some("pty-1"));
        assert_eq!(snapshot.events, events);
        assert_eq!(events.len(), 1);
        assert!(manager.sessions.lock().unwrap().is_empty());
        let after_reads = manager.resource_diagnostics().unwrap();
        assert_eq!(after_reads.durable_session_rows, 1);
        assert_eq!(after_reads.live_session_overlays, 0);
        assert!(manager.resource_roots().is_empty());
        assert!(
            !log.exists(),
            "read commands must not create a transport process"
        );
        fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn session_annotations_survive_manager_restart() {
        let root = temp_root();
        let database = root.join("sessions.db");
        let owned_id = "owned-annotation-restart";
        let manager = AgentRuntimeManager::open(ProviderRegistry::default(), &database).unwrap();
        manager
            .ensure_inner(request(
                root.to_str().unwrap(),
                owned_id,
                AgentConversationProvider::Codex,
            ))
            .unwrap();
        let saved = manager
            .add_session_annotation(
                owned_id,
                "https://example.test/restart",
                r#"{"x":10,"y":20,"width":100,"height":50}"#,
                "Keep this note",
            )
            .unwrap();
        drop(manager);

        let restarted = AgentRuntimeManager::open(ProviderRegistry::default(), &database).unwrap();
        let annotations = restarted.list_session_annotations(owned_id).unwrap();
        assert_eq!(annotations, [saved.clone()]);

        restarted.delete_session_annotation(saved.id).unwrap();
        assert!(restarted
            .list_session_annotations(owned_id)
            .unwrap()
            .is_empty());
        fs::remove_dir_all(root).unwrap();
    }

    #[tokio::test(flavor = "current_thread")]
    async fn turn_completion_suspends_and_stops_the_runtime() {
        let fixture = fixture_manager_with_acp_session("suspend_turn_completion").await;
        let pid = fixture.manager.resource_roots()[0].pid;
        let native_session_id = fixture
            .manager
            .snapshot(&fixture.owned_id)
            .unwrap()
            .unwrap()
            .connection
            .native_session_id
            .unwrap();

        fixture
            .manager
            .prompt(
                &fixture.owned_id,
                fixture.generation,
                test_prompt("finish this turn"),
            )
            .await
            .expect("prompt starts");
        wait_until(|| {
            fixture
                .manager
                .snapshot(&fixture.owned_id)
                .unwrap()
                .is_some_and(|snapshot| snapshot.suspended)
        })
        .await;
        wait_until(|| !process_is_alive(pid)).await;

        let snapshot = fixture
            .manager
            .snapshot(&fixture.owned_id)
            .unwrap()
            .unwrap();
        assert_eq!(
            snapshot.connection.native_session_id.as_deref(),
            Some(native_session_id.as_str())
        );
        assert!(fixture.manager.resource_roots().is_empty());

        fixture
            .manager
            .close(&fixture.owned_id, fixture.generation)
            .await
            .unwrap();
        fs::remove_dir_all(fixture.root).unwrap();
    }

    #[tokio::test(flavor = "current_thread")]
    async fn teardown_at_quiescence() {
        let fixture = fixture_manager_with_acp_session("suspend_active").await;

        assert!(fixture
            .manager
            .suspend_if_quiescent(&fixture.owned_id, fixture.generation)
            .await
            .unwrap());

        fixture
            .manager
            .close(&fixture.owned_id, fixture.generation)
            .await
            .unwrap();
        fs::remove_dir_all(fixture.root).unwrap();
    }

    #[tokio::test(flavor = "current_thread")]
    async fn suspend_skips_running_turn() {
        let fixture = fixture_manager_with_acp_session("suspend_running").await;
        {
            let mut sessions = fixture.manager.sessions.lock().unwrap();
            let session = sessions.get_mut(&fixture.owned_id).unwrap();
            session.active_turn_id = Some("turn-running".into());
            session.state = AgentRuntimeState::Working;
        }

        assert!(!fixture
            .manager
            .suspend_if_quiescent(&fixture.owned_id, fixture.generation)
            .await
            .unwrap());
        assert_eq!(fixture.manager.resource_roots().len(), 1);

        fixture
            .manager
            .close(&fixture.owned_id, fixture.generation)
            .await
            .unwrap();
        fs::remove_dir_all(fixture.root).unwrap();
    }

    #[tokio::test(flavor = "current_thread")]
    async fn suspend_skips_pending_permission() {
        let fixture = fixture_manager_with_acp_session("suspend_permission").await;
        fixture
            .manager
            .sessions
            .lock()
            .unwrap()
            .get_mut(&fixture.owned_id)
            .unwrap()
            .permission_requests
            .insert(
                "permission-1".into(),
                PendingPermission {
                    wire_id: json!(1),
                    options: Vec::new(),
                    summary: "Pending permission".into(),
                    command: None,
                    child_scoped: false,
                    event: pending_test_event(
                        &fixture.owned_id,
                        fixture.generation,
                        AgentConversationPayload::Approval {
                            request_id: "permission-1".into(),
                            state: ApprovalState::Requested,
                            summary: "Pending permission".into(), options: Vec::new(),
                        },
                    ),
                },
            );

        assert!(!fixture
            .manager
            .suspend_if_quiescent(&fixture.owned_id, fixture.generation)
            .await
            .unwrap());
        assert_eq!(fixture.manager.resource_roots().len(), 1);

        fixture
            .manager
            .close(&fixture.owned_id, fixture.generation)
            .await
            .unwrap();
        fs::remove_dir_all(fixture.root).unwrap();
    }

    #[tokio::test(flavor = "current_thread")]
    async fn selected_snapshot_carries_a_pending_request_outside_the_item_page() {
        let fixture = fixture_manager_with_acp_session("selected_pending").await;
        let pending = {
            let mut sessions = fixture.manager.sessions.lock().unwrap();
            let session = sessions.get_mut(&fixture.owned_id).unwrap();
            let event = record_payload_for_session(
                session,
                AgentConversationPayload::UserInputRequested {
                    request_id: "input-selected".into(),
                    title: "Input requested".into(),
                    description: None,
                    fields: Vec::new(),
                    can_decline: false,
                },
            )
            .unwrap();
            session.user_input_requests.insert(
                "input-selected".into(),
                PendingUserInput {
                    wire_id: json!(1),
                    response_shape: UserInputResponseShape::Legacy,
                    child_scoped: false,
                    event: event.clone(),
                },
            );
            record_payload_for_session(
                session,
                AgentConversationPayload::AssistantMessage {
                    item_id: "newer-message".into(),
                    text: "newer selected item".into(),
                    completed: true,
                    blocks: None,
                },
            )
            .unwrap();
            event
        };

        let snapshot = fixture
            .manager
            .latest_selection_snapshot(&fixture.owned_id, 1, 1)
            .unwrap()
            .unwrap();
        assert_eq!(snapshot.pending_events, vec![pending.clone()]);
        assert!(snapshot.pending_sequence >= pending.sequence);
        assert!(snapshot
            .page
            .events
            .iter()
            .all(|event| event.sequence != pending.sequence));

        fixture
            .manager
            .sessions
            .lock()
            .unwrap()
            .get_mut(&fixture.owned_id)
            .unwrap()
            .user_input_requests
            .clear();
        fixture
            .manager
            .close(&fixture.owned_id, fixture.generation)
            .await
            .unwrap();
        fs::remove_dir_all(fixture.root).unwrap();
    }

    #[test]
    fn ensure_records_the_project_id_once() {
        let root = temp_root();
        let other = root.join("other");
        fs::create_dir_all(&other).unwrap();
        let manager = AgentRuntimeManager::open(ProviderRegistry::default(), &root.join("sessions.db")).unwrap();
        let owned_id = "owned-project";
        let mut first = request(root.to_str().unwrap(), owned_id, AgentConversationProvider::Codex);
        first.project_id = Some("p1".into());
        manager.ensure_inner(first).unwrap();
        let mut second = request(other.to_str().unwrap(), owned_id, AgentConversationProvider::Codex);
        second.project_id = Some("p2".into());
        let (connection, prior) = manager.ensure_inner(second).unwrap();
        assert!(prior.is_some());
        assert_eq!(connection.generation, 2);
        let listed = manager.list_sessions().unwrap();
        assert_eq!(listed.iter().find(|record| record.owned_id == owned_id).unwrap().project_id.as_deref(), Some("p1"));
        assert_eq!(manager.store.get_session(owned_id).unwrap().unwrap().project_id.as_deref(), Some("p1"));
        fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn fresh_selection_restores_latest_child_descriptors_outside_the_item_page() {
        for provider in [
            AgentConversationProvider::Claude,
            AgentConversationProvider::Codex,
        ] {
            let root = temp_root();
            let database = root.join("sessions.db");
            let manager = AgentRuntimeManager::open(ProviderRegistry::default(), &database).unwrap();
            let owned_id = format!("owned-child-restore-{provider:?}");
            manager
                .ensure_inner(request(root.to_str().unwrap(), &owned_id, provider))
                .unwrap();
            {
                let mut sessions = manager.sessions.lock().unwrap();
                let session = sessions.get_mut(&owned_id).unwrap();
                record_payload_for_session(
                    session,
                    AgentConversationPayload::ChildUpdate {
                        child_id: "child-1".into(),
                        parent_tool_call_id: "parent-tool".into(),
                        parent_id: None,
                        transcript_id: Some("durable-child-1".into()),
                        label: Some("child".into()),
                        state: "running".into(),
                        latest_activity: Some("Running".into()),
                    },
                )
                .unwrap();
                record_payload_for_session(
                    session,
                    AgentConversationPayload::ChildUpdate {
                        child_id: "child-1".into(),
                        parent_tool_call_id: "parent-tool".into(),
                        parent_id: None,
                        transcript_id: Some("durable-child-1".into()),
                        label: Some("child".into()),
                        state: "finished".into(),
                        latest_activity: Some("Finished".into()),
                    },
                )
                .unwrap();
                record_payload_for_session(
                    session,
                    AgentConversationPayload::AssistantMessage {
                        item_id: "newer-message".into(),
                        text: "newer content".repeat(256),
                        completed: true,
                        blocks: None,
                    },
                )
                .unwrap();
            }
            drop(manager);

            let reopened =
                AgentRuntimeManager::open(ProviderRegistry::default(), &database).unwrap();
            let snapshot = reopened
                .latest_selection_snapshot(&owned_id, 1, 1)
                .unwrap()
                .unwrap();
            assert!(snapshot.page.events.iter().all(|event| {
                !matches!(event.payload, AgentConversationPayload::ChildUpdate { .. })
            }));
            assert_eq!(snapshot.pending_events.len(), 1);
            assert!(matches!(
                &snapshot.pending_events[0].payload,
                AgentConversationPayload::ChildUpdate { child_id, state, .. }
                    if child_id == "child-1" && state == "finished"
            ));
            drop(reopened);
            fs::remove_dir_all(root).unwrap();
        }
    }

    #[test]
    fn child_created_before_its_transcript_has_a_valid_empty_selection() {
        let root = temp_root();
        let manager = AgentRuntimeManager::new(ProviderRegistry::default());
        let parent = manager
            .ensure_inner(request(
                root.to_str().unwrap(),
                "parent-before-child-file",
                AgentConversationProvider::Codex,
            ))
            .unwrap()
            .0;
        let mut parent_row = manager.store.get_session(&parent.owned_id).unwrap().unwrap();
        parent_row.native_session_id = Some("native-parent-before-child-file".into());
        manager.store.upsert_session(&parent_row).unwrap();
        let child_owned_id = super::super::history::ensure_child_import(
            &manager.store,
            &parent.owned_id,
            None,
            "child-file-created-later",
        )
        .unwrap();

        let selection = manager
            .latest_selection_snapshot(&child_owned_id, 1024, 1024)
            .unwrap()
            .unwrap();
        assert_eq!(selection.connection.owned_id, child_owned_id);
        assert!(selection.page.events.is_empty());
        assert!(selection.page.items.is_empty());
        fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn cancelling_an_unrelated_newer_request_keeps_the_snapshot_read_in_flight() {
        let root = temp_root();
        let manager = AgentRuntimeManager::new(ProviderRegistry::default());
        let owned_id = manager
            .ensure_inner(request(
                root.to_str().unwrap(),
                "snapshot-in-flight",
                AgentConversationProvider::Codex,
            ))
            .unwrap()
            .0
            .owned_id;

        let selection = std::thread::scope(|scope| {
            // Holding the session lock parks the read after it has taken
            // ownership of request 10 and before it finishes.
            let sessions = manager.sessions.lock().unwrap();
            let read =
                scope.spawn(|| manager.latest_selection_snapshot(&owned_id, 10, 1024));
            while manager.latest_snapshot_request.load(Ordering::Acquire) != 10 {
                std::thread::yield_now();
            }
            manager.cancel_snapshot(11);
            drop(sessions);
            read.join().unwrap()
        });

        assert!(selection.unwrap().is_some());
        fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn cancelling_a_snapshot_read_in_flight_leaves_the_next_id_free() {
        let root = temp_root();
        let manager = AgentRuntimeManager::new(ProviderRegistry::default());
        let owned_id = manager
            .ensure_inner(request(
                root.to_str().unwrap(),
                "snapshot-cancelled-in-flight",
                AgentConversationProvider::Codex,
            ))
            .unwrap()
            .0
            .owned_id;

        let cancelled = std::thread::scope(|scope| {
            let sessions = manager.sessions.lock().unwrap();
            let read =
                scope.spawn(|| manager.latest_selection_snapshot(&owned_id, 10, 1024));
            while manager.latest_snapshot_request.load(Ordering::Acquire) != 10 {
                std::thread::yield_now();
            }
            manager.cancel_snapshot(10);
            drop(sessions);
            read.join().unwrap()
        });

        assert!(cancelled.unwrap().is_none());
        assert!(manager
            .latest_selection_snapshot(&owned_id, 11, 1024)
            .unwrap()
            .is_some());
        fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn cancel_that_arrives_before_its_snapshot_read_still_stops_it() {
        let root = temp_root();
        let manager = AgentRuntimeManager::new(ProviderRegistry::default());
        let owned_id = manager
            .ensure_inner(request(
                root.to_str().unwrap(),
                "snapshot-cancelled-early",
                AgentConversationProvider::Codex,
            ))
            .unwrap()
            .0
            .owned_id;

        manager.cancel_snapshot(5);
        assert!(manager
            .latest_selection_snapshot(&owned_id, 5, 1024)
            .unwrap()
            .is_none());
        assert!(manager
            .latest_selection_snapshot(&owned_id, 6, 1024)
            .unwrap()
            .is_some());
        fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn child_watch_wakes_for_a_completed_append_but_not_for_a_read() {
        let path = PathBuf::from("rollout-child-1.jsonl");
        let read = notify::Event::new(notify::EventKind::Access(
            notify::event::AccessKind::Read,
        ))
        .add_path(path.clone());
        let completed_append = notify::Event::new(notify::EventKind::Access(
            notify::event::AccessKind::Close(notify::event::AccessMode::Write),
        ))
        .add_path(path.clone());

        assert!(!child_watch_event_relevant(
            &read,
            Some(&path),
            AgentConversationProvider::Codex,
            "child-1",
        ));
        assert!(child_watch_event_relevant(
            &completed_append,
            Some(&path),
            AgentConversationProvider::Codex,
            "child-1",
        ));
    }

    #[test]
    fn stopped_or_replaced_child_selection_cannot_install_a_late_watcher() {
        let manager = AgentRuntimeManager::new(ProviderRegistry::default());
        manager.reserve_child_history_request("parent", 1);
        assert!(manager.stop_child_history("parent", 1));
        assert!(manager
            .select_reserved_child_history("parent", "child", 1, 1024, 1024, 100)
            .is_err());

        manager.reserve_child_history_request("parent", 1);
        manager.reserve_child_history_request("parent", 2);
        assert!(manager
            .select_reserved_child_history("parent", "child", 1, 1024, 1024, 100)
            .is_err());
        assert_eq!(manager.child_history_watchers.lock().unwrap()["parent"].request_id, 2);
        assert!(manager.stop_child_history("parent", 2));
        assert!(manager.child_history_watchers.lock().unwrap().is_empty());
    }

    #[tokio::test(flavor = "current_thread")]
    async fn suspend_skips_pending_user_input() {
        let fixture = fixture_manager_with_acp_session("suspend_input").await;
        fixture
            .manager
            .sessions
            .lock()
            .unwrap()
            .get_mut(&fixture.owned_id)
            .unwrap()
            .user_input_requests
            .insert(
                "input-1".into(),
                PendingUserInput {
                    wire_id: json!(1),
                    response_shape: UserInputResponseShape::Legacy,
                    child_scoped: false,
                    event: pending_test_event(
                        &fixture.owned_id,
                        fixture.generation,
                        AgentConversationPayload::UserInputRequested {
                            request_id: "input-1".into(),
                            title: "Input requested".into(),
                            description: None,
                            fields: Vec::new(),
                            can_decline: false,
                        },
                    ),
                },
            );

        assert!(!fixture
            .manager
            .suspend_if_quiescent(&fixture.owned_id, fixture.generation)
            .await
            .unwrap());
        assert_eq!(fixture.manager.resource_roots().len(), 1);

        fixture
            .manager
            .close(&fixture.owned_id, fixture.generation)
            .await
            .unwrap();
        fs::remove_dir_all(fixture.root).unwrap();
    }

    #[tokio::test(flavor = "current_thread")]
    async fn suspend_closes_runtime_but_keeps_record_and_native_id() {
        let fixture = fixture_manager_with_acp_session("suspend_runtime").await;
        let pid = fixture.manager.resource_roots()[0].pid;
        let native_session_id = fixture
            .manager
            .snapshot(&fixture.owned_id)
            .unwrap()
            .unwrap()
            .connection
            .native_session_id
            .unwrap();

        assert!(fixture
            .manager
            .suspend_if_quiescent(&fixture.owned_id, fixture.generation)
            .await
            .unwrap());
        wait_until(|| !process_is_alive(pid)).await;
        assert!(fixture.manager.resource_roots().is_empty());
        let snapshot = fixture
            .manager
            .snapshot(&fixture.owned_id)
            .unwrap()
            .unwrap();
        assert!(snapshot.suspended);
        assert_eq!(
            snapshot.connection.native_session_id.as_deref(),
            Some(native_session_id.as_str())
        );
        assert_eq!(
            snapshot.connection.state,
            ConversationConnectionState::Disconnected
        );
        assert!(fixture
            .manager
            .sessions
            .lock()
            .unwrap()
            .get(&fixture.owned_id)
            .is_none());

        fixture
            .manager
            .close(&fixture.owned_id, fixture.generation)
            .await
            .unwrap();
        fs::remove_dir_all(fixture.root).unwrap();
    }

    #[tokio::test(flavor = "current_thread")]
    async fn activation_refreshes_and_persists_a_stale_capability_snapshot() {
        let fixture = fixture_manager_with_acp_session("suspend_stale_capabilities").await;
        assert!(fixture
            .manager
            .suspend_if_quiescent(&fixture.owned_id, fixture.generation)
            .await
            .unwrap());
        // A session started before the provider gained image prompts keeps that
        // answer in its stored row, so the row is rewritten the same way here.
        fixture
            .manager
            .hydrate_overlay_from_store(&fixture.owned_id)
            .unwrap();
        {
            let mut sessions = fixture.manager.sessions.lock().unwrap();
            let session = sessions.get_mut(&fixture.owned_id).unwrap();
            session.capabilities.prompt.image = false;
            persist_session(session).unwrap();
        }
        assert!(!stored_capabilities(&fixture).prompt.image);

        let connection = fixture
            .manager
            .ensure_async(request(
                fixture.root.to_str().unwrap(),
                &fixture.owned_id,
                AgentConversationProvider::Codex,
            ))
            .await
            .expect("ensure suspended session");
        fixture
            .manager
            .activate(&fixture.owned_id, connection.generation)
            .await
            .expect("activate suspended session");

        assert!(
            fixture
                .manager
                .capabilities_for_owned_id(&fixture.owned_id)
                .unwrap()
                .prompt
                .image
        );
        assert!(stored_capabilities(&fixture).prompt.image);

        fixture
            .manager
            .close(&fixture.owned_id, fixture.generation)
            .await
            .unwrap();
        fs::remove_dir_all(fixture.root).unwrap();
    }

    fn stored_capabilities(fixture: &FixtureManager) -> AgentCapabilities {
        let row = fixture
            .manager
            .store
            .get_session(&fixture.owned_id)
            .unwrap()
            .expect("stored session row");
        serde_json::from_str::<StoredSessionExtra>(&row.extra_json)
            .unwrap()
            .capabilities
    }

    #[tokio::test(flavor = "current_thread")]
    async fn first_ready_connection_exposes_advertised_steering() {
        let root = temp_root();
        let log = root.join("steering.jsonl");
        let manifest =
            super::super::providers::acp_client::tests::fixture_manifest_named(&log, "steering");
        let providers = ProviderRegistry::new([(AgentConversationProvider::Claude, manifest)])
            .expect("fixture provider");
        let manager = AgentRuntimeManager::new(providers);
        let owned_id = "owned-first-ready-steering";
        let connection = manager
            .ensure_inner(request(
                root.to_str().unwrap(),
                owned_id,
                AgentConversationProvider::Claude,
            ))
            .expect("ensure")
            .0;
        let store = Arc::clone(&manager.store);
        let observed = Arc::new(Mutex::new(Vec::new()));
        let sink = Arc::clone(&observed);
        manager.set_emitter(Arc::new(move |event| {
            if let AgentConversationPayload::Connection {
                state: ConversationConnectionState::Connected,
                ..
            } = &event.payload
            {
                let row = store
                    .get_session(&event.owned_id)
                    .expect("read ready session")
                    .expect("persisted ready session");
                let stored: StoredSessionExtra =
                    serde_json::from_str(&row.extra_json).expect("stored capabilities");
                sink.lock().unwrap().push(stored.capabilities.session.steering);
            }
        }));

        manager
            .activate(owned_id, connection.generation)
            .await
            .expect("first activation");

        let observed = observed.lock().unwrap();
        assert_eq!(observed.len(), 1);
        assert!(observed[0], "steering must be readable when ready is emitted");
        drop(observed);
        manager.close(owned_id, connection.generation).await.unwrap();
        fs::remove_dir_all(root).unwrap();
    }

    #[tokio::test(flavor = "current_thread")]
    async fn ensure_after_suspend_resumes_with_same_native_session_id() {
        let fixture = fixture_manager_with_acp_session("suspend_resume").await;
        let native_session_id = fixture
            .manager
            .snapshot(&fixture.owned_id)
            .unwrap()
            .unwrap()
            .connection
            .native_session_id
            .unwrap();
        fixture
            .manager
            .suspend_if_quiescent(&fixture.owned_id, fixture.generation)
            .await
            .unwrap();
        let sequence_before_resume = fixture
            .manager
            .snapshot(&fixture.owned_id)
            .unwrap()
            .unwrap()
            .last_sequence;

        let connection = fixture
            .manager
            .ensure_async(request(
                fixture.root.to_str().unwrap(),
                &fixture.owned_id,
                AgentConversationProvider::Codex,
            ))
            .await
            .expect("ensure suspended session");
        let resumed = fixture
            .manager
            .activate(&fixture.owned_id, connection.generation)
            .await
            .expect("resume suspended session");
        assert_eq!(
            resumed.native_session_id.as_deref(),
            Some(native_session_id.as_str())
        );
        assert_eq!(resumed.generation, fixture.generation);
        fixture
            .manager
            .prompt(
                &fixture.owned_id,
                fixture.generation,
                test_prompt("after suspend"),
            )
            .await
            .expect("prompt after resume");
        wait_until(|| {
            fixture
                .manager
                .snapshot(&fixture.owned_id)
                .unwrap()
                .is_some_and(|snapshot| {
                    snapshot.suspended && snapshot.last_sequence > sequence_before_resume
                })
        })
        .await;
        let snapshot = fixture
            .manager
            .snapshot(&fixture.owned_id)
            .unwrap()
            .unwrap();
        assert!(snapshot.suspended);
        assert_eq!(
            snapshot.connection.state,
            ConversationConnectionState::Disconnected
        );
        let connection_states = snapshot
            .events
            .iter()
            .filter_map(|event| match &event.payload {
                AgentConversationPayload::Connection { state, .. } => Some(*state),
                _ => None,
            })
            .collect::<Vec<_>>();
        assert_eq!(
            connection_states,
            [
                ConversationConnectionState::Connected,
                ConversationConnectionState::Disconnected,
                ConversationConnectionState::Connected,
                ConversationConnectionState::Disconnected
            ]
        );
        let resumed_events = fixture
            .manager
            .list_events(&fixture.owned_id, sequence_before_resume)
            .unwrap()
            .into_iter()
            .filter(|event| event.sequence > sequence_before_resume)
            .collect::<Vec<_>>();
        assert_eq!(resumed_events[0].sequence, sequence_before_resume + 1);
        assert!(resumed_events
            .windows(2)
            .all(|events| events[1].sequence == events[0].sequence + 1));
        assert!(fixture.manager.resource_roots().is_empty());
        let log = fs::read_to_string(fixture.root.join("suspend_resume.jsonl")).unwrap();
        assert_eq!(log.matches(r#""method":"session/new""#).count(), 1);
        assert_eq!(log.matches(r#""method":"session/resume""#).count(), 1);
        assert_eq!(log.matches(r#""method":"session/prompt""#).count(), 1);

        fixture
            .manager
            .close(&fixture.owned_id, fixture.generation)
            .await
            .unwrap();
        fs::remove_dir_all(fixture.root).unwrap();
    }

    #[tokio::test(flavor = "current_thread")]
    async fn workflow_step_session_joins_its_project_and_is_announced() {
        let fixture = fixture_manager_with_acp_session("multiplex").await;
        let store = Arc::clone(&fixture.manager.store);
        store.insert_or_get_project(&mcb_core::session_store::ProjectRow {
            id: "project-demo".into(), machine: "local".into(),
            root_path: fixture.root.to_string_lossy().into_owned(), title: "demo".into(),
            repo_key: String::new(), created_at_ms: 1, group_key: String::new(),
            pinned_at_ms: None, last_used_ms: None,
        }).unwrap();
        let announced = Arc::new(Mutex::new(Vec::<(String, String)>::new()));
        let heard = Arc::clone(&announced);
        fixture.manager.set_session_renamed_listener(Arc::new(move |owned_id, title| {
            heard.lock().unwrap().push((owned_id.to_string(), title.to_string()));
        }));
        let worktree = fixture.root.join("step-worktree");
        std::fs::create_dir_all(&worktree).unwrap();
        crate::workflow::AgentRuntimePort::dispatch(&fixture.manager, crate::workflow::AgentDispatchRequest {
            owned_id: "owned-step".into(), provider: "codex".into(),
            cwd: worktree.to_string_lossy().into_owned(), title: "Build · build".into(),
            prompt: "Build it".into(), role_id: "build".into(), model: None, effort: None,
            permission: None, starter: None, policy: Default::default(),
        }).await.expect("dispatch");
        let row = store.get_session("owned-step").unwrap().unwrap();
        assert_eq!(row.project_id.as_deref(), Some("project-demo"));
        assert_eq!(*announced.lock().unwrap(), [("owned-step".to_string(), "Build · build".to_string())]);
        // The worker's chat shows the task as written, not a JSON wrapper.
        let events = fixture.manager.snapshot("owned-step").unwrap().unwrap().events;
        assert!(events.iter().any(|event| matches!(&event.payload,
            AgentConversationPayload::UserMessage { text, .. } if text == "Build it")), "{events:?}");
    }

    #[tokio::test(flavor = "current_thread")]
    async fn start_session_creates_named_child_in_callers_project() {
        let fixture = fixture_manager_with_acp_session("multiplex").await;
        let store = Arc::clone(&fixture.manager.store);
        let endpoint = super::super::messaging::MessagingEndpoint::start().unwrap();
        let caller = endpoint.issue(&fixture.owned_id);
        endpoint.bind_store(&caller.token, &store, Some(fixture.manager.clone()));
        let result = endpoint
            .handle(&caller.token, json!({"name":"start_session","arguments":{
                "provider":"codex","name":"Reviewer","prompt":"Check the patch"}}))
            .await
            .expect("start helper");
        let started: Value = serde_json::from_str(result["content"][0]["text"].as_str().unwrap()).unwrap();
        assert_eq!(started["name"], "Reviewer");
        assert!(started["handle"].as_str().unwrap().contains('-'), "{started}");
        let helper = started["owned_id"].as_str().unwrap().to_string();
        let parent_row = store.get_session(&fixture.owned_id).unwrap().unwrap();
        let helper_row = store.get_session(&helper).unwrap().unwrap();
        assert_eq!(helper_row.cwd, parent_row.cwd);
        assert_eq!(helper_row.project_id, parent_row.project_id);
        assert_eq!(store.resolve_session_name("Reviewer").unwrap(), helper);
        let parent_events = || {
            fixture.manager.snapshot(&fixture.owned_id).unwrap().unwrap().events
        };
        assert!(parent_events().iter().any(|event| matches!(&event.payload,
            AgentConversationPayload::ChildUpdate { child_id, label, state, .. }
                if *child_id == helper && label.as_deref() == Some("Reviewer") && state == "running")));

        // The helper's finished turn is reported to the parent and wakes it.
        let expected = format!("[message from {} · Reviewer]\n[Reviewer finished]\nBRAVO", started["handle"].as_str().unwrap());
        wait_until(|| parent_events().iter().any(|event| matches!(&event.payload,
            AgentConversationPayload::UserMessage { text, .. } if *text == expected))).await;
        assert!(parent_events().iter().any(|event| matches!(&event.payload,
            AgentConversationPayload::ChildUpdate { child_id, state, .. }
                if *child_id == helper && state == "finished")));
        // The parent is not a helper, so its own turn reports to no one.
        wait_until(|| fixture.manager.sessions.lock().unwrap()
            .get(&fixture.owned_id).is_none_or(|session| session.active_turn_id.is_none())).await;
        assert!(store.pending_for(&helper).unwrap().is_none());

        let helper_generation = fixture.manager.sessions.lock().unwrap()
            .get(&helper).map(|session| session.generation);
        if let Some(generation) = helper_generation {
            fixture.manager.close(&helper, generation).await.unwrap();
        }
        endpoint.revoke(&fixture.owned_id);
        fixture
            .manager
            .close(&fixture.owned_id, fixture.generation)
            .await
            .unwrap();
        fs::remove_dir_all(fixture.root).unwrap();
    }

    #[tokio::test(flavor = "current_thread")]
    async fn a_helper_starts_in_auto_even_when_its_parent_is_in_default() {
        #[allow(non_snake_case)] let (Claude, Codex) = (AgentConversationProvider::Claude, AgentConversationProvider::Codex);
        for (parent_provider, parent_mode, helper_provider, log, asked) in [
            (Claude, Some("default"), "claude", "config_options", r#""modeId":"auto""#),
            (Claude, None, "codex", "multiplex", r#""approvalPolicy":"agent""#),
            (Codex, Some("read-only"), "claude", "config_options", r#""modeId":"auto""#),
        ] {
            let root = temp_root();
            let manifest = |fixture: &str| super::super::providers::acp_client::tests::fixture_manifest_named(
                &root.join(format!("{fixture}.jsonl")), fixture);
            let manager = AgentRuntimeManager::new(ProviderRegistry::new([
                (Codex, manifest("multiplex")), (Claude, manifest("config_options"))]).unwrap());
            let parent = "owned-parent".to_string();
            let generation = manager.ensure_inner(request(root.to_str().unwrap(), &parent, parent_provider))
                .unwrap().0.generation;
            manager.activate(&parent, generation).await.unwrap();
            manager.sessions.lock().unwrap().get_mut(&parent).unwrap()
                .config.approval_policy = parent_mode.map(str::to_string);
            let store = Arc::clone(&manager.store);
            let endpoint = super::super::messaging::MessagingEndpoint::start().unwrap();
            let caller = endpoint.issue(&parent);
            endpoint.bind_store(&caller.token, &store, Some(manager.clone()));
            let result = endpoint.handle(&caller.token, json!({"name":"start_session","arguments":{
                "provider":helper_provider,"name":"Helper","prompt":"Go"}})).await.expect("start helper");
            let started: Value = serde_json::from_str(result["content"][0]["text"].as_str().unwrap()).unwrap();
            let helper = started["owned_id"].as_str().unwrap().to_string();
            let frames = fs::read_to_string(root.join(format!("{log}.jsonl"))).unwrap();
            assert!(frames.contains(asked), "{parent_provider:?} in {parent_mode:?} starting a {helper_provider} helper: {frames}");
            let helper_generation = manager.sessions.lock().unwrap().get(&helper).map(|session| session.generation);
            if let Some(helper_generation) = helper_generation { manager.close(&helper, helper_generation).await.unwrap(); }
            endpoint.revoke(&parent);
            manager.close(&parent, generation).await.unwrap();
            fs::remove_dir_all(root).unwrap();
        }
    }

    #[tokio::test(flavor = "current_thread")]
    async fn helper_first_prompt_is_labelled_with_parent_and_starts_in_auto() {
        let fixture = fixture_manager_with_acp_session("multiplex").await;
        let store = Arc::clone(&fixture.manager.store);
        // The parent's own mode does not carry over: the helper asks for Auto.
        fixture.manager.sessions.lock().unwrap().get_mut(&fixture.owned_id).unwrap()
            .config.approval_policy = Some("untrusted".into());
        let endpoint = super::super::messaging::MessagingEndpoint::start().unwrap();
        let caller = endpoint.issue(&fixture.owned_id);
        endpoint.bind_store(&caller.token, &store, Some(fixture.manager.clone()));
        let result = endpoint
            .handle(&caller.token, json!({"name":"start_session","arguments":{
                "provider":"codex","name":"Labelled","prompt":"Check the patch"}}))
            .await
            .expect("start helper");
        let started: Value = serde_json::from_str(result["content"][0]["text"].as_str().unwrap()).unwrap();
        let helper = started["owned_id"].as_str().unwrap().to_string();
        let (handle, name) = store.directory_name(&fixture.owned_id).unwrap().unwrap();
        let expected = format!("[message from {handle} · {name}]\nCheck the patch");
        let helper_events = fixture.manager.snapshot(&helper).unwrap().unwrap().events;
        assert!(helper_events.iter().any(|event| matches!(&event.payload,
            AgentConversationPayload::UserMessage { text, .. } if *text == expected)), "{expected}");
        let log = fs::read_to_string(fixture.root.join("multiplex.jsonl")).unwrap();
        assert!(log.contains(r#""approvalPolicy":"agent""#), "{log}");
        let helper_generation = fixture.manager.sessions.lock().unwrap()
            .get(&helper).map(|session| session.generation);
        if let Some(generation) = helper_generation {
            fixture.manager.close(&helper, generation).await.unwrap();
        }
        endpoint.revoke(&fixture.owned_id);
        fixture.manager.close(&fixture.owned_id, fixture.generation).await.unwrap();
        fs::remove_dir_all(fixture.root).unwrap();
    }

    #[tokio::test(flavor = "current_thread")]
    async fn helper_whose_adapter_dies_reports_failure_to_parent() {
        let fixture = fixture_manager_with_acp_session("dies_midturn").await;
        let store = Arc::clone(&fixture.manager.store);
        let endpoint = super::super::messaging::MessagingEndpoint::start().unwrap();
        let caller = endpoint.issue(&fixture.owned_id);
        endpoint.bind_store(&caller.token, &store, Some(fixture.manager.clone()));
        let result = endpoint
            .handle(&caller.token, json!({"name":"start_session","arguments":{
                "provider":"codex","name":"Fragile","prompt":"Go"}}))
            .await;
        let parent_events = || {
            fixture.manager.snapshot(&fixture.owned_id).unwrap().unwrap().events
        };
        wait_until(|| parent_events().iter().any(|event| matches!(&event.payload,
            AgentConversationPayload::ChildUpdate { label, state, .. }
                if label.as_deref() == Some("Fragile") && state == "failed"))).await;
        if let Ok(result) = result {
            let started: Value = serde_json::from_str(result["content"][0]["text"].as_str().unwrap()).unwrap();
            let helper = started["owned_id"].as_str().unwrap().to_string();
            wait_until(|| parent_events().iter().any(|event| matches!(&event.payload,
                AgentConversationPayload::UserMessage { text, .. }
                    if text.starts_with(&format!("[message from {} · Fragile]\n[Fragile failed]", started["handle"].as_str().unwrap()))))).await;
            assert!(store.pending_for(&helper).unwrap().is_none());
        }
        endpoint.revoke(&fixture.owned_id);
        fs::remove_dir_all(fixture.root).unwrap();
    }

    #[tokio::test(flavor = "current_thread")]
    async fn idle_session_receives_a_message_without_waiting_for_a_turn() {
        let fixture = fixture_manager_with_acp_session("idle_message_delivery").await;
        let message = fixture
            .manager
            .store
            .append_message(
                "",
                "worker-a",
                &fixture.owned_id,
                mcb_core::broker::MessageKind::Message,
                "Are you free?",
            )
            .expect("queue message");
        // Work the session started on its own counts as busy.
        fixture.manager.sessions.lock().unwrap().get_mut(&fixture.owned_id).unwrap()
            .autonomous_turn_started = true;
        fixture.manager.deliver_if_idle(&fixture.owned_id).await;
        assert_eq!(fixture.manager.store.message_status(&message.id, "worker-a").unwrap(), "queued");
        fixture.manager.sessions.lock().unwrap().get_mut(&fixture.owned_id).unwrap()
            .autonomous_turn_started = false;
        fixture.manager.deliver_if_idle(&fixture.owned_id).await;
        assert_eq!(fixture.manager.store.message_status(&message.id, "worker-a").unwrap(), "delivered");
        let snapshot = fixture
            .manager
            .snapshot(&fixture.owned_id)
            .expect("read snapshot")
            .expect("session exists");
        assert!(snapshot.events.iter().any(|event| matches!(
            &event.payload,
            AgentConversationPayload::UserMessage { text, .. } if text == "[message from worker-a]\nAre you free?"
        )));
        fixture
            .manager
            .close(&fixture.owned_id, fixture.generation)
            .await
            .unwrap();
        fs::remove_dir_all(fixture.root).unwrap();
    }

    #[tokio::test(flavor = "current_thread")]
    async fn stale_message_fails_instead_of_delivering() {
        let fixture = fixture_manager_with_acp_session("stale_message_expiry").await;
        let group_id = fixture
            .manager
            .store
            .create_group(
                "workflow",
                Some("orchestrator"),
                &[fixture.owned_id.clone()],
            )
            .expect("create workflow group");
        let message = fixture
            .manager
            .store
            .append_message(
                &group_id,
                "worker-a",
                &fixture.owned_id,
                mcb_core::broker::MessageKind::Message,
                "Too old to deliver",
            )
            .expect("queue workflow message");

        fixture
            .manager
            .drain_broker_message_at(
                &fixture.owned_id,
                fixture.generation,
                message.created_at_ms + BROKER_MESSAGE_TTL_MS + 1,
            )
            .await
            .expect("drain stale message");

        let stored = fixture
            .manager
            .store
            .events_for(&group_id, None)
            .expect("read workflow events")
            .into_iter()
            .find(|event| event.id == message.id)
            .expect("queued message remains stored");
        assert_eq!(stored.receipt, mcb_core::broker::Receipt::Failed);
        let log = fs::read_to_string(fixture.root.join("stale_message_expiry.jsonl"))
            .expect("read fixture log");
        assert!(!log.contains("Too old to deliver"));

        fixture
            .manager
            .close(&fixture.owned_id, fixture.generation)
            .await
            .unwrap();
        fs::remove_dir_all(fixture.root).unwrap();
    }

    #[tokio::test(flavor = "current_thread")]
    async fn resume_does_not_append_provider_history_to_an_existing_journal() {
        let fixture = fixture_manager_with_acp_session("replay_on_resume").await;
        {
            let mut sessions = fixture
                .manager
                .sessions
                .lock()
                .unwrap_or_else(std::sync::PoisonError::into_inner);
            let session = sessions.get_mut(&fixture.owned_id).unwrap();
            record_payload_for_session(
                session,
                AgentConversationPayload::AssistantMessage {
                    item_id: "live-message".into(),
                    text: "canonical answer".into(),
                    completed: true,
                    blocks: None,
                },
            )
            .unwrap();
        }
        fixture
            .manager
            .suspend_if_quiescent(&fixture.owned_id, fixture.generation)
            .await
            .unwrap();

        let connection = fixture
            .manager
            .ensure_async(request(
                fixture.root.to_str().unwrap(),
                &fixture.owned_id,
                AgentConversationProvider::Codex,
            ))
            .await
            .expect("ensure suspended session");
        fixture
            .manager
            .activate(&fixture.owned_id, connection.generation)
            .await
            .expect("resume suspended session");
        tokio::time::sleep(Duration::from_millis(50)).await;

        let snapshot = fixture
            .manager
            .snapshot(&fixture.owned_id)
            .unwrap()
            .unwrap();
        let item_ids = snapshot
            .events
            .iter()
            .filter_map(|event| match &event.payload {
                AgentConversationPayload::UserMessage { item_id, .. }
                | AgentConversationPayload::AssistantDelta { item_id, .. }
                | AgentConversationPayload::AssistantMessage { item_id, .. }
                | AgentConversationPayload::Tool { item_id, .. } => Some(item_id.as_str()),
                _ => None,
            })
            .collect::<Vec<_>>();
        assert_eq!(item_ids, ["live-message"]);

        fixture
            .manager
            .close(&fixture.owned_id, fixture.generation)
            .await
            .unwrap();
        fs::remove_dir_all(fixture.root).unwrap();
    }

    #[tokio::test(flavor = "current_thread")]
    async fn suspend_skips_provider_without_resume_capability() {
        let fixture = fixture_manager_with_acp_session("suspend_no_resume").await;
        assert!(
            !fixture
                .manager
                .capabilities(&fixture.owned_id, fixture.generation)
                .unwrap()
                .session
                .resume
        );

        assert!(!fixture
            .manager
            .suspend_if_quiescent(&fixture.owned_id, fixture.generation)
            .await
            .unwrap());
        assert_eq!(fixture.manager.resource_roots().len(), 1);

        fixture
            .manager
            .close(&fixture.owned_id, fixture.generation)
            .await
            .unwrap();
        fs::remove_dir_all(fixture.root).unwrap();
    }

    #[tokio::test(flavor = "current_thread")]
    async fn quiescence_gates_teardown() {
        let fixture = fixture_manager_with_acp_session("suspend_quiescence").await;
        {
            let mut sessions = fixture.manager.sessions.lock().unwrap();
            let session = sessions.get_mut(&fixture.owned_id).unwrap();
            session.state = AgentRuntimeState::Working;
            session.active_turn_id = Some("turn-live".into());
            session.live_tool_calls.insert("tool-live".into(), String::new());
        }
        assert!(!fixture
            .manager
            .suspend_if_quiescent(&fixture.owned_id, fixture.generation)
            .await
            .unwrap());
        assert_eq!(fixture.manager.resource_roots().len(), 1);

        {
            let mut sessions = fixture.manager.sessions.lock().unwrap();
            let session = sessions.get_mut(&fixture.owned_id).unwrap();
            session.state = AgentRuntimeState::Ready;
            session.active_turn_id = None;
            session.prompt_once_active = false;
            session.permission_requests.clear();
            session.user_input_requests.clear();
            session.writer_lease_transition = None;
            session.live_tool_calls.clear();
            session.background_work.clear();
        }
        assert!(fixture
            .manager
            .suspend_if_quiescent(&fixture.owned_id, fixture.generation)
            .await
            .unwrap());
        assert!(fixture.manager.resource_roots().is_empty());

        fixture
            .manager
            .close(&fixture.owned_id, fixture.generation)
            .await
            .unwrap();
        fs::remove_dir_all(fixture.root).unwrap();
    }

    #[test]
    fn successful_claude_task_stop_clears_background_work() {
        let task_id = "bxjxskfnq";
        let item_id = format!("background-task:{task_id}");
        let mut background_work = HashMap::from([(
            item_id.clone(),
            BackgroundWorkEntry {
                kind: BackgroundWorkKind::Command,
                label: "sleep 90".into(),
                started_at_ms: 1,
            },
        )]);
        let failed = serde_json::json!({"update": {
            "sessionUpdate": "tool_call_update", "status": "failed",
            "_meta": {"claudeCode": {"toolName": "TaskStop"}},
            "rawOutput": r#"{"message":"Successfully stopped task: bxjxskfnq (sleep 90)","task_id":"bxjxskfnq"}"#
        }});
        assert!(stopped_background_task_payload(&mut background_work, &failed).is_none());
        assert!(background_work.contains_key(&item_id));

        let stopped = serde_json::json!({"update": {
            "sessionUpdate": "tool_call_update", "status": "completed",
            "_meta": {"claudeCode": {"toolName": "TaskStop"}},
            "rawOutput": r#"{"message":"Successfully stopped task: bxjxskfnq (sleep 90)","task_id":"bxjxskfnq"}"#
        }});
        assert!(matches!(
            stopped_background_task_payload(&mut background_work, &stopped),
            Some(AgentConversationPayload::Tool { item_id: id, state: ToolState::Failed, .. }) if id == item_id
        ));
        assert!(background_work.is_empty());
        assert!(stopped_background_task_payload(&mut background_work, &stopped).is_none());
    }

    #[tokio::test(flavor = "current_thread")]
    async fn background_task_keeps_runtime_until_terminal_update() {
        let fixture = fixture_manager_with_acp_session("suspend_background_task_liveness").await;
        let spawned = serde_json::json!({"update": {
            "sessionUpdate": "async_task_spawned", "asyncTaskId": "task-1",
            "name": "Background command"
        }});
        let finished = serde_json::json!({"update": {
            "sessionUpdate": "async_task_state_update", "asyncTaskId": "task-1",
            "state": "completed"
        }});
        {
            let mut sessions = fixture.manager.sessions.lock().unwrap();
            let session = sessions.get_mut(&fixture.owned_id).unwrap();
            assert!(!update_raw_liveness(session, &spawned));
            assert_eq!(session.background_work.len(), 1);
            let item = &session.background_work["background-task:task-1"];
            assert_eq!(item.kind, BackgroundWorkKind::Command);
            assert_eq!(item.label, "Background command");
            assert!(item.started_at_ms > 0);
        }
        assert!(!fixture.manager.suspend_if_quiescent(&fixture.owned_id, fixture.generation).await.unwrap());
        assert!(matches!(background_task_payload(&finished), Some(AgentConversationPayload::Tool {
            state: ToolState::Completed, ..
        })));
        for state in ["failed", "stopped", "cancelled"] {
            let update = serde_json::json!({"update": {
                "sessionUpdate": "async_task_state_update", "asyncTaskId": "task-1", "state": state
            }});
            assert!(matches!(background_task_payload(&update), Some(AgentConversationPayload::Tool {
                state: ToolState::Failed, ..
            })));
            assert!(!raw_update_failed(&update));
        }
        let output = fixture.root.join("background.output");
        for (code, expected) in [(0, ToolState::Completed), (7, ToolState::Failed)] {
            fs::write(
                &output,
                format!("command output\n\n[exited with code {code}]\n"),
            )
            .unwrap();
            let stopped = serde_json::json!({"update": {
                "sessionUpdate": "async_task_state_update", "asyncTaskId": "task-1",
                "state": "stopped", "outputFilePath": output
            }});
            assert!(matches!(background_task_payload(&stopped), Some(AgentConversationPayload::Tool {
                state, ..
            }) if state == expected));
        }
        {
            let mut sessions = fixture.manager.sessions.lock().unwrap();
            let session = sessions.get_mut(&fixture.owned_id).unwrap();
            assert!(update_raw_liveness(session, &finished));
            assert!(session.background_work.is_empty());
        }
        assert!(fixture.manager.suspend_if_quiescent(&fixture.owned_id, fixture.generation).await.unwrap());
        assert!(fixture.manager.resource_roots().is_empty());
        fixture.manager.close(&fixture.owned_id, fixture.generation).await.unwrap();
        fs::remove_dir_all(fixture.root).unwrap();
    }

    #[tokio::test(flavor = "current_thread")]
    async fn owned_session_record_exposes_background_work_metadata() {
        let fixture = fixture_manager_with_provider(
            "owned_session_background_work",
            AgentConversationProvider::Claude,
            None,
        )
        .await;
        let (child_started_at, command_started_at) = {
            let mut sessions = fixture.manager.sessions.lock().unwrap();
            let session = sessions.get_mut(&fixture.owned_id).unwrap();
            let native_session_id = session.native_session_id.clone().unwrap();
            let child = serde_json::json!({
                "sessionId": native_session_id,
                "update": {
                    "sessionUpdate": "subagent_spawned",
                    "subagentSessionId": "agent-a",
                    "name": "Reviewer"
                }
            });
            assert!(claude_native_child_payload(session, &child).is_some());
            std::thread::sleep(Duration::from_millis(2));
            let command = serde_json::json!({"update": {
                "sessionUpdate": "async_task_spawned",
                "asyncTaskId": "task-1",
                "name": "Run checks"
            }});
            assert!(!update_raw_liveness(session, &command));
            insert_background_work(
                &mut session.background_work,
                CLAUDE_SESSION_RUNNING.to_string(),
                BackgroundWorkKind::Command,
                String::new(),
            );
            drop(sessions);

            let value = serde_json::to_value(&fixture.manager.list_sessions().unwrap()[0]).unwrap();
            let work = value["backgroundWork"].as_array().expect("background work array");
            assert_eq!(work.len(), 2);
            assert_eq!(work[0]["id"], "claude-child:agent-a");
            assert_eq!(work[0]["kind"], "subagent");
            assert_eq!(work[0]["label"], "Reviewer");
            assert_eq!(work[1]["id"], "background-task:task-1");
            assert_eq!(work[1]["kind"], "command");
            assert_eq!(work[1]["label"], "Run checks");
            assert!(work.iter().all(|item| item["id"] != CLAUDE_SESSION_RUNNING));
            (
                work[0]["startedAtMs"].as_i64().expect("child start time"),
                work[1]["startedAtMs"].as_i64().expect("command start time"),
            )
        };

        {
            let mut sessions = fixture.manager.sessions.lock().unwrap();
            let session = sessions.get_mut(&fixture.owned_id).unwrap();
            let native_session_id = session.native_session_id.clone().unwrap();
            let child_progress = serde_json::json!({
                "sessionId": native_session_id,
                "update": {
                    "sessionUpdate": "subagent_state_update",
                    "subagentSessionId": "agent-a",
                    "name": "Reviewer updated",
                    "state": "running"
                }
            });
            assert!(claude_native_child_payload(session, &child_progress).is_some());
            let command_progress = serde_json::json!({"update": {
                "sessionUpdate": "async_task_progress",
                "asyncTaskId": "task-1",
                "name": "Run checks updated"
            }});
            assert!(!update_raw_liveness(session, &command_progress));
        }
        let updated = serde_json::to_value(&fixture.manager.list_sessions().unwrap()[0]).unwrap();
        let updated = updated["backgroundWork"].as_array().unwrap();
        assert_eq!(updated[0]["startedAtMs"], child_started_at);
        assert_eq!(updated[0]["label"], "Reviewer updated");
        assert_eq!(updated[1]["startedAtMs"], command_started_at);
        assert_eq!(updated[1]["label"], "Run checks updated");

        {
            let mut sessions = fixture.manager.sessions.lock().unwrap();
            let session = sessions.get_mut(&fixture.owned_id).unwrap();
            let native_session_id = session.native_session_id.clone().unwrap();
            let child_finished = serde_json::json!({
                "sessionId": native_session_id,
                "update": {
                    "sessionUpdate": "subagent_state_update",
                    "subagentSessionId": "agent-a",
                    "state": "completed"
                }
            });
            assert!(claude_native_child_payload(session, &child_finished).is_some());
        }
        let child_finished = serde_json::to_value(&fixture.manager.list_sessions().unwrap()[0]).unwrap();
        let child_finished = child_finished["backgroundWork"].as_array().unwrap();
        assert_eq!(child_finished.len(), 1);
        assert_eq!(child_finished[0]["id"], "background-task:task-1");

        {
            let mut sessions = fixture.manager.sessions.lock().unwrap();
            let session = sessions.get_mut(&fixture.owned_id).unwrap();
            let command_finished = serde_json::json!({"update": {
                "sessionUpdate": "async_task_state_update",
                "asyncTaskId": "task-1",
                "state": "completed"
            }});
            assert!(!update_raw_liveness(session, &command_finished));
        }
        let finished = serde_json::to_value(&fixture.manager.list_sessions().unwrap()[0]).unwrap();
        assert!(finished["backgroundWork"].as_array().unwrap().is_empty());

        fixture.manager.close(&fixture.owned_id, fixture.generation).await.unwrap();
        fs::remove_dir_all(fixture.root).unwrap();
    }

    #[tokio::test(flavor = "current_thread")]
    async fn pool_shares_one_process_across_two_sessions() {
        let root = temp_root();
        let log = root.join("pool-multiplex.jsonl");
        let manifest =
            super::super::providers::acp_client::tests::fixture_manifest_named(&log, "multiplex");
        let providers = ProviderRegistry::new([(AgentConversationProvider::Codex, manifest)])
            .expect("fixture provider");
        let manager = AgentRuntimeManager::new(providers);
        let first = manager
            .ensure_async(request(
                root.to_str().unwrap(),
                "owned-pool-a",
                AgentConversationProvider::Codex,
            ))
            .await
            .unwrap();
        let second = manager
            .ensure_async(request(
                root.to_str().unwrap(),
                "owned-pool-b",
                AgentConversationProvider::Codex,
            ))
            .await
            .unwrap();
        manager
            .activate(&first.owned_id, first.generation)
            .await
            .unwrap();
        manager
            .activate(&second.owned_id, second.generation)
            .await
            .unwrap();

        let roots = manager.resource_roots();
        assert_eq!(roots.len(), 2, "each session overlays the shared root");
        assert_eq!(roots[0].pid, roots[1].pid);
        assert_eq!(
            fs::read_to_string(&log)
                .unwrap()
                .matches(r#""method":"initialize""#)
                .count(),
            1
        );

        manager
            .close(&first.owned_id, first.generation)
            .await
            .unwrap();
        assert!(process_is_alive(roots[0].pid));
        manager
            .prompt(
                &second.owned_id,
                second.generation,
                test_prompt("still live"),
            )
            .await
            .unwrap();
        manager
            .close(&second.owned_id, second.generation)
            .await
            .unwrap();
        wait_until(|| !process_is_alive(roots[0].pid)).await;
        fs::remove_dir_all(root).unwrap();
    }

    #[tokio::test(flavor = "current_thread")]
    async fn last_session_close_kills_process() {
        let fixture =
            fixture_manager_with_provider("multiplex", AgentConversationProvider::Codex, None)
                .await;
        let pid = fixture.manager.resource_roots()[0].pid;
        fixture
            .manager
            .close(&fixture.owned_id, fixture.generation)
            .await
            .unwrap();
        wait_until(|| !process_is_alive(pid)).await;
        fs::remove_dir_all(fixture.root).unwrap();
    }

    #[tokio::test(flavor = "current_thread")]
    async fn resume_failure_without_user_history_starts_fresh() {
        let root = temp_root();
        let log = root.join("resume-failure.jsonl");
        let manifest = super::super::providers::acp_client::tests::fixture_manifest_named(
            &log,
            "resume_failure",
        );
        let providers = ProviderRegistry::new([(AgentConversationProvider::Codex, manifest)])
            .expect("fixture provider");
        let manager = AgentRuntimeManager::new(providers);
        let mut ensure = request(
            root.to_str().unwrap(),
            "owned-resume-failure",
            AgentConversationProvider::Codex,
        );
        ensure.native_session_id = Some("missing-native".into());
        let connection = manager.ensure_inner(ensure).unwrap().0;
        let resumed = manager
            .activate(&connection.owned_id, connection.generation)
            .await
            .expect("empty session starts fresh");
        let snapshot = manager.snapshot(&connection.owned_id).unwrap().unwrap();
        assert!(!snapshot.suspended);
        assert_eq!(resumed.native_session_id.as_deref(), Some("new-session"));
        assert_eq!(
            snapshot.connection.native_session_id.as_deref(),
            Some("new-session")
        );
        assert!(!snapshot.events.iter().any(|event| matches!(
            &event.payload,
            AgentConversationPayload::Error { code, .. } if code == "session-resume-failed"
        )));
        let requests = fs::read_to_string(&log).unwrap();
        assert!(requests.contains(r#""method":"session/resume""#));
        assert!(requests.contains(r#""method":"session/new""#));
        manager
            .close(&connection.owned_id, connection.generation)
            .await
            .unwrap();
        fs::remove_dir_all(root).unwrap();
    }

    // Claude keeps a transcript this side can read. When it holds no turn — the
    // process was stopped before it wrote one — there is nothing on Claude's
    // side to resume however many turns this store shows, and every resume of
    // it fails the same way. Starting fresh is the only thing that works.
    #[tokio::test(flavor = "current_thread")]
    async fn resume_failure_of_a_claude_session_whose_transcript_holds_nothing_starts_fresh() {
        let root = temp_root();
        let log = root.join("resume-failure-claude.jsonl");
        let manifest = super::super::providers::acp_client::tests::fixture_manifest_named(
            &log,
            "resume_failure",
        );
        let providers = ProviderRegistry::new([(AgentConversationProvider::Claude, manifest)])
            .expect("fixture provider");
        let manager = AgentRuntimeManager::new(providers);
        let mut ensure = request(
            root.to_str().unwrap(),
            "owned-resume-failure-claude",
            AgentConversationProvider::Claude,
        );
        // No transcript by this name exists anywhere under ~/.claude/projects.
        ensure.native_session_id = Some("mcb-test-transcript-never-written".into());
        let connection = manager.ensure_inner(ensure).unwrap().0;
        {
            let mut sessions = manager.sessions.lock().unwrap();
            let session = sessions.get_mut(&connection.owned_id).unwrap();
            record_payload_for_session(
                session,
                AgentConversationPayload::UserMessage {
                    item_id: "stored-user-message".into(),
                    text: "Earlier message".into(),
                    completed: true,
                    attachment_ids: Vec::new(),
                },
            )
            .unwrap();
        }

        let resumed = manager
            .activate(&connection.owned_id, connection.generation)
            .await
            .expect("a session Claude no longer holds starts fresh");

        assert_eq!(resumed.native_session_id.as_deref(), Some("new-session"));
        let snapshot = manager.snapshot(&connection.owned_id).unwrap().unwrap();
        assert!(!snapshot.suspended);
        assert!(!snapshot.events.iter().any(|event| matches!(
            &event.payload,
            AgentConversationPayload::Error { code, .. } if code == "session-resume-failed"
        )));
        let requests = fs::read_to_string(&log).unwrap();
        assert!(requests.contains(r#""method":"session/resume""#));
        assert!(requests.contains(r#""method":"session/new""#));
        manager
            .close(&connection.owned_id, connection.generation)
            .await
            .unwrap();
        fs::remove_dir_all(root).unwrap();
    }

    #[tokio::test(flavor = "current_thread")]
    async fn late_approval_gets_stale_result() {
        let fixture = fixture_manager_with_acp_session("late_approval").await;
        {
            let mut sessions = fixture.manager.sessions.lock().unwrap();
            let session = current_session_mut(&mut sessions, &fixture.owned_id, fixture.generation)
                .unwrap();
            for (state, summary) in [
                (ApprovalState::Requested, "Earlier approval"),
                (ApprovalState::Accepted, "Earlier approval"),
                (ApprovalState::Requested, "Read git diff"),
            ] {
                record_payload_for_session_and_dispatch(
                    session,
                    &fixture.manager.emitter,
                    AgentConversationPayload::Approval {
                        request_id: "expired-request".into(),
                        state,
                        summary: summary.into(), options: Vec::new(),
                    },
                )
                .unwrap();
            }
        }
        fixture
            .manager
            .close(&fixture.owned_id, fixture.generation)
            .await
            .unwrap();
        let error = fixture
            .manager
            .respond_permission(PermissionResponse {
                identity: AgentRequestIdentity {
                    owned_id: fixture.owned_id.clone(),
                    generation: fixture.generation,
                    request_id: "expired-request".into(),
                    turn_id: None,
                    item_id: None,
                },
                decision: AgentApprovalDecision::Accept,
            })
            .await
            .unwrap_err();
        assert!(error.starts_with("Stale approval request:"));
        let snapshot = fixture.manager.snapshot(&fixture.owned_id).unwrap().unwrap();
        assert!(snapshot.events.iter().any(|event| matches!(
            &event.payload,
            AgentConversationPayload::Approval {
                request_id,
                state: ApprovalState::Expired,
                summary, ..
            } if request_id == "expired-request" && summary == "Read git diff"
        )));
        let second_error = fixture
            .manager
            .respond_permission(PermissionResponse {
                identity: AgentRequestIdentity {
                    owned_id: fixture.owned_id.clone(),
                    generation: fixture.generation,
                    request_id: "expired-request".into(),
                    turn_id: None,
                    item_id: None,
                },
                decision: AgentApprovalDecision::Accept,
            })
            .await
            .unwrap_err();
        assert!(second_error.starts_with("Stale approval request:"));
        let snapshot = fixture.manager.snapshot(&fixture.owned_id).unwrap().unwrap();
        assert_eq!(snapshot.events.iter().filter(|event| matches!(
            &event.payload,
            AgentConversationPayload::Approval {
                request_id,
                state: ApprovalState::Expired,
                ..
            } if request_id == "expired-request"
        )).count(), 1);
        fs::remove_dir_all(fixture.root).unwrap();
    }

    #[tokio::test(flavor = "current_thread")]
    async fn load_replay_populates_two_turns_once_across_repeated_ensure() {
        let root = temp_root();
        let log = root.join("replay_on_load.jsonl");
        let manifest = super::super::providers::acp_client::tests::fixture_manifest_named(
            &log,
            "replay_on_load",
        );
        let providers = ProviderRegistry::new([(AgentConversationProvider::Codex, manifest)])
            .expect("fixture provider");
        let manager = AgentRuntimeManager::new(providers);
        let owned_id = "owned-load-replay";
        let mut load_request = request(
            root.to_str().unwrap(),
            owned_id,
            AgentConversationProvider::Codex,
        );
        load_request.native_session_id = Some("loaded-session".into());
        load_request.native_session_mode = AgentNativeSessionMode::Load;

        let connection = manager
            .ensure_async(load_request.clone())
            .await
            .expect("ensure loaded session");
        manager
            .activate(owned_id, connection.generation)
            .await
            .expect("initial load");
        wait_until(|| {
            manager
                .snapshot(owned_id)
                .unwrap()
                .is_some_and(|snapshot| {
                    snapshot
                        .events
                        .iter()
                        .filter(|event| {
                            matches!(
                                &event.payload,
                                AgentConversationPayload::UserMessage { .. }
                                    | AgentConversationPayload::AssistantMessage { .. }
                            )
                        })
                        .count()
                        == 4
                })
        })
        .await;

        for _ in 0..3 {
            let same = manager
                .ensure_async(load_request.clone())
                .await
                .expect("connected ensure is idempotent");
            manager
                .activate(owned_id, same.generation)
                .await
                .expect("connected activation is idempotent");
        }

        let frames = fs::read_to_string(&log).expect("load fixture log");
        assert_eq!(frames.matches(r#""method":"session/load""#).count(), 1);
        let snapshot = manager.snapshot(owned_id).unwrap().unwrap();
        let item_ids = snapshot
            .events
            .iter()
            .filter_map(|event| match &event.payload {
                AgentConversationPayload::UserMessage { item_id, .. }
                | AgentConversationPayload::AssistantMessage { item_id, .. } => {
                    Some(item_id.as_str())
                }
                _ => None,
            })
            .collect::<Vec<_>>();
        assert_eq!(
            item_ids,
            [
                "history-user-1",
                "history-agent-1",
                "history-user-2",
                "history-agent-2"
            ]
        );
        manager
            .close(owned_id, connection.generation)
            .await
            .unwrap();
        fs::remove_dir_all(root).unwrap();
    }

    #[tokio::test(flavor = "current_thread")]
    async fn late_prompt_once_updates_are_dropped_after_registration_ends() {
        let fixture = fixture_manager_with_acp_session("late_one_shot_update").await;
        let seen: Arc<Mutex<Vec<AgentConversationEvent>>> = Default::default();
        let sink = Arc::clone(&seen);
        fixture
            .manager
            .set_emitter(Arc::new(move |event| sink.lock().unwrap().push(event)));

        let generated = fixture
            .manager
            .prompt_once(
                &fixture.owned_id,
                fixture.generation,
                test_prompt("write a subject"),
            )
            .await
            .expect("one-shot prompt");
        assert_eq!(generated.text, "generated text");
        tokio::time::sleep(Duration::from_millis(100)).await;
        assert!(
            seen.lock().unwrap().is_empty(),
            "a late update for the completed one-shot turn must be dropped"
        );

        fixture
            .manager
            .close(&fixture.owned_id, fixture.generation)
            .await
            .unwrap();
        fs::remove_dir_all(fixture.root).unwrap();
    }

    #[tokio::test(flavor = "current_thread")]
    async fn active_prompt_once_consumes_agent_minted_updates_without_emitting() {
        let fixture = fixture_manager_with_acp_session("agent_minted_in_flight_update").await;
        let seen: Arc<Mutex<Vec<AgentConversationEvent>>> = Default::default();
        let sink = Arc::clone(&seen);
        fixture
            .manager
            .set_emitter(Arc::new(move |event| sink.lock().unwrap().push(event)));

        let generated = fixture
            .manager
            .prompt_once(
                &fixture.owned_id,
                fixture.generation,
                test_prompt("write a subject"),
            )
            .await
            .expect("one-shot prompt");
        assert_eq!(generated.text, "agent-minted text");
        assert_eq!(generated.turn_id.as_deref(), Some("agent-turn-9"));
        tokio::time::sleep(Duration::from_millis(50)).await;
        assert!(
            seen.lock().unwrap().is_empty(),
            "an in-flight one-shot update must not reach the conversation emitter"
        );

        fixture
            .manager
            .close(&fixture.owned_id, fixture.generation)
            .await
            .unwrap();
        fs::remove_dir_all(fixture.root).unwrap();
    }

    #[tokio::test(flavor = "current_thread")]
    async fn shutdown_lets_a_stopping_adapter_finish_its_write() {
        let root = temp_root();
        let marker = root.join("finished-write");
        let mut manifest = super::super::providers::acp_client::tests::fixture_manifest_named(
            &root.join("shutdown-write.jsonl"), "default");
        manifest.args[1] = format!(
            "sh -c 'trap \"sleep 0.2; : > {}\" TERM; sleep 30 & wait' &\n{}",
            marker.display(), manifest.args[1]
        );
        let providers = ProviderRegistry::new([(AgentConversationProvider::Codex, manifest)]).unwrap();
        let manager = AgentRuntimeManager::new(providers);
        let owned_id = "owned-shutdown-write";
        let connection = manager.ensure_inner(request(root.to_str().unwrap(), owned_id,
            AgentConversationProvider::Codex)).unwrap().0;
        manager.activate(owned_id, connection.generation).await.unwrap();
        tokio::time::sleep(Duration::from_millis(150)).await;

        manager.shutdown().await;
        assert!(marker.is_file(), "shutdown killed the adapter before its final write");
        fs::remove_dir_all(root).unwrap();
    }

    #[tokio::test(flavor = "current_thread")]
    async fn shutdown_persists_terminal_turn_and_keeps_native_session() {
        let fixture = fixture_manager_with_acp_session("permission_midturn").await;
        let before = fixture.manager.store().get_session(&fixture.owned_id).unwrap().unwrap();
        fixture.manager.prompt(&fixture.owned_id, fixture.generation, test_prompt("hello"))
            .await.unwrap();
        let transport = fixture.manager.sessions.lock().unwrap()
            .get(&fixture.owned_id).unwrap().transport.clone().unwrap();
        let pid = transport.process_id().unwrap() as i32;
        fixture.manager.shutdown().await;
        let saved = fixture.manager.store().get_session(&fixture.owned_id).unwrap().unwrap();
        assert_eq!(saved.native_session_id, before.native_session_id);
        assert!(saved.native_session_id.is_some());
        assert_ne!(saved.state, "closed");
        assert!(fixture.manager.sessions.lock().unwrap()
            .get(&fixture.owned_id).unwrap().active_turn_id.is_none());
        let snapshot = fixture.manager.snapshot(&fixture.owned_id).unwrap().unwrap();
        assert!(snapshot.events.iter().any(|event| matches!(event.payload,
            AgentConversationPayload::Turn { state: super::super::protocol::TurnState::Failed, .. }
                | AgentConversationPayload::Turn { state: super::super::protocol::TurnState::Completed, .. }
        )));
        assert_ne!(unsafe { libc::kill(pid, 0) }, 0, "adapter survived shutdown");
        assert!(fixture.manager.adapter_pools.lock().await.is_empty());
        fs::remove_dir_all(fixture.root).unwrap();
    }

    #[tokio::test(flavor = "current_thread")]
    async fn failed_prompt_emits_a_terminal_turn_before_clearing_it() {
        let fixture = fixture_manager_with_acp_session("prompt_error").await;
        let seen: Arc<Mutex<Vec<AgentConversationEvent>>> = Default::default();
        let sink = Arc::clone(&seen);
        fixture
            .manager
            .set_emitter(Arc::new(move |event| sink.lock().unwrap().push(event)));

        fixture
            .manager
            .prompt(&fixture.owned_id, fixture.generation, test_prompt("hello"))
            .await
            .expect("prompt starts");
        wait_until(|| {
            seen.lock().unwrap().iter().any(|event| {
                matches!(
                    event.payload,
                    AgentConversationPayload::Turn {
                        state: super::super::protocol::TurnState::Failed,
                        ..
                    }
                )
            })
        })
        .await;
        let seen = seen.lock().unwrap();
        let failed_turn = seen.iter().position(|event| {
            matches!(
                event.payload,
                AgentConversationPayload::Turn {
                    state: super::super::protocol::TurnState::Failed,
                    ..
                }
            )
        });
        let error = seen.iter().position(|event| {
            matches!(
                &event.payload,
                AgentConversationPayload::Error { code, .. } if code == "acp-error"
            )
        });
        assert!(failed_turn.is_some(), "failed prompt must close its turn");
        assert!(error.is_some(), "failed prompt must retain the error");
        assert!(failed_turn.unwrap() < error.unwrap());
        drop(seen);
        assert!(fixture
            .manager
            .sessions
            .lock()
            .unwrap()
            .get(&fixture.owned_id)
            .unwrap()
            .active_turn_id
            .is_none());

        fixture
            .manager
            .close(&fixture.owned_id, fixture.generation)
            .await
            .unwrap();
        fs::remove_dir_all(fixture.root).unwrap();
    }

    #[tokio::test(flavor = "current_thread")]
    async fn the_pump_is_silent_while_the_writer_lease_is_terminal() {
        let fixture = fixture_manager_with_acp_session("prompt_with_update").await;
        let seen: Arc<Mutex<Vec<AgentConversationEvent>>> = Default::default();
        let sink = Arc::clone(&seen);
        fixture
            .manager
            .set_emitter(Arc::new(move |event| sink.lock().unwrap().push(event)));
        let runtime = fixture
            .manager
            .runtime(&fixture.owned_id, fixture.generation)
            .unwrap();
        let transport = runtime.lock().await.transport().expect("transport");
        let native_session_id = fixture
            .manager
            .snapshot(&fixture.owned_id)
            .unwrap()
            .unwrap()
            .connection
            .native_session_id
            .unwrap();
        let handoff = terminal_handoff(&fixture.owned_id, fixture.generation, native_session_id);
        fixture.manager.handoff_prepare(&handoff).unwrap();
        fixture.manager.handoff_commit(&handoff).await.unwrap();

        transport
            .request(
                "session/prompt",
                json!({ "sessionId": "new-session", "prompt": [
                    { "type": "text", "text": "hello" }
                ] }),
            )
            .await
            .expect("fixture prompt");
        tokio::time::sleep(Duration::from_millis(50)).await;
        assert!(
            seen.lock().unwrap().is_empty(),
            "terminal writer must be the only live event source"
        );

        fixture
            .manager
            .close(&fixture.owned_id, fixture.generation)
            .await
            .unwrap();
        fs::remove_dir_all(fixture.root).unwrap();
    }

    #[tokio::test(flavor = "current_thread")]
    async fn dropping_a_session_releases_the_pump_and_kills_the_sidecar() {
        let fixture = fixture_manager_with_acp_session("prompt_with_update").await;
        let seen: Arc<Mutex<Vec<AgentConversationEvent>>> = Default::default();
        let sink = Arc::clone(&seen);
        fixture
            .manager
            .set_emitter(Arc::new(move |event| sink.lock().unwrap().push(event)));
        let pid = fixture.manager.resource_roots()[0].pid;
        assert!(process_is_alive(pid));

        fixture
            .manager
            .close(&fixture.owned_id, fixture.generation)
            .await
            .unwrap();
        wait_until(|| !process_is_alive(pid)).await;
        tokio::time::sleep(Duration::from_millis(50)).await;
        let events = seen.lock().unwrap();
        assert_eq!(events.len(), 1);
        assert!(matches!(
            events[0].payload,
            AgentConversationPayload::Connection {
                state: ConversationConnectionState::Closed,
                ..
            }
        ));
        fs::remove_dir_all(fixture.root).unwrap();
    }

    #[tokio::test(flavor = "current_thread")]
    async fn a_permission_the_agent_withdraws_expires_instead_of_staying_pending() {
        let fixture = fixture_manager_with_acp_session("permission_withdrawn").await;
        let seen: Arc<Mutex<Vec<AgentConversationEvent>>> = Default::default();
        let sink = Arc::clone(&seen);
        fixture
            .manager
            .set_emitter(Arc::new(move |event| sink.lock().unwrap().push(event)));
        fixture
            .manager
            .prompt(&fixture.owned_id, fixture.generation, test_prompt("hello"))
            .await
            .expect("prompt starts");
        wait_until(|| {
            seen.lock().unwrap().iter().any(|event| {
                matches!(
                    event.payload,
                    AgentConversationPayload::Approval {
                        state: ApprovalState::Expired,
                        ..
                    }
                )
            })
        })
        .await;
        assert!(fixture
            .manager
            .sessions
            .lock()
            .unwrap()
            .get(&fixture.owned_id)
            .is_some_and(|session| session.permission_requests.is_empty()));

        fixture
            .manager
            .close(&fixture.owned_id, fixture.generation)
            .await
            .unwrap();
        fs::remove_dir_all(fixture.root).unwrap();
    }

    #[tokio::test(flavor = "current_thread")]
    async fn a_streamed_reply_is_stored_once_as_a_finished_message_with_rust_blocks() {
        let provider_update = json!({ "update": {
            "sessionUpdate": "agent_message_chunk", "messageId": "reply-a",
            "content": { "type": "text", "text": "**Bold** start" },
            "_meta": { "replay": true }
        } });
        assert!(matches!(
            payload_from_session_update_for_turn(&provider_update, None),
            Some(AgentConversationPayload::AssistantMessage { blocks: None, .. })
        ));
        let fixture = fixture_manager_with_acp_session("two_replies").await;
        let seen: Arc<Mutex<Vec<AgentConversationEvent>>> = Default::default();
        let sink = Arc::clone(&seen);
        fixture
            .manager
            .set_emitter(Arc::new(move |event| sink.lock().unwrap().push(event)));
        fixture
            .manager
            .prompt(&fixture.owned_id, fixture.generation, test_prompt("hello"))
            .await
            .expect("prompt starts");
        wait_until(|| {
            seen.lock().unwrap().iter().any(|event| {
                matches!(
                    event.payload,
                    AgentConversationPayload::Turn {
                        state: super::super::protocol::TurnState::Completed,
                        ..
                    }
                )
            })
        })
        .await;

        // The desktop records and publishes pieces; the provider supplies none.
        for event in seen.lock().unwrap().iter() {
            if let AgentConversationPayload::AssistantMessage { text, blocks, .. } = &event.payload {
                assert_eq!(blocks.as_ref(), Some(&super::super::safe_markdown::parse_safe_markdown(text)));
            }
        }
        let store = Arc::clone(
            &fixture
                .manager
                .sessions
                .lock()
                .unwrap()
                .get(&fixture.owned_id)
                .unwrap()
                .store,
        );
        let stored = store
            .list_events(&fixture.owned_id, 0, 1000)
            .unwrap()
            .into_iter()
            .filter_map(|row| {
                let event: AgentConversationEvent =
                    serde_json::from_str(&row.payload_json).unwrap();
                match event.payload {
                    AgentConversationPayload::AssistantDelta { item_id, .. } => {
                        Some(format!("delta {item_id}"))
                    }
                    AgentConversationPayload::AssistantMessage {
                        item_id,
                        text,
                        blocks,
                        ..
                    } => {
                        assert_eq!(
                            blocks,
                            Some(crate::agent_conversation::safe_markdown::parse_safe_markdown(
                                &text
                            ))
                        );
                        Some(format!("message {item_id}: {text}"))
                    }
                    AgentConversationPayload::Tool { .. } => Some("tool".to_string()),
                    AgentConversationPayload::Turn {
                        state: super::super::protocol::TurnState::Completed,
                        ..
                    } => Some("turn completed".to_string()),
                    _ => None,
                }
            })
            .collect::<Vec<_>>();
        assert_eq!(
            stored,
            [
                "delta reply-a",
                "delta reply-a",
                "tool",
                "message reply-a: **Bold** start",
                "delta reply-b",
                "message reply-b: `code`",
                "turn completed",
            ]
        );

        fixture
            .manager
            .close(&fixture.owned_id, fixture.generation)
            .await
            .unwrap();
        fs::remove_dir_all(fixture.root).unwrap();
    }

    #[tokio::test(flavor = "current_thread")]
    async fn current_mode_update_refreshes_the_stored_config() {
        let fixture = fixture_manager_with_acp_session("current_mode_update").await;

        fixture
            .manager
            .prompt(
                &fixture.owned_id,
                fixture.generation,
                test_prompt("change mode"),
            )
            .await
            .expect("prompt starts");
        wait_until(|| {
            fixture
                .manager
                .conversation_config(&fixture.owned_id)
                .is_ok_and(|config| config.approval_policy.as_deref() == Some("plan"))
        })
        .await;

        fixture
            .manager
            .close(&fixture.owned_id, fixture.generation)
            .await
            .unwrap();
        fs::remove_dir_all(fixture.root).unwrap();
    }

    #[tokio::test(flavor = "current_thread")]
    async fn changing_the_model_does_not_empty_claude_efforts() {
        // Claude ACP 0.88.0/0.93.0 retain effort on a model change; exercise
        // those mutable controls rather than an adapter with no effort option.
        let root = temp_root();
        let log = root.join("config_options.jsonl");
        let manifest = super::super::providers::acp_client::tests::fixture_manifest_named(
            &log,
            "config_options",
        );
        let providers = ProviderRegistry::new([(AgentConversationProvider::Claude, manifest)])
            .expect("fixture provider");
        let manager = AgentRuntimeManager::new(providers);
        let owned_id = "owned-config_options".to_string();
        let ensure_request = request(
            root.to_str().unwrap(),
            &owned_id,
            AgentConversationProvider::Claude,
        );
        let connection = manager.ensure_inner(ensure_request).expect("ensure").0;
        manager
            .activate(&owned_id, connection.generation)
            .await
            .expect("activate");

        let before = manager.conversation_config(&owned_id).expect("config");
        assert_eq!(before.available_efforts, ["default", "low", "medium", "high", "xhigh", "max"]);
        assert_eq!(before.reasoning_effort.as_deref(), Some("xhigh"));

        let after = manager
            .set_conversation_config(SetAgentConversationConfigRequest {
                owned_id: owned_id.clone(),
                generation: connection.generation,
                model: Some("sonnet".to_string()),
                reasoning_effort: None,
                approval_policy: None,
            })
            .await
            .expect("changing the model is allowed on a live Claude session");
        assert_eq!(after.model.as_deref(), Some("sonnet"));
        assert_eq!(
            after.available_efforts,
            ["default", "low", "medium", "high", "xhigh", "max"],
            "changing the model must not empty the effort list"
        );
        assert_eq!(after.reasoning_effort.as_deref(), Some("high"));

        manager
            .close(&owned_id, connection.generation)
            .await
            .unwrap();
        fs::remove_dir_all(root).unwrap();
    }

    #[tokio::test(flavor = "current_thread")]
    async fn choices_made_before_the_adapter_starts_are_asked_of_it_when_it_starts() {
        // A session picks its model and effort before anything is running: the
        // adapter only starts when the first turn is sent. The choice has to
        // survive that gap and be asked of the adapter once there is one, or
        // the composer reverts it and the turn runs on the provider's default.
        let root = temp_root();
        let log = root.join("claude_preselect.jsonl");
        let manifest = super::super::providers::acp_client::tests::fixture_manifest_named(
            &log,
            "claude_preselect",
        );
        let providers = ProviderRegistry::new([(AgentConversationProvider::Claude, manifest)])
            .expect("fixture provider");
        let manager = AgentRuntimeManager::new(providers);
        let owned_id = "owned-claude_preselect".to_string();
        let ensure_request = request(
            root.to_str().unwrap(),
            &owned_id,
            AgentConversationProvider::Claude,
        );
        let connection = manager.ensure_inner(ensure_request).expect("ensure").0;
        // Draft placeholders live in the frontend until the adapter answers.
        assert!(connection.config.available_efforts.is_empty());

        let config = manager
            .set_conversation_config(SetAgentConversationConfigRequest {
                owned_id: owned_id.clone(),
                generation: connection.generation,
                model: Some("gpt-5.6-luna".to_string()),
                reasoning_effort: Some("high".to_string()),
                approval_policy: None,
            })
            .await
            .expect("a choice made before the adapter starts is kept");
        assert_eq!(config.reasoning_effort.as_deref(), Some("high"));
        assert_eq!(config.model.as_deref(), Some("gpt-5.6-luna"));

        manager
            .activate(&owned_id, connection.generation)
            .await
            .expect("activate");
        let spawn_log = fs::read_to_string(&log).expect("Claude spawn log");
        assert!(
            spawn_log.contains(r#""model":"gpt-5.6-luna""#),
            "the model chosen before the start must reach the adapter: {spawn_log}"
        );
        assert!(
            spawn_log.contains("MAX_THINKING_TOKENS=<unset>"),
            "nothing in the environment decides how hard a session thinks: {spawn_log}"
        );
        // The adapter answers with the session it actually has, and that answer
        // is what the conversation reports. This fixture resolves the request to
        // a different model, and the different model is what is shown.
        let after_start = manager.conversation_config(&owned_id).expect("config");
        assert_eq!(after_start.model.as_deref(), Some("gpt-5.6-terra"));
        assert_eq!(after_start.reasoning_effort.as_deref(), Some("xhigh"));

        manager
            .close(&owned_id, connection.generation)
            .await
            .unwrap();
        fs::remove_dir_all(root).unwrap();
    }

    /// The adapter resolves what it is asked for, so the model a session ends
    /// up on and the model that was requested can differ. What the session
    /// reports afterwards is the adapter's answer, never the request.
    #[tokio::test(flavor = "current_thread")]
    async fn acp_new_reports_the_model_the_adapter_chose_not_the_one_requested() {
        let root = temp_root();
        let log = root.join("config_options.jsonl");
        let manifest = super::super::providers::acp_client::tests::fixture_manifest_named(
            &log,
            "config_options",
        );
        let providers = ProviderRegistry::new([(AgentConversationProvider::Claude, manifest)])
            .expect("fixture provider");
        let manager = AgentRuntimeManager::new(providers);
        let owned_id = "owned-config_options_model".to_string();
        let ensure_request = request(
            root.to_str().unwrap(),
            &owned_id,
            AgentConversationProvider::Claude,
        );
        let connection = manager.ensure_inner(ensure_request).expect("ensure").0;

        // Picked before anything is running, which is when a new conversation's
        // model is chosen.
        manager
            .set_conversation_config(SetAgentConversationConfigRequest {
                owned_id: owned_id.clone(),
                generation: connection.generation,
                model: Some("haiku".into()),
                reasoning_effort: None,
                approval_policy: None,
            })
            .await
            .expect("a model chosen before the adapter starts is kept");

        manager
            .activate(&owned_id, connection.generation)
            .await
            .expect("activate");

        let frames = fs::read_to_string(&log).expect("fixture request log");
        assert!(
            frames.contains(r#""configId":"model","value":"haiku""#),
            "the chosen model must be asked of the adapter: {frames}"
        );
        // The fixture's session starts on one model, is asked for "haiku", and
        // answers "sonnet". The answer is what the session is on.
        let config = manager.conversation_config(&owned_id).expect("config");
        assert_eq!(config.model.as_deref(), Some("sonnet"));

        manager
            .close(&owned_id, connection.generation)
            .await
            .unwrap();
        fs::remove_dir_all(root).unwrap();
    }

    /// An adapter that offers an effort control can be re-pointed at any time:
    /// the change goes to the adapter, the session reports what the adapter
    /// says it is now, and nothing in the process environment has a say.
    #[tokio::test(flavor = "current_thread")]
    async fn effort_changes_live_when_the_adapter_offers_it() {
        let fixture = fixture_manager_with_provider(
            "config_options",
            AgentConversationProvider::Claude,
            None,
        )
        .await;

        let configured = fixture
            .manager
            .set_conversation_config(SetAgentConversationConfigRequest {
                owned_id: fixture.owned_id.clone(),
                generation: fixture.generation,
                model: None,
                reasoning_effort: Some("high".into()),
                approval_policy: None,
            })
            .await
            .expect("an adapter with an effort control takes a live change");
        assert_eq!(configured.reasoning_effort.as_deref(), Some("high"));
        assert_eq!(
            fixture
                .manager
                .conversation_config(&fixture.owned_id)
                .expect("stored config")
                .reasoning_effort
                .as_deref(),
            Some("high")
        );

        let frames = fs::read_to_string(fixture.root.join("config_options.jsonl"))
            .expect("fixture request log");
        assert!(
            frames.contains(r#""configId":"effort","value":"high""#),
            "the effort change must go to the adapter: {frames}"
        );
        assert!(
            frames.contains("MAX_THINKING_TOKENS=<unset>"),
            "the thinking budget must not be decided by the environment: {frames}"
        );

        fixture
            .manager
            .close(&fixture.owned_id, fixture.generation)
            .await
            .unwrap();
        fs::remove_dir_all(fixture.root).unwrap();
    }

    #[tokio::test(flavor = "current_thread")]
    async fn effort_chosen_for_a_new_session_is_asked_of_the_adapter() {
        let fixture = fixture_manager_with_provider(
            "config_options",
            AgentConversationProvider::Claude,
            Some("medium"),
        )
        .await;

        let log = fs::read_to_string(fixture.root.join("config_options.jsonl"))
            .expect("Claude spawn log");
        assert!(
            log.contains(r#""configId":"effort","value":"medium""#),
            "the effort the session was started with must be asked of the adapter: {log}"
        );
        assert!(
            log.contains("MAX_THINKING_TOKENS=<unset>"),
            "nothing in the environment decides how hard a session thinks: {log}"
        );
        let config = fixture
            .manager
            .conversation_config(&fixture.owned_id)
            .expect("Claude config");
        // The adapter answers "high", and the answer is what the session is on.
        assert_eq!(config.reasoning_effort.as_deref(), Some("high"));
        assert_eq!(
            config.available_efforts,
            ["default", "low", "medium", "high", "xhigh", "max"]
        );

        fixture
            .manager
            .close(&fixture.owned_id, fixture.generation)
            .await
            .unwrap();
        fs::remove_dir_all(fixture.root).unwrap();
    }

    /// An effort chosen while a session is running has to survive the adapter
    /// going away and coming back. Adapters stop when a turn ends, so the next
    /// message reactivates one, and reactivation asks for the effort the
    /// session is on now — not the one it was created with.
    #[tokio::test(flavor = "current_thread")]
    async fn reactivating_a_session_asks_for_the_effort_it_is_on_now() {
        let fixture = fixture_manager_with_provider(
            "suspend_effort_reactivation",
            AgentConversationProvider::Codex,
            Some("medium"),
        )
        .await;

        // The adapter answers the start-time effort with one of its own, so the
        // session is on "xhigh" while it was created with "medium".
        fixture
            .manager
            .set_conversation_config(SetAgentConversationConfigRequest {
                owned_id: fixture.owned_id.clone(),
                generation: fixture.generation,
                model: None,
                reasoning_effort: Some("high".into()),
                approval_policy: None,
            })
            .await
            .expect("a live effort change");
        assert_eq!(
            fixture
                .manager
                .conversation_config(&fixture.owned_id)
                .expect("config")
                .reasoning_effort
                .as_deref(),
            Some("xhigh")
        );

        // The settings change already put the session to rest; asking again
        // is a no-op. What matters is the state, not who got there first.
        fixture
            .manager
            .suspend_if_quiescent(&fixture.owned_id, fixture.generation)
            .await
            .expect("suspend");
        assert!(fixture
            .manager
            .session_is_suspended(&fixture.owned_id, fixture.generation));
        fixture
            .manager
            .activate(&fixture.owned_id, fixture.generation)
            .await
            .expect("reactivate");

        let log = fs::read_to_string(fixture.root.join("suspend_effort_reactivation.jsonl"))
            .expect("fixture request log");
        assert!(
            log.contains(r#""reasoningEffort":"xhigh""#),
            "reactivation must ask for the effort the session is on now: {log}"
        );

        fixture
            .manager
            .close(&fixture.owned_id, fixture.generation)
            .await
            .unwrap();
        fs::remove_dir_all(fixture.root).unwrap();
    }

    /// A session's record — its name, where that name came from, the model it
    /// is on — belongs to the session, not to the effort it was started with.
    /// Every turn ends with the adapter stopped, so the next send ensures the
    /// session again and sends the effort it is on now. Reading that as a
    /// request for a different session threw the record away and started over.
    #[tokio::test(flavor = "current_thread")]
    async fn ensure_after_a_live_effort_change_keeps_the_session_record() {
        let fixture = fixture_manager_with_provider(
            "suspend_live_effort_ensure",
            AgentConversationProvider::Codex,
            Some("high"),
        )
        .await;
        let seen: Arc<Mutex<Vec<AgentConversationEvent>>> = Default::default();
        let sink = Arc::clone(&seen);
        fixture
            .manager
            .set_emitter(Arc::new(move |event| sink.lock().unwrap().push(event)));

        fixture
            .manager
            .prompt(
                &fixture.owned_id,
                fixture.generation,
                test_prompt("give this session a name"),
            )
            .await
            .expect("prompt starts");
        wait_until(|| completed_turns(&seen) == 1).await;
        let title = stored_title(&fixture).expect("the prompt names the session");
        assert_eq!(stored_title_source(&fixture).as_deref(), Some("prompt"));
        wait_until(|| {
            fixture
                .manager
                .session_is_suspended(&fixture.owned_id, fixture.generation)
        })
        .await;

        // A live change lands on the effort the adapter answers with, which is
        // not the one the session was started with.
        fixture
            .manager
            .set_conversation_config(SetAgentConversationConfigRequest {
                owned_id: fixture.owned_id.clone(),
                generation: fixture.generation,
                model: None,
                reasoning_effort: Some("medium".into()),
                approval_policy: None,
            })
            .await
            .expect("a live effort change");
        let live = fixture
            .manager
            .conversation_config(&fixture.owned_id)
            .expect("config");
        assert_eq!(live.reasoning_effort.as_deref(), Some("xhigh"));
        assert_eq!(live.model.as_deref(), Some("gpt-5.6-terra"));

        // The settings change already put the session to rest; asking again
        // is a no-op. What matters is the state, not who got there first.
        fixture
            .manager
            .suspend_if_quiescent(&fixture.owned_id, fixture.generation)
            .await
            .expect("suspend");
        assert!(fixture
            .manager
            .session_is_suspended(&fixture.owned_id, fixture.generation));

        // The next send ensures the session again, carrying the effort the
        // composer is showing: the live one, not the spawn one.
        let mut request = request(
            fixture.root.to_str().unwrap(),
            &fixture.owned_id,
            AgentConversationProvider::Codex,
        );
        request.reasoning_effort = Some("xhigh".into());
        let connection = fixture
            .manager
            .ensure_inner(request)
            .expect("ensure the suspended session")
            .0;

        assert_eq!(connection.generation, fixture.generation);
        assert_eq!(stored_title(&fixture).as_deref(), Some(title.as_str()));
        assert_eq!(stored_title_source(&fixture).as_deref(), Some("prompt"));
        let after = fixture
            .manager
            .conversation_config(&fixture.owned_id)
            .expect("config after ensure");
        assert_eq!(after.reasoning_effort.as_deref(), Some("xhigh"));
        assert_eq!(after.model.as_deref(), Some("gpt-5.6-terra"));

        fixture
            .manager
            .close(&fixture.owned_id, fixture.generation)
            .await
            .unwrap();
        fs::remove_dir_all(fixture.root).unwrap();
    }

    /// The rail saves a session's row back carrying its own copy of the fields,
    /// and a rail action that is not about the effort sends none. Writing that
    /// empty answer over the session wiped the effort it was on.
    #[tokio::test(flavor = "current_thread")]
    async fn saving_the_row_back_without_an_effort_leaves_the_effort() {
        let fixture = fixture_manager_with_provider(
            "meta_keeps_effort",
            AgentConversationProvider::Codex,
            Some("medium"),
        )
        .await;
        // The adapter answers the start-time effort with one of its own, so the
        // session is on "xhigh" while it was created with "medium".
        assert_eq!(
            fixture
                .manager
                .conversation_config(&fixture.owned_id)
                .expect("config")
                .reasoning_effort
                .as_deref(),
            Some("xhigh")
        );

        fixture
            .manager
            .update_session_meta(UpdateAgentConversationSessionMetaRequest {
                owned_id: fixture.owned_id.clone(),
                model: None,
                effort: None,
                meta: renamed_meta("Rail work"),
            })
            .expect("save the row back");

        assert_eq!(
            fixture
                .manager
                .conversation_config(&fixture.owned_id)
                .expect("config after the save")
                .reasoning_effort
                .as_deref(),
            Some("xhigh")
        );

        fixture
            .manager
            .close(&fixture.owned_id, fixture.generation)
            .await
            .unwrap();
        fs::remove_dir_all(fixture.root).unwrap();
    }

    /// An approval policy chosen while a session is running has to survive the
    /// adapter going away and coming back, just as the effort does. Adapters
    /// stop when a turn ends, so reactivation must ask for the policy the
    /// session is on now rather than let the restarted adapter's default win.
    #[tokio::test(flavor = "current_thread")]
    async fn reactivating_a_session_keeps_the_approval_policy_it_is_on() {
        let fixture = fixture_manager_with_provider(
            "suspend_approval_reactivation",
            AgentConversationProvider::Codex,
            None,
        )
        .await;

        // The adapter answers the requested policy with one of its own, so the
        // session lands on "never" having been asked for "untrusted".
        fixture
            .manager
            .set_conversation_config(SetAgentConversationConfigRequest {
                owned_id: fixture.owned_id.clone(),
                generation: fixture.generation,
                model: None,
                reasoning_effort: None,
                approval_policy: Some("untrusted".into()),
            })
            .await
            .expect("a live approval policy change");
        assert_eq!(
            fixture
                .manager
                .conversation_config(&fixture.owned_id)
                .expect("config")
                .approval_policy
                .as_deref(),
            Some("never")
        );

        // The settings change already put the session to rest; asking again
        // is a no-op. What matters is the state, not who got there first.
        fixture
            .manager
            .suspend_if_quiescent(&fixture.owned_id, fixture.generation)
            .await
            .expect("suspend");
        assert!(fixture
            .manager
            .session_is_suspended(&fixture.owned_id, fixture.generation));
        fixture
            .manager
            .activate(&fixture.owned_id, fixture.generation)
            .await
            .expect("reactivate");

        let log = fs::read_to_string(fixture.root.join("suspend_approval_reactivation.jsonl"))
            .expect("fixture request log");
        assert!(
            log.contains(r#""approvalPolicy":"never""#),
            "reactivation must ask for the approval policy the session is on now: {log}"
        );
        assert_eq!(
            fixture
                .manager
                .conversation_config(&fixture.owned_id)
                .expect("config")
                .approval_policy
                .as_deref(),
            Some("never")
        );

        fixture
            .manager
            .close(&fixture.owned_id, fixture.generation)
            .await
            .unwrap();
        fs::remove_dir_all(fixture.root).unwrap();
    }

    /// Which approval policies a session offers is the running adapter's
    /// answer, and a suspended session carries only the snapshot the adapter
    /// that last ran left behind. A policy the adapter installed now offers
    /// must not be refused on the strength of that stale list.
    #[tokio::test(flavor = "current_thread")]
    async fn a_policy_the_running_adapter_offers_is_not_refused_by_a_stale_list() {
        let fixture = fixture_manager_with_provider(
            "suspend_approval_stale_list",
            AgentConversationProvider::Codex,
            None,
        )
        .await;

        assert!(fixture
            .manager
            .suspend_if_quiescent(&fixture.owned_id, fixture.generation)
            .await
            .expect("suspend"));
        fixture
            .manager
            .hydrate_overlay_from_store(&fixture.owned_id)
            .unwrap();
        {
            let mut sessions = fixture.manager.sessions.lock().unwrap();
            let session = sessions.get_mut(&fixture.owned_id).unwrap();
            session
                .config
                .available_approval_policies
                .retain(|policy| policy.as_str() != "never");
        }

        let config = fixture
            .manager
            .set_conversation_config(SetAgentConversationConfigRequest {
                owned_id: fixture.owned_id.clone(),
                generation: fixture.generation,
                model: None,
                reasoning_effort: None,
                approval_policy: Some("never".into()),
            })
            .await
            .expect("a policy the running adapter offers is refused by a stale list");
        assert_eq!(config.approval_policy.as_deref(), Some("never"));

        fixture
            .manager
            .close(&fixture.owned_id, fixture.generation)
            .await
            .unwrap();
        fs::remove_dir_all(fixture.root).unwrap();
    }

    /// Judging a settings change against the running adapter means an adapter
    /// is started to ask. When the change is then refused it has no turn to
    /// run and nothing else would ever stop it, so the refusal has to put the
    /// session back at rest itself.
    #[tokio::test(flavor = "current_thread")]
    async fn a_refused_config_change_does_not_leave_an_adapter_running() {
        let fixture = fixture_manager_with_acp_session("suspend_refused_config").await;
        assert!(fixture
            .manager
            .suspend_if_quiescent(&fixture.owned_id, fixture.generation)
            .await
            .expect("suspend"));

        let refused = fixture
            .manager
            .set_conversation_config(SetAgentConversationConfigRequest {
                owned_id: fixture.owned_id.clone(),
                generation: fixture.generation,
                model: None,
                reasoning_effort: None,
                approval_policy: Some("acceptedits".into()),
            })
            .await
            .expect_err("a policy the adapter does not offer is refused");
        assert!(
            refused.contains("approval policy"),
            "the refusal names what was wrong: {refused}"
        );
        assert!(
            fixture
                .manager
                .session_is_suspended(&fixture.owned_id, fixture.generation),
            "a settings change that was refused leaves the session at rest"
        );

        fs::remove_dir_all(fixture.root).unwrap();
    }

    /// Whether the adapter has an effort control is a fact about the adapter,
    /// not about this run of the app. A conversation read back from the store
    /// after a restart still has one, and can still be re-pointed.
    #[tokio::test(flavor = "current_thread")]
    async fn a_session_read_back_after_a_restart_can_still_change_its_effort() {
        let root = temp_root();
        let database = root.join("sessions.db");
        let log = root.join("recovered-effort.jsonl");
        let manifest = super::super::providers::acp_client::tests::fixture_manifest_named(
            &log,
            "suspend_recovered_effort",
        );
        let providers =
            ProviderRegistry::new([(AgentConversationProvider::Claude, manifest.clone())]).unwrap();
        let manager = AgentRuntimeManager::open(providers, &database).unwrap();
        let connection = manager
            .ensure_async(request(
                root.to_str().unwrap(),
                "owned-recovered-effort",
                AgentConversationProvider::Claude,
            ))
            .await
            .unwrap();
        manager
            .activate(&connection.owned_id, connection.generation)
            .await
            .unwrap();
        drop(manager);

        let providers =
            ProviderRegistry::new([(AgentConversationProvider::Claude, manifest)]).unwrap();
        let recovered = AgentRuntimeManager::open(providers, &database).unwrap();
        let config = recovered
            .set_conversation_config(SetAgentConversationConfigRequest {
                owned_id: connection.owned_id.clone(),
                generation: connection.generation,
                model: None,
                reasoning_effort: Some("xhigh".into()),
                approval_policy: None,
            })
            .await
            .expect("an adapter that offers an effort control still offers one after a restart");
        assert_eq!(config.reasoning_effort.as_deref(), Some("xhigh"));

        recovered
            .close(&connection.owned_id, connection.generation)
            .await
            .unwrap();
        fs::remove_dir_all(root).unwrap();
    }

    /// Which efforts exist is the adapter's answer, so a new session may be
    /// started on any of them. A conversation left on an effort outside the
    /// list this app can offer before an adapter exists must still be able to
    /// start one.
    #[tokio::test(flavor = "current_thread")]
    async fn a_new_session_may_start_on_any_effort_the_adapter_offers() {
        let root = temp_root();
        let log = root.join("ensure-effort.jsonl");
        let manifest = super::super::providers::acp_client::tests::fixture_manifest_named(
            &log,
            "config_options",
        );
        let providers = ProviderRegistry::new([(AgentConversationProvider::Claude, manifest)])
            .expect("fixture provider");
        let manager = AgentRuntimeManager::new(providers);
        let mut ensure_request = request(
            root.to_str().unwrap(),
            "owned-ensure-effort",
            AgentConversationProvider::Claude,
        );
        ensure_request.reasoning_effort = Some("xhigh".to_string());

        let connection = manager
            .ensure_inner(ensure_request)
            .expect("an effort the adapter offers is not refused before it runs")
            .0;
        assert_eq!(connection.config.reasoning_effort.as_deref(), Some("xhigh"));

        fs::remove_dir_all(root).unwrap();
    }

    #[tokio::test(flavor = "current_thread")]
    async fn an_adapter_with_no_effort_control_refuses_a_live_effort_change() {
        let fixture = fixture_manager_with_provider(
            "standard_config",
            AgentConversationProvider::Claude,
            None,
        )
        .await;

        let log = fs::read_to_string(fixture.root.join("standard_config.jsonl"))
            .expect("Claude spawn log");
        assert!(
            log.contains("MAX_THINKING_TOKENS=<unset>"),
            "nothing in the environment decides how hard a session thinks: {log}"
        );
        let config = fixture
            .manager
            .conversation_config(&fixture.owned_id)
            .expect("Claude config");
        // An absent control must not acquire app-supplied choices.
        assert_eq!(config.reasoning_effort, None);
        assert!(config.available_efforts.is_empty());

        let error = fixture
            .manager
            .set_conversation_config(SetAgentConversationConfigRequest {
                owned_id: fixture.owned_id.clone(),
                generation: fixture.generation,
                model: None,
                reasoning_effort: Some("high".into()),
                approval_policy: None,
            })
            .await
            .expect_err("an adapter with no effort control cannot be re-pointed");
        assert_eq!(
            error,
            "Effort is set when the session starts. Start a new session to change it."
        );

        fixture
            .manager
            .close(&fixture.owned_id, fixture.generation)
            .await
            .unwrap();
        fs::remove_dir_all(fixture.root).unwrap();
    }

    #[tokio::test(flavor = "current_thread")]
    async fn session_start_effort_is_applied_after_start() {
        let fixture = fixture_manager_with_provider(
            "session_start_effort",
            AgentConversationProvider::Codex,
            Some("xhigh"),
        )
        .await;

        let log = fs::read_to_string(fixture.root.join("session_start_effort.jsonl"))
            .expect("fixture request log");
        assert!(log.contains(r#""method":"session/set_config_option""#));
        assert!(log.contains(r#""reasoningEffort":"xhigh""#));
        assert_eq!(
            fixture
                .manager
                .conversation_config(&fixture.owned_id)
                .expect("stored config")
                .reasoning_effort
                .as_deref(),
            Some("xhigh")
        );

        fixture
            .manager
            .close(&fixture.owned_id, fixture.generation)
            .await
            .unwrap();
        fs::remove_dir_all(fixture.root).unwrap();
    }

    #[tokio::test(flavor = "current_thread")]
    async fn stored_session_effort_is_reapplied_on_resume() {
        let root = temp_root();
        let database = root.join("sessions.db");
        let log = root.join("stored-session-effort.jsonl");
        let manifest = super::super::providers::acp_client::tests::fixture_manifest_named(
            &log,
            "suspend_stored_session_effort",
        );
        let providers =
            ProviderRegistry::new([(AgentConversationProvider::Codex, manifest.clone())]).unwrap();
        let manager = AgentRuntimeManager::open(providers, &database).unwrap();
        let mut ensure = request(
            root.to_str().unwrap(),
            "owned-stored-session-effort",
            AgentConversationProvider::Codex,
        );
        ensure.reasoning_effort = Some("xhigh".into());
        let connection = manager.ensure_async(ensure).await.unwrap();
        manager
            .activate(&connection.owned_id, connection.generation)
            .await
            .unwrap();
        drop(manager);

        let providers =
            ProviderRegistry::new([(AgentConversationProvider::Codex, manifest)]).unwrap();
        let recovered = AgentRuntimeManager::open(providers, &database).unwrap();
        recovered
            .activate(&connection.owned_id, connection.generation)
            .await
            .expect("stored session resumes");
        let snapshot = recovered.snapshot(&connection.owned_id).unwrap().unwrap();
        assert!(!snapshot.suspended);
        assert_eq!(
            snapshot.connection.config.reasoning_effort.as_deref(),
            Some("xhigh")
        );
        let requests = fs::read_to_string(&log).expect("fixture request log");
        assert!(requests.contains(r#""method":"session/resume""#));
        assert_eq!(requests.matches("session/set_config_option").count(), 2);

        recovered
            .close(&connection.owned_id, connection.generation)
            .await
            .unwrap();
        fs::remove_dir_all(root).unwrap();
    }

    #[tokio::test(flavor = "current_thread")]
    async fn refused_selected_effort_never_prompts() {
        let root = temp_root();
        let log = root.join("config_update_failure.jsonl");
        let manifest = super::super::providers::acp_client::tests::fixture_manifest_named(&log, "config_update_failure");
        let providers = ProviderRegistry::new([(AgentConversationProvider::Codex, manifest)]).unwrap();
        let manager = AgentRuntimeManager::new(providers);
        let mut ensure = request(root.to_str().unwrap(), "owned-refused-effort", AgentConversationProvider::Codex);
        ensure.reasoning_effort = Some("xhigh".into());
        let connection = manager.ensure_inner(ensure).unwrap().0;
        let error = manager.send_message(&connection.owned_id, connection.generation, test_prompt("must not send"), None, None)
            .await.unwrap_err();
        assert!(error.contains("selected model or effort could not be applied"));
        let requests = fs::read_to_string(&log).unwrap();
        assert!(requests.contains("session/set_config_option"));
        assert!(!requests.contains("session/prompt"));
        assert!(manager.resource_roots().is_empty());
        fs::remove_dir_all(root).unwrap();
    }

    #[tokio::test(flavor = "current_thread")]
    async fn provider_probe_reads_choices_without_saving_or_prompting() {
        let root = temp_root();
        let log = root.join("catalog.jsonl");
        let manifest = super::super::providers::acp_client::tests::fixture_manifest_named(&log, "config_options");
        let providers = ProviderRegistry::new([(AgentConversationProvider::Claude, manifest)]).unwrap();
        let manager = AgentRuntimeManager::new(providers);
        let config = manager.probe_provider_config(AgentConversationProvider::Claude, root.to_str().unwrap())
            .await.unwrap();
        assert!(config.available_models.contains(&"opus[1m]".to_string()));
        assert_eq!(config.model_labels.get("opus[1m]").map(String::as_str), Some("Opus (1M context)"));
        assert!(config.model_efforts.is_empty());
        assert_eq!(manager.resource_diagnostics().unwrap().durable_session_rows, 0);
        let requests = fs::read_to_string(&log).unwrap();
        assert!(requests.contains("session/new"));
        assert!(!requests.contains("session/prompt"));
        fs::remove_dir_all(root).unwrap();
    }

    #[tokio::test(flavor = "current_thread")]
    async fn codex_probe_reads_each_models_efforts_and_default() {
        let root = temp_root();
        let log = root.join("codex-catalog.jsonl");
        let config_log = root.join("codex-config.json");
        let mut manifest = super::super::providers::acp_client::tests::fixture_manifest_named(&log, "codex_catalog");
        manifest.id = "codex-acp".into();
        manifest.args[1] = format!(
            "printf '%s\\n' \"$CODEX_CONFIG\" > '{}'\n{}",
            config_log.display().to_string().replace('\'', "'\\''"), manifest.args[1],
        );
        let providers = ProviderRegistry::new([(AgentConversationProvider::Codex, manifest)]).unwrap();
        let manager = AgentRuntimeManager::new(providers);
        let config = manager.probe_provider_config(AgentConversationProvider::Codex, root.to_str().unwrap())
            .await.unwrap();
        let launch_config = fs::read_to_string(&config_log).unwrap();
        let launch_config: serde_json::Value = if launch_config.trim().is_empty() {
            json!({})
        } else {
            serde_json::from_str(&launch_config).unwrap()
        };
        let inherited = std::env::var("CODEX_CONFIG").map_or(json!({}), |value| serde_json::from_str(&value).unwrap());
        assert_eq!(launch_config, inherited, "settings probe changed the inherited CODEX_CONFIG (Assembly approval override?)");
        // The draft opens on the starting model; the other models' defaults come from one switch each.
        assert_eq!(config.model.as_deref(), Some("gpt-a"));
        assert_eq!(config.available_efforts, ["low", "high"]);
        assert_eq!(config.model_efforts.get("gpt-a").unwrap(), &["low", "high"]);
        assert_eq!(config.model_efforts.get("gpt-b").unwrap(), &["low", "medium"]);
        assert_eq!(config.model_default_efforts.get("gpt-a").map(String::as_str), Some("high"));
        assert_eq!(config.model_default_efforts.get("gpt-b").map(String::as_str), Some("medium"));
        let requests = fs::read_to_string(&log).unwrap();
        assert_eq!(requests.matches("session/set_config_option").count(), 1);
        assert!(!requests.contains("session/prompt"));
        fs::remove_dir_all(root).unwrap();
    }

    #[tokio::test(flavor = "current_thread")]
    async fn cancelling_provider_probe_stops_before_a_late_answer() {
        let root = temp_root();
        let log = root.join("slow-catalog.jsonl");
        let mut manifest = super::super::providers::acp_client::tests::fixture_manifest_named(&log, "config_options");
        manifest.args[1] = format!("sleep 30\n{}", manifest.args[1].clone());
        let providers = ProviderRegistry::new([(AgentConversationProvider::Claude, manifest)]).unwrap();
        let manager = AgentRuntimeManager::new(providers);
        let probe = manager.probe_provider_config_for_request(AgentConversationProvider::Claude, root.to_str().unwrap(), 42);
        let result = tokio::time::timeout(Duration::from_secs(2), async {
            tokio::pin!(probe);
            tokio::select! {
                result = &mut probe => result,
                _ = tokio::time::sleep(Duration::from_millis(50)) => {
                    manager.cancel_provider_probe(42);
                    probe.await
                }
            }
        }).await.unwrap();
        assert!(result.unwrap_err().contains("cancelled"));
        assert_eq!(manager.resource_diagnostics().unwrap().durable_session_rows, 0);
        fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn late_provider_probe_cancellations_keep_one_high_water_mark() {
        let manager = AgentRuntimeManager::new(ProviderRegistry::default());
        for request_id in 1..=10_000 {
            manager.cancel_provider_probe(request_id);
        }
        manager.cancel_provider_probe(42);
        assert_eq!(*manager.probe_cancellations.borrow(), 10_000);
        assert!(manager.provider_probe_cancelled(10_000));
        assert!(!manager.provider_probe_cancelled(10_001));
    }

    #[tokio::test(flavor = "current_thread")]
    async fn config_change_on_a_suspended_session_leaves_no_process_behind() {
        let fixture = fixture_manager_with_acp_session("suspend_config_wake").await;
        assert!(fixture
            .manager
            .suspend_if_quiescent(&fixture.owned_id, fixture.generation)
            .await
            .unwrap());

        let configured = fixture
            .manager
            .set_conversation_config(SetAgentConversationConfigRequest {
                owned_id: fixture.owned_id.clone(),
                generation: fixture.generation,
                model: Some("gpt-5.6-terra".into()),
                reasoning_effort: None,
                approval_policy: None,
            })
            .await
            .expect("a suspended session wakes for a settings change");
        assert_eq!(configured.model.as_deref(), Some("gpt-5.6-terra"));
        let snapshot = fixture
            .manager
            .snapshot(&fixture.owned_id)
            .unwrap()
            .unwrap();
        assert!(snapshot.suspended);
        assert!(fixture.manager.resource_roots().is_empty());

        fixture
            .manager
            .close(&fixture.owned_id, fixture.generation)
            .await
            .unwrap();
        fs::remove_dir_all(fixture.root).unwrap();
    }

    /// Both halves of the bargain, in one test: a resumed conversation learns
    /// what its agent offers, and no adapter is left running for a turn that is
    /// not coming. An earlier attempt at this suspended instead of stopping and
    /// left one process per resume behind, so the second assertion is the one
    /// that matters most.
    #[tokio::test(flavor = "current_thread")]
    async fn warming_a_conversation_keeps_its_answers_and_leaves_no_adapter() {
        let fixture = fixture_manager_with_acp_session("warm_conversation_config").await;

        // Activated up front so the adapter's pid can be taken while it is still
        // running. Warming activates too; finding the session already up is the
        // ordinary case, since a reader who has sent anything has one.
        fixture
            .manager
            .activate(&fixture.owned_id, fixture.generation)
            .await
            .expect("activate");
        let (runtime, pool_key) = {
            let sessions = fixture
                .manager
                .sessions
                .lock()
                .unwrap_or_else(std::sync::PoisonError::into_inner);
            let session = sessions.get(&fixture.owned_id).expect("activated session");
            (
                session.runtime.clone().expect("a running adapter"),
                session.pool_key.clone().expect("a pooled adapter"),
            )
        };
        let adapter_pid = runtime
            .lock()
            .await
            .process_id()
            .expect("the adapter runs in a process of its own");
        drop(runtime);

        let warmed = fixture
            .manager
            .warm_conversation_config(&fixture.owned_id, fixture.generation)
            .await
            .expect("warm config");
        assert!(
            !warmed.available_models.is_empty(),
            "warming has to come back with what the agent offers"
        );

        assert_eq!(
            fixture
                .manager
                .conversation_config(&fixture.owned_id)
                .expect("stored config"),
            warmed,
            "what warming learnt is what an ordinary read gives back"
        );

        assert!(
            fixture
                .manager
                .session_is_suspended(&fixture.owned_id, fixture.generation),
            "the adapter is stopped, not left warm for a turn nobody asked for"
        );

        // The assertion above is bookkeeping: it is equally true of the detach
        // that leaked. These two are the ones that mean the process is gone —
        // its pool entry torn down rather than short-circuited, and its pid no
        // longer answering.
        assert!(
            !fixture
                .manager
                .adapter_pools
                .lock()
                .await
                .contains_key(&pool_key),
            "the adapter pool entry was torn down"
        );
        let reaped = Instant::now() + Duration::from_secs(2);
        while unsafe { libc::kill(adapter_pid as i32, 0) } == 0 && Instant::now() < reaped {
            tokio::time::sleep(Duration::from_millis(10)).await;
        }
        assert_ne!(
            unsafe { libc::kill(adapter_pid as i32, 0) },
            0,
            "the adapter process outlived the warm that was supposed to stop it"
        );

        fs::remove_dir_all(fixture.root).unwrap();
    }

    #[tokio::test(flavor = "current_thread")]
    async fn a_send_refused_before_its_prompt_leaves_no_process_behind() {
        // The first message of a new session carries the choices made in the
        // start pane. When one of them is refused, the adapter that activation
        // just started stayed up with no turn to run and nothing to stop it —
        // a Claude session that failed this way kept five processes alive
        // until the app quit.
        let fixture = fixture_manager_with_acp_session("suspend_refused_send").await;
        let (runtime, pool_key) = {
            let sessions = fixture
                .manager
                .sessions
                .lock()
                .unwrap_or_else(std::sync::PoisonError::into_inner);
            let session = sessions.get(&fixture.owned_id).expect("activated session");
            (
                session.runtime.clone().expect("a running adapter"),
                session.pool_key.clone().expect("a pooled adapter"),
            )
        };
        let adapter_pid = runtime
            .lock()
            .await
            .process_id()
            .expect("the adapter runs in a process of its own");
        drop(runtime);

        let refused = fixture
            .manager
            .send_message(
                &fixture.owned_id,
                fixture.generation,
                test_prompt("hello"),
                None,
                Some("acceptedits".into()),
            )
            .await
            .expect_err("a policy the provider does not offer is refused");
        assert!(
            refused.contains("approval policy"),
            "the refusal names what was wrong: {refused}"
        );

        assert!(
            fixture
                .manager
                .session_is_suspended(&fixture.owned_id, fixture.generation),
            "a send that never went out leaves the session at rest"
        );
        assert!(
            !fixture
                .manager
                .adapter_pools
                .lock()
                .await
                .contains_key(&pool_key),
            "the adapter pool entry was torn down"
        );
        let reaped = Instant::now() + Duration::from_secs(2);
        while unsafe { libc::kill(adapter_pid as i32, 0) } == 0 && Instant::now() < reaped {
            tokio::time::sleep(Duration::from_millis(10)).await;
        }
        assert_ne!(
            unsafe { libc::kill(adapter_pid as i32, 0) },
            0,
            "the adapter process outlived the send that was refused"
        );

        // The refusal cost nothing: the next send, with a policy the provider
        // offers, starts the adapter again and goes out.
        fixture
            .manager
            .send_message(
                &fixture.owned_id,
                fixture.generation,
                test_prompt("hello again"),
                None,
                Some("never".into()),
            )
            .await
            .expect("a valid send after a refused one");

        // Close first: the restarted adapter still writes in the folder.
        finish(fixture).await;
    }

    #[tokio::test(flavor = "current_thread")]
    async fn conversation_config_commands_read_set_forward_and_reject_stale_generation() {
        let fixture = fixture_manager_with_acp_session("conversation_config").await;
        let initial = fixture
            .manager
            .conversation_config(&fixture.owned_id)
            .expect("initial config");
        assert_eq!(initial.model.as_deref(), Some("gpt-5.6-sol"));
        assert_eq!(initial.reasoning_effort.as_deref(), Some("high"));
        assert_eq!(initial.approval_policy.as_deref(), Some("on-request"));

        let configured = fixture
            .manager
            .set_conversation_config(SetAgentConversationConfigRequest {
                owned_id: fixture.owned_id.clone(),
                generation: fixture.generation,
                model: Some("gpt-5.6-terra".into()),
                reasoning_effort: Some("xhigh".into()),
                approval_policy: Some("never".into()),
            })
            .await
            .expect("set config");
        assert_eq!(configured.model.as_deref(), Some("gpt-5.6-terra"));
        assert_eq!(configured.reasoning_effort.as_deref(), Some("xhigh"));
        assert_eq!(configured.approval_policy.as_deref(), Some("never"));
        assert_eq!(
            fixture
                .manager
                .conversation_config(&fixture.owned_id)
                .expect("stored config"),
            configured
        );

        let stale = fixture
            .manager
            .set_conversation_config(SetAgentConversationConfigRequest {
                owned_id: fixture.owned_id.clone(),
                generation: fixture.generation.saturating_add(1),
                model: Some("gpt-5.6-sol".into()),
                reasoning_effort: None,
                approval_policy: None,
            })
            .await
            .expect_err("stale generation must fail");
        assert_eq!(
            stale,
            "Conversation connection changed; retry on the current session"
        );

        let fixture_log = fs::read_to_string(fixture.root.join("conversation_config.jsonl"))
            .expect("config fixture log");
        assert_eq!(fixture_log.matches("session/set_config_option").count(), 1);
        assert!(fixture_log.contains(r#""model":"gpt-5.6-terra""#));
        assert!(fixture_log.contains(r#""reasoningEffort":"xhigh""#));
        assert!(fixture_log.contains(r#""approvalPolicy":"never""#));

        fixture
            .manager
            .close(&fixture.owned_id, fixture.generation)
            .await
            .unwrap();
        fs::remove_dir_all(fixture.root).unwrap();
    }

    #[tokio::test(flavor = "current_thread")]
    async fn cancellation_waits_for_prompt_acceptance() {
        let fixture = fixture_manager_with_acp_session("cancelled_turn").await;
        let lifecycle = fixture.manager.lifecycle_guard(&fixture.owned_id).await.unwrap();
        let manager = fixture.manager.clone();
        let owned_id = fixture.owned_id.clone();
        let generation = fixture.generation;
        let cancel = tokio::spawn(async move { manager.cancel_turn(&owned_id, generation).await });
        tokio::task::yield_now().await;
        assert!(!cancel.is_finished());
        fixture.manager.prompt(&fixture.owned_id, generation, test_prompt("hello"))
            .await.unwrap();
        drop(lifecycle);
        cancel.await.unwrap().unwrap();
        let log = fixture.root.join("cancelled_turn.jsonl");
        wait_until(|| fs::read_to_string(&log).unwrap_or_default().contains("session/cancel")).await;
        fixture.manager.close(&fixture.owned_id, generation).await.unwrap();
        fs::remove_dir_all(fixture.root).unwrap();
    }

    #[tokio::test(flavor = "current_thread")]
    async fn cancel_right_after_prompt_reaches_the_agent_after_the_prompt() {
        let fixture = fixture_manager_with_acp_session("cancelled_turn").await;
        let generation = fixture.generation;
        fixture.manager.prompt(&fixture.owned_id, generation, test_prompt("hello"))
            .await.unwrap();
        fixture.manager.cancel_turn(&fixture.owned_id, generation).await.unwrap();
        let log = fixture.root.join("cancelled_turn.jsonl");
        wait_until(|| {
            let log = fs::read_to_string(&log).unwrap_or_default();
            log.contains(r#""method":"session/prompt""#) && log.contains(r#""method":"session/cancel""#)
        })
        .await;
        let log = fs::read_to_string(&log).unwrap();
        assert!(
            log.find(r#""method":"session/prompt""#) < log.find(r#""method":"session/cancel""#),
            "the agent received the cancel before the prompt it should stop"
        );
        fixture.manager.close(&fixture.owned_id, generation).await.unwrap();
        fs::remove_dir_all(fixture.root).unwrap();
    }

    #[tokio::test(flavor = "current_thread")]
    async fn cancelled_prompt_emits_interrupted_turn() {
        let fixture = fixture_manager_with_acp_session("cancelled_turn").await;
        let seen: Arc<Mutex<Vec<AgentConversationEvent>>> = Default::default();
        let sink = Arc::clone(&seen);
        fixture
            .manager
            .set_emitter(Arc::new(move |event| sink.lock().unwrap().push(event)));
        fixture
            .manager
            .prompt(&fixture.owned_id, fixture.generation, test_prompt("hello"))
            .await
            .expect("prompt starts");
        let fixture_log_path = fixture.root.join("cancelled_turn.jsonl");
        wait_until(|| {
            fs::read_to_string(&fixture_log_path)
                .map(|log| log.contains(r#""method":"session/prompt""#))
                .unwrap_or(false)
        })
        .await;
        fixture
            .manager
            .cancel_turn(&fixture.owned_id, fixture.generation)
            .await
            .expect("cancel notification");
        wait_until(|| {
            seen.lock().unwrap().iter().any(|event| {
                matches!(
                    event.payload,
                    AgentConversationPayload::Turn {
                        state: super::super::protocol::TurnState::Interrupted,
                        ..
                    }
                )
            })
        })
        .await;
        let seen = seen.lock().unwrap();
        assert!(!seen.iter().any(|event| {
            matches!(
                event.payload,
                AgentConversationPayload::Turn {
                    state: super::super::protocol::TurnState::Completed,
                    ..
                }
            )
        }));
        assert!(seen
            .windows(2)
            .all(|events| events[1].sequence == events[0].sequence + 1));
        drop(seen);
        fixture
            .manager
            .close(&fixture.owned_id, fixture.generation)
            .await
            .unwrap();
        fs::remove_dir_all(fixture.root).unwrap();
    }

    #[tokio::test(flavor = "current_thread")]
    async fn antigravity_authentication_send_retries_once_and_stop_preserves_unsent_prompt() {
        for fixture_name in ["auth", "auth_wait", "auth_close"] {
            let root = temp_root();
            let log = root.join("authentication.jsonl");
            let manifest = super::super::providers::acp_client::tests::fixture_manifest_named(&log, if fixture_name == "auth_close" { "auth_wait" } else { fixture_name });
            let manager = AgentRuntimeManager::new(ProviderRegistry::new([
                (AgentConversationProvider::Antigravity, manifest)
            ]).unwrap());
            let owned_id = "auth-owned";
            let connection = manager.ensure_inner(request(root.to_str().unwrap(), owned_id,
                AgentConversationProvider::Antigravity)).unwrap().0;
            let generation = connection.generation;
            let sender = manager.clone();
            let send = tokio::spawn(async move {
                sender.send_message(owned_id, generation, test_prompt("unsent until authenticated"), None, None).await
            });
            tokio::time::timeout(Duration::from_secs(5), async {
                while !fs::read_to_string(&log).unwrap_or_default().contains("\"method\":\"authenticate\"") {
                    tokio::time::sleep(Duration::from_millis(10)).await;
                }
            }).await.unwrap();
            if fixture_name != "auth" {
                assert!(manager.has_pending_provider_work());
                if fixture_name == "auth_close" {
                    tokio::time::timeout(Duration::from_secs(5), manager.close(owned_id, generation)).await.unwrap().unwrap();
                } else {
                    tokio::time::timeout(Duration::from_secs(5), manager.cancel_turn(owned_id, generation)).await.unwrap().unwrap();
                }
                assert!(tokio::time::timeout(Duration::from_secs(5), send).await.unwrap().unwrap().unwrap_err().contains("cancelled"));
                assert!(!fs::read_to_string(&log).unwrap().contains("\"method\":\"session/prompt\""));
            } else {
                tokio::time::timeout(Duration::from_secs(5), send).await.unwrap().unwrap().unwrap();
                // Send acknowledges acceptance before the prompt task writes its frame.
                tokio::time::timeout(Duration::from_secs(5), async {
                    while !fs::read_to_string(&log).unwrap_or_default().contains("\"method\":\"session/prompt\"") {
                        tokio::time::sleep(Duration::from_millis(10)).await;
                    }
                }).await.unwrap();
                let frames = fs::read_to_string(&log).unwrap();
                assert_eq!(frames.matches("\"method\":\"authenticate\"").count(), 1);
                assert_eq!(frames.matches("\"method\":\"session/prompt\"").count(), 1);
            }
            assert!(!manager.authentications.pending());
            manager.close(owned_id, generation).await.unwrap();
            fs::remove_dir_all(root).unwrap();
        }
    }

    #[tokio::test(flavor = "current_thread")]
    async fn ignored_antigravity_cancel_stops_runtime_and_preserves_resume_id() {
        let fixture = fixture_manager_with_provider(
            "agy_ignored_cancel",
            AgentConversationProvider::Antigravity,
            None,
        )
        .await;
        let native_session_id = fixture
            .manager
            .snapshot(&fixture.owned_id)
            .unwrap()
            .unwrap()
            .connection
            .native_session_id
            .expect("native session id");

        fixture
            .manager
            .prompt(&fixture.owned_id, fixture.generation, test_prompt("hello"))
            .await
            .expect("prompt starts");
        fixture
            .manager
            .cancel_turn(&fixture.owned_id, fixture.generation)
            .await
            .expect("cancel notification");
        let first_deadline = fixture
            .manager
            .sessions
            .lock()
            .unwrap()
            .get(&fixture.owned_id)
            .unwrap()
            .cancel_deadline
            .as_ref()
            .unwrap()
            .id();
        fixture
            .manager
            .cancel_turn(&fixture.owned_id, fixture.generation)
            .await
            .expect("repeated cancel notification");
        let sessions = fixture.manager.sessions.lock().unwrap();
        assert_eq!(
            sessions[&fixture.owned_id]
                .cancel_deadline
                .as_ref()
                .unwrap()
                .id(),
            first_deadline,
            "repeated cancellation must not extend the deadline"
        );
        drop(sessions);

        tokio::time::timeout(Duration::from_secs(4), async {
            loop {
                let snapshot = fixture
                    .manager
                    .snapshot(&fixture.owned_id)
                    .unwrap()
                    .unwrap();
                let interrupted = snapshot.events.iter().any(|event| {
                    matches!(
                        event.payload,
                        AgentConversationPayload::Turn {
                            state: super::super::protocol::TurnState::Interrupted,
                            ..
                        }
                    )
                });
                if interrupted
                    && snapshot.connection.state == ConversationConnectionState::Disconnected
                    && fixture.manager.resource_diagnostics().unwrap().sidecar_processes == 0
                {
                    break;
                }
                tokio::time::sleep(Duration::from_millis(10)).await;
            }
        })
        .await
        .expect("ignored cancellation must be bounded");

        let snapshot = fixture
            .manager
            .snapshot(&fixture.owned_id)
            .unwrap()
            .unwrap();
        assert_eq!(
            snapshot.connection.native_session_id.as_deref(),
            Some(native_session_id.as_str())
        );
        fixture
            .manager
            .activate(&fixture.owned_id, fixture.generation)
            .await
            .expect("resume after bounded cancellation");
        let fixture_log = fs::read_to_string(fixture.root.join("agy_ignored_cancel.jsonl"))
            .expect("Antigravity fixture log");
        assert!(fixture_log.contains(r#""method":"session/resume""#));
        assert!(fixture_log.contains(&format!(r#""sessionId":"{native_session_id}""#)));

        fixture
            .manager
            .close(&fixture.owned_id, fixture.generation)
            .await
            .unwrap();
        fs::remove_dir_all(fixture.root).unwrap();
    }

    #[tokio::test(flavor = "current_thread")]
    async fn cancelling_a_prompt_answers_pending_permissions() {
        let fixture = fixture_manager_with_acp_session("permission_cancelled").await;
        let seen: Arc<Mutex<Vec<AgentConversationEvent>>> = Default::default();
        let sink = Arc::clone(&seen);
        fixture
            .manager
            .set_emitter(Arc::new(move |event| sink.lock().unwrap().push(event)));
        fixture
            .manager
            .prompt(&fixture.owned_id, fixture.generation, test_prompt("hello"))
            .await
            .expect("prompt starts");
        wait_until(|| {
            seen.lock().unwrap().iter().any(|event| {
                matches!(
                    event.payload,
                    AgentConversationPayload::Approval {
                        state: ApprovalState::Requested,
                        ..
                    }
                )
            })
        })
        .await;
        fixture
            .manager
            .cancel_turn(&fixture.owned_id, fixture.generation)
            .await
            .expect("cancel notification");
        wait_until(|| {
            seen.lock().unwrap().iter().any(|event| {
                matches!(
                    event.payload,
                    AgentConversationPayload::Turn {
                        state: super::super::protocol::TurnState::Interrupted,
                        ..
                    }
                )
            })
        })
        .await;
        let seen = seen.lock().unwrap();
        assert!(seen.iter().any(|event| {
            matches!(
                event.payload,
                AgentConversationPayload::Approval {
                    state: ApprovalState::Declined,
                    ..
                }
            )
        }));
        drop(seen);
        // Interrupted is emitted before the asynchronous wire response is read
        // by the fixture. Wait for that separate receipt before asserting it.
        wait_until(|| {
            fs::read_to_string(fixture.root.join("permission_cancelled.jsonl"))
                .is_ok_and(|log| log.contains(r#""outcome":{"outcome":"cancelled"}"#))
        })
        .await;
        let fixture_log = fs::read_to_string(fixture.root.join("permission_cancelled.jsonl"))
            .expect("permission cancellation fixture log");
        assert!(
            fixture_log.contains(r#""outcome":{"outcome":"cancelled"}"#),
            "cancellation must answer the wire permission request: {fixture_log}"
        );
        fixture
            .manager
            .close(&fixture.owned_id, fixture.generation)
            .await
            .unwrap();
        fs::remove_dir_all(fixture.root).unwrap();
    }

    #[tokio::test(flavor = "current_thread")]
    async fn transport_exit_emits_connection_failure_and_recoverable_error() {
        let fixture = fixture_manager_with_acp_session("reply_then_dies").await;
        let seen: Arc<Mutex<Vec<AgentConversationEvent>>> = Default::default();
        let sink = Arc::clone(&seen);
        fixture
            .manager
            .set_emitter(Arc::new(move |event| sink.lock().unwrap().push(event)));
        fixture
            .manager
            .prompt(&fixture.owned_id, fixture.generation, test_prompt("hello"))
            .await
            .expect("prompt starts");
        wait_until(|| {
            seen.lock().unwrap().iter().any(|event| {
                matches!(
                    &event.payload,
                    AgentConversationPayload::Error {
                        code,
                        recoverable: true,
                        ..
                    } if code == "acp-transport"
                )
            })
        })
        .await;
        let seen = seen.lock().unwrap();
        assert!(seen.iter().any(|event| {
            matches!(
                event.payload,
                AgentConversationPayload::Connection {
                    state: ConversationConnectionState::Failed,
                    ..
                }
            )
        }));
        assert!(seen
            .windows(2)
            .all(|events| events[1].sequence == events[0].sequence + 1));
        let failed_turn = seen.iter().position(|event| {
            matches!(
                event.payload,
                AgentConversationPayload::Turn {
                    state: super::super::protocol::TurnState::Failed,
                    ..
                }
            )
        });
        assert!(failed_turn.is_some());
        // The reply the adapter was cut off in still ends as a finished message.
        let finished_reply = seen.iter().position(|event| {
            matches!(
                &event.payload,
                AgentConversationPayload::AssistantMessage { item_id, text, blocks: Some(_), .. }
                    if item_id == "cut-off" && text == "partial"
            )
        });
        assert!(finished_reply.is_some() && finished_reply < failed_turn);
        drop(seen);
        assert_eq!(
            fixture
                .manager
                .sessions
                .lock()
                .unwrap()
                .get(&fixture.owned_id)
                .unwrap()
                .state,
            AgentRuntimeState::Failed
        );

        fixture
            .manager
            .close(&fixture.owned_id, fixture.generation)
            .await
            .unwrap();
        fs::remove_dir_all(fixture.root).unwrap();
    }

    #[tokio::test(flavor = "current_thread")]
    async fn failed_prompt_racing_transport_exit_expires_pending_approval_once() {
        let fixture = fixture_manager_with_acp_session("permission_prompt_error").await;
        let seen: Arc<Mutex<Vec<AgentConversationEvent>>> = Default::default();
        let sink = Arc::clone(&seen);
        fixture
            .manager
            .set_emitter(Arc::new(move |event| sink.lock().unwrap().push(event)));
        fixture
            .manager
            .prompt(&fixture.owned_id, fixture.generation, test_prompt("hello"))
            .await
            .expect("prompt starts");
        wait_until(|| {
            let seen = seen.lock().unwrap();
            seen.iter().any(|event| {
                matches!(
                    event.payload,
                    AgentConversationPayload::Approval {
                        state: ApprovalState::Expired,
                        ..
                    }
                )
            }) && seen.iter().any(|event| {
                matches!(
                    event.payload,
                    AgentConversationPayload::Turn {
                        state: super::super::protocol::TurnState::Failed,
                        ..
                    }
                )
            })
        })
        .await;
        let seen = seen.lock().unwrap();
        assert_eq!(
            seen.iter()
                .filter(|event| {
                    matches!(
                        event.payload,
                        AgentConversationPayload::Approval {
                            state: ApprovalState::Expired,
                            ..
                        }
                    )
                })
                .count(),
            1,
            "the failed prompt must expire its approval exactly once"
        );
        assert_eq!(
            seen.iter()
                .filter(|event| {
                    matches!(
                        event.payload,
                        AgentConversationPayload::Turn {
                            state: super::super::protocol::TurnState::Failed,
                            ..
                        }
                    )
                })
                .count(),
            1,
            "the failed prompt must emit one terminal turn"
        );
        assert!(seen.iter().any(|event| {
            matches!(
                event.payload,
                AgentConversationPayload::Approval {
                    state: ApprovalState::Requested,
                    ..
                }
            )
        }));
        assert!(seen.iter().any(|event| {
            matches!(
                event.payload,
                AgentConversationPayload::Turn {
                    state: super::super::protocol::TurnState::Failed,
                    ..
                }
            )
        }));
        assert!(
            seen.windows(2)
                .all(|events| events[1].sequence == events[0].sequence + 1),
            "approval/turn race must preserve contiguous event sequences"
        );
        drop(seen);

        let _ = fixture
            .manager
            .close(&fixture.owned_id, fixture.generation)
            .await;
        fs::remove_dir_all(fixture.root).unwrap();
    }

    #[test]
    fn ensure_is_idempotent_and_generation_rejects_stale_writers() {
        let root = temp_root();
        let manager = AgentRuntimeManager::default();
        let first = manager
            .ensure_inner(request(
                root.to_str().unwrap(),
                "owned-a",
                AgentConversationProvider::Codex,
            ))
            .unwrap()
            .0;
        assert_eq!(
            manager
                .ensure_inner(request(
                    root.to_str().unwrap(),
                    "owned-a",
                    AgentConversationProvider::Codex
                ))
                .unwrap()
                .0,
            first
        );
        let changed = manager
            .ensure_inner(request(
                root.to_str().unwrap(),
                "owned-a",
                AgentConversationProvider::Claude,
            ))
            .unwrap()
            .0;
        assert_eq!(changed.generation, 2);
        let mut sessions = manager.sessions.lock().unwrap();
        assert!(current_session_mut(&mut sessions, "owned-a", first.generation).is_err());
        drop(sessions);
        fs::remove_dir_all(root).unwrap();
    }

    #[tokio::test(flavor = "current_thread")]
    async fn ensure_and_resource_listing_recover_after_sessions_mutex_is_poisoned() {
        let root = temp_root();
        let manager = AgentRuntimeManager::default();
        let poisoned_sessions = Arc::clone(&manager.sessions);

        let panic = std::thread::spawn(move || {
            let _sessions = poisoned_sessions.lock().unwrap();
            panic!("deliberately poison the agent sessions mutex");
        })
        .join();
        assert!(panic.is_err());
        assert!(manager.sessions.is_poisoned());

        let connection = manager
            .ensure_async(request(
                root.to_str().unwrap(),
                "owned-after-poison",
                AgentConversationProvider::Codex,
            ))
            .await
            .expect("ensure must recover the poisoned sessions map");
        let snapshot = manager
            .snapshot("owned-after-poison")
            .expect("snapshot listing must recover the poisoned sessions map")
            .expect("ensured session must be listed");

        assert_eq!(snapshot.connection.owned_id, connection.owned_id);
        assert_eq!(snapshot.connection.generation, connection.generation);
        assert_eq!(snapshot.connection.provider, connection.provider);
        assert_eq!(
            snapshot.connection.state,
            ConversationConnectionState::Disconnected
        );
        assert!(manager.resource_roots().is_empty());
        fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn sparse_completed_tool_dispatch_retains_committed_file_fields() {
        let root = temp_root();
        let manager = AgentRuntimeManager::default();
        let connection = manager.ensure_inner(request(
            root.to_str().unwrap(), "owned-sparse-file", AgentConversationProvider::Codex,
        )).unwrap().0;
        let seen: Arc<Mutex<Vec<AgentConversationEvent>>> = Default::default();
        let sink = Arc::clone(&seen);
        manager.set_emitter(Arc::new(move |event| sink.lock().unwrap().push(event)));
        let mut sessions = manager.sessions.lock().unwrap();
        let session = current_session_mut(&mut sessions, "owned-sparse-file", connection.generation).unwrap();
        for (state, path, diff) in [
            (ToolState::Started, Some("/workspace/new.txt".into()), Some("@@ -0,0 +1 @@\n+created\n".into())),
            (ToolState::Completed, None, None),
        ] {
            record_payload_for_session_and_dispatch(session, &manager.emitter, AgentConversationPayload::Tool {
                item_id: "edit-1".into(), name: "Edit".into(), state,
                summary: None, output: None, path, diff, has_detail: false, added: None, removed: None,
            }).unwrap();
        }
        let emitted = seen.lock().unwrap();
        let completion = emitted.last().unwrap();
        assert_eq!(completion.sequence + 1, session.next_sequence);
        assert!(!session.live_tool_calls.contains_key("edit-1"));
        match &completion.payload {
            AgentConversationPayload::Tool { state, path, diff, .. } => {
                assert_eq!(*state, ToolState::Completed);
                assert_eq!(path.as_deref(), Some("/workspace/new.txt"));
                assert_eq!(diff.as_deref(), Some("@@ -0,0 +1 @@\n+created\n"));
            }
            _ => panic!("expected completed tool"),
        }
        let persisted = session.store.list_events(&session.owned_id, completion.sequence, 1).unwrap();
        let persisted = stored_event(persisted.into_iter().next().unwrap()).unwrap();
        assert_eq!(serde_json::to_value(completion).unwrap(), serde_json::to_value(persisted).unwrap());
        drop(emitted);
        drop(sessions);
        fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn emitter_panic_is_contained_before_it_can_poison_sessions() {
        let root = temp_root();
        let manager = AgentRuntimeManager::default();
        let connection = manager
            .ensure_inner(request(
                root.to_str().unwrap(),
                "owned-emitter-panic",
                AgentConversationProvider::Codex,
            ))
            .unwrap()
            .0;
        manager.set_emitter(Arc::new(|_| panic!("fixture emitter panic")));

        {
            let mut sessions = manager
                .sessions
                .lock()
                .unwrap_or_else(std::sync::PoisonError::into_inner);
            let session =
                current_session_mut(&mut sessions, "owned-emitter-panic", connection.generation)
                    .unwrap();
            record_payload_for_session_and_dispatch(
                session,
                &manager.emitter,
                AgentConversationPayload::Error {
                    code: "fixture".into(),
                    message: "retained after emitter panic".into(),
                    recoverable: true,
                },
            )
            .unwrap();
        }

        assert!(!manager.sessions.is_poisoned());
        assert_eq!(
            manager
                .snapshot("owned-emitter-panic")
                .unwrap()
                .unwrap()
                .events
                .len(),
            1
        );
        fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn failed_event_write_leaves_memory_and_dispatch_unchanged() {
        let root = temp_root();
        let manager = AgentRuntimeManager::default();
        let connection = manager
            .ensure_inner(request(
                root.to_str().unwrap(),
                "owned-failed-event",
                AgentConversationProvider::Codex,
            ))
            .unwrap()
            .0;
        let seen: Arc<Mutex<Vec<AgentConversationEvent>>> = Default::default();
        let sink = Arc::clone(&seen);
        manager.set_emitter(Arc::new(move |event| sink.lock().unwrap().push(event)));

        let mut sessions = manager.sessions.lock().unwrap();
        let session =
            current_session_mut(&mut sessions, "owned-failed-event", connection.generation)
                .unwrap();
        let sequence = session.next_sequence;
        let last_activity_ms = session.last_activity_ms;
        session
            .store
            .append_event(&EventRow {
                owned_id: session.owned_id.clone(),
                seq: i64::try_from(sequence).unwrap(),
                turn_id: None,
                kind: "error".into(),
                payload_json: "{}".into(),
                created_at_ms: store_timestamp(last_activity_ms),
            })
            .unwrap();

        let result = record_payload_for_session_and_dispatch(
            session,
            &manager.emitter,
            AgentConversationPayload::Error {
                code: "duplicate-sequence".into(),
                message: "fixture".into(),
                recoverable: true,
            },
        );

        assert!(result.is_err());
        assert_eq!(session.next_sequence, sequence);
        assert_eq!(session.last_activity_ms, last_activity_ms);
        assert!(seen.lock().unwrap().is_empty());
        drop(sessions);
        fs::remove_dir_all(root).unwrap();
    }

    /// A build that could not hold a negative sequence wrote zero into the
    /// payload of every event it imported backwards. Those rows are in the
    /// database now, and every one of them claims to sit at position zero. The
    /// column knows better, and reading has to believe it — otherwise the
    /// transcript reads 112 events as one, and draws nothing.
    /// A tool row is a line that opens onto what the call produced. It used to
    /// open onto nothing: `content` and `locations` were joined into one string
    /// kept as the summary, and the output, the file and the change were gone
    /// before anything was written down.
    #[test]
    fn a_finished_tool_call_carries_its_output_and_the_file_it_touched() {
        let update = json!({ "update": {
            "sessionUpdate": "tool_call_update",
            "toolCallId": "t9",
            "title": "Run tests",
            "status": "completed",
            "content": [
                { "type": "text", "text": "running 3 tests\nall passed" }
            ],
            "locations": [{ "path": "core/src/lib.rs" }]
        } });
        match payload_from_session_update_for_turn(&update, None) {
            Some(AgentConversationPayload::Tool {
                summary,
                output,
                path,
                diff,
                ..
            }) => {
                assert_eq!(summary, None, "output must not become input detail");
                assert_eq!(output.as_deref(), Some("running 3 tests\nall passed"));
                assert_eq!(path.as_deref(), Some("core/src/lib.rs"));
                assert_eq!(diff, None);
            }
            other => panic!("expected Tool, got {other:?}"),
        }
    }

    #[test]
    fn a_codex_command_update_carries_its_formatted_output() {
        let update = json!({ "update": {
            "sessionUpdate": "tool_call_update",
            "toolCallId": "exec-1",
            "status": "completed",
            "rawOutput": {
                "formatted_output": "ASSEMBLY_TOOL_OUTPUT_MARKER",
                "exit_code": 0
            }
        } });
        match payload_from_session_update_for_turn(&update, None) {
            Some(AgentConversationPayload::Tool { output, .. }) => {
                assert_eq!(output.as_deref(), Some("ASSEMBLY_TOOL_OUTPUT_MARKER"));
            }
            other => panic!("expected Tool, got {other:?}"),
        }
    }

    #[test]
    fn tool_input_identity_and_output_stay_separate() {
        let command = "printf 'first\\n'\nprintf 'second\\n'";
        let start = json!({ "update": {
            "sessionUpdate": "tool_call", "toolCallId": "bash-1",
            "title": command, "kind": "execute",
            "_meta": { "claudeCode": { "toolName": "Bash" } },
            "rawInput": { "command": command, "description": "Print two lines" }
        }});
        match payload_from_session_update_for_turn(&start, None).unwrap() {
            AgentConversationPayload::Tool { name, summary, output, .. } => {
                assert_eq!(name, "Bash");
                assert_eq!(summary.as_deref(), Some(command));
                assert_eq!(output, None);
            }
            other => panic!("expected Tool, got {other:?}"),
        }
        let completion = json!({ "update": {
            "sessionUpdate": "tool_call_update", "toolCallId": "bash-1",
            "status": "completed",
            "title": "{\"results\":[{\"id\":\"result-1\"}]}",
            "content": [{ "type": "text", "text": "```console\nfirst `literal`\nsecond\n```" }]
        }});
        match payload_from_session_update_for_turn(&completion, None).unwrap() {
            AgentConversationPayload::Tool { name, summary, output, .. } => {
                assert!(name.is_empty(), "an output-only update retains the start identity");
                assert_eq!(summary, None);
                assert_eq!(output.as_deref(), Some("first `literal`\nsecond"));
            }
            other => panic!("expected Tool, got {other:?}"),
        }
        let mcp = tool_details(&json!({
            "title": "Fetch", "_meta": { "claudeCode": { "toolName": "mcp__notion__fetch" } },
            "rawInput": { "id": "3eb394b0689d812fa681d4495db06438" },
            "content": [{ "type": "text", "text": "{\"results\":[]}" }]
        }));
        assert_eq!(mcp.name, "mcp__notion__fetch");
        assert_eq!(mcp.summary.as_deref(), Some("3eb394b0689d812fa681d4495db06438"));
        assert_eq!(mcp.output.as_deref(), Some("{\"results\":[]}"));
        let completed = tool_details(&json!({
            "sessionUpdate": "tool_call_update", "status": "completed",
            "title": "{\"metadata\":{\"type\":\"page\"}}",
            "_meta": { "claudeCode": { "toolName": "mcp__notion__fetch" } }
        }));
        assert_eq!(completed.name, mcp.name);
        assert_eq!(completed.summary, None, "JSON result title must not replace the fetch input");
        assert_eq!(unwrap_console_block("```console\nliteral"), "```console\nliteral");
    }

    /// ACP hands over a file before and after rather than a patch, so the row
    /// had nothing a diff view could read.
    #[test]
    fn a_file_edit_becomes_a_diff_the_transcript_can_draw() {
        let update = json!({ "update": {
            "sessionUpdate": "tool_call_update",
            "toolCallId": "t10",
            "title": "Edit file",
            "status": "completed",
            "content": [{
                "type": "diff",
                "path": "src/main.rs",
                "oldText": "fn main() {\n    println!(\"one\");\n}",
                "newText": "fn main() {\n    println!(\"two\");\n}"
            }]
        } });
        match payload_from_session_update_for_turn(&update, None) {
            Some(AgentConversationPayload::Tool { path, diff, .. }) => {
                assert_eq!(path.as_deref(), Some("src/main.rs"));
                let diff = diff.expect("an edit carries its change");
                assert_eq!(
                    diff,
                    "@@ -1,3 +1,3 @@\n fn main() {\n-    println!(\"one\");\n+    println!(\"two\");\n }\n\\ No newline at end of file\n"
                );
            }
            other => panic!("expected Tool, got {other:?}"),
        }
    }

    #[test]
    fn live_tool_paths_include_raw_input_reads_and_every_edited_file() {
        for (field, target) in [("file_path", "src/read.rs"), ("path", "src/other.rs")] {
            let update = json!({ "update": {
                "sessionUpdate": "tool_call_update", "toolCallId": "read-1",
                "kind": "read", "rawInput": { field: target }
            } });
            match payload_from_session_update_for_turn(&update, None) {
                Some(AgentConversationPayload::Tool { path, .. }) => {
                    assert_eq!(path.as_deref(), Some(target));
                }
                other => panic!("expected Tool, got {other:?}"),
            }
        }

        let update = json!({ "update": {
            "sessionUpdate": "tool_call_update", "toolCallId": "edit-1",
            "kind": "edit", "content": [
                { "type": "diff", "path": "src/first.rs", "oldText": "old\n", "newText": "new\n" },
                { "type": "diff", "path": "src/second.rs", "oldText": "old\n", "newText": "new\n" }
            ]
        } });
        match payload_from_session_update_for_turn(&update, None) {
            Some(AgentConversationPayload::Tool { path, .. }) => {
                assert_eq!(path.as_deref(), Some("src/first.rs\nsrc/second.rs"));
            }
            other => panic!("expected Tool, got {other:?}"),
        }
    }

    #[test]
    fn a_file_with_no_change_produces_no_diff() {
        assert_eq!(unified_diff("same\nlines", "same\nlines"), "");
    }

    #[test]
    fn unified_diff_splits_distant_edits_into_small_hunks() {
        let old_lines: Vec<String> = (1..=1_000).map(|line| format!("line {line}")).collect();
        let mut new_lines = old_lines.clone();
        new_lines[9] = "changed line 10".to_owned();
        new_lines[899] = "changed line 900".to_owned();

        let patch = unified_diff(&old_lines.join("\n"), &new_lines.join("\n"));
        let line_count = patch.lines().count();

        assert!(
            line_count < 40,
            "expected a patch under 40 lines, got {line_count}"
        );
    }

    #[test]
    fn desktop_markdown_stored_event_keeps_missing_blocks() {
        let event = serde_json::json!({
            "ownedId": "owned-a", "provider": "codex", "generation": 1,
            "sequence": 1, "timestampMs": 1000,
            "payload": { "kind": "assistantMessage", "itemId": "a",
                "text": "**hello**", "completed": true }
        });
        let decoded = stored_event(EventRow {
            owned_id: "owned-a".into(), seq: 1, turn_id: None,
            kind: "item.completed".into(), payload_json: event.to_string(), created_at_ms: 1000,
        }).unwrap();
        assert!(matches!(decoded.payload,
            AgentConversationPayload::AssistantMessage { blocks: None, .. }));
    }

    #[test]
    fn desktop_markdown_remote_server_records_and_emits_without_blocks() {
        let server = std::env::args().any(|arg| arg == "--assembly-server");
        if !server {
            // libtest accepts the existing server argument as a skip filter.
            // The child then exercises the real process distinction, without global test state.
            let output = std::process::Command::new(std::env::current_exe().unwrap())
                .args(["--exact",
                    "agent_conversation::manager::tests::desktop_markdown_remote_server_records_and_emits_without_blocks",
                    "--skip", "--assembly-server", "--nocapture"])
                .output().unwrap();
            assert!(output.status.success(), "{}\n{}",
                String::from_utf8_lossy(&output.stdout), String::from_utf8_lossy(&output.stderr));
        }
        let root = temp_root();
        let manager = AgentRuntimeManager::default();
        let connection = manager.ensure_inner(request(root.to_str().unwrap(), "server-markdown",
            AgentConversationProvider::Codex)).unwrap().0;
        let emitted = Arc::new(Mutex::new(Vec::new()));
        let captured = emitted.clone();
        manager.set_emitter(Arc::new(move |event| captured.lock().unwrap().push(event)));
        {
            let mut sessions = manager.sessions.lock().unwrap();
            let session = current_session_mut(&mut sessions, "server-markdown", connection.generation).unwrap();
            record_payload_for_session_and_dispatch(session, &manager.emitter,
                AgentConversationPayload::AssistantMessage {
                    item_id: "a".into(), text: "**hello**".into(), completed: true, blocks: None,
                }).unwrap();
        }
        let saved = manager.store.list_events("server-markdown", 0, 100).unwrap();
        let saved: AgentConversationEvent = serde_json::from_str(&saved.last().unwrap().payload_json).unwrap();
        assert_eq!(emitted.lock().unwrap().last(), Some(&saved));
        assert!(matches!(saved.payload,
            AgentConversationPayload::AssistantMessage { blocks, .. } if blocks.is_some() == !server));
        fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn a_stored_event_takes_its_position_from_the_column_not_the_payload() {
        let event = serde_json::json!({
            "ownedId": "owned-a",
            "provider": "codex",
            "generation": 0,
            "sequence": 0,
            "timestampMs": 1_000,
            "payload": { "kind": "assistantMessage", "itemId": "a", "text": "hi", "completed": true }
        });
        let decoded = stored_event(EventRow {
            owned_id: "owned-a".into(),
            seq: -42,
            turn_id: None,
            kind: "item.completed".into(),
            payload_json: event.to_string(),
            created_at_ms: 1_000,
        })
        .expect("a stored row decodes");
        assert_eq!(decoded.sequence, -42);
    }

    #[test]
    fn journal_events_carry_the_turn_they_are_filed_under() {
        let root = temp_root();
        let manager = AgentRuntimeManager::default();
        let connection = manager
            .ensure_inner(request(
                root.to_str().unwrap(),
                "owned-turn",
                AgentConversationProvider::Claude,
            ))
            .unwrap()
            .0;
        let live = {
            let mut sessions = manager.sessions.lock().unwrap();
            let session =
                current_session_mut(&mut sessions, "owned-turn", connection.generation).unwrap();
            session.active_turn_id = Some("turn-live".into());
            record_payload_for_session_and_dispatch(
                session,
                &manager.emitter,
                AgentConversationPayload::AssistantDelta {
                    item_id: "msg-1".into(),
                    delta: "Hi".into(),
                },
            )
            .unwrap()
        };
        assert_eq!(live.turn_id.as_deref(), Some("turn-live"));
        let stored = manager.list_events("owned-turn", 0).unwrap();
        assert_eq!(stored.last().unwrap().turn_id.as_deref(), Some("turn-live"));

        // A row written before the event carried its turn gets it from the column.
        let older = serde_json::json!({
            "ownedId": "owned-turn",
            "provider": "claude",
            "generation": 1,
            "sequence": 7,
            "timestampMs": 1_000,
            "payload": { "kind": "assistantDelta", "itemId": "msg-0", "delta": "old" }
        });
        let decoded = stored_event(EventRow {
            owned_id: "owned-turn".into(),
            seq: 7,
            turn_id: Some("turn-old".into()),
            kind: "content.delta".into(),
            payload_json: older.to_string(),
            created_at_ms: 1_000,
        })
        .expect("a stored row decodes");
        assert_eq!(decoded.turn_id.as_deref(), Some("turn-old"));
        fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn canonical_sequence_and_bounded_snapshot_tail_are_repairable() {
        let root = temp_root();
        let manager = AgentRuntimeManager::default();
        let connection = manager
            .ensure_inner(request(
                root.to_str().unwrap(),
                "owned-a",
                AgentConversationProvider::Codex,
            ))
            .unwrap()
            .0;
        const EVENTS_WRITTEN: usize = 1_003;
        for index in 0..EVENTS_WRITTEN {
            let mut sessions = manager.sessions.lock().unwrap();
            let session =
                current_session_mut(&mut sessions, "owned-a", connection.generation).unwrap();
            record_payload_for_session_and_dispatch(
                session,
                &manager.emitter,
                AgentConversationPayload::Error {
                    code: "fixture".into(),
                    message: index.to_string(),
                    recoverable: true,
                },
            )
            .unwrap();
        }
        let stored = manager.list_events("owned-a", 0).unwrap();
        assert_eq!(stored.len(), EVENTS_WRITTEN);
        let snapshot = manager.snapshot("owned-a").unwrap().unwrap().events;
        // Every event here is small, so they all fit the window: what matters
        // is that the tail is contiguous and ends where the journal does.
        assert_eq!(
            snapshot.last().unwrap().sequence,
            stored.last().unwrap().sequence
        );
        assert!(snapshot
            .windows(2)
            .all(|pair| pair[1].sequence == pair[0].sequence + 1));
        fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn projected_terminal_payload_continues_the_journal_sequence() {
        let root = temp_root();
        let manager = AgentRuntimeManager::default();
        let connection = manager
            .ensure_inner(request(
                root.to_str().unwrap(),
                "owned-projection",
                AgentConversationProvider::Codex,
            ))
            .unwrap()
            .0;
        {
            let mut sessions = manager.sessions.lock().unwrap();
            let session =
                current_session_mut(&mut sessions, "owned-projection", connection.generation)
                    .unwrap();
            record_payload_for_session_and_dispatch(
                session,
                &manager.emitter,
                AgentConversationPayload::Error {
                    code: "fixture".into(),
                    message: "first journal event".into(),
                    recoverable: true,
                },
            )
            .unwrap();
        }

        manager
            .submit_terminal_projection(
                "owned-projection",
                super::super::protocol::TerminalProjectionPayload {
                    event_type: AgentEventType::ItemCompleted,
                    provider_instance_id: "terminal-transcript:native-projection".into(),
                    timestamp_ms: Some(42),
                    native_session_id: "native-projection".into(),
                    item_id: Some("projected-item".into()),
                    payload: BTreeMap::from([(
                        "text".into(),
                        Value::String("projected answer".into()),
                    )]),
                    provider_metadata: BTreeMap::from([(
                        "source".into(),
                        Value::String("terminal-transcript".into()),
                    )]),
                    raw_frame_reference: super::super::protocol::AgentRawFrameReference {
                        id: "frame-projection".into(),
                        redacted: true,
                    },
                },
            )
            .unwrap();

        let stored = manager.list_events("owned-projection", 0).unwrap();
        assert_eq!(stored.len(), 2);
        assert_eq!(stored[1].generation, connection.generation);
        assert_eq!(stored[1].sequence, 2);
        assert!(matches!(
            stored[1].payload,
            AgentConversationPayload::TerminalProjection(_)
        ));
        let snapshot = manager.snapshot("owned-projection").unwrap().unwrap();
        assert_eq!(snapshot.last_sequence, 2);
        assert_eq!(snapshot.events, stored);
        fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn structured_session_does_not_claim_a_user_pty() {
        let root = temp_root();
        let manager = AgentRuntimeManager::default();
        manager
            .ensure_inner(request(
                root.to_str().unwrap(),
                "owned-a",
                AgentConversationProvider::Codex,
            ))
            .unwrap();
        assert_eq!(
            manager.session_terminal_ownership("owned-a"),
            Some(AgentWriterLeaseOwner::None)
        );
        let terminal_registry = crate::terminal::TerminalRegistry::default();
        assert!(crate::terminal::list_terminal_sessions(&terminal_registry)
            .unwrap()
            .is_empty());
        fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn distinct_tool_terminal_identity_is_not_a_user_pty_identity() {
        let first = crate::terminal::ToolTerminalIdentity {
            owned_id: "owned-a".into(),
            turn_id: "turn-a".into(),
            tool_call_id: "tool-a".into(),
            terminal_id: "terminal-a".into(),
        };
        let second = crate::terminal::ToolTerminalIdentity {
            owned_id: "owned-a".into(),
            turn_id: "turn-a".into(),
            tool_call_id: "tool-b".into(),
            terminal_id: "terminal-b".into(),
        };
        assert_ne!(first, second);
        assert_ne!(
            crate::terminal::TerminalKind::AgentTool,
            crate::terminal::TerminalKind::UserPty
        );
    }
}
