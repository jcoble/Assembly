//! Real SessionPane + real coordinator + real Chat codec, against source-derived socket fixtures.
use gpui::TestAppContext;
use crate::core::{engine::SessionKey,state::selected_session::SelectedSession};
use super::{test_navigation::{NavigationFixture,NavigationHost},chat_codec::ChatPlace};
#[gpui::test]
async fn automatic_workspace_switch_restores_chat_only_after_the_target_rows_are_prepared(cx:&mut TestAppContext) {
    cx.executor().allow_parking();cx.update(gpui_kit::init);cx.update(SelectedSession::init);
    let(host,cx)=cx.add_window_view(|_,_|NavigationHost::default());
    let mut nav=cx.update(|window,cx|NavigationFixture::new(true,window,cx));
    host.update(cx,|host,cx|{host.pane=Some(nav.pane.clone());cx.notify();});
    nav.ready(cx).await;nav.request("a",cx).await;
    cx.update(|window,cx|window.draw(cx).clear(cx));
    nav.pane.update_in(cx,|pane,_,cx|pane.timeline.update(cx,|timeline,cx|timeline.restore_place(&ChatPlace{item_key:Some("seq:11".into()),offset:0.,following:false},cx)).unwrap());
    cx.run_until_parked();cx.update(|window,cx|window.draw(cx).clear(cx));
    nav.pane.read_with(cx,|pane,cx|{let place=pane.timeline.read(cx).workspace_place(cx);assert_eq!(place.item_key.as_deref(),Some("seq:11"));assert!(!place.following);assert_eq!(place.offset,0.);});
    nav.request("b",cx).await;
    nav.pane.read_with(cx,|pane,cx|{assert_eq!(pane.session.as_deref(),Some("b"));assert!(pane.timeline.read(cx).workspace_place(cx).following);});
    nav.request("a",cx).await;
    nav.pane.read_with(cx,|pane,cx|{
        assert_eq!(SelectedSession::global(cx).read(cx).key(),Some(&SessionKey{machine:"owner".into(),id:"a".into()}));
        let place=pane.timeline.read(cx).workspace_place(cx);assert_eq!(place.item_key.as_deref(),Some("seq:11"));assert_eq!(place.offset,0.);assert!(!place.following);
        assert!(pane.view.items.iter().all(|item|matches!(&item.kind,crate::features::session::model::item::ItemKind::User{text,..} if text.starts_with("a:"))));
    });
    let calls=nav.calls();let saved=calls.iter().find(|call|call["feature"]=="workspaces"&&call["method"]=="save"&&call["args"]["sessionId"]=="a").unwrap();
    assert_eq!(saved["args"]["snapshot"]["chat"]["itemKey"],"seq:11");
    assert_eq!(saved["args"]["snapshot"]["chat"]["offset"],0.);assert_eq!(saved["args"]["snapshot"]["chat"]["following"],false);
}
