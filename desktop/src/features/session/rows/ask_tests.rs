//! A question's form reads its fields in the order the agent wrote them.

use serde_json::json;

use super::ask::AskSchema;

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
