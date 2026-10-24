use std::collections::BTreeMap;
use std::path::PathBuf;

use serde::{Deserialize, Serialize};
use serde_json::Value;

/// Carries one predictable error object across the conversation command boundary.
#[derive(Clone, Debug, Eq, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct CommandError {
    pub code: &'static str,
    pub message: String,
    pub recoverable: bool,
}

impl From<String> for CommandError {
    /// Preserves the backend message while giving every command rejection one stable shape.
    fn from(message: String) -> Self {
        Self {
            code: "agent-conversation-command-failed",
            message,
            recoverable: false,
        }
    }
}

/// Keeps conversation command signatures concise while enforcing the typed error boundary.
pub type CommandResult<T> = Result<T, CommandError>;

#[derive(Clone, Copy, Debug, Deserialize, Eq, PartialEq, Serialize)]
#[serde(rename_all = "kebab-case")]
pub enum AgentExecutionOwner {
    Structured,
    Terminal,
    TransitioningToStructured,
    TransitioningToTerminal,
    Stopped,
}

#[derive(Clone, Copy, Debug, Deserialize, Eq, PartialEq, Serialize)]
#[serde(rename_all = "kebab-case")]
pub enum AgentRuntimeState {
    Starting,
    Ready,
    Working,
    WaitingApproval,
    WaitingInput,
    Interrupting,
    Suspended,
    Failed,
    Closed,
}

/// A provider id from `providers/descriptors.json`; its methods and serde are in `providers/descriptors.rs`.
#[derive(Clone, Copy, Debug, Eq, Hash, PartialEq)]
pub struct AgentConversationProvider(pub(crate) &'static str);

#[derive(Clone, Copy, Debug, Default, Deserialize, Eq, PartialEq, Serialize)]
#[serde(rename_all = "lowercase")]
pub enum AgentNativeSessionMode {
    #[default]
    Resume,
    Load,
}

#[derive(Clone, Debug, Deserialize, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct AgentConfigOptionChoice {
    pub value: Value,
    pub label: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub description: Option<String>,
}

#[derive(Clone, Debug, Deserialize, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct AgentConfigOption {
    pub id: String,
    pub label: String,
    pub category: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub description: Option<String>,
    pub value: Value,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub choices: Option<Vec<AgentConfigOptionChoice>>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub provider_metadata: Option<BTreeMap<String, Value>>,
}

#[derive(Clone, Debug, Deserialize, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct AgentCommandDescriptor {
    pub id: String,
    pub label: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub description: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub input_hint: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub provider_metadata: Option<BTreeMap<String, Value>>,
}

#[derive(Clone, Copy, Debug, Deserialize, Eq, PartialEq, Serialize)]
#[serde(rename_all = "kebab-case")]
pub enum ProviderTransport {
    AcpStdio,
    AcpWebSocket,
    BuiltIn,
}

#[derive(Clone, Copy, Debug, Deserialize, Eq, PartialEq, Serialize)]
#[serde(rename_all = "kebab-case")]
pub enum ProviderSource {
    Bundled,
    Trusted,
    Configured,
}

#[derive(Clone, Debug, Deserialize, Eq, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct AgentProviderManifest {
    pub id: String,
    pub display_name: String,
    pub transport: ProviderTransport,
    pub executable: PathBuf,
    pub args: Vec<String>,
    pub version: String,
    pub content_hash: String,
    pub trusted_source: ProviderSource,
}

#[derive(Clone, Debug, Deserialize, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct AgentCapabilities {
    pub revision: u64,
    pub provider: AgentConversationProvider,
    pub implementation: AgentImplementation,
    pub session: AgentSessionCapabilities,
    pub prompt: AgentPromptCapabilities,
    pub interaction: AgentInteractionCapabilities,
    pub config_options: Vec<AgentConfigOption>,
    pub commands: Vec<AgentCommandDescriptor>,
}

#[derive(Clone, Debug, Deserialize, Eq, PartialEq, Serialize)]
pub struct AgentImplementation {
    pub name: String,
    pub version: String,
}

