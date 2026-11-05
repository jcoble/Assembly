//! The one place an answer's kind becomes words, so an approval reads the same on the card, in the chat and above the composer.
use super::payload::AskOption;
use super::presentation::NeedOption;

fn refuses(kind: &str) -> bool {
    ["reject", "deny", "decline"]
        .iter()
        .any(|word| kind.contains(word))
}

/// A provider option's words, by its kind. A refusal that lasts keeps the agent's own words, so it never reads as an allow.
pub fn option_words(option: &AskOption) -> String {
    let kind = option.kind.to_ascii_lowercase();
    let named = |fallback: &str| {
        if option.name.is_empty() {
            fallback.to_string()
        } else {
            option.name.clone()
        }
    };
    if refuses(&kind) {
        return if kind.contains("always") {
            named("Deny")
        } else {
            "Deny".to_string()
        };
    }
    if kind.contains("always") || kind.contains("session") {
        "Always allow this session".to_string()
    } else if kind.contains("allow") || kind.contains("accept") {
        "Allow once".to_string()
    } else {
        named("Continue")
    }
}

/// Whether an option turns the action down, which reads in the danger color.
pub fn is_deny(option: &AskOption) -> bool {
    refuses(&option.kind.to_ascii_lowercase())
        || option_words(option).to_ascii_lowercase().contains("cancel")
}

/// The words for a bare answer id, which is all a workflow's approval sends and records.
fn id_words(id: &str) -> Option<&'static str> {
    Some(match id {
        "allow_once" => "Allow once",
        "allow_always" => "Always allow this session",
        "deny" | "reject" | "reject_once" => "Deny",
        "approve" => "Approve",
        "revise" => "Revise",
        "retry" => "Retry",
        "cancel" => "Cancel",
        _ => return None,
    })
}

/// A listed choice's words: a bare id reads as words, anything the asker wrote stays as written.
pub fn choice_words(option: &NeedOption) -> String {
    id_words(&option.label).map_or_else(|| option.label.clone(), str::to_owned)
}

/// What an answer reads as afterward.
pub fn answered_words(answer: &str) -> String {
    id_words(answer).map_or_else(|| answer.to_owned(), str::to_owned)
}
