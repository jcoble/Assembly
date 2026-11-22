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

async fn on_a_remote_session(
    cx: &mut TestAppContext,
) -> (NavigationFixture, &mut gpui::VisualTestContext) {
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
    let of = |id: &str| crate::core::engine::SessionKey {
        machine: "owner".into(),
        id: id.into(),
    };
    composer.update(cx, |_, cx| {
        cx.emit(ComposerEvent::SourceLost(of("earlier")))
    });
    cx.run_until_parked();
    assert_eq!(opens(&nav), before, "the session it was shown before");
    composer.update(cx, |_, cx| cx.emit(ComposerEvent::SourceLost(of("a"))));
    nav.calls_at_least("open_target", before + 1).await;
    assert_eq!(
        nav.pane
            .read_with(cx, |pane, _| pane.status.clone())
            .as_ref(),
        ""
    );
}

// A copied remote session's rows never refuse while its machine is away, so the composer's own config read is the
// first live call to hear it. That refusal must reach the pane like a refused row stream does, or nothing reopens
// the session when the machine answers and Send stays off behind a cleared offline line. And reachable is not
// ready: a returned machine answers probes before its mirror is live again, so reopening on reachable alone
// spends every immediate attempt on refusals and gives up (TSK-1692 window check and its recheck).
#[gpui::test]
async fn a_config_read_refused_while_its_machine_is_away_reopens_the_open_session_by_itself(
    cx: &mut TestAppContext,
) {
    use crate::core::state::machines::Machines;
    use serde_json::json;
    let lane = |reachable: bool, live: bool| {
        vec![(
            "lane".to_string(),
            json!({"name": "lane", "kind": "remote", "reachable": reachable, "checked": true, "live": live}),
        )]
    };
    let (mut nav, cx) = on_a_remote_session(cx).await;
    let composer = nav.pane.read_with(cx, |pane, _| pane.composer.clone());
    composer.update_in(cx, |composer, window, cx| {
        composer.type_text("still here", window, cx)
    });
    // The machine is away, and the machines list the pane follows says so.
    cx.update(|_, cx| Machines::start_holding(lane(false, false), cx));
    cx.run_until_parked();
    let before = opens(&nav);
    let warms = nav
        .calls()
        .iter()
        .filter(|call| call["method"] == "warm")
        .count();
    let gets = nav
        .calls()
        .iter()
        .filter(|call| call["method"] == "get")
        .count();
    nav.forget_results();
    nav.refuse_next_config();
    // A turn ending reads the config again, which the backend refuses while the machine is away.
    composer.update(cx, |composer, cx| {
        composer.set_running(Some("run".into()), cx);
        composer.set_running(None, cx);
    });
    nav.calls_at_least("get", gets + 1).await;
    nav.control("ping", json!({}), cx).await;
    cx.run_until_parked();
    assert_eq!(opens(&nav), before, "no reopen while its machine is away");
    // It reports reachable before its source is ready. No reopen may be spent on that: on the old gating each
    // attempt's own config read refused until the attempts ran out. Pings give any such attempt its round trips.
    cx.update(|_, cx| Machines::change_holding(lane(true, false), cx));
    for _ in 0..40 {
        if opens(&nav) > before {
            break;
        }
        cx.run_until_parked();
        nav.control("ping", json!({}), cx).await;
    }
    cx.run_until_parked();
    assert_eq!(
        opens(&nav),
        before,
        "reachable is not ready: no reopen is spent on it"
    );
    assert_eq!(
        nav.pane
            .read_with(cx, |pane, _| pane.status.clone())
            .as_ref(),
        "",
        "waiting for the source is no error"
    );
    // The source is ready: its mirror is live, and config reads answer again.
    nav.allow_config();
    cx.update(|_, cx| Machines::change_holding(lane(true, true), cx));
    // The session reopens by itself, and its composer warms again, which only an operational, online composer does:
    // the same gate that enables Send. The typed draft is still in the box and no error is left behind.
    nav.calls_at_least("open_target", before + 1).await;
    nav.opened("a", cx).await;
    nav.calls_at_least("warm", warms + 1).await;
    cx.run_until_parked();
    assert_eq!(
        nav.pane
            .read_with(cx, |pane, _| pane.status.clone())
            .as_ref(),
        ""
    );
    let text = composer.update_in(cx, |composer, window, cx| {
        composer.draft_state(window, cx).0
    });
    assert_eq!(text, "still here");
}

#[gpui::test]
async fn an_answer_refused_on_a_returning_machine_says_so_plainly_and_reopens_the_session(
    cx: &mut TestAppContext,
) {
    use crate::features::session::client::session_client::CallError;
    let (mut nav, cx) = on_a_remote_session(cx).await;
    let before = opens(&nav);
    nav.forget_results();
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
    // Opening clears the line, but the press still went unheard.
    nav.opened("a", cx).await;
    cx.run_until_parked();
    assert_eq!(
        nav.pane
            .read_with(cx, |pane, _| pane.status.clone())
            .as_ref(),
        "That didn't reach lane. Try again."
    );
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

#[gpui::test]
async fn background_reads_on_a_machine_going_away_say_nothing_and_reopen_the_session(
    cx: &mut TestAppContext,
) {
    use crate::features::session::client::session_client::CallError;
    let (mut nav, cx) = on_a_remote_session(cx).await;
    let moved = || CallError::Refused {
        code: "source_changed".into(),
        message: "The selected row source changed.".into(),
    };
    let before = opens(&nav);
    nav.pane.update(cx, |pane, cx| {
        pane.read_refused(moved(), cx);
        assert_eq!(pane.status.as_ref(), "");
    });
    nav.calls_at_least("open_target", before + 1).await;
    nav.pane.update(cx, |pane, cx| {
        pane.say_failed("Couldn't read the tool call", moved(), cx);
        assert_eq!(pane.status.as_ref(), "");
        let gone = CallError::Unreachable {
            machine: "lane".into(),
        };
        pane.read_refused(gone, cx);
        assert_eq!(pane.status.as_ref(), "");
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

// TSK-1692: a remote session's saved copy stays open while its machine is offline, so an action that needs the machine
// (a touched file, an attachment image) says so instead of failing silently.
#[gpui::test]
async fn an_action_that_needs_an_offline_machine_says_so_on_a_remote_session(
    cx: &mut TestAppContext,
) {
    use crate::features::session::client::session_client::CallError;
    let (nav, cx) = on_a_remote_session(cx).await;
    nav.pane.update(cx, |pane, cx| {
        let offline = CallError::Unreachable {
            machine: "workbox-test".into(),
        };
        pane.say_failed("Couldn't open the image", offline, cx);
        assert_eq!(pane.status.as_ref(), "On workbox-test, which is offline.");
    });
}