#[derive(Clone, Debug, Deserialize, Eq, PartialEq, Serialize)]
pub struct AgentSessionCapabilities {
    #[serde(default, skip_serializing_if = "is_false")]
    pub multi_session: bool,
    pub list: bool,
    pub load: bool,
    pub resume: bool,
    pub close: bool,
    pub steering: bool,
    #[serde(default, skip_serializing_if = "is_false")]
    pub fork: bool,
}

fn is_false(value: &bool) -> bool {
    !*value
}

#[derive(Clone, Debug, Deserialize, Eq, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct AgentPromptCapabilities {
    pub text: bool,
    pub image: bool,
    pub embedded_context: bool,
    pub resource_links: bool,
}

#[derive(Clone, Debug, Deserialize, Eq, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct AgentInteractionCapabilities {
    pub permissions: bool,
    pub structured_user_input: bool,
    pub tool_terminals: bool,
    pub plans: bool,
    pub tasks: bool,
    pub subagents: bool,
}

#[derive(Clone, Copy, Debug, Deserialize, Eq, PartialEq, Serialize)]
pub enum AgentEventType {
    #[serde(rename = "session.started")]
    SessionStarted,
    #[serde(rename = "session.config.updated")]
    SessionConfigUpdated,
    #[serde(rename = "session.state.changed")]
    SessionStateChanged,
    #[serde(rename = "session.closed")]
    SessionClosed,
    #[serde(rename = "turn.started")]
    TurnStarted,
    #[serde(rename = "turn.completed")]
    TurnCompleted,
    #[serde(rename = "turn.interrupted")]
    TurnInterrupted,
    #[serde(rename = "item.started")]
    ItemStarted,
    #[serde(rename = "item.updated")]
    ItemUpdated,
    #[serde(rename = "item.completed")]
    ItemCompleted,
    #[serde(rename = "content.delta")]
    ContentDelta,
    #[serde(rename = "approval.requested")]
    ApprovalRequested,
    #[serde(rename = "approval.resolved")]
    ApprovalResolved,
    #[serde(rename = "user-input.requested")]
    UserInputRequested,
    #[serde(rename = "user-input.resolved")]
    UserInputResolved,
    #[serde(rename = "plan.updated")]
    PlanUpdated,
    #[serde(rename = "tasks.updated")]
    TasksUpdated,
    #[serde(rename = "children.updated")]
    ChildrenUpdated,
    #[serde(rename = "commands.updated")]
    CommandsUpdated,
    #[serde(rename = "usage.updated")]
    UsageUpdated,
    #[serde(rename = "rate-limits.updated")]
    RateLimitsUpdated,
    #[serde(rename = "runtime.warning")]
    RuntimeWarning,
    #[serde(rename = "runtime.error")]
    RuntimeError,
}

#[derive(Clone, Debug, Deserialize, Eq, PartialEq, Serialize)]
pub struct AgentRawFrameReference {
    pub id: String,
    pub redacted: bool,
}

#[derive(Clone, Debug, Deserialize, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct TerminalProjectionPayload {
    pub event_type: AgentEventType,
    pub provider_instance_id: String,
    pub timestamp_ms: Option<u64>,
    pub native_session_id: String,
    pub item_id: Option<String>,
    pub payload: BTreeMap<String, Value>,
    pub provider_metadata: BTreeMap<String, Value>,
    pub raw_frame_reference: AgentRawFrameReference,
}

#[derive(Clone, Debug, Deserialize, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct AgentEvent {
    #[serde(rename = "type")]
    pub event_type: AgentEventType,
    pub owned_id: String,
    pub provider: AgentConversationProvider,
    pub provider_instance_id: String,
    pub generation: u64,
    /// Position in the session journal. Signed because importing older history
    /// writes it below what is already stored, counting down through zero.
    pub sequence: i64,
    pub timestamp_ms: u128,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub native_session_id: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub turn_id: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub item_id: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub request_id: Option<String>,
    pub payload: BTreeMap<String, Value>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub provider_metadata: Option<BTreeMap<String, Value>>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub raw_frame_reference: Option<AgentRawFrameReference>,
}

#[derive(Clone, Debug, Deserialize, Eq, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct AgentRequestIdentity {
    pub owned_id: String,
    pub generation: u64,
    pub request_id: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub turn_id: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub item_id: Option<String>,
}

