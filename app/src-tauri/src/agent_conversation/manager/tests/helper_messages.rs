use super::*;

async fn busy_helper(kind: &str, provider: AgentConversationProvider) -> FixtureManager {
    let fixture = fixture_manager_with_provider(kind, provider, None).await;
    {
        let mut sessions = fixture.manager.sessions.lock().unwrap();
        let session = sessions.get_mut(&fixture.owned_id).unwrap();
        session.helper_of = Some("parent".into());
        session.active_turn_id = Some("turn-running".into());
        session.state = AgentRuntimeState::Working;
        persist_session(session).unwrap();
    }
    fixture
}

#[tokio::test(flavor = "current_thread")]
async fn an_orchestrator_message_steers_a_busy_helper() {
    for provider in [AgentConversationProvider::Codex, AgentConversationProvider::Claude] {
        let fixture = busy_helper("steering", provider).await;
        let message = fixture.manager.store.append_message("", "parent", &fixture.owned_id,
            mcb_core::broker::MessageKind::Message, "change direction").unwrap();
        fixture.manager.deliver_if_idle(&fixture.owned_id).await;
        assert_eq!(fixture.manager.store.message_status(&message.id, "parent").unwrap(), "delivered");
        let log = fs::read_to_string(fixture.root.join("steering.jsonl")).unwrap();
        assert!(log.contains(r#""method":"_session/steering""#));
        assert!(!log.contains(r#""method":"session/prompt""#));
        let events = fixture.manager.list_events(&fixture.owned_id, 0).unwrap();
        assert!(events.iter().any(|event| matches!(&event.payload,
            AgentConversationPayload::UserMessage { text, .. }
            if text == "[message from parent]\nchange direction")));
        fixture.manager.close(&fixture.owned_id, fixture.generation).await.unwrap();
        fs::remove_dir_all(fixture.root).unwrap();
    }
}

#[tokio::test(flavor = "current_thread")]
async fn a_message_from_another_session_waits_for_the_helper_turn_to_end() {
    let fixture = busy_helper("steering", AgentConversationProvider::Codex).await;
    let message = fixture.manager.store.append_message("", "unrelated", &fixture.owned_id,
        mcb_core::broker::MessageKind::Message, "wait for me").unwrap();
    fixture.manager.deliver_if_idle(&fixture.owned_id).await;
    assert_eq!(fixture.manager.store.message_status(&message.id, "unrelated").unwrap(), "queued");
    assert!(!fs::read_to_string(fixture.root.join("steering.jsonl")).unwrap().contains("_session/steering"));
    fixture.manager.close(&fixture.owned_id, fixture.generation).await.unwrap();
    fs::remove_dir_all(fixture.root).unwrap();
}

#[tokio::test(flavor = "current_thread")]
async fn orchestrator_steering_passes_older_unrelated_messages_and_drains_the_queue() {
    let fixture = busy_helper("steering", AgentConversationProvider::Codex).await;
    let store = &fixture.manager.store;
    let unrelated = store.append_message("", "unrelated", &fixture.owned_id,
        mcb_core::broker::MessageKind::Message, "later").unwrap();
    let first = store.append_message("", "parent", &fixture.owned_id,
        mcb_core::broker::MessageKind::Message, "one").unwrap();
    let second = store.append_message("", "parent", &fixture.owned_id,
        mcb_core::broker::MessageKind::Message, "two").unwrap();
    fixture.manager.deliver_if_idle(&fixture.owned_id).await;
    assert_eq!(store.message_status(&unrelated.id, "unrelated").unwrap(), "queued");
    for message in [first, second] {
        assert_eq!(store.message_status(&message.id, "parent").unwrap(), "delivered");
    }
    assert_eq!(fs::read_to_string(fixture.root.join("steering.jsonl")).unwrap()
        .matches(r#""method":"_session/steering""#).count(), 2);
    fixture.manager.close(&fixture.owned_id, fixture.generation).await.unwrap();
    fs::remove_dir_all(fixture.root).unwrap();
}

#[tokio::test(flavor = "current_thread")]
async fn refused_or_unsupported_helper_steering_stays_queued() {
    for kind in ["steering_idle", "steering_failed", "multiplex"] {
        let fixture = busy_helper(kind, AgentConversationProvider::Codex).await;
        let message = fixture.manager.store.append_message("", "parent", &fixture.owned_id,
            mcb_core::broker::MessageKind::Message, "keep queued").unwrap();
        fixture.manager.deliver_if_idle(&fixture.owned_id).await;
        assert_eq!(fixture.manager.store.message_status(&message.id, "parent").unwrap(), "queued");
        assert!(!fs::read_to_string(fixture.root.join(format!("{kind}.jsonl"))).unwrap()
            .contains(r#""method":"session/prompt""#));
        fixture.manager.close(&fixture.owned_id, fixture.generation).await.unwrap();
        fs::remove_dir_all(fixture.root).unwrap();
    }
}

#[tokio::test(flavor = "current_thread")]
async fn a_message_during_an_active_turn_uses_session_steering() {
    let fixture = fixture_manager_with_acp_session("steering").await;
    {
        let mut sessions = fixture.manager.sessions.lock().unwrap();
        let session = sessions.get_mut(&fixture.owned_id).unwrap();
        session.active_turn_id = Some("turn-running".into());
        session.state = AgentRuntimeState::Working;
    }

    let receipt = fixture
        .manager
        .prompt(
            &fixture.owned_id,
            fixture.generation,
            test_prompt("change direction"),
        )
        .await
        .expect("steer active turn");

    let events = fixture.manager.list_events(&fixture.owned_id, 0).unwrap();
    assert_eq!(receipt.owned_id, fixture.owned_id);
    assert_eq!(receipt.generation, fixture.generation);
    assert_eq!(receipt.turn_id, "turn-running");
    assert!(receipt.user_item_id.starts_with("user-steer-"));
    let admitted = events.iter().find(|event| event.sequence == receipt.admitted_sequence).unwrap();
    assert_eq!(admitted.turn_id.as_deref(), Some(receipt.turn_id.as_str()));
    assert!(matches!(&admitted.payload,
        AgentConversationPayload::UserMessage { item_id, text, .. }
            if item_id == &receipt.user_item_id && text == "change direction"));
    assert!(!events
        .iter()
        .any(|event| matches!(event.payload, AgentConversationPayload::Turn { .. })));
    let log = fs::read_to_string(fixture.root.join("steering.jsonl")).unwrap();
    assert!(log.contains("\"method\":\"_session/steering\""));
    assert!(log.contains("\"idleBehavior\":\"promptRequired\""));

    fixture
        .manager
        .close(&fixture.owned_id, fixture.generation)
        .await
        .unwrap();
    fs::remove_dir_all(fixture.root).unwrap();
}

#[tokio::test(flavor = "current_thread")]
async fn steering_receipt_keeps_the_original_turn_after_completion() {
    let root = temp_root();
    let log = root.join("steering.jsonl");
    let mut manifest = crate::agent_conversation::providers::acp_client::tests::fixture_manifest_named(&log, "steering");
    manifest.args[1] = manifest.args[1].replace("outcome=injected",
        "outcome=injected\n      while [ ! -f \"$log.steer-ack\" ]; do sleep 0.01; done");
    let manager = AgentRuntimeManager::new(ProviderRegistry::new([
        (AgentConversationProvider::Codex, manifest),
    ]).unwrap());
    let owned_id = "owned-steering-completed";
    let generation = manager.ensure_inner(request(root.to_str().unwrap(), owned_id,
        AgentConversationProvider::Codex)).unwrap().0.generation;
    manager.activate(owned_id, generation).await.unwrap();
    {
        let mut sessions = manager.sessions.lock().unwrap();
        let session = sessions.get_mut(owned_id).unwrap();
        session.active_turn_id = Some("turn-original".into());
        session.state = AgentRuntimeState::Working;
    }
    let sender = manager.clone();
    let pending = tokio::spawn(async move {
        sender.prompt(owned_id, generation, test_prompt("late steering ack")).await
    });
    wait_until(|| fs::read_to_string(&log).unwrap_or_default().contains("_session/steering")).await;
    {
        let mut sessions = manager.sessions.lock().unwrap();
        let session = sessions.get_mut(owned_id).unwrap();
        record_payload_for_session_and_dispatch(session, &manager.emitter,
            AgentConversationPayload::Turn {
                turn_id: "turn-original".into(),
                state: crate::agent_conversation::protocol::TurnState::Completed,
            }).unwrap();
        session.active_turn_id = None;
        session.state = AgentRuntimeState::Ready;
    }
    fs::write(root.join("steering.jsonl.steer-ack"), "release").unwrap();
    let receipt = tokio::time::timeout(Duration::from_secs(5), pending).await.unwrap().unwrap().unwrap();
    assert_eq!(receipt.turn_id, "turn-original");
    let stored = manager.list_events(owned_id, receipt.admitted_sequence).unwrap();
    let admitted = stored.first().unwrap();
    assert_eq!(admitted.turn_id.as_deref(), Some("turn-original"));
    assert!(matches!(&admitted.payload, AgentConversationPayload::UserMessage { item_id, .. }
        if item_id == &receipt.user_item_id));
    assert_eq!(manager.sessions.lock().unwrap().get(owned_id).unwrap().active_turn_id, None);
    manager.close(owned_id, generation).await.unwrap();
    fs::remove_dir_all(root).unwrap();
}

#[tokio::test(flavor = "current_thread")]
async fn steering_not_consumed_preserves_the_existing_turn_and_journal() {
    for kind in ["steering_idle", "steering_failed"] {
        let fixture = fixture_manager_with_acp_session(kind).await;
        {
            let mut sessions = fixture.manager.sessions.lock().unwrap();
            let session = sessions.get_mut(&fixture.owned_id).unwrap();
            session.active_turn_id = Some("turn-running".into());
            session.state = AgentRuntimeState::Working;
        }
        let result = fixture
            .manager
            .prompt(
                &fixture.owned_id,
                fixture.generation,
                test_prompt("keep my correction"),
            )
            .await;
        assert!(
            result.is_err(),
            "unconsumed input must return to draft recovery"
        );
        let events = fixture.manager.list_events(&fixture.owned_id, 0).unwrap();
        assert!(!events.iter().any(|event| matches!(&event.payload, AgentConversationPayload::UserMessage { text, .. } if text == "keep my correction")));
        assert_eq!(
            fixture
                .manager
                .sessions
                .lock()
                .unwrap()
                .get(&fixture.owned_id)
                .unwrap()
                .active_turn_id
                .as_deref(),
            Some("turn-running")
        );
        let wire = fs::read_to_string(fixture.root.join(format!("{kind}.jsonl"))).unwrap();
        assert!(wire.contains("_session/steering"));
        assert!(!wire.contains("\"method\":\"session/prompt\""));
        fixture
            .manager
            .close(&fixture.owned_id, fixture.generation)
            .await
            .unwrap();
        fs::remove_dir_all(fixture.root).unwrap();
    }
}


#[tokio::test(flavor = "current_thread")]
async fn sibling_steering_uses_the_same_authority_and_delivery_path() {
    let fixture = busy_helper("steering", AgentConversationProvider::Codex).await;
    let mut row = fixture.manager.store.get_session(&fixture.owned_id).unwrap().unwrap();
    row.owned_id = "sibling".into(); row.title = Some("Sibling".into());
    fixture.manager.store.upsert_session(&row).unwrap();
    let message = fixture.manager.store.append_message("", "sibling", &fixture.owned_id,
        mcb_core::broker::MessageKind::Message, "agent advice").unwrap();
    fixture.manager.deliver_if_idle(&fixture.owned_id).await;
    assert_eq!(fixture.manager.store.message_status(&message.id, "sibling").unwrap(), "delivered");
    let events = fixture.manager.list_events(&fixture.owned_id, 0).unwrap();
    assert!(events.iter().any(|event| matches!(&event.payload,
        AgentConversationPayload::UserMessage { text, .. }
        if text.starts_with("[message from ") && text.ends_with(" · Sibling]\nagent advice"))));
    fixture.manager.close(&fixture.owned_id, fixture.generation).await.unwrap();
    fs::remove_dir_all(fixture.root).unwrap();
}

#[tokio::test(flavor = "current_thread")]
async fn a_sibling_copy_waits_for_the_orchestrator_turn_to_end() {
    let fixture = busy_helper("steering", AgentConversationProvider::Codex).await;
    for id in ["sender-helper", "recipient-helper"] {
        let mut row = fixture.manager.store.get_session(&fixture.owned_id).unwrap().unwrap();
        row.owned_id = id.into(); row.title = Some(id.into());
        row.extra_json = json!({"helperOf":fixture.owned_id}).to_string();
        fixture.manager.store.upsert_session(&row).unwrap();
    }
    fixture.manager.store.append_message("", "sender-helper", "recipient-helper",
        mcb_core::broker::MessageKind::Message, "copy to parent").unwrap();
    fixture.manager.deliver_if_idle(&fixture.owned_id).await;
    let copy = fixture.manager.store.pending_for(&fixture.owned_id).unwrap().unwrap();
    assert!(copy.body.starts_with("[copy: "));
    assert_eq!(copy.receipt, mcb_core::broker::Receipt::Queued);
    assert!(!fs::read_to_string(fixture.root.join("steering.jsonl")).unwrap().contains("_session/steering"));
    fixture.manager.close(&fixture.owned_id, fixture.generation).await.unwrap();
    fs::remove_dir_all(fixture.root).unwrap();
}

#[tokio::test(flavor = "current_thread")]
async fn queued_message_delivers_when_session_goes_quiescent() {
    let fixture = fixture_manager_with_acp_session("queued_message_feed").await;
    let message = fixture
        .manager
        .store
        .append_message(
            "",
            "worker-a",
            &fixture.owned_id,
            mcb_core::broker::MessageKind::Message,
            "Review the patch",
        )
        .expect("queue message");
    let prompts = || {
        fs::read_to_string(fixture.root.join("queued_message_feed.jsonl"))
            .unwrap()
            .matches(r#""method":"session/prompt""#)
            .count()
    };
    let receipt = || {
        fixture.manager.store.pending_for(&fixture.owned_id).unwrap().map(|pending| pending.id)
    };

    fixture
        .manager
        .prompt(&fixture.owned_id, fixture.generation, test_prompt("finish current turn"))
        .await
        .expect("prompt starts");
    let end_first_turn = prompt_result(&fixture).await;
    fixture.manager.deliver_if_idle(&fixture.owned_id).await;
    assert_eq!(receipt(), Some(message.id.clone()), "a busy session keeps the message queued");
    assert_eq!(prompts(), 1);

    feed(&fixture, &[end_first_turn]);
    wait_until(|| prompts() == 2).await;
    wait_until(|| receipt().is_none()).await;
    assert_eq!(fixture.manager.store.message_status(&message.id, "worker-a").unwrap(), "delivered");
    let expected = "[message from worker-a]\nReview the patch";
    let snapshot = fixture
        .manager
        .snapshot(&fixture.owned_id)
        .expect("read snapshot")
        .expect("session exists");
    assert_eq!(snapshot.events.iter().filter(|event| matches!(
        &event.payload,
        AgentConversationPayload::UserMessage { text, .. } if text == expected
    )).count(), 1);

    fixture
        .manager
        .close(&fixture.owned_id, fixture.generation)
        .await
        .unwrap();
    fs::remove_dir_all(fixture.root).unwrap();
}

/// A model that names the session and then explains itself must not put the
/// explanation on the rail: only the first line is the name.
#[test]
fn a_helper_title_keeps_only_the_first_line_of_the_answer() {
    assert_eq!(
        helper_title("Ping Pong Echo Test\n\nThe person asked to reply with PONG").as_deref(),
        Some("Ping Pong Echo Test")
    );
    assert_eq!(helper_title("\n\n  \"Fix rail titles\"  \nWhy").as_deref(), Some("Fix rail titles"));
}
