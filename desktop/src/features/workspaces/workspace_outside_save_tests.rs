//! Proof fixes brief item 3: a layout save refused while a file operation runs ("Not saved: Finish the current file
//! operation…") goes through when it ends, and the line clears even when the layout is already saved as it stands.
use super::outside_tests::{Stage, hosts_document, stage};
use super::{SocketClient, WorkspaceCoordinator};
use crate::core::socket::{pending_call::PendingCall, test_wait::until};
use crate::features::files::document_store::DocumentStore;
use crate::features::files::file_view::FileView;
use futures_channel::mpsc::{UnboundedReceiver, UnboundedSender, unbounded};
use gpui::TestAppContext;
use gpui_kit::{Entity, Subscription};
use serde_json::{Value, json};

/// Hears each change of the coordinator and the file's tab.
struct Heard {
    changes: UnboundedReceiver<()>,
    changed: UnboundedSender<()>,
    _heard: [Subscription; 2],
}

/// A workspace open with an unlocked outside /etc/hosts, saved and idle.
async fn open(stage: &mut Stage<'_>) -> (Entity<FileView>, Heard) {
    stage.open_workspace().await;
    let view = stage.open_hosts(Some("box")).await;
    let (changed, changes) = unbounded();
    let (one, two) = (changed.clone(), changed.clone());
    let coordinator = stage.coordinator.clone();
    let _heard = stage.cx.update(|_, cx| {
        [
            cx.observe(&coordinator, move |_, _| _ = one.unbounded_send(())),
            cx.observe(&view, move |_, _| _ = two.unbounded_send(())),
        ]
    });
    stage
        .cx
        .update(|_, cx| view.update(cx, |view, cx| view.edit_anyway(cx)));
    let mut heard = Heard {
        changes,
        changed,
        _heard,
    };
    settled(stage, &view, &mut heard, "the open saved").await;
    (view, heard)
}

fn status(stage: &mut Stage<'_>) -> Option<String> {
    let coordinator = stage.coordinator.clone();
    stage.cx.update(|_, cx| coordinator.read(cx).status.clone())
}

/// Waits until neither the workspace nor the file has anything running.
async fn settled(stage: &mut Stage<'_>, view: &Entity<FileView>, heard: &mut Heard, what: &str) {
    let coordinator = stage.coordinator.clone();
    until(what, &mut heard.changes, &heard.changed, || {
        stage.cx.run_until_parked();
        stage
            .cx
            .update(|_, cx| coordinator.read(cx).saving.is_none() && view.read(cx).rename_ready())
    })
    .await;
}

/// A draft the person typed, saved with the workspace, so the layout as it stands is already saved.
async fn saved_draft(stage: &mut Stage<'_>, view: &Entity<FileView>, heard: &mut Heard) {
    stage.cx.update(|_, cx| {
        let docs = cx.global_mut::<DocumentStore>();
        let version = docs.snapshot(&hosts_document()).unwrap().version;
        docs.replace(&hosts_document(), version, "edited\n".into())
            .unwrap();
        WorkspaceCoordinator::changed(cx);
    });
    settled(stage, view, heard, "the draft saved with the workspace").await;
    let last = stage.saves.lock().unwrap().last().cloned().unwrap();
    assert_eq!(last["files"][0]["draft"], "edited\n");
}

/// A view change while a file operation runs: its layout save is refused for now.
fn refused(stage: &mut Stage<'_>) {
    stage.cx.update(|_, cx| WorkspaceCoordinator::changed(cx));
    stage.cx.run_until_parked();
    assert!(
        status(stage).is_some_and(|status| status.starts_with("Not saved:")),
        "refused while the file operation runs"
    );
}

/// Lets the held file save commit answer.
fn release(stage: &mut Stage<'_>) -> PendingCall<Value> {
    // The file save goes over the bulk connection, so its held answer comes back on it.
    stage.cx.update(|_, cx| {
        SocketClient::try_bulk(cx).unwrap().read(cx).call::<Value>(
            "test",
            "release",
            json!({}),
            None,
        )
    })
}

fn save(stage: &mut Stage<'_>, view: &Entity<FileView>) {
    let (answer, _saved) = futures_channel::oneshot::channel();
    stage
        .cx
        .update(|_, cx| view.update(cx, |view, cx| view.save_for_guard(answer, cx)));
}

fn reload(stage: &mut Stage<'_>, view: &Entity<FileView>) {
    stage
        .cx
        .update(|window, cx| view.update(cx, |view, cx| view.request_reload(window, cx)));
    stage.cx.run_until_parked();
}

