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
