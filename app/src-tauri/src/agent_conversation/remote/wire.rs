use std::collections::BTreeMap;
use std::io::{Read, Write};

use base64::{engine::general_purpose::STANDARD as BASE64, Engine as _};
use futures_util::SinkExt;
use serde::{Deserialize, Serialize};
use tokio_tungstenite::tungstenite::Message as TungsteniteMessage;

use super::{MAX_SERVER_FRAME_BYTES, MAX_WIRE_FRAME_BYTES};
use crate::agent_conversation::attachments::{DeleteConversationAttachmentRequest, SavedConversationAttachment};
use crate::agent_conversation::protocol::{
    AgentCapabilities, AgentConfigOption, AgentConversationConfigState, AgentConversationConnection,
    AgentConversationEvent, AgentConversationEventPage, AgentConversationProvider,
    AgentConversationSendReceipt, AgentConversationSessionRecord, AgentConversationSnapshot,
    ChangeAgentConversationCheckoutRequest, EnsureAgentConversationRequest,
    RespondAgentConversationApprovalRequest, RespondAgentConversationInputRequest,
    RespondAgentConversationPermissionRequest, SendAgentConversationMessageRequest,
    SetAgentConversationConfigRequest, StopAgentConversationTurnRequest,
    UpdateAgentConversationSessionMetaRequest,
};
use crate::terminal::{TerminalOutputFrame, TerminalScrollbackSnapshot, TerminalSessionInfo, TerminalStartRequest};

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(tag = "type", rename_all = "camelCase")]
pub(super) enum ClientFrame {
    Authenticate { proof: String },
    Request { id: u64, command: RemoteCommand },
    Cancel { id: u64 },
    Resume { cursors: BTreeMap<String, i64> },
    /// The desktop's answer to a `RelayRequest`.
    RelayReply { request_id: u64, result: Result<serde_json::Value, String> },
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(tag = "command", content = "input", rename_all = "camelCase")]
pub(in crate::agent_conversation) enum RemoteCommand {
    Workspace { operation: String, args: serde_json::Value },
    ProbeProviderConfig { provider: AgentConversationProvider, cwd: String },
    ListSessions,
    CheckProviderUpdates,
    InstallProviderUpdates { provider: String },
    RestartForProviderUpdates,
    Ensure(EnsureAgentConversationRequest),
    Snapshot {
        owned_id: String,
        request_id: u64,
        after_sequence: Option<i64>,
    },
    ExtendImport { owned_id: String },
    /// Past provider sessions this machine's scanner finds, or one transcript's details when `log_path` is set.
    ScanTranscripts { log_path: Option<String>, project_roots: Vec<String> },
    /// Names a transcript on this machine as a session here, filed under the desktop's project, and reads its newest page.
    BeginImport { provider: AgentConversationProvider, native_session_id: String, transcript_path: String, cwd: String,
        title: Option<String>, project_id: Option<String> },
    SelectChildHistory {
        parent_owned_id: String,
        child_session_id: String,
        request_id: u64,
    },
    StopChildHistory {
        parent_owned_id: String,
        request_id: u64,
    },
    DownloadHistory { owned_id: String, before_sequence: Option<i64> },
    EventsAfter {
        owned_id: String,
        after_sequence: i64,
        max_bytes: u32,
    },
    Capabilities {
        owned_id: String,
    },
    Config {
        owned_id: String,
    },
    WarmConfig {
        owned_id: String,
        generation: u64,
    },
    SetConfig(SetAgentConversationConfigRequest),
    SetConfigOption(crate::agent_conversation::SetAgentConversationConfigOptionRequest),
    SetDraft {
        owned_id: String,
        text: String,
    },
    GetDraft {
        owned_id: String,
    },
    ClearDraft {
        owned_id: String,
    },
    WriteWorkspace {
        owned_id: String,
        snapshot_json: String,
    },
    ReadWorkspace {
        owned_id: String,
    },
    ReadExpandedPaths {
        owned_id: String,
        root: String,
    },
    WriteExpandedPaths {
        owned_id: String,
        root: String,
        paths: Vec<String>,
    },
    DeleteWorkspace {
        owned_id: String,
    },
    ChangeCheckout(ChangeAgentConversationCheckoutRequest),
    UpdateMeta(UpdateAgentConversationSessionMetaRequest),
    Close {
        owned_id: String,
        generation: u64,
    },
    Delete {
        owned_id: String,
    },
    AttachmentChunk { upload_id: String, owned_id: String, mime_type: String, first: bool, last: bool, bytes: String },
    AbortAttachmentUpload { upload_id: String },
    ReadAttachments { owned_id: String },
    ReadSelectedAttachments { owned_id: String, attachment_ids: Vec<String> },
    ReadAttachmentChunk { owned_id: String, attachment_id: String, thumbnail: bool, offset: u64 },
    DeleteAttachment(DeleteConversationAttachmentRequest),
    Send(SendAgentConversationMessageRequest),
    RespondApproval(RespondAgentConversationApprovalRequest),
    RespondPermission(RespondAgentConversationPermissionRequest),
    RespondInput(RespondAgentConversationInputRequest),
    Stop(StopAgentConversationTurnRequest),
    /// One `workflow::execute_operation` on this machine's workflow engine.
    Workflow { operation: String, args: serde_json::Value },
    /// One `messaging::answer` on this machine's sessions, for a message from another machine.
    Messaging { op: String, args: serde_json::Value },
    TerminalStart(TerminalStartRequest),
    TerminalWrite { session_id: String, data: String },
    TerminalResize { session_id: String, cols: Option<u16>, rows: Option<u16> },
    TerminalScrollback { session_id: String, max_bytes: Option<usize> },
    TerminalClose { session_id: String },
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(tag = "result", content = "value", rename_all = "camelCase")]
pub(in crate::agent_conversation) enum RemoteResponse {
    Workspace(serde_json::Value),
    Sessions(Vec<AgentConversationSessionRecord>),
    Transcripts { sessions: Vec<mcb_core::scanners::sessions::AgentSessionRecord>, common_dirs: BTreeMap<String, String> },
    ProviderUpdates(crate::agent_conversation::providers::updates::ProviderUpdateStatus),
    Restarting,
    Connection(AgentConversationConnection),
    Snapshot(#[serde(deserialize_with = "deserialize_wire_payload")] Option<AgentConversationSnapshot>),
    ChildHistoryId(String),
    EventPage(#[serde(deserialize_with = "deserialize_wire_payload")] AgentConversationEventPage),
    Capabilities(AgentCapabilities),
    Config(AgentConversationConfigState),
    ConfigOptions(Vec<AgentConfigOption>),
    Session(AgentConversationSessionRecord),
    Attachment(SavedConversationAttachment),
    Attachments(Vec<SavedConversationAttachment>),
    Bytes(String),
    OptionalString(Option<String>),
    Strings(Vec<String>),
    Bool(bool),
    ImportProgress { added: usize, reached_start: bool },
    /// The session a transcript was named as; `rebuilt` when its saved turns were cleared to be read again.
    Imported { owned_id: String, rebuilt: bool },
    SendReceipt(AgentConversationSendReceipt),
    Workflow(serde_json::Value),
    Messaging(serde_json::Value),
    Empty,
    TerminalSession(#[serde(deserialize_with = "deserialize_wire_payload")] TerminalSessionInfo),
    TerminalScrollback(Option<TerminalScrollbackSnapshot>),
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(tag = "type", rename_all = "camelCase")]
pub(super) enum ServerFrame {
    HistoryDownloadStart { id: u64, owned_id: String, low_sequence: Option<i64>, high_sequence: Option<i64>, row_count: u64, total_bytes: u64, covered_bytes: u64,
        #[serde(deserialize_with = "deserialize_wire_payload")] snapshot: AgentConversationSnapshot },
    HistoryDownloadChunk { id: u64, owned_id: String, oldest_sequence: i64, covered_bytes: u64, gzip_base64: String },
    HistoryDownloadDone { id: u64, owned_id: String },
    Ready { protocol_version: u16, proof: String, challenge: String },
    MessagesChanged,
    ReplayStart { owned_id: String },
    ReplayComplete { owned_id: String, head: i64, #[serde(deserialize_with = "deserialize_wire_payload")] snapshot: AgentConversationSnapshot },
    Response { id: u64, response: RemoteResponse },
    Error { id: u64, message: String },
    ProviderDownloadProgress { id: u64, progress: crate::agent_conversation::providers::updates::ProviderDownloadProgress },
    Event { #[serde(deserialize_with = "deserialize_wire_payload")] event: AgentConversationEvent },
    TerminalOutput { frame: TerminalOutputFrame },
    /// A session here messaging another machine; the desktop routes it and answers with `RelayReply`.
    RelayRequest { request_id: u64, op: String, args: serde_json::Value },
}

pub(super) fn encode_history_chunk(events: &[AgentConversationEvent]) -> Result<String, String> {
    let mut encoder = flate2::write::GzEncoder::new(Vec::new(), flate2::Compression::default());
    encoder.write_all(&serde_json::to_vec(events).map_err(|error| error.to_string())?).map_err(|error| error.to_string())?;
    Ok(BASE64.encode(encoder.finish().map_err(|error| error.to_string())?))
}

/// A chunk holds about 512 KiB of saved events plus one more row (`HISTORY_DOWNLOAD_CHUNK_BYTES`), and the
/// largest saved row on a busy machine is about 1 MiB, so 64 MiB fits every real chunk with room to spare.
/// Past it the backend is trying to run this Mac out of memory with a small, highly compressed frame.
const MAX_HISTORY_CHUNK_JSON_BYTES: u64 = 64 * 1024 * 1024;

pub(super) fn decode_history_chunk(gzip_base64: &str) -> Result<Vec<AgentConversationEvent>, String> {
    let bytes = BASE64.decode(gzip_base64).map_err(|error| error.to_string())?;
    let mut json = Vec::new();
    flate2::read::GzDecoder::new(bytes.as_slice()).take(MAX_HISTORY_CHUNK_JSON_BYTES + 1).read_to_end(&mut json).map_err(|error| error.to_string())?;
    if json.len() as u64 > MAX_HISTORY_CHUNK_JSON_BYTES { return Err("A history chunk from the remote machine is too large to unpack".into()); }
    serde_json::from_slice(&json).map_err(|error| error.to_string())
}

// Serde's buffered tagged-enum decoder cannot deserialize u128 timestamps.
// Decode these existing payloads through JSON's numeric decoder; wire shape stays unchanged.
fn deserialize_wire_payload<'de, D, T>(deserializer: D) -> Result<T, D::Error>
where D: serde::Deserializer<'de>, T: serde::de::DeserializeOwned {
    let value = serde_json::Value::deserialize(deserializer)?;
    serde_json::from_value(value).map_err(serde::de::Error::custom)
}

pub(super) async fn send_client_frame<S>(socket: &mut S, frame: &ClientFrame) -> Result<(), String>
where
    S: futures_util::Sink<TungsteniteMessage> + Unpin,
    S::Error: std::fmt::Display,
{
    let json = serde_json::to_string(frame).map_err(|error| error.to_string())?;
    if json.len() > MAX_WIRE_FRAME_BYTES {
        return Err("Remote Assembly frame exceeds one MiB".to_string());
    }
    socket
        .send(TungsteniteMessage::Text(json.into()))
        .await
        .map_err(|error| error.to_string())
}

pub(super) fn parse_server_frame(message: TungsteniteMessage) -> Result<Option<ServerFrame>, String> {
    let TungsteniteMessage::Text(text) = message else { return Ok(None); };
    serde_json::from_str(text.as_str()).map(Some).map_err(|error| {
        format!("Backend response is incompatible with this client: {error}")
    })
}

pub(super) fn encode_server_frame(frame: &ServerFrame) -> Result<String, String> {
    let json = serde_json::to_string(frame).map_err(|error| error.to_string())?;
    if json.len() <= MAX_SERVER_FRAME_BYTES {
        return Ok(json);
    }
    let message = format!("Remote response exceeds the {} MiB transport limit", MAX_SERVER_FRAME_BYTES / 1024 / 1024);
    match frame {
        ServerFrame::Response { id, .. } | ServerFrame::HistoryDownloadStart { id, .. } | ServerFrame::HistoryDownloadChunk { id, .. } => serde_json::to_string(&ServerFrame::Error {
            id: *id,
            message,
        }).map_err(|error| error.to_string()),
        _ => Err(message),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn a_history_chunk_that_unpacks_past_the_limit_is_refused() {
        // A few hundred KiB of gzip that would unpack to just over the limit.
        let mut encoder = flate2::write::GzEncoder::new(Vec::new(), flate2::Compression::best());
        let zeros = vec![b'0'; 1024 * 1024];
        for _ in 0..=MAX_HISTORY_CHUNK_JSON_BYTES / zeros.len() as u64 { encoder.write_all(&zeros).unwrap(); }
        let bomb = BASE64.encode(encoder.finish().unwrap());
        assert!(bomb.len() < 1024 * 1024);
        assert!(decode_history_chunk(&bomb).unwrap_err().contains("too large"));
        assert_eq!(decode_history_chunk(&encode_history_chunk(&[]).unwrap()).unwrap(), Vec::new());
    }
}

// Regression tests for connecting, reconnecting and catching up over these frames. They live
// beside the frames because remote/mod.rs is at its size ceiling.
#[cfg(test)]
#[path = "connection_pin_tests.rs"]
mod connection_pin_tests;

impl RemoteCommand {
    /// What the head server refuses in proof mode: whatever reaches outside the run or changes the mode.
    pub(in crate::agent_conversation) fn refuse_in_proof_mode(&self) -> Result<(), String> {
        use crate::proof_mode::refuse;
        match self {
            Self::Workspace { operation, .. } => crate::proof_mode::refuse_command(operation, &serde_json::Value::Null),
            Self::BeginImport { .. } | Self::ExtendImport { .. } => refuse("Importing transcripts"),
            Self::InstallProviderUpdates { .. } | Self::RestartForProviderUpdates => refuse("Updating providers"),
            Self::SetConfig(request) if request.approval_policy.is_some() => refuse("Changing the mode"),
            Self::SetConfigOption(request) if request.option_id == "mode" => refuse("Changing the mode"),
            Self::TerminalStart(_) => refuse("The terminal"),
            Self::Workflow { operation, .. } if matches!(operation.as_str(), "create" | "start") => refuse("Starting workflow runs"),
            _ => Ok(()),
        }
    }
}

#[cfg(test)]
mod proof_mode_tests {
    use super::RemoteCommand;

    fn command(value: serde_json::Value) -> RemoteCommand {
        serde_json::from_value(value).unwrap()
    }

    #[test]
    fn the_head_server_refuses_imports_updates_mode_changes_the_terminal_and_runs() {
        let refused = [
            serde_json::json!({"command":"beginImport","input":{"provider":"claude","native_session_id":"n","transcript_path":"/t","cwd":"/","title":null,"project_id":null}}),
            serde_json::json!({"command":"extendImport","input":{"owned_id":"o"}}),
            serde_json::json!({"command":"installProviderUpdates","input":{"provider":"codex"}}),
            serde_json::json!({"command":"restartForProviderUpdates"}),
            serde_json::json!({"command":"setConfig","input":{"ownedId":"o","generation":1,"model":null,"reasoningEffort":null,"approvalPolicy":"auto"}}),
            serde_json::json!({"command":"setConfigOption","input":{"ownedId":"o","generation":1,"optionId":"mode","value":"auto"}}),
            serde_json::json!({"command":"terminalStart","input":{"cwd":"/","shell":null,"cols":null,"rows":null,"ownedId":null,"command":null}}),
            serde_json::json!({"command":"workflow","input":{"operation":"start","args":{}}}),
            serde_json::json!({"command":"workspace","input":{"operation":"merge_github_pull_request","args":{}}}),
        ].map(command);
        let allowed = [
            serde_json::json!({"command":"listSessions"}),
            serde_json::json!({"command":"setConfigOption","input":{"ownedId":"o","generation":1,"optionId":"model","value":"m"}}),
            serde_json::json!({"command":"workflow","input":{"operation":"list","args":{}}}),
        ].map(command);
        for command in refused.iter().chain(&allowed) {
            assert!(command.refuse_in_proof_mode().is_ok(), "only in proof mode: {command:?}");
        }
        crate::proof_mode::turn_on_for_this_thread("planted\n\n");
        for command in &refused {
            assert!(command.refuse_in_proof_mode().unwrap_err().ends_with("is off in proof mode"), "{command:?}");
        }
        for command in &allowed {
            assert!(command.refuse_in_proof_mode().is_ok(), "{command:?}");
        }
    }
}
