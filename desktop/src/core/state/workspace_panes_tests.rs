use super::{MAX_SNAPSHOT_BYTES, PaneCodec, WorkspaceKey, WorkspacePanes, validate};
use gpui::TestAppContext;
use gpui_kit::{App, Context, IntoElement, Render, Window, div};
use serde_json::{Value, json};
use std::cell::RefCell;
use std::rc::Rc;

struct Host;
impl Render for Host {
    fn render(&mut self, _: &mut Window, _: &mut Context<Self>) -> impl IntoElement {
        div()
    }
}

struct Probe {
    state: Value,
    restored: Rc<RefCell<Vec<Value>>>,
}
impl PaneCodec for Probe {
    fn name(&self) -> &'static str {
        "files"
    }
    fn capture(&self, _: &Window, _: &App) -> Result<Value, String> {
        Ok(self.state.clone())
    }
    fn validate(&self, _: &Value, _: &WorkspaceKey, _: &Value, _: &App) -> Result<(), String> {
        Ok(())
    }
    fn restore(&self, value: &Value, _: &mut Window, _: &mut App) {
        self.restored.borrow_mut().push(value.clone());
    }
}

#[gpui::test]
fn codec_roundtrip_preserves_drafts_and_unknown_versions_never_restore(cx: &mut TestAppContext) {
    let restored = Rc::new(RefCell::new(Vec::new()));
    cx.update(|cx| {
        WorkspacePanes::register(Box::new(Probe { state: json!({"files":[{"path":"a","draft":"é\r\n","baseRevision":"r"}],"activePath":"a"}), restored: restored.clone() }), cx).unwrap();
    });
    cx.update(|cx| {
        WorkspacePanes::set_key(
            WorkspaceKey {
                machine: "m".into(),
                session_id: "s".into(),
                checkout_root: "/checkout".into(),
            },
            cx,
        )
    });
    let (_, cx) = cx.add_window_view(|_, _| Host);
    cx.update(|window, cx| {
        let snapshot = WorkspacePanes::capture(window, cx).unwrap();
        assert_eq!(snapshot["files"][0]["draft"], "é\r\n");
        assert_eq!(snapshot["activePath"], "a");
        WorkspacePanes::restore(&snapshot, window, cx).unwrap();
        assert_eq!(restored.borrow().len(), 1);
        assert!(WorkspacePanes::restore(&json!({"version":999,"files":[]}), window, cx).is_err());
        assert_eq!(restored.borrow().len(), 1);
    });
}

#[gpui::test]
fn oversized_drafts_and_duplicate_codecs_are_refused_instead_of_silently_dropped(
    cx: &mut TestAppContext,
) {
    let restored = Rc::new(RefCell::new(Vec::new()));
    cx.update(|cx| {
        WorkspacePanes::register(
            Box::new(Probe {
                state: json!({"files":[{"path":"a","draft":"x".repeat(MAX_SNAPSHOT_BYTES)}]}),
                restored: restored.clone(),
            }),
            cx,
        )
        .unwrap();
        assert!(
            WorkspacePanes::register(
                Box::new(Probe {
                    state: Value::Null,
                    restored: restored.clone()
                }),
                cx
            )
            .is_err()
        );
    });
    let (_, cx) = cx.add_window_view(|_, _| Host);
    cx.update(|window, cx| assert!(WorkspacePanes::capture(window, cx).is_err()));
}

#[test]
fn operational_snapshot_fields_are_never_accepted_as_workspace_view_state() {
    assert!(validate(&json!({"version":1,"files":[],"run":{"state":"running"}})).is_err());
    assert!(validate(&json!({"version":1,"files":[],"permissions":{}})).is_err());
    assert!(
        validate(&json!({"version":1,"files":[],"chat":{"offset":5,"following":false}})).is_ok()
    );
}

struct Unavailable;
impl PaneCodec for Unavailable {
    fn name(&self) -> &'static str {
        "tree"
    }
    fn capture(&self, _: &Window, _: &App) -> Result<Value, String> {
        Err("The tree capture is unavailable.".into())
    }
    fn validate(&self, _: &Value, _: &WorkspaceKey, _: &Value, _: &App) -> Result<(), String> {
        Ok(())
    }
    fn restore(&self, _: &Value, _: &mut Window, _: &mut App) {
        panic!("failed capture cannot restore")
    }
}
#[gpui::test]
fn capture_failure_preserves_the_reason_instead_of_saving_an_empty_pane(cx: &mut TestAppContext) {
    cx.update(|cx| WorkspacePanes::register(Box::new(Unavailable), cx).unwrap());
    let (_, cx) = cx.add_window_view(|_, _| Host);
    cx.update(|window, cx| {
        assert_eq!(
            WorkspacePanes::capture(window, cx).unwrap_err(),
            "The tree capture is unavailable."
        )
    });
}
