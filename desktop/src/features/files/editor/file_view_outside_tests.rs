//! Feature 28, task 6: an outside file opens by its path target, keys its draft by its engine, real parent and name,
//! saves by target, and comes back locked. Session files keep sending their session and relative path.
use super::super::{DocumentKey, DocumentStore, FILE, FileView};
use super::outside_key;
use crate::core::intents::open_file::{OpenFile, OutsideFile};
use crate::core::socket::{
    client::SocketClient,
    endpoint::Endpoint,
    fake_backend::{FakeBackend, Reply, hello},
    test_wait::until,
};
use crate::core::state::selected_session::SelectedSession;
use crate::core::ui::center_panes::CenterPanes;
use crate::features::workspaces::workspace_coordinator::{
    WorkspaceActivated, WorkspaceCoordinator, WorkspaceTarget,
};
use futures_channel::mpsc::unbounded;
use gpui::{TestAppContext, VisualTestContext};
use gpui_kit::component::dock::{
    BasePanel, DockArea, PanelBuildContext, PanelHandle, PanelRegistry,
};
use gpui_kit::{
    AnyView, AppContext as _, Context, Entity, IntoElement, ParentElement as _, Render,
    Styled as _, WeakEntity, Window, div,
};
use serde_json::{Value, json};
use std::sync::{Arc, Mutex};

/// The test window's root: it draws the file view a test shows, as its tab would.
pub(crate) struct Host {
    shown: Option<AnyView>,
}
impl Render for Host {
    fn render(&mut self, _: &mut Window, _: &mut Context<Self>) -> impl IntoElement {
        div().size_full().children(self.shown.clone())
    }
}

/// Each files call's method and arguments, in the order sent.
pub(crate) type Calls = Arc<Mutex<Vec<(String, Value)>>>;
/// A test's answer to a files call: its result, or an error's code and message.
pub(crate) type Answer =
    Box<dyn FnMut(&str, &Value) -> Result<Value, (&'static str, &'static str)> + Send>;

pub(crate) const ENGINE: &str = "engine-1";

pub(crate) fn hosts() -> OutsideFile {
    OutsideFile {
        machine: None,
        path: "/etc/hosts".into(),
    }
}

pub(crate) fn owner() -> Value {
    json!({"machine": ENGINE, "checkoutRoot": "/etc", "remoteSessionId": "", "file": "hosts"})
}

/// The answers a backend gives an outside file that opens and saves.
pub(crate) fn answers(method: &str, _: &Value) -> Result<Value, (&'static str, &'static str)> {
    Ok(match method {
        "owner" => owner(),
        "read" => {
            json!({"content": "disk\n", "size": 5, "revision": "r1", "lineCount": 1, "binary": false, "nextOffset": null})
        }
        "beginSave" => json!({"upload": "u1"}),
        "commitSave" => json!({"state": "saved", "revision": "r2"}),
        _ => json!({}),
    })
}

pub(crate) struct Stage<'a> {
    pub cx: &'a mut VisualTestContext,
    calls: Calls,
    host: Entity<Host>,
    _backend: FakeBackend,
    _dock: Entity<DockArea>,
}

pub(crate) fn stage(cx: &mut TestAppContext, mut answer: Answer) -> Stage<'_> {
    let calls = Calls::default();
    let seen = calls.clone();
    let backend = FakeBackend::start(move |frame| {
        if let Some(reply) = hello(frame) {
            return reply;
        }
        if frame["t"] != "call" || frame["feature"] != "files" {
            return Vec::new();
        }
        let method = frame["method"].as_str().unwrap().to_string();
        let args = frame["args"].clone();
        seen.lock().unwrap().push((method.clone(), args.clone()));
        vec![Reply::Frame(match answer(&method, &args) {
            Ok(result) => json!({"t": "result", "id": frame["id"], "result": result}),
            Err((code, message)) => {
                json!({"t": "error", "id": frame["id"], "code": code, "message": message})
            }
        })]
    });
    cx.executor().allow_parking();
    cx.update(|cx| {
        gpui_kit::init(cx);
        SelectedSession::init(cx);
        FileView::register(cx);
        let endpoint = Endpoint {
            port: backend.port,
            token: "secret".into(),
        };
        SocketClient::start(endpoint.clone(), cx);
        SocketClient::start_bulk(endpoint, cx);
    });
    let (host, cx) = cx.add_window_view(|_, _| Host { shown: None });
    let dock = cx.update(|window, cx| {
        let dock = cx.new(|cx| DockArea::new("outside-files", Some(2), window, cx));
        CenterPanes::init(&dock, cx);
        window.activate_window();
        dock
    });
    Stage {
        cx,
        calls,
        host,
        _backend: backend,
        _dock: dock,
    }
}

