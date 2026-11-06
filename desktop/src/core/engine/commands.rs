//! Checked window commands over the existing main socket. The backend owns identity,
//! provenance and transition validation; uncertain mutation outcomes are never retried.
use super::engine_items::NeedItem;
use super::{EngineFeed, EngineKey, EngineRefusal, EngineStore};
use crate::core::socket::{client::SocketClient, error_code::ErrorCode, outcome::Outcome};
use gpui_kit::*;
use serde::{Deserialize, Serialize};
use serde_json::{Value, json};

#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct WindowCommand {
    pub command_id: String,
    pub entity_id: String,
    pub expected_version: i64,
    pub epoch: i64,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub machine: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub surface: Option<String>,
    pub command: Value,
}
#[derive(Clone, Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct CommandReceipt {
    pub applied: bool,
    pub error: Option<String>,
    pub version: i64,
}
#[derive(Clone, Debug)]
pub enum NeedAnswer {
    Answer(Value),
    Dismiss,
}

pub(super) fn resolve_command(
    store: &EngineStore,
    key: &EngineKey,
    answer: NeedAnswer,
    surface: Option<&str>,
) -> Result<WindowCommand, EngineRefusal> {
    if !matches!(surface, None | Some("rail" | "board")) {
        return Err(refused("bad_args", "Unsupported answer surface."));
    }
    let need = store
        .item(key)
        .and_then(NeedItem::of)
        .ok_or_else(|| refused("need_unavailable", "The current question is not available."))?;
    let session_key = EngineKey {
        machine: need.session.machine.clone(),
        kind: "session".into(),
        id: need.session.id.clone(),
    };
    let epoch = store
        .item(&session_key)
        .and_then(|session| session.value["epoch"].as_i64())
        .filter(|epoch| *epoch >= 0)
        .ok_or_else(|| {
            refused(
                "session_unavailable",
                "The current session ownership is not available.",
            )
        })?;
    let command = match answer {
        NeedAnswer::Answer(answer) => {
            json!({"kind":"resolve_need","state":"answered","answer":answer})
        }
        NeedAnswer::Dismiss => json!({"kind":"resolve_need","state":"dismissed","answer":null}),
    };
    Ok(WindowCommand {
        command_id: uuid::Uuid::new_v4().to_string(),
        entity_id: key.id.clone(),
        expected_version: need.version,
        epoch,
        machine: Some(key.machine.clone()),
        surface: surface.map(str::to_owned),
        command,
    })
}
impl EngineFeed {
    pub fn resolve_need(
        &mut self,
        key: EngineKey,
        answer: NeedAnswer,
        surface: Option<&str>,
        cx: &mut Context<Self>,
    ) -> Task<Result<CommandReceipt, EngineRefusal>> {
        match resolve_command(&self.store, &key, answer, surface) {
            Ok(command) => self.submit(command, cx),
            Err(error) => Task::ready(Err(error)),
        }
    }
    /// As `submit`, for a command a row's unrelated moves don't change the meaning of. A refusal for a version that moved
    /// on the way is a definite answer, not an uncertain outcome, so it's asked once more at the version the refusal named.
    pub fn submit_following(
        &mut self,
        command: WindowCommand,
        cx: &mut Context<Self>,
    ) -> Task<Result<CommandReceipt, EngineRefusal>> {
        let first = self.submit(command.clone(), cx);
        cx.spawn(async move |feed, cx| {
            let answer = first.await;
            let version = match &answer {
                Ok(receipt) if receipt.error.as_deref() == Some("version_conflict") => {
                    receipt.version
                }
                _ => return answer,
            };
            let again = WindowCommand {
                command_id: uuid::Uuid::new_v4().to_string(),
                expected_version: version,
                ..command
            };
            match feed.update(cx, |feed, cx| feed.submit(again, cx)) {
                Ok(second) => second.await,
                Err(_) => answer,
            }
        })
    }
    pub fn submit(
        &mut self,
        command: WindowCommand,
        cx: &mut Context<Self>,
    ) -> Task<Result<CommandReceipt, EngineRefusal>> {
        let Some(socket) = SocketClient::try_global(cx) else {
            return Task::ready(Err(refused(
                "disconnected",
                "The backend is not connected.",
            )));
        };
        let args = match serde_json::to_value(command) {
            Ok(args) => args,
            Err(error) => return Task::ready(Err(refused("bad_args", &error.to_string()))),
        };
        let call = socket
            .read(cx)
            .call::<CommandReceipt>("engine", "command", args, None);
        cx.foreground_executor().spawn(async move {
            match call.await {
                Outcome::Ok(receipt) => Ok(receipt),
                Outcome::Aborted => Err(refused(
                    "outcome_unknown",
                    "The command outcome is unknown. Check the current state before sending again.",
                )),
                Outcome::Failed(error) => {
                    let code = match &error.code {
                        ErrorCode::Refused { code } => code.as_str(),
                        ErrorCode::NotFound => "not_found",
                        ErrorCode::BadArgs => "bad_args",
                        ErrorCode::Failed
                        | ErrorCode::ConnectionLost
                        | ErrorCode::TerminalMissing => "failed",
                        ErrorCode::Uncertain { .. }
                        | ErrorCode::Timeout
                        | ErrorCode::Unreachable { .. } => "outcome_unknown",
                    };
                    Err(refused(code, &error.message))
                }
            }
        })
    }
}
fn refused(code: &str, message: &str) -> EngineRefusal {
    EngineRefusal {
        code: code.into(),
        message: message.into(),
    }
}
