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
    pub fn pick(&mut self,field:String,value:String,many:bool) {
        if many {
            let list=self.picked.entry(field).or_insert_with(||Value::Array(Vec::new()));
            if let Value::Array(list)=list {
                match list.iter().position(|item|item.as_str()==Some(&value)) {
                    Some(at)=>drop(list.remove(at)),None=>list.push(Value::String(value)),
                }
            }
        } else {self.picked.insert(field,Value::String(value));}
    }


    /// The form's inputs for an open question with fields; `None` for anything else.
    pub fn of(ask: &AskPayload, window: &mut Window, cx: &mut App) -> Option<AskForm> {
        let fields = ask.schema.as_ref().and_then(AskSchema::of)?;
        // A field that must be answered starts on its first choice, as the old form's did.
        let picked = (fields.properties.iter())
            .filter(|(name, _)| fields.required.contains(name))
            .filter_map(|(name, field)| match field.input() {
                AskInput::One(choices) => choices
                    .first()
                    .map(|choice| (name.clone(), Value::String(choice.value.clone()))),
                _ => None,
            })
            .collect();
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
        Some(AskForm { picked, texts })
    }

    /// The first required field still empty, by its label. Continue waits for it, as the old form's did.
    pub fn missing(
        ask: &AskPayload,
        picked: &Map<String, Value>,
        texts: &[(String, String)],
    ) -> Option<String> {
        let fields = ask.schema.as_ref().and_then(AskSchema::of)?;
        let filled = |name: &str| {
            let typed = texts.iter().any(|(text, value)| text == name && !value.trim().is_empty());
            typed
                || picked.get(name).is_some_and(|value| match value {
                    Value::Array(list) => !list.is_empty(),
                    Value::String(text) => !text.is_empty(),
                    _ => true,
                })
        };
        (fields.properties.into_iter())
            .find(|(name, _)| fields.required.contains(name) && !filled(name))
            .map(|(name, field)| field.title.unwrap_or(name))
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