impl Stage<'_> {
    pub(crate) fn open(&mut self, session: &str, path: &str, outside: Option<OutsideFile>) {
        let intent = OpenFile {
            session_id: session.into(),
            path: path.into(),
            expected_source: None,
            line: None,
            column: None,
            pinned: true,
            outside,
        };
        self.cx
            .update(|_, cx| cx.defer(move |cx| cx.dispatch_action(&intent)));
        self.cx.run_until_parked();
    }

    pub(crate) fn tab(&mut self, alias: &str) -> Entity<FileView> {
        self.cx
            .update(|_, cx| CenterPanes::find::<FileView>(alias, cx))
            .expect("the file's tab")
    }

    pub(crate) async fn until(
        &mut self,
        view: &Entity<FileView>,
        what: &str,
        done: impl Fn(&FileView) -> bool,
    ) {
        let (changed, mut changes) = unbounded();
        let tell = changed.clone();
        let _heard = self.cx.update(|_, cx| {
            cx.observe(view, move |_, _| {
                let _ = tell.unbounded_send(());
            })
        });
        until(what, &mut changes, &changed, || {
            view.read_with(self.cx, |view, _| done(view))
        })
        .await;
    }

    /// Waits until the file's text is read, then puts it in its editor, as drawing the tab does.
    pub(crate) async fn loaded(&mut self, view: &Entity<FileView>) {
        self.until(view, "the file's text", |view| {
            view.text.content().is_some() && !view.text.is_loading()
        })
        .await;
        view.update_in(self.cx, |view, window, cx| view.bind_editor(window, cx));
        self.cx.run_until_parked();
    }

    pub(crate) fn sent(&self, method: &str) -> Vec<Value> {
        let calls = self.calls.lock().unwrap();
        calls
            .iter()
            .filter(|(sent, _)| sent == method)
            .map(|(_, args)| args.clone())
            .collect()
    }

    /// Types `text` over the file's whole draft.
    pub(crate) fn edit(&mut self, view: &Entity<FileView>, text: &'static str) {
        view.update_in(self.cx, |view, window, cx| {
            let editor = view.editor.as_ref().expect("the file's editor");
            editor
                .state
                .update(cx, |state, cx| state.set_value(text, window, cx));
            view.editor_changed(window, cx);
        });
    }

    /// Draws `view` as the window's content, as its tab would be drawn.
    pub(crate) fn show(&mut self, view: &Entity<FileView>) {
        let shown = AnyView::from(view.clone());
        self.host.update(self.cx, |host, cx| {
            host.shown = Some(shown);
            cx.notify();
        });
        self.draw();
    }

    pub(crate) fn draw(&mut self) {
        self.cx.update(|window, cx| window.draw(cx).clear(cx));
    }
}

pub(crate) fn hosts_document() -> DocumentKey {
    DocumentKey {
        machine: ENGINE.into(),
        checkout_root: "/etc".into(),
        path: "hosts".into(),
    }
}

pub(crate) fn target() -> Value {
    json!({"path": "/etc/hosts", "machine": null})
}

#[gpui::test]
async fn an_outside_file_loads_by_target_and_keys_its_draft_by_real_parent_and_name(
    cx: &mut TestAppContext,
) {
    let mut stage = stage(cx, Box::new(answers));
    stage.open("", "/etc/hosts", Some(hosts()));
    let view = stage.tab(&outside_key(None, "/etc/hosts"));
    stage.loaded(&view).await;

    assert_eq!(
        stage.sent("owner"),
        [json!({"target": target(), "expectedOwner": null})]
    );
    assert_eq!(
        stage.sent("read"),
        [json!({"target": target(), "expectedOwner": owner(), "offset": 0, "revision": null})]
    );
    view.read_with(stage.cx, |view, cx| {
        assert_eq!(view.document.as_ref(), Some(&hosts_document()));
        assert!(view.locked, "an outside file opens locked");
        assert_eq!(
            cx.global::<DocumentStore>()
                .snapshot(&hosts_document())
                .unwrap()
                .text,
            "disk\n"
        );
    });
}

