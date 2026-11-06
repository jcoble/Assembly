//! The canonical answer a question's form sends. `ask_questionnaire` holds the form itself.
use super::payload::{AskField, AskInput, AskPayload, AskSchema};
use serde_json::{Map, Value, json};
use std::str::FromStr as _;

pub struct AskForm;

impl AskForm {
    /// The first required field still empty, by its label. Continue waits for it.
    pub fn missing(
        ask: &AskPayload,
        picked: &Map<String, Value>,
        texts: &[(String, String)],
    ) -> Option<String> {
        let fields = ask.schema.as_ref().and_then(AskSchema::of)?;
        let filled = |name: &str, field: &AskField| {
            let typed = texts.iter().any(|(text, value)| {
                text == name
                    && match field.input() {
                        AskInput::Secret => !value.is_empty(),
                        _ => !value.trim().is_empty(),
                    }
            });
            typed
                || picked.get(name).is_some_and(|value| match value {
                    Value::Array(list) => !list.is_empty(),
                    Value::String(text) => !text.is_empty(),
                    _ => true,
                })
        };
        (fields.properties.into_iter())
            .find(|(name, field)| fields.required.contains(name) && !filled(name, field))
            .map(|(name, field)| field.title.unwrap_or(name))
    }

    /// What Continue sends, or why it can't go yet. Each answer goes back as its field's type: a password exactly as
    /// typed, a number as a number, a check box as a boolean.
    pub fn answer(
        ask: &AskPayload,
        picked: &Map<String, Value>,
        texts: Vec<(String, String)>,
    ) -> Result<Value, String> {
        if let Some(label) = Self::missing(ask, picked, &texts) {
            return Err(format!("{label} is required."));
        }
        let fields = ask.schema.as_ref().and_then(AskSchema::of);
        let field_of = |name: &str| {
            let fields = fields.as_ref()?;
            let (_, field) = fields.properties.iter().find(|(field, _)| field == name)?;
            Some(field)
        };
        let (mut plain, mut typed) = (Vec::new(), Map::new());
        for (name, text) in texts {
            match field_of(&name) {
                Some(field) if field.input() == AskInput::Secret => {
                    if !text.is_empty() {
                        typed.insert(name, Value::String(text));
                    }
                }
                Some(field) if field.numeric() && !text.trim().is_empty() => {
                    let label = field.title.clone().unwrap_or_else(|| name.clone());
                    let number =
                        Self::number(text.trim(), field.kind.as_deref() == Some("integer"))
                            .ok_or_else(|| format!("{label} must be a number."))?;
                    typed.insert(name, number);
                }
                _ => plain.push((name, text)),
            }
        }
        let mut answer = Self::content(picked, plain);
        if let Some(content) = answer["content"].as_object_mut() {
            content.extend(typed);
        }
        Ok(answer)
    }

    pub(super) fn number(text: &str, whole: bool) -> Option<Value> {
        if whole {
            return text.parse::<i64>().ok().map(Value::from);
        }
        serde_json::Number::from_str(text).ok().map(Value::Number)
    }

    /// An answer as a settled card may show it: without what the schema marks hidden.
    pub fn shown(schema: Option<&Value>, answer: &Value) -> Value {
        let mut shown = answer.clone();
        let Some(content) = shown.get_mut("content").and_then(Value::as_object_mut) else {
            return shown;
        };
        if let Some(fields) = schema.and_then(AskSchema::of) {
            for (name, field) in fields.properties {
                if field.input() == AskInput::Secret {
                    content.remove(&name);
                }
            }
        }
        shown
    }

    /// Each picked choice and typed text, leaving out the empty ones.
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
