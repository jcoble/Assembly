//! A failed Git action in the pane's words: the backend's plain sentence, git's own text for Details, and the next
//! step when one is known. The backend writes "sentence\n\ndetails"; nothing else parses it.
use crate::core::socket::{call_failure::CallFailure, error_code::ErrorCode};

pub const LOST_REPLY: &str =
    "The connection dropped before the answer came. Refresh to see what happened.";

#[derive(Clone, Debug, PartialEq)]
pub enum Recovery {
    Refresh,
    Connect(String),
    ReconcileOperation,
}
#[derive(Clone, Debug, PartialEq)]
pub struct GitFailure {
    pub sentence: String,
    pub details: Option<String>,
    pub recovery: Option<Recovery>,
}
impl GitFailure {
    pub fn plain(text: &str) -> Self {
        let (sentence, details) = match text.split_once("\n\n") {
            Some((sentence, details)) if !details.trim().is_empty() => {
                (sentence, Some(details.trim().to_string()))
            }
            _ => (text, None),
        };
        Self {
            sentence: sentence.trim().to_string(),
            details,
            recovery: None,
        }
    }
    pub fn lost_reply() -> Self {
        Self {
            sentence: LOST_REPLY.into(),
            details: None,
            recovery: Some(Recovery::Refresh),
        }
    }
    pub fn from_call(failure: &CallFailure) -> Self {
        match &failure.code {
            ErrorCode::Uncertain { .. } => Self::lost_reply(),
            ErrorCode::Unreachable { machine } => Self {
                recovery: Some(Recovery::Connect(machine.clone())),
                ..Self::plain(&failure.message)
            },
            _ => Self::plain(&failure.message),
        }
    }
}
#[cfg(test)]
#[path = "git_failure_tests.rs"]
mod tests;
