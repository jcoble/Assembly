//! `AskPayload` is a card asking the user: a permission (one of the agent's options, by ACP kind) or a question (a
//! flat JSON Schema form). Its answer goes back through `send/answer`.

use serde::Deserialize;
use serde_json::Value;

#[derive(Clone, Debug, PartialEq, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct AskPayload {
    /// permission or question.
    pub kind: String,
    /// open, answered or cancelled.
    pub state: String,
    pub title: Option<String>,
    pub message: Option<String>,
    pub tool_call_id: Option<String>,
    #[serde(default)]
    pub options: Vec<AskOption>,
    /// A question's form, as JSON Schema; `AskSchema::of` reads it.
    pub schema: Option<Value>,
    /// The option's `{ optionId }` or the form's `{ action, content }`.
    pub answer: Option<Value>,
}

#[derive(Clone, Debug, PartialEq, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct AskOption {
    pub option_id: String,
    pub name: String,
    /// allow_once, allow_always, reject_once, reject_always, or another the agent names.
    pub kind: String,
}

/// The form's fields in the JSON's order, which is the form's order (read by hand in step 2: serde's maps sort).
#[derive(Clone, Debug, PartialEq)]
pub struct AskSchema {
    pub properties: Vec<(String, AskField)>,
    pub required: Vec<String>,
}

impl AskSchema {
    pub fn of(_schema: &Value) -> Option<AskSchema> {
        todo!()
    }
}

#[derive(Clone, Debug, PartialEq, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct AskField {
    #[serde(rename = "type")]
    pub kind: Option<String>,
    pub title: Option<String>,
    pub description: Option<String>,
    pub one_of: Option<Vec<AskChoice>>,
    #[serde(rename = "enum")]
    pub choices: Option<Vec<String>>,
    /// A multi-select's choices.
    pub items: Option<AskItems>,
}

#[derive(Clone, Debug, PartialEq, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct AskItems {
    pub any_of: Option<Vec<AskChoice>>,
    #[serde(rename = "enum")]
    pub choices: Option<Vec<String>>,
}

#[derive(Clone, Debug, PartialEq, Deserialize)]
pub struct AskChoice {
    #[serde(rename = "const")]
    pub value: String,
    pub title: Option<String>,
    pub description: Option<String>,
}
