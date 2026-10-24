//! Explicit ownership handoffs between the structured view and a user terminal.
//!
//! The state machine in this module is deliberately independent of Tauri and the
//! provider transport.  That keeps the identity, generation, history, and
//! process-tree rules testable before a native window is available.

use serde::{Deserialize, Serialize};

use super::manager::AgentRuntimeManager;
use super::protocol::{AgentWriterLeaseOwner, CommandResult};

#[derive(Clone, Copy, Debug, Deserialize, Eq, PartialEq, Serialize)]
#[serde(rename_all = "kebab-case")]
pub enum AgentConversationHandoffDirection {
    StructuredToTerminal,
    TerminalToStructured,
}

#[derive(Clone, Copy, Debug, Deserialize, Eq, PartialEq, Serialize)]
#[serde(rename_all = "kebab-case")]
pub enum AgentConversationHandoffMode {
    SameSession,
    Fork,
}

#[derive(Clone, Copy, Debug, Deserialize, Eq, PartialEq, Serialize)]
#[serde(rename_all = "kebab-case")]
pub enum AgentConversationHandoffPhase {
    Prepare,
    Commit,
    Rollback,
}

#[derive(Clone, Debug, Default, Deserialize, Eq, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct AgentConversationHistoryBoundary {
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub native_session_id: Option<String>,
    #[serde(default)]
    pub first_sequence: i64,
    #[serde(default)]
    pub last_sequence: i64,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub reconciled_sequence: Option<i64>,
}

#[derive(Clone, Debug, Default, Deserialize, Eq, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct AgentConversationProcessTreeAssertion {
    #[serde(default)]
    pub checked: bool,
    #[serde(default)]
    pub tui_live: bool,
    #[serde(default)]
    pub tui_released: bool,
    #[serde(default)]
    pub writer_count: u32,
    #[serde(default, rename = "ptyCount", alias = "userPtyCount")]
    pub user_pty_count: u32,
    #[serde(default)]
    pub sidecar_count: u32,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub pty_session_id: Option<String>,
}

#[derive(Clone, Debug, Deserialize, Eq, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct AgentConversationHandoffRequest {
    pub owned_id: String,
    pub generation: u64,
    pub direction: AgentConversationHandoffDirection,
    pub mode: AgentConversationHandoffMode,
    pub phase: AgentConversationHandoffPhase,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub expected_owner: Option<AgentWriterLeaseOwner>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub target_owned_id: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub native_session_id: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub pty_session_id: Option<String>,
    #[serde(default)]
    pub history_boundary: AgentConversationHistoryBoundary,
    #[serde(default)]
    pub process_tree: AgentConversationProcessTreeAssertion,
}

#[derive(Clone, Debug, Deserialize, Eq, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct AgentConversationHandoffReceipt {
    pub owned_id: String,
    pub generation: u64,
    pub direction: AgentConversationHandoffDirection,
    pub mode: AgentConversationHandoffMode,
    pub phase: AgentConversationHandoffPhase,
    pub previous_owner: AgentWriterLeaseOwner,
    pub owner: AgentWriterLeaseOwner,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub native_session_id: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub pty_session_id: Option<String>,
    pub history_boundary: AgentConversationHistoryBoundary,
    pub process_tree: AgentConversationProcessTreeAssertion,
    pub rollback_available: bool,
    pub message: String,
}

fn validate_terminal_assertion(
    assertion: &AgentConversationProcessTreeAssertion,
) -> Result<(), String> {
    if !assertion.checked || !assertion.tui_live {
        return Err("Commit requires proof that the native terminal UI is live".to_string());
    }
    if assertion.writer_count != 1 || assertion.user_pty_count != 1 || assertion.sidecar_count != 0
    {
        return Err(
            "Commit requires one writer, one user pty, and no duplicate sidecar".to_string(),
        );
    }
    if assertion.pty_session_id.is_none() {
        return Err("Commit requires the live user pty session id".to_string());
    }
    Ok(())
}

fn validate_tui_release(assertion: &AgentConversationProcessTreeAssertion) -> Result<(), String> {
    if !assertion.checked || !assertion.tui_released {
        return Err(
            "Resume requires proof that the native terminal UI released the writer".to_string(),
        );
    }
    if assertion.writer_count > 1 || assertion.user_pty_count > 1 || assertion.sidecar_count > 1 {
        return Err("Resume rejected a duplicate writer or process".to_string());
    }
    Ok(())
}

#[tauri::command]
/// Moves the writer lease through one validated handoff phase and returns typed failures.
pub async fn handoff_agent_conversation(
    manager: tauri::State<'_, AgentRuntimeManager>,
    terminal_registry: tauri::State<'_, crate::terminal::TerminalRegistry>,
    request: AgentConversationHandoffRequest,
) -> CommandResult<AgentConversationHandoffReceipt> {
    let _lifecycle = manager.lifecycle_guard(&request.owned_id).await?;
    match request.phase {
        AgentConversationHandoffPhase::Prepare => {
            manager.validate_handoff_identity(&request)?;
            let context = manager.handoff_prepare(&request)?;
            Ok(receipt_from_context(
                &request,
                context,
                true,
                "Handoff prepared",
            ))
        }
        AgentConversationHandoffPhase::Commit => {
            manager.validate_handoff_identity(&request)?;
            if request.direction == AgentConversationHandoffDirection::StructuredToTerminal {
                validate_terminal_assertion(&request.process_tree)?;
                let pty_id = request
                    .pty_session_id
                    .as_deref()
                    .or(request.process_tree.pty_session_id.as_deref())
                    .ok_or_else(|| "Commit requires a user pty session id".to_string())?;
                let session = crate::terminal::list_terminal_sessions(&terminal_registry)?
                    .into_iter()
                    .find(|session| session.session_id == pty_id)
                    .ok_or_else(|| "The requested user pty is not registered".to_string())?;
                if session.kind != crate::terminal::TerminalKind::UserPty || session.exited {
                    return Err("The requested user pty is not live".to_string().into());
                }
                if request.mode == AgentConversationHandoffMode::SameSession {
                    manager
                        .detach_structured_runtime(&request.owned_id, request.generation)
                        .await?;
                }
            } else {
                validate_tui_release(&request.process_tree)?;
                manager.validate_handoff_history(&request)?;
                manager
                    .activate_locked(&request.owned_id, request.generation)
                    .await?;
            }
            let receipt = manager.handoff_commit(&request).await?;
            Ok(receipt)
        }
        AgentConversationHandoffPhase::Rollback => {
            manager.validate_handoff_identity(&request)?;
            Ok(manager.handoff_rollback(&request)?)
        }
    }
}

fn receipt_from_context(
    request: &AgentConversationHandoffRequest,
    context: super::manager::HandoffContext,
    rollback_available: bool,
    message: &str,
) -> AgentConversationHandoffReceipt {
    AgentConversationHandoffReceipt {
        owned_id: request.owned_id.clone(),
        generation: request.generation,
        direction: request.direction,
        mode: request.mode,
        phase: AgentConversationHandoffPhase::Prepare,
        previous_owner: context.previous_owner,
        owner: context.owner,
        native_session_id: context.native_session_id,
        pty_session_id: request
            .pty_session_id
            .clone()
            .or_else(|| request.process_tree.pty_session_id.clone()),
        history_boundary: request.history_boundary.clone(),
        process_tree: request.process_tree.clone(),
        rollback_available,
        message: message.to_string(),
    }
}
