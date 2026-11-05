//! `OpenFile` reaches the real listener and opens a real tab wherever focus is, or with no window active: a link from
//! the system can arrive at any of these moments.

use gpui::TestAppContext;
use gpui_kit::component::dock::DockArea;
use gpui_kit::{
    AppContext as _, Context, Entity, FocusHandle, InteractiveElement as _, IntoElement,
    ParentElement as _, Render, VisualTestContext, Window, div,
};

use super::{FileView, key};
use crate::core::intents::open_file::OpenFile;
use crate::core::state::selected_session::SelectedSession;
use crate::core::ui::center_panes::CenterPanes;
use crate::core::window_root::WindowRoot;

/// What the window root draws, focusable.
struct Content {
    focus: FocusHandle,
}

impl Render for Content {
    fn render(&mut self, _: &mut Window, _: &mut Context<Self>) -> impl IntoElement {
        div().track_focus(&self.focus)
    }
}

/// The window root beside a layer of its own, as the kit's `Root` draws dialogs beside its content.
struct Stage {
    root: Entity<WindowRoot>,
    layer: FocusHandle,
}

impl Render for Stage {
    fn render(&mut self, _: &mut Window, _: &mut Context<Self>) -> impl IntoElement {
        div()
            .child(self.root.clone())
            .child(div().track_focus(&self.layer))
    }
}

struct Harness<'a> {
    cx: &'a mut VisualTestContext,
    inside: FocusHandle,
    layer: FocusHandle,
    _dock: Entity<DockArea>,
}

fn harness(cx: &mut TestAppContext) -> Harness<'_> {
    let (inside, layer) = cx.update(|cx| {
        gpui_kit::init(cx);
        SelectedSession::init(cx);
        FileView::register(cx);
        (cx.focus_handle(), cx.focus_handle())
    });
    let (focus, beside) = (inside.clone(), layer.clone());
    let (_, cx) = cx.add_window_view(move |window, cx| {
        let content = cx.new(|_| Content { focus });
        let root = cx.new(|cx| WindowRoot::new(content, window, cx));
        let stage = cx.new(|_| Stage {
            root,
            layer: beside,
        });
        gpui_kit::base::Root::new(stage, window, cx)
    });
    let dock = cx.update(|window, cx| {
        let dock = cx.new(|cx| DockArea::new("open-file-intent", None, window, cx));
        CenterPanes::init(&dock, cx);
        dock
    });
    cx.update(|window, _| window.activate_window());
    cx.run_until_parked();
    Harness {
        cx,
        inside,
        layer,
        _dock: dock,
    }
}

impl Harness<'_> {
    /// As a link does: outside any window update, and with the window drawn.
    fn open_file(&mut self) {
        self.cx.update(|window, cx| window.draw(cx).clear(cx));
        self.cx.update(|_, cx| {
            cx.defer(|cx| {
                cx.dispatch_action(&OpenFile {
                    session_id: "s1".into(),
                    path: "src/main.rs".into(),
                    expected_source: None,
                    line: None,
                    column: None,
                    pinned: true,
                    outside: None,
                })
            })
        });
        self.cx.run_until_parked();
    }

    fn opened(&mut self) -> Option<(String, String, bool)> {
        self.cx.update(|_, cx| {
            let view = CenterPanes::find::<FileView>(&key("s1", "src/main.rs"), cx)?;
            let view = view.read(cx);
            Some((view.session.clone(), view.path.clone(), view.pinned))
        })
    }

    fn assert_opened(&mut self) {
        assert_eq!(
            self.opened(),
            Some(("s1".to_string(), "src/main.rs".to_string(), true))
        );
    }
}

#[gpui::test]
async fn it_opens_with_nothing_focused(cx: &mut TestAppContext) {
    let mut h = harness(cx);
    h.cx.update(|window, cx| assert!(window.focused(cx).is_none()));
    h.open_file();
    h.assert_opened();
}

#[gpui::test]
async fn it_opens_with_no_window_active(cx: &mut TestAppContext) {
    let mut h = harness(cx);
    h.cx.deactivate_window();
    h.cx.update(|_, cx| assert!(cx.active_window().is_none()));
    h.open_file();
    h.assert_opened();
}

#[gpui::test]
async fn it_opens_with_focus_in_a_layer_beside_the_window_root(cx: &mut TestAppContext) {
    let mut h = harness(cx);
    let layer = h.layer.clone();
    h.cx.update(|window, cx| window.focus(&layer, cx));
    h.cx.run_until_parked();
    h.cx.update(|window, _| assert!(layer.is_focused(window)));
    h.open_file();
    h.assert_opened();
}

// The control: this one needs the window drawn, and it's how a person's click in a pane reaches the intent.
#[gpui::test]
async fn it_opens_with_focus_inside_the_window_root(cx: &mut TestAppContext) {
    let mut h = harness(cx);
    let inside = h.inside.clone();
    h.cx.update(|window, cx| window.focus(&inside, cx));
    h.cx.run_until_parked();
    h.cx.update(|window, _| assert!(inside.is_focused(window)));
    h.open_file();
    h.assert_opened();
}

// Feature 28, ruling 15: an absolute or `~` path the intent doesn't place outside goes to the one resolver, with the
// intent's session and its source's route machine, instead of opening as a session-relative path.
#[gpui::test]
async fn an_absolute_or_tilde_path_goes_to_the_resolver_instead_of_opening(
    cx: &mut TestAppContext,
) {
    use crate::core::navigation::deep_links::notices;
    use crate::core::socket::row_source::ExpectedRowSource;
    use crate::core::state::machines::Machines;
    let h = harness(cx);
    h.cx.update(|_, cx| {
        Machines::start_holding(
            vec![(
                "Box".into(),
                serde_json::json!({"name": "Box", "kind": "remote", "reachable": true, "checked": true}),
            )],
            cx,
        )
    });
    let send = |path: &str, route: Option<&str>| OpenFile {
        session_id: "s1".into(),
        path: path.into(),
        expected_source: Some(ExpectedRowSource {
            machine: "owner".into(),
            generation: 0,
            route_machine: route.map(String::from),
        }),
        line: None,
        column: None,
        pinned: true,
        outside: None,
    };
    for intent in [
        send("/etc/hosts", None),
        send("~/notes.txt", Some("Nowhere")),
    ] {
        h.cx.update(|_, cx| cx.defer(move |cx| cx.dispatch_action(&intent)));
        h.cx.run_until_parked();
    }
    let shown = h.cx.update(|_, cx| notices(cx));
    assert_eq!(
        shown,
        [
            "That file didn't open: The backend isn't running.",
            "That file is on Nowhere, which this app isn't connected to.",
        ]
    );
    let tabs = h.cx.update(|_, cx| {
        [key("s1", "/etc/hosts"), key("s1", "~/notes.txt")]
            .iter()
            .filter(|tab| CenterPanes::find::<FileView>(tab, cx).is_some())
            .count()
    });
    assert_eq!(tabs, 0, "neither opened as a session file");
}
