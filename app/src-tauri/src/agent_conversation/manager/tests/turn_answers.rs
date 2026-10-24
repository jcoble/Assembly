//! TSK-1167: what a chat turn sends back to the agent when the user answers a
//! permission or a question, steers a running turn, or sends a screenshot.
//! Each test pins behaviour that works today so a change that breaks it fails.
use super::*;

fn offered(options: &[(&str, &str)]) -> Vec<PermissionOption> {
    options
        .iter()
        .map(|(option_id, kind)| PermissionOption {
            option_id: (*option_id).into(),
            kind: (*kind).into(),
            name: (*option_id).into(),
        })
        .collect()
}

fn selected(option_id: &str) -> Value {
    json!({ "outcome": { "outcome": "selected", "optionId": option_id } })
}

// Protects 4cad8925c (valid approval responses): Allow answers with an allow
// option even when the agent lists Reject first, and Deny never falls back to
// an allow option.
#[test]
fn allow_and_deny_answer_with_the_matching_kind_of_option() {
    let options = offered(&[("no", "reject_once"), ("yes-always", "allow_always")]);
    assert_eq!(permission_response(&options, AgentApprovalDecision::Accept).unwrap(), selected("yes-always"));
    assert_eq!(permission_response(&options, AgentApprovalDecision::Decline).unwrap(), selected("no"));
    assert_eq!(
        permission_response(&options, AgentApprovalDecision::Cancel).unwrap(),
        json!({ "outcome": { "outcome": "cancelled" } })
    );
    let allow_only = offered(&[("yes", "allow_once")]);
    assert!(permission_response(&allow_only, AgentApprovalDecision::Decline).is_err(), "Deny must not allow");
    let unnamed = offered(&[("first", "custom"), ("second", "custom")]);
    assert_eq!(permission_response(&unnamed, AgentApprovalDecision::Accept).unwrap(), selected("first"));
    assert!(permission_response(&[], AgentApprovalDecision::Accept).is_err());
}

// A policy "allow" must never write a standing rule into the agent's settings.
#[test]
fn a_policy_allow_picks_allow_once_and_never_allow_always() {
    use crate::workflow::policy::Decision;
    // The option lists the Claude and Codex adapters send.
    let claude = offered(&[("allow_always", "allow_always"), ("allow", "allow_once"), ("reject", "reject_once")]);
    let codex = offered(&[("approved-for-session", "allow_always"), ("approved", "allow_once"), ("abort", "reject_once")]);
    assert_eq!(policy_answer(Decision::Allow, &claude), Some(selected("allow")));
    assert_eq!(policy_answer(Decision::Allow, &codex), Some(selected("approved")));
    assert_eq!(policy_answer(Decision::Deny, &claude), Some(selected("reject")));
    let only_always = offered(&[("allow_always", "allow_always"), ("reject", "reject_once")]);
    assert_eq!(policy_answer(Decision::Allow, &only_always), None, "a person decides");
    assert_eq!(policy_answer(Decision::Ask, &claude), None);
}

#[test]
fn a_chosen_option_is_sent_as_chosen_and_recorded_as_allowed_or_declined() {
    let options = offered(&[("no-always", "reject_always"), ("yes-always", "allow_always")]);
    let choose = |option_id: &str| PermissionSelection::Option(option_id.into());
    assert_eq!(permission_response_for_selection(&options, &choose("no-always")).unwrap(), selected("no-always"));
    assert!(permission_response_for_selection(&options, &choose("not-offered")).is_err());
    assert_eq!(permission_state(&options, &choose("no-always")), ApprovalState::Declined);
    assert_eq!(permission_state(&options, &choose("yes-always")), ApprovalState::Accepted);
    assert_eq!(permission_state(&options, &choose("not-offered")), ApprovalState::Declined);
    assert_eq!(
        permission_state(&options, &PermissionSelection::Decision(AgentApprovalDecision::Cancel)),
        ApprovalState::Declined
    );
}

