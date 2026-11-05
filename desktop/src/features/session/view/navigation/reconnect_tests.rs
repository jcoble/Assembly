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

// A running session's row changes all the time, and a reconnect brings its rows again. Neither is a reason to refuse.
#[gpui::test]
async fn a_session_whose_row_changes_while_it_prepares_still_opens(cx: &mut TestAppContext) {
    use crate::core::engine::{EngineFeed, EngineScope, SessionKey};
    use crate::core::intents::session_open::{SessionOpenRequest, SessionOpenTarget};
    use serde_json::{Value, json};
    let display = |version: i64, activity: &str| -> Value {
        json!({"entityKind":"session","id":"a","machine":"owner","version":version,"provider":"fixture","model":null,
            "folder":"/fixture/a","title":null,"parentId":null,"parentMachine":null,"runId":null,"state":"idle",
            "activityState":activity,"epoch":1,"holder":"local","projectId":"project"})
    };
    cx.executor().allow_parking();
    cx.update(gpui_kit::init);
    cx.update(SelectedSession::init);
    let (host, cx) = cx.add_window_view(|_, _| NavigationHost::default());
    let mut nav = cx.update(|window, cx| NavigationFixture::new(false, window, cx));
    nav.operational();
    nav.start_case(display(1, "idle"), json!({}));
    host.update(cx, |host, cx| {
        host.pane = Some(nav.pane.clone());
        cx.notify();
    });
    nav.ready(cx).await;
    let key = SessionKey {
        machine: "owner".into(),
        id: "a".into(),
    };
    let feed = cx.update(|_, cx| EngineFeed::global(cx));
    let scope = EngineScope {
        machine: "owner".into(),
        topic: "sessions".into(),
        key: None,
    };
    feed.update(cx, |feed, cx| {
        feed.test_snapshot(scope.clone(), vec![display(1, "idle")], cx)
    });
    nav.hold_target();
    nav.pane.update_in(cx, |pane, window, cx| {
        pane.request_open(
            SessionOpenRequest {
                key: key.clone(),
                request_id: "engine:a".into(),
                target: SessionOpenTarget::Engine,
            },
            window,
            cx,
        )
    });
    nav.call_entered("open_target").await;
    feed.update(cx, |feed, cx| {
        feed.test_snapshot(scope.clone(), vec![display(2, "running")], cx)
    });
    nav.control("release_target", json!({}), cx).await;
    nav.opened("a", cx).await;
    nav.pane
        .read_with(cx, |pane, _| assert_eq!(pane.session.as_deref(), Some("a")));
}

#[test]
fn only_what_an_open_was_built_on_stops_it() {
    use super::opening::opens_the_same;
    use crate::core::engine::{EngineItem, EngineKey};
    use serde_json::json;
    let item = |version: i64, provider: &str, state: &str, activity: &str, parent: Option<&str>| {
        let value = json!({"entityKind":"session","id":"a","machine":"owner","version":version,"provider":provider,
            "model":null,"folder":"/a","title":null,"parentId":parent,"parentMachine":parent.map(|_|"owner"),
            "runId":null,"state":state,"activityState":activity,"epoch":1});
        EngineItem {
            key: EngineKey {
                machine: "owner".into(),
                kind: "session".into(),
                id: "a".into(),
            },
            version,
            value,
        }
    };
    let checked = item(1, "claude", "idle", "idle", None);
    assert!(opens_the_same(
        &checked,
        &item(2, "claude", "running", "running", None)
    ));
    assert!(!opens_the_same(
        &checked,
        &item(2, "codex", "idle", "idle", None)
    ));
    assert!(!opens_the_same(
        &checked,
        &item(2, "claude", "ended", "idle", None)
    ));
    assert!(!opens_the_same(
        &checked,
        &item(2, "claude", "idle", "idle", Some("p"))
    ));
}
