//! Brief item 3: an outside tab opened with no workspace closes when a session opens (ruling 8), and isn't saved
//! anywhere, so a draft in it is never dropped silently. The drafts guard asks first, in its own words, before a session
//! opens and before the window closes.
use super::outside_tests::{Stage, hosts_document, stage};
use super::{WorkspaceActivated, WorkspaceKey, WorkspaceRefused, WorkspaceTarget};
use crate::core::ui::center_panes::CenterPanes;
use crate::features::files::document_store::DocumentStore;
use crate::features::workspaces::drafts_guard;
use futures_channel::mpsc::unbounded;
use gpui::TestAppContext;
use gpui_kit::Subscription;

const ASKED: &str = "This file has unsaved changes. Save them before leaving?";
const REFUSED: &str = "Not saved: the current workspace and its drafts remain open.";

fn key() -> WorkspaceKey {
    WorkspaceKey {
        machine: "actual-owner".into(),
        session_id: "session".into(),
        checkout_root: "/checkout".into(),
    }
}

/// Asks for the session's workspace, as picking it in the rail does.
fn request(stage: &mut Stage<'_>) {
    let coordinator = stage.coordinator.clone();
    stage.cx.update(|window, cx| {
        coordinator.update(cx, |view, cx| {
            let target = WorkspaceTarget::Checkout(key());
            view.request(target, Box::new(|_, _, _| Ok(())), window, cx)
        })
    });
    stage.cx.run_until_parked();
}

/// The draft text, or `None` once it's clean.
fn draft(cx: &gpui_kit::App) -> Option<String> {
    let docs = cx.global::<DocumentStore>();
    let document = hosts_document();
    docs.is_dirty(&document)
        .then(|| docs.snapshot(&document).unwrap().text.clone())
}

fn edit(cx: &mut gpui_kit::App) {
    let docs = cx.global_mut::<DocumentStore>();
    let document = hosts_document();
    let version = docs.snapshot(&document).unwrap().version;
    docs.replace(&document, version, "edited\n".into()).unwrap();
}

#[gpui::test]
async fn a_dirty_outside_tab_with_no_workspace_asks_before_a_session_opens(
    cx: &mut TestAppContext,
) {
    let mut stage = stage(cx);
    stage._refused = Subscription::new(|| {});
    stage.open_hosts(None).await;
    stage.cx.update(|_, cx| edit(cx));
    let (refused, mut refusals) = unbounded();
    let (activated, mut activations) = unbounded();
    let coordinator = stage.coordinator.clone();
    let _heard = stage.cx.update(|_, cx| {
        (
            cx.subscribe(&coordinator, move |_, event: &WorkspaceRefused, _| {
                refused.unbounded_send(event.reason.clone()).unwrap();
            }),
            cx.subscribe(&coordinator, move |_, event: &WorkspaceActivated, _| {
                activated.unbounded_send(event.target.clone()).unwrap();
            }),
        )
    });
    // Cancel keeps the tab, its draft and the session closed.
    request(&mut stage);
    let asked = stage.cx.pending_prompt().expect("the guard asks first");
    assert_eq!(asked.0, ASKED);
    assert_eq!(asked.1, "hosts");
    stage.cx.simulate_prompt_answer("Cancel");
    stage.cx.run_until_parked();
    assert_eq!(refusals.try_recv().ok().as_deref(), Some(REFUSED));
    stage.cx.update(|_, cx| {
        assert_eq!(CenterPanes::file_views(cx).len(), 1, "the tab stays");
        assert_eq!(draft(cx).as_deref(), Some("edited\n"));
    });

    // Discard lets the session open, and the tab closes with nothing left behind.
    request(&mut stage);
    stage.cx.simulate_prompt_answer("Discard");
    let target = activations.recv().await.unwrap();
    assert_eq!(target, WorkspaceTarget::Checkout(key()));
    stage.cx.run_until_parked();
    stage.cx.update(|_, cx| {
        assert!(CenterPanes::file_views(cx).is_empty());
        assert_eq!(draft(cx), None);
    });
}

#[gpui::test]
async fn closing_the_window_asks_about_a_dirty_outside_tab_with_no_workspace(
    cx: &mut TestAppContext,
) {
    let mut stage = stage(cx);
    stage.open_hosts(None).await;
    stage.cx.update(drafts_guard::guard_close);
    stage.cx.update(|_, cx| edit(cx));

    assert!(!stage.cx.simulate_close(), "the window stays while it asks");
    stage.cx.run_until_parked();
    assert_eq!(
        stage.cx.pending_prompt().map(|asked| asked.0).as_deref(),
        Some(ASKED)
    );
    stage.cx.simulate_prompt_answer("Cancel");
    stage.cx.run_until_parked();
    assert_eq!(stage.cx.read(|cx| cx.windows().len()), 1);
    stage
        .cx
        .update(|_, cx| assert_eq!(draft(cx).as_deref(), Some("edited\n")));

    assert!(!stage.cx.simulate_close());
    stage.cx.run_until_parked();
    stage.cx.simulate_prompt_answer("Discard");
    stage.cx.run_until_parked();
    assert_eq!(stage.cx.read(|cx| cx.windows().len()), 0, "then it closes");
}

#[gpui::test]
async fn a_clean_outside_tab_lets_the_window_close_at_once(cx: &mut TestAppContext) {
    let mut stage = stage(cx);
    stage.open_hosts(None).await;
    stage.cx.update(drafts_guard::guard_close);
    assert!(stage.cx.simulate_close());
    assert!(!stage.cx.has_pending_prompt());
}
