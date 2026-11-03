//! Actual source keys isolate draft ownership; fixtures never contact providers or owner data.
use gpui::TestAppContext;
use crate::core::{engine::SessionKey,socket::row_source::ExpectedRowSource};
use super::{Composer,Attachment,AttachmentState,owner_prompt::{PendingPrompt,PromptRequest}};
fn key(owner:&str)->SessionKey {SessionKey{machine:owner.into(),id:"same".into()}}
fn source(owner:&str)->ExpectedRowSource {ExpectedRowSource{machine:owner.into(),generation:7,route_machine:Some(format!("route-{owner}"))}}
#[gpui::test]
fn same_id_on_two_actual_owners_never_reuses_the_other_draft_attachment_or_pending_prompt(cx:&mut TestAppContext) {
    let(composer,client,_,cx)=super::tests::open(cx);
    composer.update_in(cx,|composer,window,cx| {
        composer.set_target(Some(key("a")),Some(source("a")),window,cx);
        composer.draft.update(cx,|draft,cx|draft.set_value("alpha",window,cx));
        composer.attachments.push(Attachment{id:1,name:"a.png".into(),size:4,state:AttachmentState::Saved{path:"/owner-a/a.png".into()}});
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
    cx.run_until_parked();assert!(client.calls().is_empty());
    let owners:Vec<_>=client.engine_calls().iter().map(|(_,args)|args["machine"].as_str().unwrap().to_string()).collect();
    assert!(owners.contains(&"a".into())&&owners.contains(&"b".into()));
}
#[gpui::test]
fn source_invalidation_preserves_the_typed_draft_but_blocks_send_stop_config_and_recovery(cx:&mut TestAppContext) {
    let(composer,client,_,cx)=super::tests::open(cx);
    composer.update_in(cx,|composer,window,cx| {
        composer.set_target(Some(key("a")),Some(source("a")),window,cx);
        composer.draft.update(cx,|draft,cx|draft.set_value("preserve me",window,cx));
        composer.running=Some("run-a".into());composer.invalidate_source(cx);
    });
    cx.run_until_parked();let before=(client.calls().len(),client.config_calls().len(),client.engine_calls().len());
    composer.update_in(cx,|composer,window,cx| {
        composer.send(window,cx);composer.stop(cx);composer.choose("model".into(),"choice".into(),cx);composer.recover_pending(cx);
        assert_eq!(composer.draft.read(cx).value().as_ref(),"preserve me");assert!(!composer.operational());
    });
    cx.run_until_parked();assert_eq!((client.calls().len(),client.config_calls().len(),client.engine_calls().len()),before);
    assert_eq!(client.message_subscriptions(),0);
}
