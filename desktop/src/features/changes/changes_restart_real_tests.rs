//! What a person picks in the center tabs against the real backend, across a restart: two apps, one after the other, the
//! second starting from the layout and the workspace the first really saved. A fake saved anything it was handed.

use std::cell::{Cell, RefCell};
use std::rc::Rc;

use futures_channel::mpsc::{UnboundedReceiver, UnboundedSender, unbounded};
use gpui::{TestAppContext, VisualTestContext};
use gpui_kit::component::dock::DockPlacement;
use gpui_kit::{AppContext as _, Entity, Subscription, Task};
use serde_json::{Value, json};

use super::{ChangesPane, DiffMode, Open, Picked};
use crate::core::intents::{
    open_git_history::OpenGitHistory, open_pull_requests::OpenPullRequests,
};
use crate::core::socket::client::SocketClient;
use crate::core::socket::connection_state::ConnectionState;
use crate::core::socket::endpoint::Endpoint;
use crate::core::socket::outcome::Outcome;
use crate::core::socket::real_backend::RealBackend;
use crate::core::socket::real_backend_cancel_tests::{
    add_project, backend_with_stub, stub_session,
};
use crate::core::socket::test_wait::until;
use crate::core::state::workspace_panes::WorkspaceKey;
use crate::core::state::{preferences::Preferences, selected_session::SelectedSession};
use crate::core::ui::center_panes::CenterPanes;
use crate::features::changes::working_copy::Scope;
use crate::features::session::view::session_pane::SessionPane;
use crate::features::shell::frame::Frame;
use crate::features::shell::layout_store::WriteLayout;
use crate::features::shell::placeholder_pane::PlaceholderPane;
use crate::features::source_control::{history_pane::HistoryPane, pull_requests::pr_pane::PrPane};
use crate::features::workspaces::workspace_coordinator::{
    WorkspaceCoordinator, WorkspaceSaved, WorkspaceTarget,
};

type Layout = Rc<RefCell<Option<Value>>>;

/// One app: its sockets, its coordinator and its window's frame, as the app starts them.
struct Running<'a> {
    bulk: Entity<SocketClient>,
    main: Entity<SocketClient>,
    coordinator: Entity<WorkspaceCoordinator>,
    saves: Rc<Cell<usize>>,
    changes: UnboundedReceiver<()>,
    changed: UnboundedSender<()>,
    cx: &'a mut VisualTestContext,
    _heard: Vec<Subscription>,
}

async fn start<'a>(cx: &'a mut TestAppContext, endpoint: Endpoint, layout: &Layout) -> Running<'a> {
    cx.executor().allow_parking();
    let (main, bulk, coordinator) = cx.update(|cx| {
        gpui_kit::init(cx);
        SelectedSession::init(cx);
        Preferences::init(cx);
        PlaceholderPane::register_all(cx);
        SessionPane::register(cx);
        ChangesPane::register(cx);
        HistoryPane::register(cx);
        PrPane::register(cx);
        let main = SocketClient::start(endpoint.clone(), cx);
        let bulk = SocketClient::start_bulk(endpoint, cx);
        crate::features::workspaces::init(cx).expect("the workspace codecs");
        let coordinator = WorkspaceCoordinator::try_global(cx).expect("the coordinator");
        (main, bulk, coordinator)
    });
    let (changed, mut changes) = unbounded();
    let (tell_main, tell_bulk) = (changed.clone(), changed.clone());
    let sockets = cx.update(|cx| {
        [
            cx.observe(&main, move |_, _| _ = tell_main.unbounded_send(())),
            cx.observe(&bulk, move |_, _| _ = tell_bulk.unbounded_send(())),
        ]
    });
    until("both sockets", &mut changes, &changed, || {
        cx.read(|cx| {
            [&main, &bulk]
                .iter()
                .all(|socket| matches!(socket.read(cx).state(), ConnectionState::Connected { .. }))
        })
    })
    .await;
    let saved = layout.borrow().clone();
    let (sink, tell) = (layout.clone(), changed.clone());
    let write: WriteLayout = Box::new(move |value, _| {
        *sink.borrow_mut() = Some(value);
        _ = tell.unbounded_send(());
        Task::ready(())
    });
    let (_, cx) =
        cx.add_window_view(|window, cx| Frame::new(Task::ready(saved), write, window, cx));
    let saves = Rc::new(Cell::new(0));
    let (tell_view, tell_saved, count) = (changed.clone(), changed.clone(), saves.clone());
    let mut heard = cx.update(|_, cx| {
        vec![
            cx.observe(&coordinator, move |_, _| _ = tell_view.unbounded_send(())),
            cx.subscribe(&coordinator, move |_, _: &WorkspaceSaved, _| {
                count.set(count.get() + 1);
                _ = tell_saved.unbounded_send(());
            }),
        ]
    });
    heard.extend(sockets);
    Running {
        bulk,
        main,
        coordinator,
        saves,
        changes,
        changed,
        cx,
        _heard: heard,
    }
}

