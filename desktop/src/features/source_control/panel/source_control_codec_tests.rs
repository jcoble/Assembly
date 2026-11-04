//! Leaving one workspace for another, against a backend that records what each read asked for.
use super::super::SourceControlPane;
use super::SavedView;
use crate::core::engine::SessionKey;
use crate::core::socket::{
    checkout_selector::CheckoutSelector,
    client::SocketClient,
    endpoint::Endpoint,
    fake_backend::{FakeBackend, Reply, hello},
    row_source::ExpectedRowSource,
    test_wait::until,
};
use crate::core::state::selected_session::SelectedSession;
use crate::core::state::session_read::Shown;
use crate::core::state::workspace_panes::{WorkspaceKey, WorkspacePanes};
use crate::core::ui::center_panes::CenterPanes;
use crate::features::shell::{default_layout::default_state, placeholder_pane::PlaceholderPane};
use crate::features::source_control::git_checkouts::CheckoutHint;
use crate::features::source_control::git_status::Section;
use futures_channel::mpsc::{UnboundedReceiver, UnboundedSender, unbounded};
use gpui::{TestAppContext, VisualTestContext};
use gpui_kit::component::dock::{BasePanel as _, DockArea};
use gpui_kit::{AppContext as _, EmptyView, Entity, Subscription, px, size};
use serde_json::{Value, json};
use std::collections::HashSet;
use std::sync::{Arc, Mutex};

const ONE: &str = "/one";
const TWO: &str = "/two";
const LINKED: &str = "/one-linked";

