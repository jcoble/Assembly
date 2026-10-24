use super::*;

fn siblings() -> Arc<SessionStore> {
    let store = hierarchy();
    for (id, parent) in [("help2", "top1"), ("other-helper", "top2")] {
        let mut row = store.get_session("help1").unwrap().unwrap();
        row.owned_id = id.into();
        row.title = Some(id.into());
        row.extra_json = json!({"helperOf":parent}).to_string();
        store.upsert_session(&row).unwrap();
    }
    store
}

#[tokio::test]
async fn a_helper_can_message_its_sibling() {
    let store = siblings();
    assert_eq!(reachable(&store, "help1"), ["help2", "top1", "w3"]);
    let (endpoint, tokens, carried) = relayed(&store, &["help1", "help2"]);
    let handle = store.directory_name("help2").unwrap().unwrap().0;
    endpoint.handle(&tokens[0], send(&handle)).await.unwrap();
    endpoint.handle(&tokens[1], send("help1")).await.unwrap();
    let message = store.pending_for("help2").unwrap().unwrap();
    assert_eq!(message.from_agent, "help1");
    assert!(message.sender_name.ends_with(" · help1"));
    assert_eq!(message.body, "hello");
    assert_eq!(store.pending_for("help1").unwrap().unwrap().from_agent, "help2");
    assert!(carried.lock().unwrap().is_empty());
}

#[tokio::test]
async fn the_orchestrator_gets_a_copy_of_a_sibling_message() {
    let store = siblings();
    let (endpoint, tokens, _) = relayed(&store, &["help1"]);
    endpoint.handle(&tokens[0], send("help2")).await.unwrap();
    let copy = store.pending_for("top1").unwrap().unwrap();
    let label = |id| { let (handle, name) = store.directory_name(id).unwrap().unwrap(); format!("{handle} · {name}") };
    assert_eq!(copy.from_agent, "help1");
    assert_eq!(copy.sender_name, label("help1"));
    assert_eq!(copy.body, format!("[copy: {} to {}]\nhello", label("help1"), label("help2")));
    assert_eq!(copy.receipt, mcb_core::broker::Receipt::Queued);
    let messages = store.list_messages(&TrafficQuery { limit: 10, ..Default::default() }).unwrap();
    assert_eq!(messages.len(), 2, "one original and exactly one parent copy");
}

#[tokio::test]
async fn sibling_messages_stop_after_the_pair_cap() {
    let store = siblings();
    let (endpoint, tokens, _) = relayed(&store, &["help1", "help2"]);
    for _ in 0..20 {
        endpoint.handle(&tokens[0], send("help2")).await.unwrap();
        endpoint.handle(&tokens[1], send("help1")).await.unwrap();
    }
    for (token, to) in [(&tokens[0], "help2"), (&tokens[1], "help1")] {
        assert_eq!(endpoint.handle(token, send(to)).await.unwrap_err(),
            "Too many messages between these two helpers; send it through your orchestrator.");
    }
    assert_eq!(store.list_messages(&TrafficQuery { limit: 100, ..Default::default() }).unwrap().len(), 80);
    // The cap is per pair, and a full pair can still reach its parent.
    endpoint.handle(&tokens[0], send("top1")).await.unwrap();
    let mut row = store.get_session("help2").unwrap().unwrap();
    row.owned_id = "help3".into(); row.title = Some("help3".into());
    store.upsert_session(&row).unwrap();
    endpoint.handle(&tokens[0], send("help3")).await.unwrap();
}

#[tokio::test]
async fn a_helper_cannot_message_another_orchestrators_helper() {
    let store = siblings();
    let (endpoint, tokens, carried) = relayed(&store, &["help1"]);
    let handle = store.directory_name("other-helper").unwrap().unwrap().0;
    for to in ["other-helper", handle.as_str()] {
        assert_eq!(endpoint.handle(&tokens[0], send(to)).await.unwrap_err(), "Unknown session name");
    }
    assert!(store.pending_for("other-helper").unwrap().is_none());
    assert!(carried.lock().unwrap().is_empty());
}
