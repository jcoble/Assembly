//! Workspace metadata tests use the existing MessageScroller; no provider state is persisted.
use gpui::TestAppContext;
use serde_json::json;
use super::chat_codec::{ChatPlace,decode};

#[test]
fn chat_metadata_is_closed_and_keeps_exact_anchor_offset_and_following() {
    let place=decode(&json!({"itemKey":"item:stable","offset":13.5,"following":false})).unwrap();
    assert_eq!(place.item_key.as_deref(),Some("item:stable"));assert_eq!(place.offset,13.5);assert!(!place.following);
    assert!(decode(&json!({"itemKey":"","offset":0,"following":false})).is_err());
    assert!(decode(&json!({"offset":0,"following":false,"model":"operational"})).is_err());
}

#[gpui::test]
fn a_nonzero_restore_refuses_before_changing_the_current_timeline(cx:&mut TestAppContext) {
    let(pane,client,cx)=super::tests::open(cx);
    super::tests::show(&pane,"outgoing",cx);
    let before=client.subscribed();
    pane.update_in(cx,|pane,_,cx| {
        let place=ChatPlace{item_key:Some("recorded".into()),offset:13.5,following:false};
        let result=pane.timeline.update(cx,|timeline,cx|timeline.restore_place(&place,cx));
        assert!(result.unwrap_err().contains("logical pixel offsets"));assert_eq!(pane.session.as_deref(),Some("outgoing"));
    });
    assert_eq!(client.subscribed(),before);
}
