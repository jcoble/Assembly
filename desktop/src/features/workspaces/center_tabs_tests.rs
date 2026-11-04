use super::*;
fn key() -> WorkspaceKey {
    WorkspaceKey {
        machine: "source".into(),
        session_id: "session".into(),
        checkout_root: "/checkout".into(),
    }
}
fn history() -> Value {
    json!({"key":"git-history:session","state":{"panel_name":"Git history","children":[],"info":{"panel":{"session":"session","path":"src/λ.rs"}}}})
}
#[test]
fn actual_file_git_and_chat_interleaving_roundtrips_without_duplicating_file_payloads() {
    let file = format!(
        "document:{}",
        serde_json::to_string(&("source", "/checkout", "src/λ.rs")).unwrap()
    );
    let value = json!({"order":["Chat",file,"git-history:session"],"activeKey":"git-history:session","panes":[history()]});
    let root = json!({"version":1,"files":[{"path":"src/λ.rs","draft":"actual λ\r\n","baseRevision":"r"}],"topTabs":value});
    let parsed = parse(&root["topTabs"], &key(), &root).unwrap().unwrap();
    assert_eq!(serde_json::to_value(parsed).unwrap(), root["topTabs"]);
    assert!(
        root["topTabs"]["panes"]
            .as_array()
            .unwrap()
            .iter()
            .all(|pane| pane["state"]["panel_name"] != "File")
    );
}
#[test]
fn duplicate_unknown_missing_file_and_foreign_leaf_metadata_refuse_before_restore() {
    let missing = format!(
        "document:{}",
        serde_json::to_string(&("source", "/checkout", "missing")).unwrap()
    );
    let mut foreign = history();
    foreign["state"]["info"]["panel"]["session"] = json!("other");
    for value in [
        json!({"order":["Chat","Chat"],"activeKey":"Chat","panes":[]}),
        json!({"order":["unknown"],"activeKey":"unknown","panes":[]}),
        json!({"order":[missing],"activeKey":missing,"panes":[]}),
        json!({"order":["git-history:session"],"activeKey":"git-history:session","panes":[foreign]}),
        json!({"order":["Chat"],"activeKey":"git-history:session","panes":[history()]}),
    ] {
        assert!(parse(&value, &key(), &json!({"files":[]})).is_err());
    }
}
#[test]
fn panel_children_and_operational_or_unknown_fields_are_never_workspace_metadata() {
    for field in ["state", "info", "children"] {
        let mut pane = history();
        match field {
            "state" => {
                pane["state"]["engineState"] = json!("running");
            }
            "info" => {
                pane["state"]["info"]["panel"]["permissions"] = json!("allow");
            }
            _ => {
                pane["state"]["children"] =
                    json!([{"panel_name":"Terminal","children":[],"info":{"panel":null}}]);
            }
        }
        assert!(parse(&json!({"order":["git-history:session"],"activeKey":"git-history:session","panes":[pane]}),&key(),&json!({"files":[]})).is_err());
    }
}

#[test]
fn registered_text_fields_cannot_hide_operational_objects() {
    let value = json!({"order":["Changes"],"activeKey":"Changes","panes":[{"key":"Changes","state":{"panel_name":"Changes","children":[],"info":{"panel":{"session":"session","title":{"permissions":"allow"}}}}}]});
    assert!(parse(&value, &key(), &json!({"files":[]})).is_err());
}

#[test]
fn captured_git_source_assertions_roundtrip_but_malformed_or_operational_fields_refuse() {
    for source in [Value::Null, json!({"machine":"source","generation":7,"routeMachine":"route"})] {
        let mut pane=history();
        pane["state"]["info"]["panel"]["expectedSource"]=source;
        let value=json!({"order":["git-history:session"],"activeKey":"git-history:session","panes":[pane]});
        let decoded=parse(&value,&key(),&json!({"files":[]})).unwrap().unwrap();
        assert_eq!(serde_json::to_value(decoded).unwrap(),value);
    }
    for source in [json!("route"),json!({"machine":"source","generation":"7","routeMachine":"route"}),
        json!({"machine":"source","generation":7,"routeMachine":"route","permissions":"allow"})] {
        let mut pane=history();
        pane["state"]["info"]["panel"]["expectedSource"]=source;
        let value=json!({"order":["git-history:session"],"activeKey":"git-history:session","panes":[pane]});
        assert!(parse(&value,&key(),&json!({"files":[]})).is_err());
    }
}

fn hinted() -> Value {
    let mut pane = history();
    let source = json!({"machine":"source","generation":7,"routeMachine":"route"});
    pane["state"]["info"]["panel"]["expectedSource"] = source.clone();
    pane["state"]["info"]["panel"]["checkoutHint"] =
        json!({"session":"session","source":source,"path":"/linked","expectedRoot":"/checkout"});
    pane
}
fn tabs(pane: Value) -> Value {
    let key = pane["key"].clone();
    json!({"order":[key],"activeKey":key,"panes":[pane]})
}
#[test]
fn a_history_tab_keeps_the_checkout_hint_it_wrote_and_an_absent_hint_is_no_hint() {
    let value = tabs(hinted());
    let decoded = parse(&value, &key(), &json!({"files":[]})).unwrap().unwrap();
    assert_eq!(serde_json::to_value(decoded).unwrap(), value);
    let mut none = hinted();
    none["state"]["info"]["panel"]["checkoutHint"] = Value::Null;
    assert!(parse(&tabs(none), &key(), &json!({"files":[]})).is_ok());
}
#[test]
fn a_checkout_hint_outside_its_workspace_or_with_other_fields_is_refused() {
    let change = |field: &str, value: Value| {
        let mut pane = hinted();
        pane["state"]["info"]["panel"]["checkoutHint"][field] = value;
        pane
    };
    let other_owner = json!({"machine":"other","generation":7,"routeMachine":"route"});
    let replaced = json!({"machine":"source","generation":8,"routeMachine":"route"});
    let mut no_field = hinted();
    no_field["state"]["info"]["panel"]["checkoutHint"].as_object_mut().unwrap().remove("path");
    let mut text = hinted();
    text["state"]["info"]["panel"]["checkoutHint"] = json!("/linked");
    let mut no_pane_source = hinted();
    no_pane_source["state"]["info"]["panel"]["expectedSource"] = Value::Null;
    let mut both_elsewhere = change("source", other_owner.clone());
    both_elsewhere["state"]["info"]["panel"]["expectedSource"] = other_owner.clone();
    for pane in [
        change("permissions", json!("allow")),
        change("session", json!("other")),
        change("source", other_owner),
        both_elsewhere,
        change("source", replaced),
        change("expectedRoot", json!("/moved")),
        change("path", json!("")),
        change("path", json!("/linked\0")),
        change("path", json!("/".repeat(4097))),
        no_field,
        text,
        no_pane_source,
    ] {
        assert!(parse(&tabs(pane), &key(), &json!({"files":[]})).is_err());
    }
}
#[test]
fn only_a_history_tab_can_carry_a_checkout_hint() {
    let hint = hinted()["state"]["info"]["panel"]["checkoutHint"].clone();
    for (name, pane_key, info) in [
        ("Changes", "Changes", json!({"session":"session","checkoutHint":hint})),
        ("Pull requests", "pull-requests", json!({"session":"session","checkoutHint":hint})),
    ] {
        let pane = json!({"key":pane_key,"state":{"panel_name":name,"children":[],"info":{"panel":info}}});
        assert!(parse(&tabs(pane), &key(), &json!({"files":[]})).is_err());
    }
}