#[gpui::test]
async fn a_layout_save_refused_during_an_outside_save_goes_through_when_it_ends(
    cx: &mut TestAppContext,
) {
    let mut stage = stage(cx);
    let (view, mut heard) = open(&mut stage).await;
    stage.cx.update(|_, cx| {
        let docs = cx.global_mut::<DocumentStore>();
        let version = docs.snapshot(&hosts_document()).unwrap().version;
        docs.replace(&hosts_document(), version, "edited\n".into())
            .unwrap();
    });
    save(&mut stage, &view);
    refused(&mut stage);

    let release = release(&mut stage);
    settled(
        &mut stage,
        &view,
        &mut heard,
        "the layout saved after the file",
    )
    .await;
    drop(release);
    assert_eq!(status(&mut stage), None);
    let last = stage.saves.lock().unwrap().last().cloned().unwrap();
    assert_eq!(last["files"][0]["path"], "/etc/hosts");
    assert!(
        last["files"][0]["draft"].is_null(),
        "the saved file has no draft left: {last}"
    );
}

// The operation ends with the layout just as it was last saved, so the retried save has nothing to write; the line
// still clears.
#[gpui::test]
async fn a_refused_layout_save_clears_when_the_operation_ends_with_the_layout_unchanged(
    cx: &mut TestAppContext,
) {
    let mut stage = stage(cx);
    let (view, mut heard) = open(&mut stage).await;
    saved_draft(&mut stage, &view, &mut heard).await;
    let saves = stage.saves.lock().unwrap().len();

    reload(&mut stage, &view);
    assert!(stage.cx.pending_prompt().is_some(), "Reload asks first");
    refused(&mut stage);
    stage.cx.simulate_prompt_answer("Cancel");
    settled(&mut stage, &view, &mut heard, "the prompt ended").await;

    assert_eq!(status(&mut stage), None);
    assert_eq!(
        stage.saves.lock().unwrap().len(),
        saves,
        "nothing new to save"
    );
}

// The revision check refuses the outside file's save with the layout unchanged, then Reload takes the disk's text.
#[gpui::test]
async fn a_refused_layout_save_clears_after_a_conflicting_outside_save_and_its_reload(
    cx: &mut TestAppContext,
) {
    let mut stage = stage(cx);
    *stage.commit.lock().unwrap() = "conflict";
    let (view, mut heard) = open(&mut stage).await;
    saved_draft(&mut stage, &view, &mut heard).await;

    save(&mut stage, &view);
    refused(&mut stage);
    let release = release(&mut stage);
    settled(&mut stage, &view, &mut heard, "the conflict").await;
    drop(release);
    stage.cx.update(|_, cx| {
        let docs = cx.global::<DocumentStore>();
        assert!(
            docs.is_dirty(&hosts_document()),
            "the refused save keeps the draft"
        );
        assert_eq!(docs.base_revision(&hosts_document()), Some("r1"));
    });
    assert_eq!(status(&mut stage), None, "cleared at the conflict");

    reload(&mut stage, &view);
    refused(&mut stage);
    stage.cx.simulate_prompt_answer("Reload");
    until(
        "the disk's text",
        &mut heard.changes,
        &heard.changed,
        || {
            stage.cx.run_until_parked();
            // As a draw does: the reloaded text goes into the editor once read.
            stage
                .cx
                .update(|window, cx| view.update(cx, |view, cx| view.bind_for_test(window, cx)));
            stage.cx.run_until_parked();
            let last = stage.saves.lock().unwrap().last().cloned().unwrap();
            last["files"][0]["draft"].is_null()
        },
    )
    .await;
    settled(&mut stage, &view, &mut heard, "the reload saved").await;
    assert_eq!(status(&mut stage), None, "cleared after Reload");
    stage.cx.update(|_, cx| {
        assert!(!cx.global::<DocumentStore>().is_dirty(&hosts_document()));
    });
}

// Review item 1: only the capture refusal clears when the layout is already saved; another status, as a workspace
// changed elsewhere, stays through a file save.
#[gpui::test]
async fn a_changed_elsewhere_status_stays_through_a_file_save(cx: &mut TestAppContext) {
    const ELSEWHERE: &str = "This workspace changed elsewhere. The live view is preserved; another save requires reconciliation.";
    let mut stage = stage(cx);
    *stage.commit.lock().unwrap() = "conflict";
    let (view, mut heard) = open(&mut stage).await;
    saved_draft(&mut stage, &view, &mut heard).await;
    let coordinator = stage.coordinator.clone();
    stage.cx.update(|_, cx| {
        coordinator.update(cx, |coordinator, cx| {
            coordinator.status = Some(ELSEWHERE.into());
            cx.notify();
        })
    });

    let release = release(&mut stage);
    save(&mut stage, &view);
    settled(&mut stage, &view, &mut heard, "the file save").await;
    drop(release);
    assert_eq!(status(&mut stage).as_deref(), Some(ELSEWHERE));
}
