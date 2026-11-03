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