#[derive(Clone, Copy, Debug, Deserialize, Eq, PartialEq, Serialize)]
#[serde(rename_all = "kebab-case")]
pub enum AgentApprovalDecision {
    Accept,
    Decline,
    Cancel,
}

#[derive(Clone, Debug, Deserialize, Eq, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct AgentApprovalResponse {
    #[serde(flatten)]
    pub identity: AgentRequestIdentity,
    pub decision: AgentApprovalDecision,
}

#[derive(Clone, Copy, Debug, Deserialize, Eq, PartialEq, Serialize)]
#[serde(rename_all = "kebab-case")]
pub enum AgentUserInputKind {
    Text,
    Password,
    Select,
    MultiSelect,
    Boolean,
}

#[derive(Clone, Debug, Deserialize, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct AgentUserInputField {
    pub id: String,
    pub label: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub description: Option<String>,
    pub required: bool,
    pub kind: AgentUserInputKind,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub choices: Option<Vec<AgentConfigOptionChoice>>,
}

#[derive(Clone, Debug, Deserialize, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct AgentUserInputResponse {
    #[serde(flatten)]
    pub identity: AgentRequestIdentity,
    pub action: AgentUserInputAction,
    pub content: BTreeMap<String, Value>,
}

#[derive(Clone, Copy, Debug, Deserialize, Eq, PartialEq, Serialize)]
#[serde(rename_all = "lowercase")]
pub enum AgentUserInputAction {
    Accept,
    Decline,
    Cancel,
}

#[derive(Clone, Copy, Debug, Deserialize, Eq, PartialEq, Serialize)]
#[serde(rename_all = "kebab-case")]
pub enum AgentWriterLeaseOwner {
    Structured,
    Terminal,
    None,
}

#[derive(Clone, Copy, Debug, Deserialize, Eq, PartialEq, Serialize)]
#[serde(rename_all = "kebab-case")]
pub enum AgentWriterLeaseTransitionState {
    Requested,
    Committed,
    Failed,
}

#[derive(Clone, Debug, Deserialize, Eq, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct AgentWriterLease {
    pub owned_id: String,
    pub generation: u64,
    pub owner: AgentWriterLeaseOwner,
}

#[derive(Clone, Debug, Deserialize, Eq, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct AgentWriterLeaseTransition {
    pub owned_id: String,
    pub generation: u64,
    pub from: AgentWriterLeaseOwner,
    pub to: AgentWriterLeaseOwner,
    pub state: AgentWriterLeaseTransitionState,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub error: Option<String>,
}

#[derive(Clone, Debug, Deserialize, Eq, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ToolTerminalIdentity {
    pub owned_id: String,
    pub turn_id: String,
    pub tool_call_id: String,
    pub terminal_id: String,
}

#[derive(Clone, Copy, Debug, Deserialize, Eq, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub enum ConversationConnectionState {
    Disconnected,
    Connecting,
    Connected,
    Reconnecting,
    Failed,
    Closed,
}

#[derive(Clone, Copy, Debug, Deserialize, Eq, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub enum ToolState {
    Started,
    Updated,
    Completed,
    Failed,
}

#[derive(Clone, Copy, Debug, Deserialize, Eq, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub enum ApprovalState {
    Requested,
    Accepted,
    Declined,
    Expired,
}

#[derive(Clone, Copy, Debug, Deserialize, Eq, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub enum TurnState {
    Started,
    Completed,
    Interrupted,
    Failed,
}

#[derive(Clone, Debug, Deserialize, Eq, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct PlanItem {
    pub text: String,
    pub status: String,
}

#[derive(Clone, Debug, Deserialize, PartialEq, Serialize)]
#[serde(
    tag = "kind",
    rename_all = "camelCase",
    rename_all_fields = "camelCase"
)]
pub enum AgentConversationPayload {
    Connection {
        state: ConversationConnectionState,
        #[serde(skip_serializing_if = "Option::is_none")]
        native_session_id: Option<String>,
    },
    UserMessage {
        item_id: String,
        text: String,
        completed: bool,
        /// Ids of the saved screenshots this message went out with. Journal
        /// records written before this field existed simply carry none.
        #[serde(default)]
        attachment_ids: Vec<String>,
    },
    AssistantDelta { item_id: String, delta: String },
    /// The backend marked this finished helper done with this stored ISO stamp.
    SessionCompleted { completed_at: String },

