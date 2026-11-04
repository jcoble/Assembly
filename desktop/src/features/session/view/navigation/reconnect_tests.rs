//! Actual navigator recovery uses descriptor/page/workspace completion, never a connection-ready flag.
use super::test_navigation::{NavigationFixture, NavigationHost};
use crate::core::state::selected_session::SelectedSession;
use gpui::TestAppContext;
#[gpui::test]
async fn same_owner_reconnect_reprepares_before_config_or_warm_and_newer_selection_wins(
    cx: &mut TestAppContext,
) {
    cx.executor().allow_parking();
    cx.update(gpui_kit::init);
    cx.update(SelectedSession::init);
    let (host, cx) = cx.add_window_view(|_, _| NavigationHost::default());
    let mut nav = cx.update(|window, cx| NavigationFixture::new(false, window, cx));
    nav.operational();
    nav.fail_next_warm();
    host.update(cx, |host, cx| {
        host.pane = Some(nav.pane.clone());
        cx.notify();
    });
    nav.ready(cx).await;
    nav.request_engine("a", cx).await;
    nav.calls_at_least("warm", 1).await;
    cx.run_until_parked();
    let before = nav.calls();
    let targets = before
        .iter()
        .filter(|call| call["method"] == "open_target")
        .count();
    let configs = before
        .iter()
        .filter(|call| call["feature"] == "config")
        .count();
    assert!(
        configs > 0,
        "actual Engine activation reached Composer config"
    );
    assert_eq!(
        before
            .iter()
            .filter(|call| call["method"] == "warm")
            .count(),
        1,
        "failed warm never retries itself"
    );
    nav.hold_target();
    nav.drop_main(cx).await;
    nav.calls_at_least("open_target", targets + 1).await;
    cx.run_until_parked();
    assert_eq!(
        nav.calls()
            .iter()
            .filter(|call| call["feature"] == "config")
            .count(),
        configs,
        "reconnect may not start config or warm before descriptor and coherent page"
    );
    nav.control("release_target", serde_json::json!({}), cx)
        .await;
    nav.opened("a", cx).await;
    nav.calls_at_least("warm", 2).await;
    assert!(
        nav.calls()
            .iter()
            .filter(|call| call["feature"] == "config")
            .count()
            > configs
    );
    assert_eq!(
        nav.calls()
            .iter()
            .filter(|call| call["method"] == "warm")
            .count(),
        2,
        "actual checked re-preparation permits exactly one new warm"
    );
    // A later user selection supersedes a held reconnect read; releasing it cannot restore A over B.
    let targets = nav
        .calls()
        .iter()
        .filter(|call| call["method"] == "open_target")
        .count();
    nav.hold_target();
    nav.drop_main(cx).await;
    nav.calls_at_least("open_target", targets + 1).await;
    nav.request_engine("b", cx).await;
    nav.control("release_target", serde_json::json!({}), cx)
        .await;
    cx.run_until_parked();
    nav.pane.read_with(cx, |_, cx| {
        assert_eq!(SelectedSession::global(cx).read(cx).key().unwrap().id, "b")
    });
}
#[gpui::test]
async fn replacement_engine_cannot_retarget_the_visible_session_or_restart_config(
    cx: &mut TestAppContext,
) {
    cx.executor().allow_parking();
    cx.update(gpui_kit::init);
    cx.update(SelectedSession::init);
    let (host, cx) = cx.add_window_view(|_, _| NavigationHost::default());
    let mut nav = cx.update(|window, cx| NavigationFixture::new(false, window, cx));
    nav.operational();
    host.update(cx, |host, cx| {
        host.pane = Some(nav.pane.clone());
        cx.notify();
    });
    nav.ready(cx).await;
    nav.request_engine("a", cx).await;
    let before = nav.calls();
    nav.replace_owner();
    nav.drop_main(cx).await;
    nav.ready(cx).await;
    cx.run_until_parked();
    let after = nav.calls();
    assert_eq!(
        after
            .iter()
            .filter(|call| call["method"] == "open_target")
            .count(),
        before
            .iter()
            .filter(|call| call["method"] == "open_target")
            .count()
    );
    nav.pane.read_with(cx, |pane, cx| {
        assert!(pane.active_source_cut.is_none());
        assert!(pane.status.contains("changed"));
        assert_eq!(
            SelectedSession::global(cx).read(cx).key().unwrap().machine,
            "owner"
        );
    });
}