#[gpui::test]
fn an_outside_tab_is_saved_with_its_machine_and_path_and_rebuilt_locked(cx: &mut TestAppContext) {
    cx.update(|cx| {
        gpui_kit::init(cx);
        FileView::register(cx);
    });
    let file = OutsideFile {
        machine: Some("workbox-test".into()),
        path: "/etc/hosts".into(),
    };
    let (view, cx) = cx.add_window_view(|_, cx| FileView::new_outside(file, None, None, cx));
    let state = cx.update(|_, cx| BasePanel::dump(view.read(cx), cx));
    assert_eq!(state.panel_name, FILE);
    assert_eq!(
        serde_json::to_value(&state.info).unwrap(),
        json!({ "panel": { "outside": true, "machine": "workbox-test", "path": "/etc/hosts" } })
    );
    let rebuilt = cx.update(|window, cx| {
        let context = PanelBuildContext::new(WeakEntity::new_invalid(), &state, &state.info);
        PanelRegistry::build_panel(FILE, context, window, cx).expect("File is registered")
    });
    assert_eq!(cx.update(|_, cx| rebuilt.dump(cx)), state);
    let rebuilt = PanelHandle::of(&rebuilt)
        .and_then(|panel| panel.view().downcast::<FileView>().ok())
        .expect("a file view");
    rebuilt.read_with(cx, |view, _| {
        assert!(view.locked, "a rebuilt outside tab comes back locked");
        assert_eq!(
            view.tab_key(),
            outside_key(Some("workbox-test"), "/etc/hosts")
        );
    });
}

#[gpui::test]
async fn a_link_and_its_target_end_in_one_tab(cx: &mut TestAppContext) {
    let mut stage = stage(cx, Box::new(answers));
    stage.open("", "/etc/hosts", Some(hosts()));
    let first = stage.tab(&outside_key(None, "/etc/hosts"));
    stage.loaded(&first).await;
    let link = OutsideFile {
        machine: None,
        path: "/link/hosts".into(),
    };
    stage.open("", "/link/hosts", Some(link));
    let second = stage.tab(&outside_key(None, "/link/hosts"));
    stage.loaded(&second).await;

    let views = stage.cx.update(|_, cx| CenterPanes::file_views(cx));
    assert_eq!(views.len(), 1, "one tab for one file");
    assert!(views[0] == first, "the tab opened first is kept");
}

#[gpui::test]
async fn saving_an_unlocked_outside_file_sends_the_target_and_the_revision(
    cx: &mut TestAppContext,
) {
    let mut stage = stage(cx, Box::new(answers));
    stage.open("", "/etc/hosts", Some(hosts()));
    let view = stage.tab(&outside_key(None, "/etc/hosts"));
    stage.loaded(&view).await;
    view.update(stage.cx, |view, _| view.locked = false);
    stage.edit(&view, "edited\n");
    view.update(stage.cx, |view, cx| view.save_document(cx));
    stage
        .until(&view, "the save", |view| view.saving.is_none())
        .await;

    assert_eq!(
        stage.sent("beginSave"),
        [
            json!({"target": target(), "expectedOwner": owner(), "expectedRevision": "r1", "size": 7})
        ]
    );
    assert_eq!(stage.sent("commitSave"), [json!({"upload": "u1"})]);
    view.read_with(stage.cx, |view, cx| {
        let docs = cx.global::<DocumentStore>();
        assert!(!docs.is_dirty(&hosts_document()));
        assert_eq!(docs.base_revision(&hosts_document()), Some("r2"));
        assert_eq!(view.editor_error, None);
    });
}

#[gpui::test]
async fn a_moved_file_keeps_the_draft_and_says_so(cx: &mut TestAppContext) {
    let moved = |method: &str, args: &Value| match method {
        "beginSave" => Err(("checkout_changed", "the file moved; the draft is preserved")),
        _ => answers(method, args),
    };
    let mut stage = stage(cx, Box::new(moved));
    stage.open("", "/etc/hosts", Some(hosts()));
    let view = stage.tab(&outside_key(None, "/etc/hosts"));
    stage.loaded(&view).await;
    view.update(stage.cx, |view, _| view.locked = false);
    stage.edit(&view, "kept draft\n");
    view.update(stage.cx, |view, cx| view.save_document(cx));
    stage
        .until(&view, "the refused save", |view| view.saving.is_none())
        .await;

    view.read_with(stage.cx, |view, cx| {
        assert_eq!(
            view.editor_error.as_deref(),
            Some("the file moved; the draft is preserved")
        );
        let docs = cx.global::<DocumentStore>();
        assert!(docs.is_dirty(&hosts_document()));
        assert_eq!(docs.draft(&hosts_document()), Some("kept draft\n"));
    });
}