    AssistantMessage {
        item_id: String,
        text: String,
        completed: bool,
        #[serde(default, skip_serializing_if = "Option::is_none")]
        blocks: Option<Vec<crate::agent_conversation::safe_markdown::SafeMarkdownBlock>>,
    },
    Tool {
        item_id: String,
        name: String,
        state: ToolState,
        /// The one line a collapsed row shows.
        #[serde(skip_serializing_if = "Option::is_none")] summary: Option<String>,
        /// What the call produced, kept whole. A row with nothing here has
        /// nothing to open, which is what a tool call used to be: its content
        /// was flattened into the summary and the rest thrown away.
        #[serde(default, skip_serializing_if = "Option::is_none")] output: Option<String>,
        /// The file the call was about.
        #[serde(default, skip_serializing_if = "Option::is_none")] path: Option<String>,
        /// A unified diff, when the call changed a file.
        #[serde(default, skip_serializing_if = "Option::is_none")] diff: Option<String>,
        /// On a history page's row, which has no output or diff: whether one is read by id on open, and the diff's line counts.
        #[serde(default, skip_serializing_if = "std::ops::Not::not")] has_detail: bool,
        #[serde(default, skip_serializing_if = "Option::is_none")] added: Option<u32>,
        #[serde(default, skip_serializing_if = "Option::is_none")] removed: Option<u32>,
    },
    ChildUpdate {
        child_id: String,
        parent_tool_call_id: String,
        #[serde(default, skip_serializing_if = "Option::is_none")]
        parent_id: Option<String>,
        /// Provider-durable transcript identity, distinct from resumed ACP generations.
        #[serde(default, skip_serializing_if = "Option::is_none")]
        transcript_id: Option<String>,
        #[serde(skip_serializing_if = "Option::is_none")]
        label: Option<String>,
        state: String,
        #[serde(skip_serializing_if = "Option::is_none")]
        latest_activity: Option<String>,
    },
    Approval {
        request_id: String, state: ApprovalState, summary: String,
        /// `(option id, label, kind)` as the provider offered them on the request; none on a legacy approval or an answer.
        #[serde(default, skip_serializing_if = "Vec::is_empty")] options: Vec<(String, String, String)>,
    },
    UserInputRequested {
        request_id: String,
        title: String,
        #[serde(skip_serializing_if = "Option::is_none")]
        description: Option<String>,
        fields: Vec<AgentUserInputField>,
        #[serde(default)]
        can_decline: bool,
    },
    UserInputResolved {
        request_id: String,
        cancelled: bool,
    },
    Plan {
        items: Vec<PlanItem>,
    },
    Turn {
        turn_id: String,
        state: TurnState,
    },
    AvailableCommandsUpdate {
        available_commands: Vec<AgentCommandDescriptor>,
    },
    Usage {
        #[serde(skip_serializing_if = "Option::is_none")]
        input_tokens: Option<u64>,
        #[serde(skip_serializing_if = "Option::is_none")]
        output_tokens: Option<u64>,
        #[serde(skip_serializing_if = "Option::is_none")]
        used_tokens: Option<u64>,
        #[serde(skip_serializing_if = "Option::is_none")]
        context_window: Option<u64>,
        #[serde(skip_serializing_if = "Option::is_none")]
        total_tokens: Option<u64>,
    },
    /// The agent threw away the older part of the conversation to make room.
    /// The provider says so outright, or the reported occupancy falls far
    /// enough that nothing else explains it; either way the transcript says
    /// where it happened rather than leaving a silent gap.
    ContextCompaction {
        #[serde(skip_serializing_if = "Option::is_none")]
        trigger: Option<String>,
        #[serde(skip_serializing_if = "Option::is_none")]
        pre_tokens: Option<u64>,
        #[serde(skip_serializing_if = "Option::is_none")]
        post_tokens: Option<u64>,
    },
    /// Records the durable Codex checkout change separately from provider
    /// transcript content. The old root is retained so the timeline explains
    /// which projection was released and where the session now runs.
    CheckoutChanged {
        from_cwd: String,
        to_cwd: String,
    },
    TerminalProjection(TerminalProjectionPayload),
    Error {
        code: String,
        message: String,
        recoverable: bool,
    },
}

