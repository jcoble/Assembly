use super::*;
use crate::features::session::composer::agent_config::{ConfigChoice, ConfigOption};

fn option(choices: &[(&str, &str, Option<&str>)]) -> ConfigOption {
    ConfigOption {
        id: "x".into(),
        name: "X".into(),
        category: None,
        kind: "select".into(),
        current_value: None,
        options: choices
            .iter()
            .map(|(value, name, description)| ConfigChoice {
                value: (*value).into(),
                name: (*name).into(),
                description: description.map(Into::into),
            })
            .collect(),
    }
}

#[test]
fn known_levels_read_as_the_old_table_and_unknown_ones_as_the_provider_wrote_them() {
    let levels = option(&[("xhigh", "very high", None), ("turbo", "Turbo mode", None)]);
    assert_eq!(effort_label(&levels, "xhigh"), "Extra High");
    assert_eq!(effort_label(&levels, "turbo"), "Turbo mode");
    assert_eq!(effort_label(&levels, "gone"), "gone");
}

#[test]
fn approval_words_ignore_case_and_fall_back_to_the_provider() {
    let modes = option(&[
        ("acceptEdits", "accept", None),
        ("yolo", "Yolo", Some("All of it.")),
    ]);
    assert_eq!(approval_label(Some(&modes), "acceptEdits"), "Accept Edits");
    assert_eq!(approval_label(Some(&modes), "yolo"), "Yolo");
    assert_eq!(approval_label(Some(&modes), ""), "Approvals");
    assert_eq!(
        approval_description(&modes.options[0]).as_deref(),
        Some("File edits are accepted automatically; other actions still ask.")
    );
    assert_eq!(
        approval_description(&modes.options[1]).as_deref(),
        Some("All of it.")
    );
}

#[test]
fn a_model_uses_the_providers_label_else_its_raw_id() {
    let models = option(&[("claude-sonnet-5-5", "Sonnet 5.5", None)]);
    assert_eq!(model_label(&models, "claude-sonnet-5-5"), "Sonnet 5.5");
    assert_eq!(model_label(&models, "claude-opus-5-5"), "claude-opus-5-5");
    assert_eq!(model_label(&models, "gpt-5-sol"), "gpt-5-sol");
}

#[test]
fn the_pill_joins_model_and_effort_and_says_agent_settings_with_neither() {
    assert_eq!(
        model_effort_label(Some("Sonnet".into()), Some("High".into())),
        "Sonnet · High"
    );
    assert_eq!(model_effort_label(None, Some("High".into())), "High");
    assert_eq!(model_effort_label(None, None), "Agent settings");
}
