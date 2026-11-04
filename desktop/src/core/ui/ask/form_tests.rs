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

fn typed_ask() -> super::payload::AskPayload {
    serde_json::from_value(json!({
        "kind": "question", "state": "open", "options": [],
        "schema": { "required": ["token"], "properties": {
            "token": { "type": "string", "title": "Token", "format": "password" },
            "pin": { "type": "string", "writeOnly": true },
            "agree": { "type": "boolean", "title": "Agree" },
            "keep": { "type": "boolean", "default": true },
            "count": { "type": "integer", "title": "Count" },
            "ratio": { "type": "number" },
            "note": { "type": "string" },
        } },
    }))
    .unwrap()
}

#[gpui::test]
fn a_password_field_is_masked_and_a_check_box_starts_as_the_schema_says(cx: &mut gpui::TestAppContext) {
    use super::form::AskForm;
    cx.update(gpui_kit::init);
    let ask = typed_ask();
    let cx = cx.add_empty_window();
    let form = cx.update(|window, cx| AskForm::of(&ask, window, cx)).unwrap();
    cx.update(|_, cx| {
        let masked = |name: &str| {
            let (_, input) = form.texts.iter().find(|(text, _)| text == name).unwrap();
            input.read(cx).presentation().is_masked()
        };
        assert!(masked("token"));
        assert!(masked("pin"));
        assert!(!masked("note"));
        assert!(!masked("count"));
    });
    assert_eq!(form.picked["agree"], json!(false));
    assert_eq!(form.picked["keep"], json!(true));
}

#[test]
fn each_answer_goes_back_as_its_fields_type() {
    use super::form::AskForm;
    let mut picked = serde_json::Map::new();
    picked.insert("agree".into(), json!(false));
    picked.insert("keep".into(), json!(true));
    let texts = vec![
        ("token".to_string(), "  s3 cret ".to_string()),
        ("pin".to_string(), String::new()),
        ("count".to_string(), " 12 ".to_string()),
        ("ratio".to_string(), "1.5".to_string()),
        ("note".to_string(), "  hi ".to_string()),
    ];
    assert_eq!(
        AskForm::answer(&typed_ask(), &picked, texts).unwrap(),
        json!({ "action": "accept", "content": {
            "agree": false, "keep": true, "token": "  s3 cret ", "count": 12, "ratio": 1.5, "note": "hi",
        } })
    );
}

#[test]
fn an_answer_that_cannot_go_says_why_and_a_password_of_spaces_counts() {
    use super::form::AskForm;
    let picked = serde_json::Map::new();
    let text = |name: &str, value: &str| vec![(name.to_string(), value.to_string())];
    assert_eq!(
        AskForm::answer(&typed_ask(), &picked, text("note", "hi")).unwrap_err(),
        "Token is required."
    );
    let mut texts = text("token", "   ");
    texts.extend(text("count", "1.5"));
    assert_eq!(
        AskForm::answer(&typed_ask(), &picked, texts).unwrap_err(),
        "Count must be a number."
    );
    assert_eq!(
        AskForm::answer(&typed_ask(), &picked, text("token", "   ")).unwrap()["content"]["token"],
        "   "
    );
}

#[test]
fn a_shown_answer_leaves_out_what_the_schema_hides() {
    use super::form::AskForm;
    let ask = typed_ask();
    let answer = json!({ "action": "accept", "content": { "token": "s3cret", "pin": "1", "agree": true } });
    assert_eq!(
        AskForm::shown(ask.schema.as_ref(), &answer),
        json!({ "action": "accept", "content": { "agree": true } })
    );
    assert_eq!(AskForm::shown(None, &json!("text")), json!("text"));
}