#[derive(Clone, Debug, Deserialize, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct AgentConversationEvent {
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub runtime_state: Option<AgentRuntimeState>,
    pub owned_id: String,
    pub provider: AgentConversationProvider,
    pub generation: u64,
    /// Position in the session journal. Signed because importing older history
    /// writes it below what is already stored, counting down through zero.
    pub sequence: i64,
    pub timestamp_ms: u128,
    /// The turn the journal files this event under, so a row can say which
    /// turn it belongs to when its payload does not.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub turn_id: Option<String>,
    pub payload: AgentConversationPayload,
}

#[derive(Clone, Debug, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct EnsureAgentConversationRequest {
    pub owned_id: String,
    #[serde(default)]
    pub execution_environment: ExecutionEnvironment,
    #[serde(default)]
    pub remote_profile_id: Option<String>,
    pub provider: AgentConversationProvider,
    pub cwd: String,
    pub native_session_id: Option<String>,
    #[serde(default)]
    pub native_session_mode: AgentNativeSessionMode,
    #[serde(default)]
    pub reasoning_effort: Option<String>,
    #[serde(default)]
    pub project_id: Option<String>,
}

/// The machine that owns a conversation's complete runtime and durable state.
#[derive(Clone, Copy, Debug, Default, Deserialize, PartialEq, Eq, Serialize)]
#[serde(rename_all = "kebab-case")]
pub enum ExecutionEnvironment {
    #[default]
    Local,
    Remote,
}

#[derive(Clone, Debug, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct SendAgentConversationMessageRequest {
    pub owned_id: String,
    pub generation: u64,
    pub text: String,
    /// Ordered prompt blocks from the composer, including pasted images.
    #[serde(default)]
    pub content: Vec<super::prompt_content::AgentPromptContentBlock>,
    /// Ids of the saved screenshots this send is delivering, so the recorded
    /// user message can find them again after a restart.
    #[serde(default)]
    pub attachment_ids: Vec<String>,
    #[serde(default)]
    pub model: Option<String>,
    #[serde(default)]
    pub approval_policy: Option<String>,
}

/// Identifies the durable user item admitted by one normal send or steering request.
#[derive(Clone, Debug, Deserialize, Eq, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct AgentConversationSendReceipt {
    pub owned_id: String,
    pub generation: u64,
    pub turn_id: String,
    pub user_item_id: String,
    pub admitted_sequence: i64,
}

#[derive(Clone, Copy, Debug, Deserialize, Eq, PartialEq, Serialize)]
#[serde(rename_all = "lowercase")]
pub enum ApprovalDecision {
    Accept,
    Decline,
}

#[derive(Clone, Debug, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct RespondAgentConversationApprovalRequest {
    pub owned_id: String,
    pub generation: u64,
    pub request_id: String,
    pub decision: ApprovalDecision,
}

#[derive(Clone, Debug, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct RespondAgentConversationPermissionRequest {
    pub owned_id: String,
    pub generation: u64,
    pub request_id: String,
    pub option_id: String,
}

#[derive(Clone, Debug, Deserialize, Serialize)]
#[serde(deny_unknown_fields, rename_all = "camelCase")]
pub struct RespondAgentConversationInputRequest {
    pub owned_id: String,
    pub generation: u64,
    pub request_id: String,
    pub content: BTreeMap<String, Value>,
    pub action: AgentUserInputAction,
}

#[derive(Clone, Debug, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct StopAgentConversationTurnRequest {
    pub owned_id: String,
    pub generation: u64,
}

#[derive(Clone, Debug, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ChangeAgentConversationCheckoutRequest {
    pub owned_id: String,
    pub generation: u64,
    pub cwd: String,
}

