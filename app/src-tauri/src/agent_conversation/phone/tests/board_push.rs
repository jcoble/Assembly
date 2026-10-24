//! A board card or question an agent sends with `notify` reaches the phone. It reuses the phone test
//! server in `phone/mod.rs`, which is at its size baseline.

use super::super::push::tests::{fake_fcm, test_sender};
use super::{client, paired, phone_server};
use serde_json::{json, Value};
use std::time::Duration;

#[tokio::test]
async fn notify_board_card_pushes_to_the_phone_once() {
    let (state, base) = phone_server().await;
    let token = paired(&state, &base).await;
    let set = client(reqwest::Method::POST, &base, "/push-token", &token).json(&json!({"fcmToken": "fcm-1"})).send().await.unwrap();
    assert_eq!(set.status(), reqwest::StatusCode::NO_CONTENT);
    let store = state.manager.store();
    store.upsert_session(&mcb_core::session_store::SessionRow { owned_id: "lead".into(), native_session_id: None,
        provider: "codex".into(), model: None, effort: None, cwd: "/work/assembly".into(), worktree: None, branch: None,
        title: Some("snowy-coral".into()), title_source: None, project: None, project_id: None,
        state: "ready".into(), suspended: false, created_at_ms: 1, last_activity_at_ms: 1, extra_json: "{}".into() }).unwrap();
    let fake = fake_fcm(200, "{}").await;
    super::super::spawn_push(state.clone(), test_sender(&fake));
    tokio::time::sleep(Duration::from_millis(200)).await;
    let post = |patch: Value| {
        store.board_update("deploy", "lead", false, &patch, super::super::now_ms()).unwrap();
        mcb_core::broker::messages_changed();
    };
    let sent = fake.messages.clone();
    let pushes = |count: usize| {
        let sent = sent.clone();
        async move {
            for _ in 0..200 {
                if sent.lock().unwrap().len() >= count { break; }
                tokio::time::sleep(Duration::from_millis(50)).await;
            }
            tokio::time::sleep(Duration::from_millis(300)).await;
            sent.lock().unwrap().clone()
        }
    };
    post(json!({"title": "Server down", "state": "needs_you", "summary": "The API answers 502.", "notify": true}));
    let messages = pushes(1).await;
    assert_eq!(messages.len(), 1, "{messages:?}");
    let message = &messages[0]["message"];
    assert_eq!(message["notification"]["title"], "From snowy-coral · Server down");
    assert_eq!(message["notification"]["body"], "The API answers 502.");
    assert_eq!(message["data"], json!({"machineId": message["data"]["machineId"], "kind": "turn", "sessionId": "lead", "cardKey": "deploy"}));
    assert_eq!(message["android"]["priority"], "high");
    post(json!({"summary": "Still down."}));
    assert_eq!(pushes(2).await.len(), 1, "an update without notify sends nothing");
    post(json!({"summary": "Data at risk.", "notify": true}));
    assert_eq!(pushes(2).await.len(), 2, "notify again sends again");
    store.board_question_update("q", "lead", false, &json!({"title": "Merge?", "question": "PR #1 is green.", "session": "lead", "notify": true}), super::super::now_ms()).unwrap();
    mcb_core::broker::messages_changed();
    let messages = pushes(3).await;
    assert_eq!(messages.len(), 3, "{messages:?}");
    assert_eq!(messages[2]["message"]["notification"], json!({"title": "From snowy-coral · Merge?", "body": "PR #1 is green."}));
}

#[tokio::test]
async fn desktop_notification_sends_no_phone_push() {
    let (state, base) = phone_server().await;
    let token = paired(&state, &base).await;
    let set = client(reqwest::Method::POST, &base, "/push-token", &token).json(&json!({"fcmToken": "fcm-1"})).send().await.unwrap();
    assert_eq!(set.status(), reqwest::StatusCode::NO_CONTENT);
    let fake = fake_fcm(200, "{}").await;
    super::super::spawn_push(state.clone(), test_sender(&fake));
    tokio::time::sleep(Duration::from_millis(200)).await;
    // What the desktop notifies about: a card entering Needs you and a new question, both without notify.
    let store = state.manager.store();
    store.board_update("card", "lead", true, &json!({"title": "Merge?", "state": "needs_you", "steps": ["Approve"]}), super::super::now_ms()).unwrap();
    store.board_question_update("q", "lead", true, &json!({"title": "Ship?", "question": "Ship it?"}), super::super::now_ms()).unwrap();
    mcb_core::broker::messages_changed();
    tokio::time::sleep(Duration::from_millis(800)).await;
    assert!(fake.messages.lock().unwrap().is_empty(), "a desktop notification reached the phone");
}