impl Running<'_> {
    async fn session(&mut self, repo: &str) -> WorkspaceKey {
        let main = self.main.clone();
        let call = self.cx.read(|cx| {
            main.read(cx)
                .call::<Value>("projects", "add", add_project(repo), None)
        });
        let Outcome::Ok(added) = call.await else {
            panic!("the project was not added");
        };
        let call = self.cx.read(|cx| {
            main.read(cx)
                .call::<Value>("sessions", "create", stub_session(&added), None)
        });
        let Outcome::Ok(made) = call.await else {
            panic!("the session was not made");
        };
        WorkspaceKey {
            machine: self
                .cx
                .read(|cx| main.read(cx).engine_machine())
                .expect("the owning Engine"),
            session_id: made["sessionId"].as_str().expect("its id").to_string(),
            checkout_root: repo.to_string(),
        }
    }
    async fn open_workspace(&mut self, key: &WorkspaceKey) {
        let (coordinator, target) = (
            self.coordinator.clone(),
            WorkspaceTarget::Checkout(key.clone()),
        );
        self.cx.update(|window, cx| {
            coordinator.update(cx, |view, cx| {
                view.request(target, Box::new(|_, _, _| Ok(())), window, cx);
            })
        });
        let cx = &mut *self.cx;
        until("the workspace", &mut self.changes, &self.changed, || {
            cx.run_until_parked();
            coordinator.read_with(cx, |view, _| view.current_key() == Some(key))
        })
        .await;
    }
    async fn saved_at_least(&mut self, count: usize) {
        let (cx, saves) = (&mut *self.cx, self.saves.clone());
        until(
            "the workspace save",
            &mut self.changes,
            &self.changed,
            || {
                cx.run_until_parked();
                saves.get() >= count
            },
        )
        .await;
    }
    /// What the backend holds for this workspace, read over the connection that saves it.
    async fn stored_panes(&mut self, key: &WorkspaceKey) -> Value {
        let bulk = self.bulk.clone();
        let args = json!({"machine": key.machine, "sessionId": key.session_id, "checkoutRoot": key.checkout_root});
        let call = self.cx.read(|cx| {
            bulk.read(cx)
                .call::<Value>("workspaces", "read", args, None)
        });
        let Outcome::Ok(stored) = call.await else {
            panic!("the workspace did not read");
        };
        stored["snapshot"]["topTabs"]["panes"].clone()
    }
    fn changes_pane(&mut self) -> Entity<ChangesPane> {
        self.cx
            .update(|_, cx| cx.try_global::<Open>().and_then(|open| open.0.upgrade()))
            .expect("the Changes tab")
    }
    fn center_tabs(&mut self) -> Vec<String> {
        self.cx.update(|_, cx| {
            let dock = CenterPanes::dock(cx).expect("the dock");
            let dock = dock.read(cx);
            let mut names = Vec::new();
            if let Some(tree) = dock.layout(DockPlacement::Center) {
                tree.root().walk(&mut |node| {
                    if let gpui_kit::component::dock::PaneRef::Tabs { panels, .. } = node.kind() {
                        names.extend(
                            panels
                                .iter()
                                .filter_map(|id| dock.panel(*id))
                                .map(|panel| panel.panel_name(cx).to_string()),
                        );
                    }
                });
            }
            names
        })
    }
}

