//! Actual source keys isolate draft ownership; fixtures never contact providers or owner data.
use super::{
    Attachment, AttachmentState,
    owner_prompt::{PendingPrompt, PromptRequest},
};
use crate::core::{engine::SessionKey, socket::row_source::ExpectedRowSource};
use gpui::TestAppContext;
fn key(owner: &str) -> SessionKey {
    SessionKey {
        machine: owner.into(),
        id: "same".into(),
    }
}
fn source(owner: &str) -> ExpectedRowSource {
    ExpectedRowSource {
        machine: owner.into(),
        generation: 7,
        route_machine: Some(format!("route-{owner}")),
    }
}
#[gpui::test]
fn same_id_on_two_actual_owners_never_reuses_the_other_draft_attachment_or_pending_prompt(
    cx: &mut TestAppContext,
) {
    let (composer, client, _, cx) = super::tests::open(cx);
    // Complete A's actual owner read before the rapid switches cancel intermediate visits.
    composer.update_in(cx, |composer, window, cx| {
        composer.set_target(Some(key("a")), Some(source("a")), window, cx)
    });
    cx.run_until_parked();
    composer.update_in(cx,|composer,window,cx| {
        composer.set_target(Some(key("a")),Some(source("a")),window,cx);
        composer.draft.update(cx,|draft,cx|draft.set_value("alpha",window,cx));
        composer.attachments.push(Attachment{id:1,name:"a.png".into(),size:4,state:AttachmentState::Saved{path:"/owner-a/a.png".into()},preview:None});
        composer.pending.insert(key("a"),vec![PendingPrompt::sending(PromptRequest{text:"A pending".into(),client_id:"original-a".into(),attachment_ids:vec![],epoch:None})]);
        composer.set_target(Some(key("b")),Some(source("b")),window,cx);
        assert!(composer.draft.read(cx).value().is_empty());assert!(composer.attachments.is_empty());assert!(composer.pending("same").is_empty());
        composer.draft.update(cx,|draft,cx|draft.set_value("beta",window,cx));
        composer.set_target(Some(key("a")),Some(source("a")),window,cx);
        assert_eq!(composer.draft.read(cx).value().as_ref(),"alpha");assert_eq!(composer.attachments.len(),1);
        assert!(matches!(&composer.attachments[0].state,AttachmentState::Saved{path} if path=="/owner-a/a.png"));
        assert_eq!(composer.pending("same")[0].client_id(),"original-a");
        composer.set_target(Some(key("b")),Some(source("b")),window,cx);
        assert_eq!(composer.draft.read(cx).value().as_ref(),"beta");assert!(composer.attachments.is_empty());
    });
    cx.run_until_parked();
    assert!(client.calls().is_empty());
    let owners: Vec<_> = client
        .engine_calls()
        .iter()
        .map(|(_, args)| args["machine"].as_str().unwrap().to_string())
        .collect();
    assert!(owners.contains(&"a".into()) && owners.contains(&"b".into()));
}
#[gpui::test]
fn source_invalidation_preserves_the_typed_draft_but_blocks_send_stop_config_and_recovery(
    cx: &mut TestAppContext,
) {
    let (composer, client, _, cx) = super::tests::open(cx);
    composer.update_in(cx, |composer, window, cx| {
        composer.set_target(Some(key("a")), Some(source("a")), window, cx);
        composer
            .draft
            .update(cx, |draft, cx| draft.set_value("preserve me", window, cx));
        composer.running = Some("run-a".into());
        composer.invalidate_source(cx);
    });
    cx.run_until_parked();
    let before = (
        client.calls().len(),
        client.config_calls().len(),
        client.engine_calls().len(),
    );
    composer.update_in(cx, |composer, window, cx| {
        composer.send(window, cx);
        composer.stop(cx);
        composer.choose("model".into(), "choice".into(), cx);
        composer.recover_pending(cx);
        assert_eq!(composer.draft.read(cx).value().as_ref(), "preserve me");
        assert!(!composer.operational());
    });
    cx.run_until_parked();
    assert_eq!(
        (
            client.calls().len(),
            client.config_calls().len(),
            client.engine_calls().len()
        ),
        before
    );
    assert_eq!(client.message_subscriptions(), 0);
}

