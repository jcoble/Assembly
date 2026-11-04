use super::SourceControlPane;
use super::codec::SavedView;
use crate::core::socket::{
    client::SocketClient,
    endpoint::Endpoint,
    fake_backend::{FakeBackend, Reply, hello},
    test_wait::until,
};
use crate::core::state::workspace_panes::WorkspaceKey;
use crate::core::state::{selected_session::SelectedSession, sessions::Sessions};
use crate::features::source_control::git_status::Section;
use futures_channel::mpsc::unbounded;
use gpui::TestAppContext;
use gpui_kit::EmptyView;
use gpui_kit::component::dock::BasePanel as _;
use serde_json::json;
use std::collections::HashSet;
use std::sync::{Arc, Mutex};

// Pins: g9#26; observes actual window activation, with no timer.
#[gpui::test]
async fn regaining_focus_refreshes_once_and_a_hidden_pane_reads_nothing(cx: &mut TestAppContext) {
    let reads = Arc::new(Mutex::new(0usize));
    let observed = reads.clone();
    let (changed, mut changes) = unbounded();
    let heard = changed.clone();
    let backend = FakeBackend::start(move |frame| {
        if let Some(reply) = hello(frame) {
            return reply;
        }
        if frame["t"] == "sub" {
            return vec![Reply::Frame(
                json!({"t":"state","sub":frame["id"],"items":[]}),
            )];
        }
        if frame["t"] != "call" {
            return vec![];
        }
        let result = if frame["method"] == "status" {
            *observed.lock().unwrap() += 1;
            json!({"branch":"main","head":"a".repeat(40),"ahead":0,"behind":0,"hasUpstream":false,"files":[],"totalFiles":0,"totalStaged":0,"fingerprint":"actual"})
        } else if frame["method"] == "branches" {
            json!({"remotes":[]})
        } else {
            json!({})
        };
        let _ = heard.unbounded_send(());
        vec![Reply::Frame(
            json!({"t":"result","id":frame["id"],"result":result}),
        )]
    });
    cx.executor().allow_parking();
    cx.update(|cx| {
        gpui_kit::init(cx);
        SelectedSession::init(cx);
        SocketClient::start(
            Endpoint {
                port: backend.port,
                token: "secret".into(),
            },
            cx,
        );
        Sessions::start(cx);
        SelectedSession::global(cx)
            .update(cx, |selected, cx| selected.select(Some("s1".into()), cx));
    });
    let mut other = None;
    let _ = cx.add_window_view(|window, _| {
        other = Some(window.window_handle());
        EmptyView
    });
    let other = other.unwrap();
    let (pane, cx) = cx.add_window_view(SourceControlPane::new);
    let tell = changed.clone();
    let _heard = cx.update(|_, cx| {
        cx.observe(&pane, move |_, _| {
            let _ = tell.unbounded_send(());
        })
    });
    cx.update(|window, _| window.activate_window());
    cx.run_until_parked();
    pane.update_in(cx, |pane, window, cx| pane.set_active(true, window, cx));
    until("initial status", &mut changes, &changed, || {
        cx.run_until_parked();
        *reads.lock().unwrap() == 1
    })
    .await;
    cx.update(|window, _| window.activate_window());
    cx.run_until_parked();
    assert_eq!(*reads.lock().unwrap(), 1);
    other
        .update(cx, |_, window, _| window.activate_window())
        .unwrap();
    cx.run_until_parked();
    cx.update(|window, _| window.activate_window());
    until("focus status refresh", &mut changes, &changed, || {
        cx.run_until_parked();
        *reads.lock().unwrap() == 2
    })
    .await;
    pane.update_in(cx, |pane, window, cx| pane.set_active(false, window, cx));
    other
        .update(cx, |_, window, _| window.activate_window())
        .unwrap();
    cx.run_until_parked();
    cx.update(|window, _| window.activate_window());
    cx.run_until_parked();
    assert_eq!(*reads.lock().unwrap(), 2);
}
#[gpui::test]
fn a_session_comes_back_with_its_own_open_sections_and_place(cx: &mut TestAppContext) {
    cx.update(|cx| {
        gpui_kit::init(cx);
        SelectedSession::init(cx);
    });
    let (pane, visual) = cx.add_window_view(SourceControlPane::new);
    let at = |y: f32| gpui_kit::point(gpui_kit::px(0.), gpui_kit::px(y));
    let key = |session: &str| WorkspaceKey {
        machine: "owner".into(),
        session_id: session.into(),
        checkout_root: format!("/{session}"),
    };
    pane.update(visual, |pane, cx| {
        pane.bind_workspace(Some(key("a")), SavedView::default(), cx);
        pane.session_changed(Some("a".into()), cx);
        pane.open_sections.insert(Section::Staged);
        pane.scroll.set_offset(at(-40.));
        let saved = pane.saved_view(&key("a"));
        let view = SavedView::parse(&serde_json::Value::Null, &key("b")).unwrap();
        pane.bind_workspace(Some(key("b")), view, cx);
        pane.session_changed(Some("b".into()), cx);
        assert!(
            pane.open_sections.is_empty(),
            "another session starts folded"
        );
        assert_eq!(pane.scroll.offset(), at(0.));
        pane.open_sections.insert(Section::Untracked);
        let view = SavedView::parse(&saved, &key("a")).unwrap();
        pane.bind_workspace(Some(key("a")), view, cx);
        pane.session_changed(Some("a".into()), cx);
        assert_eq!(pane.open_sections, HashSet::from([Section::Staged]));
        assert_eq!(
            pane.pending_place.map(|place| place.top),
            Some(40.),
            "the place waits for a read that proves it"
        );
    });
}
#[gpui::test]
fn a_session_with_no_workspace_starts_folded(cx: &mut TestAppContext) {
    cx.update(|cx| {
        gpui_kit::init(cx);
        SelectedSession::init(cx);
    });
    let (pane, visual) = cx.add_window_view(SourceControlPane::new);
    pane.update(visual, |pane, cx| {
        pane.open_sections.insert(Section::Staged);
        pane.scroll
            .set_offset(gpui_kit::point(gpui_kit::px(0.), gpui_kit::px(-40.)));
        pane.session_changed(Some("a".into()), cx);
        assert!(pane.open_sections.is_empty());
        assert_eq!(f32::from(pane.scroll.offset().y), 0.);
    });
}
// Pins: g9#9
#[gpui::test]
async fn a_plain_folder_offers_initialize(cx: &mut TestAppContext) {
    let (changed, mut changes) = unbounded();
    let backend = FakeBackend::start(move |frame| {
        if let Some(reply) = hello(frame) {
            return reply;
        }
        if frame["t"] == "sub" {
            return vec![Reply::Frame(
                json!({"t":"state","sub":frame["id"],"items":[]}),
            )];
        }
        if frame["t"] != "call" {
            return vec![];
        }
        if frame["method"] == "status" {
            return vec![Reply::Frame(json!({
                "t":"error","id":frame["id"],"code":"git_failed",
                "message":"This folder isn't a git repository yet.","machine":"machine"
            }))];
        }
        vec![Reply::Frame(
            json!({"t":"result","id":frame["id"],"result":{"remotes":[]}}),
        )]
    });
    cx.executor().allow_parking();
    cx.update(|cx| {
        gpui_kit::init(cx);
        SelectedSession::init(cx);
        SocketClient::start(
            Endpoint {
                port: backend.port,
                token: "secret".into(),
            },
            cx,
        );
        Sessions::start(cx);
        SelectedSession::global(cx)
            .update(cx, |selected, cx| selected.select(Some("s1".into()), cx));
    });
    let (pane, visual) = cx.add_window_view(SourceControlPane::new);
    let tell = changed.clone();
    let _heard = visual.update(|_, cx| {
        cx.observe(&pane, move |_, _| {
            let _ = tell.unbounded_send(());
        })
    });
    visual.update(|window, _| window.activate_window());
    pane.update_in(visual, |pane, window, cx| pane.set_active(true, window, cx));
    until("the folder is told apart", &mut changes, &changed, || {
        visual.run_until_parked();
        visual.update(|window, cx| window.draw(cx).clear(cx));
        visual.debug_bounds("git-initialize").is_some()
    })
    .await;
}
