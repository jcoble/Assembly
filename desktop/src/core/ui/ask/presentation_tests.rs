use super::presentation::NeedPresentation;
use serde_json::{Value, json};

#[test]
fn permission_keeps_actual_ids_labels_order_and_nullable_metadata() {
    let display: NeedPresentation = serde_json::from_value(json!({
        "title": null, "recommendation": null, "inputMode": "permission",
        "schema": null, "freeText": false,
        "options": [
            {"id":"deny-real","label":"Same label","kind":"reject_once"},
            {"id":"allow-real","label":"Same label","kind":"allow_once"}
        ]
    }))
    .unwrap();
    let answer = json!({"optionId":"allow-real"});
    let ask = display
        .provider_payload(
            "answered",
            Some("Actual reason".into()),
            Some(answer.clone()),
        )
        .unwrap();
    assert_eq!(ask.kind, "permission");
    assert_eq!(ask.state, "answered");
    assert_eq!(ask.title, None);
    assert_eq!(display.recommendation, None);
    assert_eq!(ask.message.as_deref(), Some("Actual reason"));
    assert_eq!(
        ask.options
            .iter()
            .map(|option| option.option_id.as_str())
            .collect::<Vec<_>>(),
        ["deny-real", "allow-real"]
    );
    assert_eq!(ask.options[0].name, "Same label");
    assert_eq!(ask.answer, Some(answer));
}

#[test]
fn form_preserves_requested_schema_and_answer_without_flattening() {
    let schema = json!({"type":"object","required":["color"],"properties":{
        "color":{"type":"string","enum":["red","blue"]},
        "sizes":{"type":"array","items":{"enum":["small","large"]}}
    }});
    let display: NeedPresentation = serde_json::from_value(json!({
        "title":"Choose","recommendation":"Explicit recommendation","inputMode":"form",
        "schema":schema,"options":[],"freeText":false
    }))
    .unwrap();
    let answer = json!({"action":"accept","content":{"color":"red","sizes":["large"]}});
    let ask = display
        .provider_payload("open", None, Some(answer.clone()))
        .unwrap();
    assert_eq!(ask.kind, "question");
    assert_eq!(ask.schema, Some(schema));
    assert_eq!(ask.answer, Some(answer));
    assert_eq!(
        display.recommendation.as_deref(),
        Some("Explicit recommendation")
    );
}

#[test]
fn text_and_choice_are_not_reinterpreted_as_provider_permissions_or_forms() {
    for mode in ["text", "choice", "future-mode"] {
        let display: NeedPresentation = serde_json::from_value(json!({
            "title":null,"recommendation":null,"inputMode":mode,"schema":null,
            "options":[{"id":"approve","label":"Approve","kind":"confirmation"}],"freeText":false
        }))
        .unwrap();
        assert!(
            display
                .provider_payload("open", None, Some(Value::String("approve".into())))
                .is_none()
        );
    }
}

#[test]
fn missing_typed_options_do_not_turn_legacy_options_json_into_display_metadata() {
    assert!(
        serde_json::from_value::<NeedPresentation>(json!({
            "title":null,"recommendation":null,"inputMode":"permission","schema":null,
            "optionsJson":"[\"guessed label\"]","freeText":false
        }))
        .is_err()
    );
}