fn panel(panes: &Value, name: &str) -> Value {
    let panes = panes.as_array().cloned().unwrap_or_default();
    panes
        .iter()
        .find(|pane| pane["state"]["panel_name"] == name)
        .map_or(Value::Null, |pane| pane["state"]["info"]["panel"].clone())
}

fn backend() -> (RealBackend, String) {
    backend_with_stub().expect("the desktop backend is not built")
}

#[gpui::test]
async fn a_picked_split_and_scope_come_back_after_a_restart(
    first: &mut TestAppContext,
    second: &mut TestAppContext,
) {
    let (backend, repo) = backend();
    let layout = Layout::default();
    let mut app = start(first, backend.endpoint(), &layout).await;
    let key = app.session(&repo).await;
    app.open_workspace(&key).await;
    app.cx
        .update(|window, cx| ChangesPane::open(Picked::File("README".into()), window, cx));
    app.cx.run_until_parked();
    let pane = app.changes_pane();
    let session = key.session_id.clone();
    pane.update(app.cx, |pane, _| pane.owner = Some((session, None)));
    let cx = &mut *app.cx;
    cx.update(|_, cx| WorkspaceCoordinator::changed(cx));
    app.saved_at_least(1).await;
    let saved = app.saves.get();

    // A person's two clicks, each with its own save.
    pane.update(app.cx, |pane, cx| pane.set_mode(DiffMode::Split, cx));
    app.cx.run_until_parked();
    pane.update(app.cx, |pane, cx| pane.set_scope(Scope::Branch, cx));
    app.saved_at_least(saved + 2).await;
    let stored = panel(&app.stored_panes(&key).await, "Changes");
    assert_eq!(stored["split"], true);
    assert_eq!(stored["scope"], "branch");
    let wrote = |layout: &Layout| {
        layout.borrow().as_ref().is_some_and(|layout| {
            let layout = layout.to_string();
            layout.contains("\"split\":true") && layout.contains("\"scope\":\"branch\"")
        })
    };
    let (cx, changed) = (&mut *app.cx, app.changed.clone());
    until("the device layout", &mut app.changes, &changed, || {
        cx.run_until_parked();
        wrote(&layout)
    })
    .await;

    // The app has no save of its own on the way out: what comes back is what those clicks saved.
    let mut again = start(second, backend.endpoint(), &layout).await;
    again.open_workspace(&key).await;
    let pane = again.changes_pane();
    pane.read_with(again.cx, |pane, _| {
        assert!(pane.mode == DiffMode::Split, "the split came back");
        assert!(pane.scope == Scope::Branch, "the scope came back");
    });
}

#[gpui::test]
async fn tabs_opened_in_a_session_never_saved_come_back_after_a_restart(
    first: &mut TestAppContext,
    second: &mut TestAppContext,
) {
    let (backend, repo) = backend();
    let layout = Layout::default();
    let mut app = start(first, backend.endpoint(), &layout).await;
    let key = app.session(&repo).await;
    app.open_workspace(&key).await;
    app.cx
        .update(|window, cx| ChangesPane::open(Picked::File("README".into()), window, cx));
    app.saved_at_least(1).await;
    let history = OpenGitHistory {
        session_id: key.session_id.clone(),
        expected_source: None,
        path: None,
        checkout: None,
    };
    app.cx.update(|_, cx| cx.dispatch_action(&history));
    app.saved_at_least(2).await;
    let requests = OpenPullRequests {
        session_id: Some(key.session_id.clone()),
        repo: None,
        number: None,
    };
    app.cx.update(|_, cx| cx.dispatch_action(&requests));
    app.saved_at_least(3).await;

    let mut again = start(second, backend.endpoint(), &layout).await;
    again.open_workspace(&key).await;
    let tabs = again.center_tabs();
    for tab in ["Changes", "Git history", "Pull requests"] {
        assert!(
            tabs.iter().any(|name| name == tab),
            "{tab} came back: {tabs:?}"
        );
    }
}
