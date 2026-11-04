use super::{DocumentStore, WorkspaceKey, run};
use crate::features::files::document_store::DocumentKey;
use gpui::TestAppContext;
use gpui_kit::{AppContext as _, Context, IntoElement, Render, Window, div};

struct Host;
impl Render for Host {
    fn render(&mut self, _: &mut Window, _: &mut Context<Self>) -> impl IntoElement {
        div()
    }
}
fn setup(cx: &mut TestAppContext) -> (WorkspaceKey, DocumentKey) {
    cx.update(|cx| {
        gpui_kit::init(cx);
        let key = WorkspaceKey {
            machine: "actual-source".into(),
            session_id: "s".into(),
            checkout_root: "/checkout".into(),
        };
        let document = DocumentKey {
            machine: key.machine.clone(),
            checkout_root: key.checkout_root.clone(),
            path: "a".into(),
        };
        let mut docs = DocumentStore::default();
        docs.open(document.clone(), "r".into(), "disk".into())
            .unwrap();
        docs.replace(&document, 0, "preserved draft".into())
            .unwrap();
        cx.set_global(docs);
        (key, document)
    })
}
#[gpui::test]
async fn cancelling_the_workspace_guard_preserves_the_dirty_draft(cx: &mut TestAppContext) {
    let (key, document) = setup(cx);
    let (_, cx) = cx.add_window_view(|_, _| Host);
    let task = cx.update(|window, cx| run(key, window, cx));
    cx.run_until_parked();
    cx.simulate_prompt_answer("Cancel");
    cx.run_until_parked();
    assert!(!task.await);
    cx.update(|_, cx| {
        assert!(cx.global::<DocumentStore>().is_dirty(&document));
        assert_eq!(
            cx.global::<DocumentStore>()
                .snapshot(&document)
                .unwrap()
                .text,
            "preserved draft"
        );
    });
}
#[gpui::test]
async fn a_newer_draft_during_confirmation_is_not_discarded(cx: &mut TestAppContext) {
    let (key, document) = setup(cx);
    let (_, cx) = cx.add_window_view(|_, _| Host);
    let task = cx.update(|window, cx| run(key, window, cx));
    cx.run_until_parked();
    cx.update(|_, cx| {
        cx.global_mut::<DocumentStore>()
            .replace(&document, 1, "newer draft".into())
            .unwrap();
    });
    cx.simulate_prompt_answer("Discard");
    cx.run_until_parked();
    assert!(!task.await);
    cx.update(|_, cx| {
        assert_eq!(
            cx.global::<DocumentStore>()
                .snapshot(&document)
                .unwrap()
                .text,
            "newer draft"
        )
    });
}
