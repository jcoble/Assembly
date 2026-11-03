//! A draft covers the Chat pane's session: another session opening takes the pane back, the same one reopening doesn't.
use crate::core::state::selected_session::SelectedSession;
use crate::features::session::view::session_pane::test_navigation::{NavigationFixture, NavigationHost};
use gpui::TestAppContext;
use gpui_kit::{AnyView, AppContext as _};

#[gpui::test]
async fn a_draft_stays_until_another_session_opens_or_its_own_is_asked_for(cx: &mut TestAppContext) {
    cx.executor().allow_parking();
    cx.update(gpui_kit::init);
    cx.update(SelectedSession::init);
    let (host, cx) = cx.add_window_view(|_, _| NavigationHost::default());
    let mut nav = cx.update(|window, cx| NavigationFixture::new(true, window, cx));
    host.update(cx, |host, cx| {
        host.pane = Some(nav.pane.clone());
        cx.notify();
    });
    nav.ready(cx).await;
    nav.request("a", cx).await;
    let draft: AnyView = cx.update(|_, cx| cx.new(|_| NavigationHost::default())).into();
    let cover = |cx: &mut gpui_kit::VisualTestContext, nav: &NavigationFixture| {
        nav.pane.update(cx, |pane, _| pane.draft = Some(draft.clone()));
    };
    let covered = |cx: &mut gpui_kit::VisualTestContext, nav: &NavigationFixture| {
        nav.pane.read_with(cx, |pane, _| pane.draft.is_some())
    };

    cover(cx, &nav);
    cx.run_until_parked();
    cx.update(|window, cx| window.draw(cx).clear(cx));
    nav.request("a", cx).await;
    assert!(covered(cx, &nav), "the same session reopening, as after a reconnect, leaves the draft");
    nav.pane.update(cx, |pane, _| pane.uncover("b"));
    assert!(covered(cx, &nav), "asking for another session waits for it to open");
    nav.request("b", cx).await;
    assert!(!covered(cx, &nav), "another session opening takes the pane back");

    cover(cx, &nav);
    nav.pane.update(cx, |pane, _| pane.uncover("b"));
    assert!(!covered(cx, &nav), "asking for the session under the draft shows it");
}