#[gpui::test]
async fn a_gone_file_shows_the_missing_file_words_in_the_tab(cx: &mut TestAppContext) {
    let gone = |method: &str, args: &Value| match method {
        "owner" => Err(("failed", "that file isn't there")),
        _ => answers(method, args),
    };
    let mut stage = stage(cx, Box::new(gone));
    stage.open("", "/etc/hosts", Some(hosts()));
    let view = stage.tab(&outside_key(None, "/etc/hosts"));
    stage
        .until(&view, "the refusal", |view| view.text.error.is_some())
        .await;

    view.read_with(stage.cx, |view, _| {
        assert_eq!(view.text.error.as_deref(), Some("that file isn't there"));
        assert!(view.document.is_none());
    });
    assert!(stage.sent("read").is_empty());
}

#[gpui::test]
async fn session_files_still_send_session_and_relative_path(cx: &mut TestAppContext) {
    let session = |method: &str, args: &Value| match method {
        "owner" => Ok(json!({"machine": ENGINE, "checkoutRoot": "/work", "remoteSessionId": "s1"})),
        _ => answers(method, args),
    };
    let mut stage = stage(cx, Box::new(session));
    stage.open("s1", "src/a.rs", None);
    let view = stage.tab(&FileView::session_tab_key("s1", "src/a.rs"));
    stage.loaded(&view).await;
    stage.edit(&view, "edited\n");
    view.update(stage.cx, |view, cx| view.save_document(cx));
    stage
        .until(&view, "the save", |view| view.saving.is_none())
        .await;

    let owner = json!({"machine": ENGINE, "checkoutRoot": "/work", "remoteSessionId": "s1"});
    assert_eq!(
        stage.sent("owner"),
        [json!({"session": "s1", "expectedSource": null, "expectedOwner": null})]
    );
    assert_eq!(
        stage.sent("read"),
        [
            json!({"session": "s1", "path": "src/a.rs", "expectedSource": null, "expectedOwner": owner,
            "offset": 0, "revision": null})
        ]
    );
    assert_eq!(
        stage.sent("beginSave"),
        [
            json!({"session": "s1", "path": "src/a.rs", "expectedSource": null, "expectedOwner": owner,
            "expectedRevision": "r1", "size": 7})
        ]
    );
    view.read_with(stage.cx, |view, _| assert!(!view.locked));
}

// Ruling 9: a transcript has no checkout to open session files from, but an outside file needs none.
#[gpui::test]
async fn a_link_with_no_session_opens_with_a_coordinator_present(cx: &mut TestAppContext) {
    let mut stage = stage(cx, Box::new(answers));
    let coordinator = stage.cx.update(|_, cx| WorkspaceCoordinator::init(cx));
    let (sent, mut activated) = unbounded();
    let _heard = stage.cx.update(|_, cx| {
        cx.subscribe(&coordinator, move |_, event: &WorkspaceActivated, _| {
            sent.unbounded_send(event.clone()).unwrap();
        })
    });
    let transcript = WorkspaceTarget::TranscriptOnly(crate::core::engine::SessionKey {
        machine: ENGINE.into(),
        id: "history".into(),
    });
    stage.cx.update(|window, cx| {
        coordinator.update(cx, |view, cx| {
            view.request(transcript, Box::new(|_, _, _| Ok(())), window, cx)
        })
    });
    activated.recv().await.unwrap();

    stage.open("", "/etc/hosts", Some(hosts()));
    let view = stage.tab(&outside_key(None, "/etc/hosts"));
    stage.loaded(&view).await;

    view.read_with(stage.cx, |view, _| {
        assert_eq!(view.text.error, None);
        assert_eq!(view.document.as_ref(), Some(&hosts_document()));
    });
    let status = stage.cx.update(|_, cx| coordinator.read(cx).status.clone());
    assert_eq!(status, None);
}
