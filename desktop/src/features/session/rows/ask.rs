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

/// The form's fields in the JSON's order, which is the form's order (serde_json keeps it: `preserve_order`).
#[derive(Clone, Debug, PartialEq)]
pub struct AskSchema {
    pub properties: Vec<(String, AskField)>,
    pub required: Vec<String>,
}

impl AskSchema {
    /// `None` for a form with no fields; a field it can't read is a plain text field.
    pub fn of(schema: &Value) -> Option<AskSchema> {
        let properties: Vec<(String, AskField)> = schema
            .get("properties")?
            .as_object()?
            .iter()
            .map(|(name, field)| {
                (
                    name.clone(),
                    serde_json::from_value(field.clone()).unwrap_or_default(),
                )
            })
            .collect();
        let required = schema
            .get("required")
            .and_then(Value::as_array)
            .map(|names| {
                names
                    .iter()
                    .filter_map(Value::as_str)
                    .map(String::from)
                    .collect()
            })
            .unwrap_or_default();
        (!properties.is_empty()).then_some(AskSchema {
            properties,
            required,
        })
    }
}

#[derive(Clone, Debug, Default, PartialEq, Deserialize)]
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
