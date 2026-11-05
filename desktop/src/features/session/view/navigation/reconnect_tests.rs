//! Actual navigator recovery uses descriptor/page/workspace completion, never a connection-ready flag.
use super::test_navigation::{NavigationFixture, NavigationHost};
use crate::core::socket::row_source::ExpectedRowSource;
use crate::core::state::selected_session::SelectedSession;
use crate::features::session::client::session_client::RowUpdate;
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
#[gpui::test]
async fn a_remote_machine_going_away_is_no_error_and_its_session_reopens_when_it_answers(
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
    nav.calls_at_least("warm", 1).await;
    cx.run_until_parked();
    let lost = || RowUpdate::Refused {
        code: "source_changed".into(),
        message: "The selected row source changed.".into(),
    };
    nav.pane.update(cx, |pane, cx| pane.on_rows(lost(), cx));
    nav.pane.read_with(cx, |pane, _| {
        assert_eq!(
            pane.status.as_ref(),
            "The selected row source changed.",
            "this machine's own source changing is still reported"
        );
    });
    let targets = nav
        .calls()
        .iter()
        .filter(|call| call["method"] == "open_target")
        .count();
    nav.pane.update(cx, |pane, cx| {
        pane.row_source = Some(ExpectedRowSource {
            machine: "owner".into(),
            generation: 3,
            route_machine: Some("lane".into()),
        });
        pane.status = Default::default();
        pane.on_rows(lost(), cx);
        assert_eq!(pane.status.as_ref(), "", "a machine going away is no error");
    });
    nav.calls_at_least("open_target", targets + 1).await;
}

async fn on_a_remote_session<'a>(
    cx: &'a mut TestAppContext,
) -> (NavigationFixture, &'a mut gpui::VisualTestContext) {
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
    nav.calls_at_least("warm", 1).await;
    cx.run_until_parked();
    nav.pane.update(cx, |pane, _| {
        pane.row_source = Some(ExpectedRowSource {
            machine: "owner".into(),
            generation: 3,
            route_machine: Some("lane".into()),
        });
    });
    (nav, cx)
}

fn opens(nav: &NavigationFixture) -> usize {
    nav.calls()
        .iter()
        .filter(|call| call["method"] == "open_target")
        .count()
}

#[gpui::test]
async fn a_read_refused_on_a_returning_machine_reopens_the_session_with_no_error(
    cx: &mut TestAppContext,
) {
    use crate::features::session::composer::composer_view::ComposerEvent;
    let (mut nav, cx) = on_a_remote_session(cx).await;
    let before = opens(&nav);
    let composer = nav.pane.read_with(cx, |pane, _| pane.composer.clone());
    composer.update(cx, |_, cx| cx.emit(ComposerEvent::SourceLost));
    nav.calls_at_least("open_target", before + 1).await;
    assert_eq!(
        nav.pane
            .read_with(cx, |pane, _| pane.status.clone())
            .as_ref(),
        ""
    );
}

#[gpui::test]
async fn an_answer_refused_on_a_returning_machine_says_so_plainly_and_reopens_the_session(
    cx: &mut TestAppContext,
) {
    use crate::features::session::client::session_client::CallError;
    let (mut nav, cx) = on_a_remote_session(cx).await;
    let before = opens(&nav);
    nav.pane.update(cx, |pane, cx| {
        pane.send_refused(
            CallError::Refused {
                code: "source_changed".into(),
                message: "The selected row source changed.".into(),
            },
            cx,
        );
        assert_eq!(pane.status.as_ref(), "That didn't reach lane. Try again.");
    });
    nav.calls_at_least("open_target", before + 1).await;
}

#[gpui::test]
async fn a_source_that_keeps_moving_stops_reopening_and_says_so(cx: &mut TestAppContext) {
    let (nav, cx) = on_a_remote_session(cx).await;
    let before = opens(&nav);
    nav.pane.update(cx, |pane, cx| {
        pane.source_reopens = 3;
        pane.on_rows(
            RowUpdate::Refused {
                code: "source_changed".into(),
                message: "The selected row source changed.".into(),
            },
            cx,
        );
        assert_eq!(
            pane.status.as_ref(),
            "lane keeps reconnecting. Open the session again."
        );
    });
    cx.run_until_parked();
    assert_eq!(opens(&nav), before);
}