// Protects dda06a519: an answer to a question goes back in the shape the agent
// asked in, and a question that cannot be declined refuses a decline.
#[test]
fn question_answers_go_back_in_the_shape_the_agent_asked_in() {
    let content = BTreeMap::from([("channel".to_string(), json!("stable"))]);
    let answer = |shape, action| user_input_response(shape, action, content.clone());
    assert_eq!(
        answer(UserInputResponseShape::Legacy, AgentUserInputAction::Accept).unwrap(),
        json!({ "values": { "channel": "stable" }, "cancelled": false })
    );
    assert_eq!(
        answer(UserInputResponseShape::Legacy, AgentUserInputAction::Cancel).unwrap(),
        json!({ "values": {}, "cancelled": true })
    );
    assert!(answer(UserInputResponseShape::Legacy, AgentUserInputAction::Decline).is_err());
    assert_eq!(
        answer(UserInputResponseShape::Elicitation, AgentUserInputAction::Accept).unwrap(),
        json!({ "action": "accept", "content": { "channel": "stable" } })
    );
    assert_eq!(answer(UserInputResponseShape::Elicitation, AgentUserInputAction::Decline).unwrap(), json!({ "action": "decline" }));
    assert_eq!(answer(UserInputResponseShape::Elicitation, AgentUserInputAction::Cancel).unwrap(), json!({ "action": "cancel" }));
}

// Protects 41477d87b: a second message during a running turn on an adapter
// that cannot steer is refused, and nothing is written as if it had been sent.
#[tokio::test(flavor = "current_thread")]
async fn a_message_during_a_turn_the_adapter_cannot_steer_is_refused_and_not_recorded() {
    let fixture = fixture_manager_with_acp_session("prompt_with_update").await;
    {
        let mut sessions = fixture.manager.sessions.lock().unwrap();
        let session = sessions.get_mut(&fixture.owned_id).unwrap();
        session.active_turn_id = Some("turn-running".into());
        session.state = AgentRuntimeState::Working;
    }
    let error = fixture.manager.prompt(&fixture.owned_id, fixture.generation, test_prompt("one more thing")).await.unwrap_err();
    assert!(error.contains("does not support steering"), "{error}");
    assert!(!fixture.manager.list_events(&fixture.owned_id, 0).unwrap().iter()
        .any(|event| matches!(event.payload, AgentConversationPayload::UserMessage { .. })));
    let log = fs::read_to_string(fixture.root.join("prompt_with_update.jsonl")).unwrap();
    assert!(!log.contains("session/prompt") && !log.contains("_session/steering"), "{log}");
    fixture.manager.close(&fixture.owned_id, fixture.generation).await.unwrap();
    fs::remove_dir_all(fixture.root).unwrap();
}

// Protects e3eca0e50: a sent screenshot is remembered by the message that
// sent it, whether that message started a turn or steered one.
#[tokio::test(flavor = "current_thread")]
async fn a_sent_screenshot_stays_on_the_message_that_sent_it() {
    for (fixture_name, steering) in [("prompt_with_update", false), ("steering", true)] {
        let fixture = fixture_manager_with_acp_session(fixture_name).await;
        if steering {
            let mut sessions = fixture.manager.sessions.lock().unwrap();
            let session = sessions.get_mut(&fixture.owned_id).unwrap();
            session.active_turn_id = Some("turn-running".into());
            session.state = AgentRuntimeState::Working;
        }
        let mut prompt = test_prompt("look at this");
        prompt.attachment_ids = vec!["shot-1".into()];
        let receipt = fixture.manager.prompt(&fixture.owned_id, fixture.generation, prompt).await.expect("sent");
        let events = fixture.manager.list_events(&fixture.owned_id, receipt.admitted_sequence).unwrap();
        assert!(matches!(&events[0].payload,
            AgentConversationPayload::UserMessage { item_id, attachment_ids, .. }
                if item_id == &receipt.user_item_id && attachment_ids == &vec!["shot-1".to_string()]),
            "{fixture_name}: {:?}", events[0].payload);
        fixture.manager.close(&fixture.owned_id, fixture.generation).await.unwrap();
        fs::remove_dir_all(fixture.root).unwrap();
    }
}