#[gpui::test]
fn pending_reads_carry_the_prepared_source_and_a_refusal_disables_writes_without_losing_the_draft(
    cx: &mut TestAppContext,
) {
    use crate::features::session::client::session_client::CallError;
    use serde_json::json;
    let (composer, client, _, cx) = super::tests::open(cx);
    client.queue_engine_answer(Err(CallError::Refused {
        code: "source_changed".into(),
        message: "Actual captured source changed".into(),
    }));
    composer.update_in(cx, |composer, window, cx| {
        composer.set_target(Some(key("a")), Some(source("a")), window, cx);
        composer
            .draft
            .update(cx, |draft, cx| draft.set_value("kept", window, cx));
    });
    cx.run_until_parked();
    let calls = client.engine_calls();
    assert_eq!(calls.len(), 1);
    assert_eq!(calls[0].0, "owner_prompts");
    assert_eq!(calls[0].1["expectedSource"], json!(source("a")));
    composer.read_with(cx, |composer, cx| {
        assert!(!composer.operational());
        assert_eq!(composer.draft.read(cx).value().as_ref(), "kept");
    });
    assert!(client.calls().is_empty());
}

#[gpui::test]
fn a_source_moving_under_the_pending_read_is_no_error_and_goes_to_the_pane(
    cx: &mut TestAppContext,
) {
    use crate::features::session::client::session_client::CallError;
    let (composer, client, _, cx) = super::tests::open(cx);
    let heard = std::rc::Rc::new(std::cell::Cell::new(false));
    let told = heard.clone();
    let _listening = cx.update(|_, cx| {
        cx.subscribe(&composer, move |_, event: &super::ComposerEvent, _| {
            if matches!(event, super::ComposerEvent::SourceLost) {
                told.set(true);
            }
        })
    });
    client.queue_engine_answer(Err(CallError::Refused {
        code: "source_changed".into(),
        message: "The selected row source changed.".into(),
    }));
    composer.update_in(cx, |composer, window, cx| {
        composer.set_target(Some(key("a")), Some(source("a")), window, cx);
    });
    cx.run_until_parked();
    composer.read_with(cx, |composer, _| {
        assert!(!composer.operational());
        assert_eq!(composer.error, None);
    });
    assert!(heard.get());
}

#[gpui::test]
fn prompt_stop_and_config_keep_the_prepared_source_and_refusal_never_retries_unqualified(
    cx: &mut TestAppContext,
) {
    use crate::features::session::client::session_client::CallError;
    use serde_json::json;
    let (composer, client, _, cx) = super::tests::open(cx);
    composer.update_in(cx, |composer, window, cx| {
        composer.set_target(Some(key("a")), Some(source("a")), window, cx);
        composer.running = Some("actual-run".into());
        composer.stop(cx);
    });
    cx.run_until_parked();
    let stop = client
        .calls()
        .into_iter()
        .find(|(method, _)| method == "send/stop")
        .unwrap()
        .1;
    assert_eq!(
        stop,
        json!({"runId":"actual-run","session":"same","expectedSource":source("a")})
    );
    assert!(
        client
            .config_calls()
            .iter()
            .all(|(_, args)| args["expectedSource"] == json!(source("a")))
    );
    client.queue_answer(Err(CallError::Refused {
        code: "source_changed".into(),
        message: "Captured owner replaced".into(),
    }));
    composer.update_in(cx, |composer, window, cx| {
        composer.draft.update(cx, |draft, cx| {
            draft.set_value("keep exact draft", window, cx)
        });
        composer.send(window, cx);
    });
    cx.run_until_parked();
    let sent = client
        .calls()
        .into_iter()
        .filter(|(method, _)| method == "send/send")
        .collect::<Vec<_>>();
    assert_eq!(sent.len(), 1);
    assert_eq!(sent[0].1["expectedSource"], json!(source("a")));
    composer.update_in(cx, |composer, window, cx| {
        assert!(!composer.operational());
        assert_eq!(composer.draft.read(cx).value().as_ref(), "keep exact draft");
        composer.send(window, cx);
        composer.stop(cx);
    });
    cx.run_until_parked();
    assert_eq!(
        client
            .calls()
            .iter()
            .filter(|(method, _)| method == "send/send")
            .count(),
        1
    );
}
