//! Pre-session input owns local bytes only. No create/upload/config/provider work is performed here.
use gpui::{AppContext as _,TestAppContext};
use gpui_kit::{ClipboardItem,Image,ImageFormat};
use std::{cell::RefCell,rc::Rc,sync::Arc};
use super::{Composer,ComposerEvent,AttachmentState};
use crate::features::session::client::fake_client::FakeClient;
fn image()->Image {
    use base64::Engine as _;
    Image::from_bytes(ImageFormat::Png,base64::engine::general_purpose::STANDARD.decode("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/l9sAAAAASUVORK5CYII=").unwrap())
}
#[gpui::test]
fn image_only_start_preserves_one_staged_payload_across_refusal_and_unknown_create(cx:&mut TestAppContext) {
    cx.update(gpui_kit::init);let client=FakeClient::default();let fake=client.clone();
    let(composer,cx)=cx.add_window_view(move|window,cx|Composer::new_start_draft(Rc::new(fake),window,cx));
    let requests=Rc::new(RefCell::new(Vec::new()));let observed=requests.clone();
    let _events=cx.update(|_,cx|cx.subscribe(&composer,move|_,event:&ComposerEvent,_|{
        if let ComposerEvent::StartRequested{text,images}=event {observed.borrow_mut().push((text.clone(),images.clone()));}
    }));
    composer.update_in(cx,|composer,window,cx| {
        assert!(composer.pasted(&ClipboardItem::new_image(&image()),cx));
        assert_eq!(composer.attachments.len(),1);assert!(matches!(composer.attachments[0].state,AttachmentState::Staged{..}));
        composer.send(window,cx);assert!(requests.borrow().is_empty());
        composer.set_start_state(true,false,None,cx);composer.send(window,cx);composer.send(window,cx);
    });
    assert_eq!(requests.borrow().len(),1);assert!(requests.borrow()[0].0.is_empty());
    let original=requests.borrow()[0].1[0].clone();
    composer.update_in(cx,|composer,window,cx| {
        composer.set_start_state(true,false,Some("Checkout was refused".into()),cx);
        assert_eq!(composer.attachments.len(),1);
        composer.send(window,cx);
        composer.set_start_state(false,true,Some("Create outcome is not known".into()),cx);
        composer.send(window,cx);assert_eq!(composer.attachments.len(),1);
    });
    assert_eq!(requests.borrow().len(),2);assert!(Arc::ptr_eq(original.image(),requests.borrow()[1].1[0].image()));
    composer.update(cx,|composer,cx|{
        let id=composer.attachments[0].id;
        composer.remove_attachment(id,cx);assert_eq!(composer.attachments.len(),1,"unknown create keeps its captured images unchanged");
        assert!(matches!(&composer.attachments[0].state,AttachmentState::Staged{image,..} if Arc::ptr_eq(original.image(),image)));
        assert_eq!(composer.draft.read(cx).value().as_ref(),"","unknown create preserves its captured text as well");
        composer.set_start_state(true,false,Some("Reconciled refusal before any prompt".into()),cx);
        composer.remove_attachment(id,cx);assert!(composer.attachments.is_empty());
    });
    assert_eq!(original.size(),image().bytes.len() as u64);
    cx.run_until_parked();assert!(client.calls().is_empty());assert!(client.config_calls().is_empty());assert!(client.engine_calls().is_empty());assert_eq!(client.message_subscriptions(),0);
}
#[gpui::test]
fn start_text_and_images_use_the_same_editor_and_no_source_none_write_path(cx:&mut TestAppContext) {
    cx.update(gpui_kit::init);let client=FakeClient::default();let fake=client.clone();
    let(composer,cx)=cx.add_window_view(move|window,cx|Composer::new_start_draft(Rc::new(fake),window,cx));
    let requests=Rc::new(RefCell::new(Vec::new()));let observed=requests.clone();
    let _events=cx.update(|_,cx|cx.subscribe(&composer,move|_,event:&ComposerEvent,_|{
        if let ComposerEvent::StartRequested{text,images}=event {observed.borrow_mut().push((text.clone(),images.len()));}
    }));
    composer.update_in(cx,|composer,window,cx| {
        composer.draft.update(cx,|draft,cx|draft.set_value("  Actual first prompt  ",window,cx));
        composer.pasted(&ClipboardItem::new_image(&image()),cx);composer.set_start_state(true,false,None,cx);composer.send(window,cx);
        assert_eq!(composer.draft.read(cx).value().as_ref(),"  Actual first prompt  ");
        composer.stop(cx);composer.recover_pending(cx);composer.focus_input(window,cx);
    });
    assert_eq!(&*requests.borrow(),&[("Actual first prompt".to_string(),1)]);
    cx.run_until_parked();assert!(client.calls().is_empty());assert!(client.config_calls().is_empty());assert!(client.engine_calls().is_empty());
}

