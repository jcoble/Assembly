//! The actual pane recovery path with a pre-provider user row and an authoritative held message.
use super::{jump, open, page, row, show};
use base64::Engine as _;
use gpui::TestAppContext;
use serde_json::json;

#[gpui::test]
fn reopening_auth_refusal_keeps_one_user_row_and_follows_the_owned_message(
    cx: &mut TestAppContext,
) {
    let (pane, client, cx) = open(cx);
    show(&pane, "s1", cx);
    let digest = "a".repeat(64);
    client.queue_engine_answer(Ok(json!({"engineMachine":"owner","items":[{
        "clientId":"c","contentSha256":digest,"textPreview":"exact body",
        "textComplete":true,"textUtf8Bytes":10,"attachmentJsonUtf8Bytes":2,
        "targetEpoch":null,"state":"queued","version":3}],"nextCreatedMs":null,"nextClientId":null})));
    client.queue_engine_answer(Ok(json!({"contentSha256":digest,"part":"attachments","offset":0,
        "totalBytes":2,"data":base64::engine::general_purpose::STANDARD.encode(b"[]"),"nextOffset":null})));
    client.queue_answer(Ok(page(
        vec![row(
            6,
            "user",
            None,
            json!({"text":"exact body","clientId":"c","authBlocker":{"messageId":"prompt/s1/c"}}),
        )],
        false,
        None,
    )));
    jump(&client, 6, cx);
    assert_eq!(client.message_subscriptions(), 1);
    let snapshot = json!({"machine":"owner","entityKind":"message","id":"prompt/s1/c",
        "clientId":"c","toSessionId":"s1","version":3,"state":"queued","reason":"auth_required"});
    cx.update(|_, cx| client.push_message("prompt/s1/c", None, snapshot.clone(), cx));
    cx.run_until_parked();
    pane.read_with(cx, |pane, cx| {
        assert_eq!(pane.view.items.len(), 1);
        assert_eq!(pane.view.items[0].key, "seq:6");
        assert_eq!(pane.view.items[0].run_id.as_deref(), Some("r1"));
        assert_eq!(pane.view.pending["seq:6"], "queued: auth_required");
        assert_eq!(
            pane.composer.read(cx).pending("s1")[0].state,
            "queued: auth_required"
        );
    });
    for (field, value) in [
        ("clientId", json!("other")),
        ("toSessionId", json!("other")),
        ("version", json!(2)),
    ] {
        let mut stale = snapshot.clone();
        stale[field] = value;
        stale["state"] = json!("next_turn");
        cx.update(|_, cx| client.push_message("prompt/s1/c", None, stale, cx));
    }
    cx.run_until_parked();
    pane.read_with(cx, |pane, _| {
        assert_eq!(pane.view.pending["seq:6"], "queued: auth_required")
    });
    let mut delivered = snapshot;
    delivered["version"] = json!(4);
    delivered["state"] = json!("next_turn");
    delivered["reason"] = json!(null);
    cx.update(|_, cx| client.push_message("prompt/s1/c", None, delivered, cx));
    cx.run_until_parked();
    pane.read_with(cx, |pane, cx| {
        assert_eq!(pane.view.items.len(), 1);
        assert!(pane.view.pending.is_empty());
        assert!(pane.composer.read(cx).pending("s1").is_empty());
    });
    assert_eq!(client.message_subscriptions(), 0);
    assert!(
        client
            .calls()
            .iter()
            .all(|(method, _)| method != "send/send")
    );
}