#[derive(Clone, Debug, Default, Deserialize, Eq, PartialEq, Serialize)]
#[serde(default, rename_all = "camelCase")]
pub struct AgentConversationConfigState {
    pub model: Option<String>,
    pub available_models: Vec<String>,
    #[serde(skip_serializing_if = "std::collections::BTreeMap::is_empty")]
    pub model_labels: std::collections::BTreeMap<String, String>,
    pub reasoning_effort: Option<String>,
    pub available_efforts: Vec<String>,
    /// Each model's own efforts, for providers that publish them (Codex).
    #[serde(skip_serializing_if = "std::collections::BTreeMap::is_empty")]
    pub model_efforts: std::collections::BTreeMap<String, Vec<String>>,
    /// Each model's default effort, where the adapter recommends one.
    #[serde(skip_serializing_if = "std::collections::BTreeMap::is_empty")]
    pub model_default_efforts: std::collections::BTreeMap<String, String>,
    pub approval_policy: Option<String>,
    pub available_approval_policies: Vec<String>,
}

#[derive(Clone, Debug, Deserialize, Eq, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct SetAgentConversationConfigRequest {
    pub owned_id: String,
    pub generation: u64,
    pub model: Option<String>,
    pub reasoning_effort: Option<String>,
    pub approval_policy: Option<String>,
}

#[derive(Clone, Debug, Deserialize, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct AgentConversationConnection {
    pub owned_id: String,
    pub provider: AgentConversationProvider,
    pub generation: u64,
    pub native_session_id: Option<String>,
    pub state: ConversationConnectionState,
    pub config: AgentConversationConfigState,
}

/// One backward page of transcript events, with whether older history remains.
#[derive(Clone, Debug, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct AgentConversationEventPage {
    pub events: Vec<AgentConversationEvent>,
    pub has_more: bool,
}

#[derive(Clone, Copy, Debug, Deserialize, Eq, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct AgentConversationEventCoverage {
    pub low: i64,
    pub high: i64,
    pub start_complete: bool,
}

#[derive(Clone, Debug, Deserialize, Eq, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct AgentConversationItemDescriptor {
    pub stable_id: String,
    pub item_id: String,
    pub selection_mode: String,
    pub first_sequence: i64,
    pub first_timestamp_ms: i64,
    pub last_sequence: i64,
    pub authority_seq: Option<i64>,
    pub turn_id: Option<String>,
    pub completed: bool,
    pub prefix_complete: bool,
    pub position_known: bool,
    pub required_bytes: u64,
}

#[derive(Clone, Debug, Deserialize, Eq, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct AgentConversationTurnFacts {
    pub turn_id: String,
    pub started_at_ms: Option<i64>,
    pub ended_at_ms: Option<i64>,
    pub terminal_state: Option<String>,
    pub final_assistant_item_id: Option<String>,
}

#[derive(Clone, Debug, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct AgentConversationItemPage {
    pub items: Vec<AgentConversationItemDescriptor>,
    pub events: Vec<AgentConversationEvent>,
    pub turns: Vec<AgentConversationTurnFacts>,
    pub before_cursor: Option<i64>,
    pub after_cursor: Option<i64>,
    pub has_before: bool,
    pub has_earlier_transcript: bool,
    pub has_after: bool,
    pub watermark: i64,
    pub transfer_bytes: u64,
    pub oversized: bool,
    pub coverage: Option<AgentConversationEventCoverage>,
}

#[derive(Clone, Debug, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct AgentConversationChildHistorySelection {
    pub history_owned_id: String,
    pub page: AgentConversationItemPage,
}

#[derive(Clone, Debug, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct AgentConversationSelectionSnapshot {
    pub connection: AgentConversationConnection,
    pub suspended: bool,
    pub page: AgentConversationItemPage,
    pub pending_events: Vec<AgentConversationEvent>,
    pub pending_sequence: i64,
    pub active_turn_id: Option<String>,
}

#[derive(Clone, Debug, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct AgentConversationSnapshot {
    pub connection: AgentConversationConnection,
    pub suspended: bool,
    pub last_sequence: i64,
    pub events: Vec<AgentConversationEvent>,
    pub has_earlier_transcript: bool,
    pub pending_events: Vec<AgentConversationEvent>,
    pub active_turn_id: Option<String>,
}