fn key(root: &str) -> WorkspaceKey {
    in_session("s1", root)
}
fn in_session(session: &str, root: &str) -> WorkspaceKey {
    WorkspaceKey {
        machine: "owner".into(),
        session_id: session.into(),
        checkout_root: root.into(),
    }
}
fn source() -> ExpectedRowSource {
    ExpectedRowSource {
        machine: "owner".into(),
        generation: 0,
        route_machine: None,
    }
}
fn hint() -> CheckoutHint {
    CheckoutHint {
        session: "s1".into(),
        source: source(),
        path: LINKED.into(),
        expected_root: ONE.into(),
    }
}
/// A place far enough down a long open list that it only holds once the rows have been laid out.
fn at(top: f32) -> SavedView {
    SavedView {
        open_sections: HashSet::from([Section::Untracked]),
        scroll_top: top,
        ..SavedView::default()
    }
}
fn rows() -> Value {
    (0..100)
        .map(
            |at| json!({"path":format!("f{at}.rs"),"originalPath":null,"index":"?","worktree":"?"}),
        )
        .collect()
}
#[derive(Default)]
struct Seen {
    statuses: Vec<Value>,
    lists: usize,
    cancels: usize,
    root: Option<String>,
    alternate_root: Option<String>,
    hold_status: bool,
    hold_list: bool,
    offered: Value,
}
struct Bench<'a> {
    pane: Entity<SourceControlPane>,
    cx: &'a mut VisualTestContext,
    seen: Arc<Mutex<Seen>>,
    changed: UnboundedSender<()>,
    changes: UnboundedReceiver<()>,
    _backend: FakeBackend,
    _heard: Subscription,
}
async fn bench(cx: &mut TestAppContext, offered: Value) -> Bench<'_> {
    let (changed, changes) = unbounded();
    let tell = changed.clone();
    let seen = Arc::new(Mutex::new(Seen {
        root: Some(ONE.into()),
        offered,
        ..Seen::default()
    }));
    let answering = seen.clone();
    let backend = FakeBackend::start(move |frame| {
        if let Some(reply) = hello(frame) {
            return reply;
        }
        let id = frame["id"].clone();
        let mut seen = answering.lock().unwrap();
        let replies = match (frame["t"].as_str(), frame["method"].as_str()) {
            (Some("sub"), _) => vec![Reply::Frame(json!({"t":"state","sub":id,"items":[]}))],
            (Some("cancel"), _) => {
                seen.cancels += 1;
                vec![]
            }
            (Some("call"), Some("status")) => {
                seen.statuses.push(frame["args"].clone());
                if seen.hold_status {
                    vec![]
                } else {
                    let root = match (frame["args"].get("checkoutPath"), &seen.alternate_root) {
                        (Some(_), Some(root)) => json!(root),
                        (Some(path), None) => path.clone(),
                        (None, _) => json!(seen.root),
                    };
                    vec![Reply::Frame(json!({"t":"result","id":id,"result":{
                        "branch":"main","head":"a".repeat(40),"ahead":0,"behind":0,"hasUpstream":false,"files":rows(),
                        "totalFiles":100,"totalStaged":0,"fingerprint":"same","checkoutRoot":root}}))]
                }
            }
            (Some("call"), Some("checkouts")) => {
                seen.lists += 1;
                if seen.hold_list {
                    vec![]
                } else {
                    vec![Reply::Frame(json!({"t":"result","id":id,"result":
                        {"sessionRoot":ONE,"worktrees":seen.offered}}))]
                }
            }
            (Some("call"), Some("branches")) => {
                vec![Reply::Frame(
                    json!({"t":"result","id":id,"result":{"remotes":[]}}),
                )]
            }
            (Some("call"), _) => vec![Reply::Frame(json!({"t":"result","id":id,"result":{}}))],
            _ => vec![],
        };
        let _ = tell.unbounded_send(());
        replies
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
    });
    let (pane, visual) = cx.add_window_view(|window, cx| SourceControlPane::new(window, cx));
    visual.simulate_resize(size(px(500.), px(300.)));
    let tell = changed.clone();
    let heard = visual.update(|_, cx| {
        cx.observe(&pane, move |_, _| {
            let _ = tell.unbounded_send(());
        })
    });
    let mut bench = Bench {
        pane,
        cx: visual,
        seen,
        changed,
        changes,
        _backend: backend,
        _heard: heard,
    };
    bench.select("s1");
    bench
}
fn linked() -> Value {
    json!([{"path":LINKED,"branch":"feature"}])
}
impl Bench<'_> {
    /// The coordinator's order: the session is selected, and then its workspace restores.
    fn select(&mut self, session: &str) {
        let session = session.to_string();
        self.cx.update(|_, cx| {
            SelectedSession::global(cx).update(cx, |selected, cx| {
                selected.commit_open(
                    SessionKey {
                        machine: "owner".into(),
                        id: session,
                    },
                    source(),
                    false,
                    cx,
                )
            })
        });
    }
    fn bind(&mut self, workspace: Option<WorkspaceKey>, view: SavedView) {
        self.pane
            .clone()
            .update(self.cx, |pane, cx| pane.bind_workspace(workspace, view, cx));
    }
    fn show(&mut self) {
        self.pane.clone().update_in(self.cx, |pane, window, cx| {
            pane.set_active(true, window, cx)
        });
    }
    fn refresh(&mut self) {
        self.pane.clone().update(self.cx, |pane, cx| {
            pane.read.update(cx, |read, cx| read.refresh(cx))
        });
    }
    async fn until(
        &mut self,
        what: &str,
        mut done: impl FnMut(&SourceControlPane, &gpui_kit::App, &Seen) -> bool,
    ) {
        let (pane, seen, cx) = (self.pane.clone(), self.seen.clone(), &mut *self.cx);
        until(what, &mut self.changes, &self.changed, || {
            cx.run_until_parked();
            cx.update(|_, cx| done(pane.read(cx), cx, &seen.lock().unwrap()))
        })
        .await;
    }
    async fn answered(&mut self, count: u64) {
        self.until("the status answer", |pane, cx, _| {
            pane.read.read(cx).answers() == count
        })
        .await;
    }
    async fn asked(&mut self, count: usize) {
        self.until("the status read", |_, _, seen| seen.statuses.len() == count)
            .await;
    }
    /// The place still waiting, and where the scroller is.
    fn spot(&mut self) -> (Option<f32>, f32) {
        let pane = self.pane.clone();
        self.cx.update(|_, cx| {
            let pane = pane.read(cx);
            let waiting = pane.pending_place.map(|place| place.top);
            (waiting, f32::from(pane.scroll.offset().y))
        })
    }
    fn selection(&mut self) -> Option<CheckoutSelector> {
        let pane = self.pane.clone();
        self.cx.update(|_, cx| pane.read(cx).selection(cx))
    }
}
#[gpui::test]
async fn a_shown_status_is_not_proof_for_the_next_workspace_of_the_same_session(
    cx: &mut TestAppContext,
) {
    let mut bench = bench(cx, json!([])).await;
    bench.show();
    bench.answered(1).await;
    bench.seen.lock().unwrap().hold_status = true;
    bench.bind(Some(key(TWO)), at(40.));
    bench.asked(2).await;
    let pane = bench.pane.clone();
    let cached = bench
        .cx
        .update(|_, cx| matches!(pane.read(cx).read.read(cx).shown(), Shown::Read(_)));
    assert!(cached, "the same session and source keep the last answer");
    assert_eq!(
        bench.spot(),
        (Some(40.), 0.),
        "a shown answer isn't a new one"
    );
    bench.seen.lock().unwrap().hold_status = false;
    bench.refresh();
    bench.answered(2).await;
    assert_eq!(
        bench.spot(),
        (Some(40.), 0.),
        "this answer came from the last root"
    );
    let saved = bench.cx.update(|_, cx| pane.read(cx).saved_view(&key(TWO)));
    assert_eq!(
        saved["scrollTop"], 40.,
        "a save keeps the place still waiting"
    );
    bench.seen.lock().unwrap().root = None;
    bench.refresh();
    bench.answered(3).await;
    assert_eq!(
        bench.spot(),
        (Some(40.), 0.),
        "an answer that names no root proves nothing"
    );
    bench.seen.lock().unwrap().root = Some(TWO.into());
    bench.refresh();
    bench.answered(4).await;
    assert_eq!(bench.spot(), (None, -40.));
}
#[gpui::test]
async fn coming_back_to_the_same_root_still_waits_for_a_new_answer(cx: &mut TestAppContext) {
    let mut bench = bench(cx, json!([])).await;
    bench.show();
    bench.answered(1).await;
    bench.seen.lock().unwrap().hold_status = true;
    bench.bind(Some(key(ONE)), at(40.));
    bench.asked(2).await;
    assert_eq!(
        bench.spot(),
        (Some(40.), 0.),
        "the shown root matches, but it's from before"
    );
    bench.seen.lock().unwrap().hold_status = false;
    bench.refresh();
    bench.answered(2).await;
    assert_eq!(bench.spot(), (None, -40.));
}
#[gpui::test]
async fn a_saved_checkout_is_read_only_after_a_fresh_list_and_then_proves_the_place(
    cx: &mut TestAppContext,
) {
    let mut bench = bench(cx, linked()).await;
    bench.seen.lock().unwrap().hold_list = true;
    bench.show();
    bench.answered(1).await;
    bench.bind(
        Some(key(ONE)),
        SavedView {
            checkout_hint: Some(hint()),
            ..at(40.)
        },
    );
    bench.answered(2).await;
    assert_eq!(bench.selection(), None, "a hint alone reads nothing");
    assert_eq!(
        bench.spot().0,
        Some(40.),
        "the session's own folder isn't what was saved"
    );
    let own = bench.seen.lock().unwrap().statuses.clone();
    assert!(own.iter().all(|args| args.get("checkoutPath").is_none()));
    bench.seen.lock().unwrap().hold_list = false;
    bench.refresh();
    bench
        .until("the saved checkout and its place", |pane, _, _| {
            pane.pending_place.is_none()
        })
        .await;
    assert_eq!(bench.selection(), Some(hint().selector()));
    assert_eq!(bench.spot(), (None, -40.));
}
#[gpui::test]
async fn a_saved_checkout_no_longer_offered_is_dropped_and_the_place_comes_back_on_the_session_folder(
    cx: &mut TestAppContext,
) {
    let mut bench = bench(cx, json!([])).await;
    bench.bind(
        Some(key(ONE)),
        SavedView {
            checkout_hint: Some(hint()),
            ..at(40.)
        },
    );
    bench.show();
    bench
        .until("the dropped hint", |pane, _, _| {
            pane.checkout_hint.is_none()
        })
        .await;
    assert_eq!(bench.selection(), None);
    assert_eq!(bench.spot(), (None, -40.));
    let pane = bench.pane.clone();
    assert!(
        bench.cx.update(|_, cx| pane.read(cx).result.is_some()),
        "and it says so"
    );
}
#[gpui::test]
async fn the_read_on_its_way_stops_before_the_next_workspace_is_bound(cx: &mut TestAppContext) {
    let mut bench = bench(cx, json!([])).await;
    bench.show();
    bench.answered(1).await;
    bench.seen.lock().unwrap().hold_status = true;
    bench.refresh();
    bench.asked(2).await;
    bench.bind(Some(key(TWO)), SavedView::default());
    bench.asked(3).await;
    bench
        .until("the old read is cancelled", |_, _, seen| seen.cancels > 0)
        .await;
    let pane = bench.pane.clone();
    assert_eq!(
        bench
            .cx
            .update(|_, cx| pane.read(cx).read.read(cx).answers()),
        1
    );
}
#[gpui::test]
async fn a_hidden_pane_restores_without_reading_until_it_is_shown(cx: &mut TestAppContext) {
    let mut bench = bench(cx, linked()).await;
    let open = HashSet::from([Section::Staged]);
    bench.bind(
        Some(key(ONE)),
        SavedView {
            open_sections: open.clone(),
            checkout_hint: Some(hint()),
            ..at(40.)
        },
    );
    bench.delivered().await;
    {
        let seen = bench.seen.lock().unwrap();
        assert_eq!((seen.statuses.len(), seen.lists), (0, 0));
    }
    let pane = bench.pane.clone();
    let saved = bench.cx.update(|_, cx| pane.read(cx).saved_view(&key(ONE)));
    assert_eq!(saved["openSections"], json!(["Staged"]));
    assert_eq!(saved["scrollTop"], 40.);
    assert_eq!(saved["checkoutHint"], serde_json::to_value(hint()).unwrap());
    bench.show();
    bench
        .until("the saved view", |pane, _, _| pane.pending_place.is_none())
        .await;
    assert_eq!(bench.selection(), Some(hint().selector()));
    assert_eq!(
        bench.cx.update(|_, cx| pane.read(cx).open_sections.clone()),
        open
    );
}
#[gpui::test]
async fn a_list_asked_for_under_the_last_workspace_is_not_kept(cx: &mut TestAppContext) {
    let mut bench = bench(cx, linked()).await;
    bench.seen.lock().unwrap().hold_list = true;
    bench.show();
    bench
        .until("the list is on its way", |pane, _, seen| {
            seen.lists == 1 && pane.checkouts_read.is_some()
        })
        .await;
    bench.bind(Some(key(TWO)), SavedView::default());
    let pane = bench.pane.clone();
    bench.cx.update(|_, cx| {
        let pane = pane.read(cx);
        assert!(pane.checkouts_read.is_none() && pane.checkouts.is_none());
    });
    bench.seen.lock().unwrap().hold_list = false;
    bench.refresh();
    bench
        .until("the next workspace's own list", |pane, _, _| {
            pane.checkouts.is_some()
        })
        .await;
}
#[gpui::test]
async fn the_read_following_the_session_keeps_the_view_its_workspace_restored(
    cx: &mut TestAppContext,
) {
    let mut bench = bench(cx, json!([])).await;
    bench.show();
    bench.answered(1).await;
    bench.select("s2");
    let open = HashSet::from([Section::Untracked]);
    bench.bind(
        Some(in_session("s2", ONE)),
        SavedView {
            open_sections: open.clone(),
            ..SavedView::default()
        },
    );
    bench
        .until("the next session", |pane, _, _| {
            pane.last_session.as_deref() == Some("s2")
        })
        .await;
    let pane = bench.pane.clone();
    assert_eq!(
        bench.cx.update(|_, cx| pane.read(cx).open_sections.clone()),
        open
    );
    assert_eq!(bench.spot(), (None, 0.));
    bench.select("s3");
    let nothing = SavedView::parse(&Value::Null, &in_session("s3", ONE)).unwrap();
    bench.bind(Some(in_session("s3", ONE)), nothing);
    bench
        .until("the third session", |pane, _, _| {
            pane.last_session.as_deref() == Some("s3")
        })
        .await;
    assert!(
        bench
            .cx
            .update(|_, cx| pane.read(cx).open_sections.is_empty()),
        "no fallback to another's view"
    );
}
#[gpui::test]
fn what_a_save_holds_is_checked_before_anything_restores(_: &mut TestAppContext) {
    let good = |hint: Value| json!({"openSections":["Untracked","Staged"],"scrollTop":12.5,"checkoutHint":hint});
    let view = SavedView::parse(&good(serde_json::to_value(hint()).unwrap()), &key(ONE)).unwrap();
    assert_eq!(
        view.open_sections,
        HashSet::from([Section::Staged, Section::Untracked])
    );
    assert_eq!((view.scroll_top, view.checkout_hint), (12.5, Some(hint())));
    assert_eq!(
        SavedView::parse(&Value::Null, &key(ONE)),
        Ok(SavedView::default())
    );
    for refused in [
        json!("Staged"),
        json!({"openSections":[],"checkoutHint":null}),
        json!({"openSections":[],"scrollTop":0,"checkoutHint":null,"extra":1}),
        json!({"openSections":["Nope"],"scrollTop":0,"checkoutHint":null}),
        json!({"openSections":["Staged","Staged"],"scrollTop":0,"checkoutHint":null}),
        json!({"openSections":[],"scrollTop":-1,"checkoutHint":null}),
        json!({"openSections":[],"scrollTop":1e300,"checkoutHint":null}),
        json!({"openSections":[],"scrollTop":0,"checkoutHint":{"session":"s1"}}),
    ] {
        assert!(SavedView::parse(&refused, &key(ONE)).is_err(), "{refused}");
    }
    let mut extra = serde_json::to_value(hint()).unwrap();
    extra["other"] = json!(1);
    assert!(
        SavedView::parse(&good(extra), &key(ONE)).is_err(),
        "nested fields are checked too"
    );
    let hint_value = serde_json::to_value(hint()).unwrap();
    for other in [
        key(TWO),
        in_session("s2", ONE),
        WorkspaceKey {
            machine: "else".into(),
            ..key(ONE)
        },
    ] {
        assert!(
            SavedView::parse(&good(hint_value.clone()), &other).is_err(),
            "{other:?}"
        );
    }
}
#[gpui::test]
fn a_workspace_round_trips_through_the_registry_and_only_under_its_own_key(
    cx: &mut TestAppContext,
) {
    cx.update(|cx| {
        gpui_kit::init(cx);
        SelectedSession::init(cx);
        PlaceholderPane::register_all(cx);
        SourceControlPane::register(cx);
        SourceControlPane::register_workspace(cx).unwrap();
    });
    let (_, cx) = cx.add_window_view(|_, _| EmptyView);
    cx.update(|window, cx| {
        let dock = cx.new(|cx| DockArea::new("workspace-source-control", Some(2), window, cx));
        CenterPanes::init(&dock, cx);
        dock.update(cx, |dock, cx| {
            dock.load(default_state(), window, cx).unwrap()
        });
        let saved = json!({"openSections":["Staged","Untracked"],"scrollTop":40.0,
            "checkoutHint":serde_json::to_value(hint()).unwrap()});
        WorkspacePanes::set_key(key(ONE), cx);
        WorkspacePanes::restore(&json!({"version":1,"sourceControl":saved}), window, cx).unwrap();
        assert_eq!(
            WorkspacePanes::capture(window, cx).unwrap()["sourceControl"],
            saved
        );
        WorkspacePanes::set_key(key(TWO), cx);
        assert_eq!(
            WorkspacePanes::capture(window, cx).unwrap()["sourceControl"],
            Value::Null,
            "the pane is still on the last workspace"
        );
        WorkspacePanes::restore(&json!({"version":1}), window, cx).unwrap();
        let defaults = json!({"openSections":[],"scrollTop":0.0,"checkoutHint":null});
        assert_eq!(
            WorkspacePanes::capture(window, cx).unwrap()["sourceControl"],
            defaults
        );
        let refused = json!({"version":1,"sourceControl":saved});
        assert!(
            WorkspacePanes::restore(&refused, window, cx).is_err(),
            "that hint isn't this workspace's"
        );
        assert_eq!(
            WorkspacePanes::capture(window, cx).unwrap()["sourceControl"],
            defaults
        );
        WorkspacePanes::clear_key(cx);
        WorkspacePanes::deactivate(window, cx);
        let (_, _, pane) = SourceControlPane::located(cx).unwrap();
        assert_eq!(pane.read(cx).workspace, None);
        assert_eq!(
            WorkspacePanes::capture(window, cx).unwrap()["sourceControl"],
            Value::Null
        );
    });
}
#[path = "codec_tests/fence_tests.rs"]
mod fence;