#[gpui::test]
async fn initial_images_upload_only_for_the_prepared_owner_then_use_the_existing_prompt_receipt(cx:&mut TestAppContext) {
    use serde_json::json;
    use crate::core::{engine::SessionKey,socket::row_source::ExpectedRowSource};
    let(composer,client,_,cx)=super::tests::open(cx);
    let key=SessionKey{machine:"actual-owner".into(),id:"created".into()};
    let source=ExpectedRowSource{machine:key.machine.clone(),generation:7,route_machine:Some("Captured route".into())};
    let image=super::start::StagedAttachment::of("chosen.png".into(),"image/png",Arc::new(image())).unwrap();
    composer.update_in(cx,|composer,window,cx|assert!(composer.submit_initial(&key,"first-id","",vec![image.clone()],window,cx).is_err()));
    assert!(client.calls().is_empty());
    composer.update_in(cx,|composer,window,cx|composer.set_target(Some(key.clone()),Some(source.clone()),window,cx));cx.run_until_parked();
    client.queue_answer(Ok(json!({"upload":"actual-upload"})));client.queue_answer(Ok(json!({"bytes":image.size()})));client.queue_answer(Ok(json!({"path":"/actual-owner/chosen.png"})));client.queue_owner_accepted();
    let task=composer.update_in(cx,|composer,window,cx|{let task=composer.submit_initial(&key,"first-id","",vec![image],window,cx).unwrap();assert!(composer.submit_initial(&key,"other-id","duplicate",vec![],window,cx).is_err());task});
    task.await.unwrap();cx.run_until_parked();let calls=client.calls();
    assert_eq!(calls.iter().map(|(method,_)|method.as_str()).collect::<Vec<_>>(),["attachments/begin","attachments/chunk","attachments/commit","send/send"]);
    assert_eq!(calls[0].1["expectedSource"],json!(source));assert_eq!(calls[0].1["session"],"created");
    assert_eq!(calls[3].1["expectedSource"],json!(source));assert_eq!(calls[3].1["clientId"],"first-id");assert_eq!(calls[3].1["attachmentIds"],json!(["/actual-owner/chosen.png"]));
    composer.update_in(cx,|composer,window,cx|assert!(composer.submit_initial(&key,"first-id","",vec![],window,cx).is_err()));
    assert_eq!(client.calls().len(),4,"an existing pending first prompt is not replayed");
}
#[gpui::test]
async fn a_source_refused_initial_upload_sends_no_prompt_and_the_local_image_stays_owned(cx:&mut TestAppContext) {
    use crate::core::{engine::SessionKey,socket::row_source::ExpectedRowSource};
    use crate::features::session::client::session_client::CallError;
    let(composer,client,_,cx)=super::tests::open(cx);let key=SessionKey{machine:"owner".into(),id:"s".into()};
    composer.update_in(cx,|composer,window,cx|composer.set_target(Some(key.clone()),Some(ExpectedRowSource{machine:"owner".into(),generation:0,route_machine:None}),window,cx));cx.run_until_parked();
    let image=super::start::StagedAttachment::of("kept.png".into(),"image/png",Arc::new(image())).unwrap();let held=image.clone();
    client.queue_answer(Err(CallError::Refused{code:"source_changed".into(),message:"Captured source replaced".into()}));
    let task=composer.update_in(cx,|composer,window,cx|composer.submit_initial(&key,"stable-id","keep text",vec![image],window,cx).unwrap());
    assert!(task.await.is_err());cx.run_until_parked();assert_eq!(client.calls().len(),1);assert_eq!(client.calls()[0].0,"attachments/begin");
    assert!(!held.image().bytes.is_empty());composer.read_with(cx,|composer,_|assert!(!composer.operational()));
}

#[gpui::test]
fn busy_start_keeps_the_captured_editor_and_images_unchanged_until_the_form_releases_it(cx:&mut TestAppContext) {
    cx.update(gpui_kit::init);let client=FakeClient::default();let fake=client.clone();
    let(composer,cx)=cx.add_window_view(move|window,cx|Composer::new_start_draft(Rc::new(fake),window,cx));
    composer.update_in(cx,|composer,window,cx| {
        composer.draft.update(cx,|draft,cx|draft.set_value("Captured",window,cx));composer.pasted(&ClipboardItem::new_image(&image()),cx);
        let id=composer.attachments[0].id;composer.set_start_state(true,false,None,cx);composer.send(window,cx);
        assert!(composer.pasted(&ClipboardItem::new_image(&image()),cx));
        composer.dropped(&[std::path::PathBuf::from("must-not-read.png")],window,cx);composer.remove_attachment(id,cx);
        assert_eq!(composer.draft.read(cx).value().as_ref(),"Captured");assert_eq!(composer.attachments.len(),1);assert_eq!(composer.attachments[0].id,id);
        composer.set_start_state(true,false,Some("Checkout refused before any prompt".into()),cx);composer.remove_attachment(id,cx);
        assert!(composer.attachments.is_empty());composer.pasted(&ClipboardItem::new_image(&image()),cx);assert_eq!(composer.attachments.len(),1);assert_ne!(composer.attachments[0].id,id);
    });
    cx.run_until_parked();assert!(client.calls().is_empty());assert!(client.config_calls().is_empty());assert!(client.engine_calls().is_empty());
}