#[derive(Clone, Debug, Default, Deserialize, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct AgentConversationSessionMeta {
    pub worktree: Option<String>,
    pub branch: Option<String>,
    pub title: Option<String>,
    pub project: Option<String>,
    pub pty_session_id: Option<String>,
    pub origin: Option<String>,
    pub source: Option<String>,
    pub via_cmux: bool,
    pub resume_command: Option<String>,
    pub completed_at: Option<String>,
    pub settled_at: Option<String>,
    /// When the person pinned the session to the top of the rail; `None` when unpinned.
    pub pinned_at: Option<String>,
    pub task_id: Option<String>,
    pub pull_request: Option<String>,
    pub message_count: Option<u64>,
    pub latest_turn_preview: Option<String>,
    pub scanned_last_activity: Option<String>,
}

#[derive(Clone, Debug, Deserialize, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct UpdateAgentConversationSessionMetaRequest {
    pub owned_id: String,
    pub model: Option<String>,
    pub effort: Option<String>,
    pub meta: AgentConversationSessionMeta,
}

#[derive(Clone, Debug, Deserialize, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct BackgroundWorkItem {
    pub id: String,
    pub kind: BackgroundWorkKind,
    pub label: String,
    pub started_at_ms: i64,
}

#[derive(Clone, Copy, Debug, Deserialize, PartialEq, Serialize)]
#[serde(rename_all = "lowercase")]
pub enum BackgroundWorkKind {
    Subagent,
    Command,
}

#[derive(Clone, Debug, Deserialize, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct AgentConversationSessionRecord {
    pub owned_id: String,
    pub execution_environment: ExecutionEnvironment,
    #[serde(default)]
    pub remote_profile_id: Option<String>,
    pub provider: AgentConversationProvider,
    #[serde(default)]
    pub can_change_checkout: bool,
    pub model: Option<String>,
    pub effort: Option<String>,
    pub cwd: String,
    pub state: AgentRuntimeState,
    pub suspended: bool,
    pub created_at_ms: i64,
    pub last_activity_at_ms: i64,
    pub active_turn_id: Option<String>,
    pub pending_permission: bool,
    pub pending_input: bool,
    #[serde(default)]
    pub background_work: Vec<BackgroundWorkItem>,
    pub native_session_id: Option<String>,
    #[serde(default)]
    pub project_id: Option<String>,
    /// The session's permanent handle (`brisk-heron`); renaming never changes it.
    #[serde(default)]
    pub handle: Option<String>,
    /// The rail group the desktop computed in SQL from `project_id`, and its
    /// label. Empty, and left off the wire, wherever no desktop grouped the
    /// record (a remote server never does), so protocol 13 is unchanged.
    #[serde(default, skip_serializing_if = "String::is_empty")]
    pub project_group_key: String,
    #[serde(default, skip_serializing_if = "String::is_empty")] pub project_group_label: String,
    /// When the owner marked this session as the main orchestrator; absent otherwise.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub orchestrator_marked_at_ms: Option<i64>,
    /// The session that started this helper; absent for any other session.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub helper_of: Option<String>,
    /// The workflow run this worker belongs to, and its `<task key> · <workflow name>`.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub run_id: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")] pub run_label: Option<String>,
    /// The session that started this worker's run; absent when the owner started it.
    #[serde(default, skip_serializing_if = "Option::is_none")] pub run_lead: Option<String>,
    /// The head a session read again from its transcript numbered on from; absent until that happens. A desktop
    /// whose copy was saved under another value drops it, and a server too old to send it leaves copies alone.
    #[serde(default, skip_serializing_if = "Option::is_none")] pub numbered_after: Option<i64>,
    #[serde(flatten)]
    pub meta: AgentConversationSessionMeta,
}

/// A registered project, as the frontend sees it.
#[derive(Clone, Debug, Deserialize, Serialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct ProjectRecord {
    pub id: String,
    pub machine: String,
    pub root_path: String,
    pub title: String,
    pub repo_key: String,
    pub created_at_ms: i64,
    /// The rail group its sessions share, computed in SQL.
    pub group_key: String,
    pub pinned_at_ms: Option<i64>,
    /// The latest activity of its sessions on this Mac, computed in SQL.
    pub last_used_ms: Option<i64>,
    /// Set on the answer to `add_project` when the folder was already a project.
    #[serde(default, skip_serializing_if = "std::ops::Not::not")]
    pub already_existed: bool,
}

#[cfg(test)]
mod contract_tests;
