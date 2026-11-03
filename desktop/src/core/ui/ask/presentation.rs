//! Exact need presentation metadata. Only provider permissions and forms use AskPayload;
//! MCP text and choice answers retain their own string contract.
use super::payload::{AskOption, AskPayload};
use serde::Deserialize;
use serde_json::Value;

#[derive(Clone, Debug, PartialEq, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct NeedOption {
    pub id: String,
    pub label: String,
    pub kind: String,
}

#[derive(Clone, Debug, PartialEq, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct NeedPresentation {
    pub title: Option<String>,
    pub recommendation: Option<String>,
    pub input_mode: String,
    pub schema: Option<Value>,
    pub options: Vec<NeedOption>,
    pub free_text: bool,
}

impl NeedPresentation {
    /// Adapts the stored provider presentation to the one shared form implementation.
    /// State, reason and answer come from the same displayed need, without inference.
    pub fn provider_payload(
        &self,
        state: &str,
        message: Option<String>,
        answer: Option<Value>,
    ) -> Option<AskPayload> {
        let kind = match self.input_mode.as_str() {
            "permission" => "permission",
            "form" => "question",
            _ => return None,
        };
        Some(AskPayload {
            kind: kind.into(),
            state: state.into(),
            title: self.title.clone(),
            message,
            tool_call_id: None,
            options: self
                .options
                .iter()
                .map(|option| AskOption {
                    option_id: option.id.clone(),
                    name: option.label.clone(),
                    kind: option.kind.clone(),
                })
                .collect(),
            schema: self.schema.clone(),
            answer,
        })
    }
}
