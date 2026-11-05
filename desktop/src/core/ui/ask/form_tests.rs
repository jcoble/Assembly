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
fn an_empty_required_field_is_named_and_a_choices_free_text_fills_it() {
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
    assert_eq!(
        AskForm::missing(&ask, &picked, &empty).as_deref(),
        Some("Note")
    );
    let typed = vec![("note".to_string(), "hi".to_string())];
    assert_eq!(AskForm::missing(&ask, &picked, &typed), None);
    picked.insert("zone".into(), json!(""));
    assert_eq!(
        AskForm::missing(&ask, &picked, &typed).as_deref(),
        Some("zone")
    );
    let other = vec![typed[0].clone(), ("zone_custom".into(), "north".into())];
    assert_eq!(AskForm::missing(&ask, &picked, &other), None);
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
    let answer =
        json!({ "action": "accept", "content": { "token": "s3cret", "pin": "1", "agree": true } });
    assert_eq!(
        AskForm::shown(ask.schema.as_ref(), &answer),
        json!({ "action": "accept", "content": { "agree": true } })
    );
    assert_eq!(AskForm::shown(None, &json!("text")), json!("text"));
}
