//! A question's form reads its fields in the order the agent wrote them.

use serde_json::json;

use super::payload::AskSchema;

// Pins: g0-session#16
#[test]
fn a_form_keeps_its_fields_in_the_agents_order_with_their_choices_and_what_is_required() {
    let schema = json!({
        "type": "object",
        "properties": {
            "zone": { "type": "string", "title": "Zone", "enum": ["east", "west"] },
            "approve": { "type": "boolean", "title": "Approve" },
            "tags": { "type": "array", "items": { "anyOf": [{ "const": "a", "title": "A" }] } },
            "colour": { "type": "string", "oneOf": [{ "const": "red", "title": "Red" }] },
        },
        "required": ["zone"],
    });
    let form = AskSchema::of(&schema).unwrap();
    let names: Vec<&str> = form
        .properties
        .iter()
        .map(|(name, _)| name.as_str())
        .collect();
    assert_eq!(names, ["zone", "approve", "tags", "colour"]);
    assert_eq!(form.required, ["zone"]);
    assert_eq!(
        form.properties[0].1.choices.as_deref(),
        Some(&["east".to_string(), "west".to_string()][..])
    );
    assert_eq!(
        form.properties[2]
            .1
            .items
            .as_ref()
            .unwrap()
            .any_of
            .as_ref()
            .unwrap()[0]
            .value,
        "a"
    );
    assert_eq!(
        form.properties[3].1.one_of.as_ref().unwrap()[0]
            .title
            .as_deref(),
        Some("Red")
    );
}

// Pins: g0-session#16
#[test]
fn a_form_with_no_fields_is_none_and_a_field_it_cannot_read_is_a_plain_text_field() {
    assert_eq!(AskSchema::of(&json!({ "type": "object" })), None);
    let form = AskSchema::of(&json!({ "properties": { "odd": { "type": 3 } } })).unwrap();
    assert_eq!(form.properties[0].0, "odd");
    assert_eq!(form.properties[0].1.kind, None);
    assert!(form.required.is_empty());
}

// Pins: g0-session#16
#[test]
fn each_field_answers_by_its_choices_or_as_text() {
    use super::payload::AskInput;
    let schema = json!({
        "type": "object",
        "properties": {
            "zone": { "type": "string", "enum": ["east", "west"] },
            "tags": { "type": "array", "items": { "anyOf": [{ "const": "a", "title": "A" }] } },
            "colour": { "type": "string", "oneOf": [{ "const": "red", "title": "Red" }] },
            "note": { "type": "string" },
        },
    });
    let form = AskSchema::of(&schema).unwrap();
    let inputs: Vec<AskInput> = form
        .properties
        .iter()
        .map(|(_, field)| field.input())
        .collect();
    let values = |input: &AskInput| match input {
        AskInput::One(choices) | AskInput::Many(choices) => choices
            .iter()
            .map(|choice| choice.value.clone())
            .collect::<Vec<_>>(),
        _ => vec![],
    };
    assert!(matches!(inputs[0], AskInput::One(_)));
    assert_eq!(values(&inputs[0]), ["east", "west"]);
    assert!(matches!(inputs[1], AskInput::Many(_)));
    assert_eq!(values(&inputs[1]), ["a"]);
    assert!(matches!(inputs[2], AskInput::One(_)));
    assert_eq!(inputs[3], AskInput::Text);
}

#[test]
fn a_field_is_read_as_its_type_and_marked_hidden_only_by_the_schema() {
    use super::payload::{AskField, AskInput};
    let input = |field: serde_json::Value| serde_json::from_value::<AskField>(field).unwrap().input();
    assert_eq!(input(json!({ "type": "boolean" })), AskInput::Flag);
    assert_eq!(input(json!({ "type": "string", "format": "password" })), AskInput::Secret);
    assert_eq!(input(json!({ "type": "string", "writeOnly": true })), AskInput::Secret);
    assert_eq!(input(json!({ "type": "string", "format": "email" })), AskInput::Text);
    assert_eq!(input(json!({ "type": "string", "writeOnly": false })), AskInput::Text);
}
