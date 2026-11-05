//! The composer's agent settings as the Context tab's Session card reads them: the pills' own words, and none for a
//! setting the agent doesn't offer or has no value for.

use gpui::TestAppContext;
use serde_json::{Value, json};

use super::tests::open;

/// The composer on s1 with this config read, answering its live words.
fn live_words(
    config: Value,
    cx: &mut TestAppContext,
) -> (Option<String>, Option<String>, Option<String>) {
    let (composer, client, _, cx) = open(cx);
    client.set_config(Some(config));
    composer.update_in(cx, |composer, window, cx| {
        composer.set_session(Some("s1".into()), window, cx)
    });
    cx.run_until_parked();
    cx.update(|_, cx| composer.read(cx).live_config())
}

#[gpui::test]
fn the_card_reads_model_effort_and_access_in_the_pills_words(cx: &mut TestAppContext) {
    let words = live_words(
        json!({
            "options": [
                { "id": "model", "name": "Model", "category": "model", "type": "select", "currentValue": "sonnet",
                  "options": [{ "value": "opus", "name": "Opus" }, { "value": "sonnet", "name": "Sonnet" }] },
                { "id": "effort", "name": "Effort", "type": "select", "currentValue": "xhigh",
                  "options": [{ "value": "high", "name": "high" }, { "value": "xhigh", "name": "xhigh" }] },
                { "id": "mode", "name": "Mode", "category": "mode", "type": "select", "currentValue": "acceptEdits",
                  "options": [{ "value": "default", "name": "Default" }, { "value": "acceptEdits", "name": "Accept edits" }] }
            ],
            "choices": {}
        }),
        cx,
    );
    assert_eq!(
        words,
        (
            Some("Sonnet".into()),
            Some("Extra High".into()),
            Some("Accept Edits".into())
        )
    );
}

#[gpui::test]
fn a_setting_the_agent_does_not_offer_or_has_no_value_for_reads_none(cx: &mut TestAppContext) {
    let words = live_words(
        json!({
            "options": [
                { "id": "model", "name": "Model", "category": "model", "type": "select", "currentValue": "opus",
                  "options": [{ "value": "opus", "name": "Opus" }] },
                { "id": "effort", "name": "Effort", "type": "select",
                  "options": [{ "value": "high", "name": "High" }] }
            ],
            "choices": {}
        }),
        cx,
    );
    assert_eq!(words, (Some("Opus".into()), None, None));
}
