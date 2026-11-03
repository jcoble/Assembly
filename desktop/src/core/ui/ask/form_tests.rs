use serde_json::json;

// Pins: g0-session#16
#[test]
fn submit_sends_what_was_picked_and_typed_leaving_out_the_empty_fields() {
    use super::form::AskForm;
    let mut picked = serde_json::Map::new();
    picked.insert("zone".into(), json!("east"));
    picked.insert("tags".into(), json!([]));
    let texts = vec![
        ("note".to_string(), "  hi ".to_string()),
        ("zone_custom".to_string(), " ".to_string()),
    ];
    assert_eq!(
        AskForm::content(&picked, texts),
        json!({ "action": "accept", "content": { "zone": "east", "note": "hi" } })
    );
}

#[test]
fn picking_form_values_preserves_actual_ids_and_multi_choice_order() {
    let mut form=super::form::AskForm{picked:serde_json::Map::new(),texts:Vec::new()};
    form.pick("actual-field".into(),"provider:z".into(),true);
    form.pick("actual-field".into(),"provider:a".into(),true);
    assert_eq!(form.picked["actual-field"],serde_json::json!(["provider:z","provider:a"]));
    form.pick("actual-field".into(),"provider:z".into(),true);
    assert_eq!(form.picked["actual-field"],serde_json::json!(["provider:a"]));
    form.pick("other-field".into(),"Exact supplied value".into(),false);
    assert_eq!(form.picked["other-field"],"Exact supplied value");
}

#[test]
fn a_required_choice_starts_on_its_first_and_an_empty_required_field_is_named() {
    use super::form::AskForm;
    use super::payload::AskPayload;
    let ask: AskPayload = serde_json::from_value(json!({
        "kind": "question", "state": "open", "options": [],
        "schema": { "required": ["zone", "note"], "properties": {
            "zone": { "type": "string", "enum": ["east", "west"] },
            "note": { "type": "string", "title": "Note" },
            "tag": { "type": "string", "enum": ["a", "b"] },
        } },
    }))
    .unwrap();
    let mut picked = serde_json::Map::new();
    picked.insert("zone".into(), json!("east"));
    let empty = vec![("note".to_string(), "  ".to_string())];
    assert_eq!(AskForm::missing(&ask, &picked, &empty).as_deref(), Some("Note"));
    let typed = vec![("note".to_string(), "hi".to_string())];
    assert_eq!(AskForm::missing(&ask, &picked, &typed), None);
    picked.insert("zone".into(), json!(""));
    assert_eq!(AskForm::missing(&ask, &picked, &typed).as_deref(), Some("zone"));
}
