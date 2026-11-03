//! The words the settings pills use, from the old composer's table. The provider's own name for a choice is used
//! wherever the table has none, so an unknown value still reads as the provider wrote it.

use super::agent_config::{ConfigChoice, ConfigOption};

/// What a thinking level reads as.
pub fn effort_label(option: &ConfigOption, value: &str) -> String {
    match value.to_ascii_lowercase().as_str() {
        "minimal" => "Minimal".into(),
        "low" => "Low".into(),
        "medium" => "Medium".into(),
        "high" => "High".into(),
        "xhigh" => "Extra High".into(),
        "max" => "Max".into(),
        _ => provider_name(option, value),
    }
}

/// An approval level's label and sentence, or the provider's own name and description for one the table doesn't know.
pub fn approval_label(option: Option<&ConfigOption>, value: &str) -> String {
    if value.is_empty() {
        return "Approvals".into();
    }
    approval_words(value).map_or_else(
        || option.map_or_else(|| value.to_string(), |option| provider_name(option, value)),
        |(label, _)| label.to_string(),
    )
}

pub fn approval_description(choice: &ConfigChoice) -> Option<String> {
    approval_words(&choice.value)
        .map(|(_, description)| description.to_string())
        .or_else(|| choice.description.clone())
}

/// A model's name: the provider's label first, else its id in title case.
pub fn model_label(option: &ConfigOption, value: &str) -> String {
    match option.options.iter().find(|choice| choice.value == value) {
        Some(choice) if !choice.name.is_empty() => choice.name.clone(),
        _ => tidy_model(value),
    }
}

/// The model pill's words: "Model · Effort", or "Agent settings" with neither.
pub fn model_effort_label(model: Option<String>, effort: Option<String>) -> String {
    let parts: Vec<String> = model.into_iter().chain(effort).collect();
    if parts.is_empty() {
        "Agent settings".into()
    } else {
        parts.join(" · ")
    }
}

fn provider_name(option: &ConfigOption, value: &str) -> String {
    option
        .options
        .iter()
        .find(|choice| choice.value == value)
        .map_or_else(|| value.to_string(), |choice| choice.name.clone())
}

fn approval_words(value: &str) -> Option<(&'static str, &'static str)> {
    Some(match value.to_ascii_lowercase().as_str() {
        "untrusted" => ("Ask every time", "Nothing runs until you say yes."),
        "on-request" => (
            "Ask when needed",
            "The agent works on its own and asks before anything risky.",
        ),
        "on-failure" => (
            "Ask after a failure",
            "The agent works on its own and asks only when a command fails.",
        ),
        "never" => (
            "Never ask",
            "The agent runs everything itself. Nothing stops to wait for you.",
        ),
        "read-only" => (
            "Ask for approval",
            "Can edit this workspace; asks before external files or internet access.",
        ),
        "agent" => (
            "Approve for me",
            "Can edit this workspace; automatic review checks actions for safety.",
        ),
        "agent-full-access" => (
            "Full access",
            "The agent can make changes and run commands without asking.",
        ),
        "default" => (
            "Default",
            "Standard behavior. The agent asks before anything dangerous.",
        ),
        "acceptedits" => (
            "Accept Edits",
            "File edits are accepted automatically; other actions still ask.",
        ),
        "plan" => ("Plan Mode", "Planning only. No tools actually run."),
        "dontask" => (
            "Don't Ask",
            "Never prompts. Anything not pre-approved is denied.",
        ),
        "bypasspermissions" => (
            "Bypass Permissions",
            "Runs everything without permission checks.",
        ),
        _ => return None,
    })
}

fn tidy_model(value: &str) -> String {
    let words: Vec<&str> = value.split(['-', '_', ' ']).filter(|word| !word.is_empty()).collect();
    let mut out: Vec<String> = Vec::new();
    let mut at = 0;
    while at < words.len() {
        let word = words[at];
        // GPT and its version read as one word.
        if word.eq_ignore_ascii_case("gpt") {
            match words.get(at + 1) {
                Some(version) => {
                    out.push(format!("GPT-{version}"));
                    at += 2;
                }
                None => {
                    out.push("GPT".into());
                    at += 1;
                }
            }
            continue;
        }
        let mut letters = word.chars();
        out.push(letters.next().map_or_else(String::new, |first| {
            first.to_uppercase().chain(letters).collect()
        }));
        at += 1;
    }
    out.join(" ")
}

#[cfg(test)]
#[path = "config_labels_tests.rs"]
mod tests;
