//! `Notice` is how a failed run reads: the provider's sentence lifted out of its JSON, and "the
//! conversation is full" said plainly. A cancelled run is the person's own doing, so it has none. Ported from
//! chat/notices.ts.

use serde_json::Value;

#[derive(Clone, Debug, PartialEq)]
pub struct Notice {
    pub label: String,
    pub message: String,
    pub hint: Option<String>,
}

impl Notice {
    pub fn of_error(message: &str, code: Option<&str>) -> Option<Notice> {
        if code == Some("cancelled") {
            return None;
        }
        let message = unwrap(message);
        let message = message.trim();
        let message =
            strip_prefix_ignoring_case(message, "internal error:").map_or(message, str::trim_start);
        if message.to_lowercase().contains("prompt is too long") {
            return Some(Notice {
                label: "Conversation is full".to_string(),
                message: "This conversation has outgrown the model's context window, so nothing more can be sent to it."
                    .to_string(),
                hint: Some(
                    "Send /compact if this agent offers it, or start a new session and bring what you need with you."
                        .to_string(),
                ),
            });
        }
        let message = if message.is_empty() {
            "The agent reported an error without saying what went wrong."
        } else {
            message
        };
        Some(Notice {
            label: "Conversation error".to_string(),
            message: message.to_string(),
            hint: None,
        })
    }
}

/// Provider errors arrive as the JSON they were sent in: lifts out the sentence, through any nesting.
fn unwrap(raw: &str) -> String {
    let mut message = raw.to_string();
    while let Ok(parsed) = serde_json::from_str::<Value>(&message) {
        let next = parsed
            .pointer("/error/message")
            .and_then(Value::as_str)
            .or(parsed["message"].as_str());
        let Some(next) = next else { break };
        message = next.to_string();
    }
    message
}

fn strip_prefix_ignoring_case<'a>(text: &'a str, prefix: &str) -> Option<&'a str> {
    let head = text.get(..prefix.len())?;
    head.eq_ignore_ascii_case(prefix)
        .then(|| &text[prefix.len()..])
}
