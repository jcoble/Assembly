//! The shared question draft and canonical action/content payload, moved from chat.
use super::payload::{AskInput, AskPayload, AskSchema};
use gpui_kit::component::input::InputState;
use gpui_kit::{App, AppContext as _, Entity, Window};
use serde_json::{Map, Value, json};

/// An open question's form: the choices picked, by field, and each text field's input.
#[derive(Clone)]
pub struct AskForm {
    pub picked: Map<String, Value>,
    pub texts: Vec<(String, Entity<InputState>)>,
}

impl AskForm {
    /// The form's inputs for an open question with fields; `None` for anything else.
    pub fn of(ask: &AskPayload, window: &mut Window, cx: &mut App) -> Option<AskForm> {
        let fields = ask.schema.as_ref().and_then(AskSchema::of)?;
        let texts = fields
            .properties
            .into_iter()
            .filter(|(_, field)| field.input() == AskInput::Text)
            .map(|(name, _)| {
                let other = if name.ends_with("_custom") {
                    "Other"
                } else {
                    ""
                };
                let input = cx.new(|cx| InputState::new(window, cx).placeholder(other));
                (name, input)
            })
            .collect();
        Some(AskForm {
            picked: Map::new(),
            texts,
        })
    }

    /// What Submit sends: each picked choice and typed text, leaving out the empty ones.
    pub fn content(picked: &Map<String, Value>, texts: Vec<(String, String)>) -> Value {
        let mut content: Map<String, Value> = picked
            .iter()
            .filter(|(_, value)| match value {
                Value::Array(list) => !list.is_empty(),
                Value::String(text) => !text.is_empty(),
                _ => true,
            })
            .map(|(name, value)| (name.clone(), value.clone()))
            .collect();
        for (name, text) in texts {
            let text = text.trim();
            if !text.is_empty() {
                content.insert(name, Value::String(text.to_string()));
            }
        }
        json!({ "action": "accept", "content": content })
    }
}
