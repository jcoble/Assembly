//! One file action at a time, run straight from the entry bar or the menu, with no dialog, as in the old panel.
//! The owner is checked first; a rename or move checks open documents before and after, and moves their tabs. The
//! pane holds the task; hiding the tab or switching session leaves it running to its end, so open tabs are never
//! left on old paths.
use super::FilesPane;
use super::actions::FileClipboard;
use crate::core::socket::{client::SocketClient, outcome::Outcome};
use crate::core::ui::center_panes::CenterPanes;
use crate::features::files::document_store::{DocumentKey, DocumentStore};
use crate::features::files::entry::FileOwnership;
use crate::features::files::file_actions::{ActionReply, ActionRun, ActionState};
use gpui_kit::*;
use serde_json::json;

impl FilesPane {
    pub(in crate::features::files) fn action_running(&self) -> bool {
        self.action.state == ActionState::Running
    }

    /// Why a write can't run here, if it can't.
    pub(in crate::features::files) fn write_block(&self, cx: &App) -> Option<String> {
        self.read_only_reason(cx).or_else(|| {
            self.action_running()
                .then(|| "Another file action is still running.".into())
        })
    }

    /// Pastes what was cut or copied into `folder`; a cut moves it and empties the clipboard.
    pub(in crate::features::files) fn paste_into(
        &mut self,
        folder: &str,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        let Some(copied) = cx
            .try_global::<FileClipboard>()
            .map(|clipboard| clipboard.0.clone())
        else {
            self.action_error = Some("Copy or cut a file first.".into());
            cx.notify();
            return;
        };
        if copied.source != self.source {
            self.action_error = Some("The copied item or destination source changed.".into());
            cx.notify();
            return;
        }
        let name = copied.key.path.rsplit('/').next().unwrap_or_default();
        let target = if folder.is_empty() {
            name.to_string()
        } else {
            format!("{folder}/{name}")
        };
        self.run_action(
            if copied.cut { "move" } else { "copy" },
            copied.key.path.clone(),
            Some(target),
            Some(copied.key),
            window,
            cx,
        );
    }

    pub(in crate::features::files) fn delete_entry(
        &mut self,
        path: String,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        self.run_action("trash", path, None, None, window, cx);
    }

    pub(in crate::features::files) fn run_action(
        &mut self,
        kind: &'static str,
        source: String,
        destination: Option<String>,
        expected_owner: Option<DocumentKey>,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        let Some(session) = self.session.clone() else {
            return;
        };
        if let Some(reason) = self.write_block(cx) {
            self.entry_failed(reason);
            cx.notify();
            return;
        }
        let Some(socket) = SocketClient::try_global(cx) else {
            self.entry_failed("The backend isn't connected.".into());
            cx.notify();
            return;
        };
        self.action.begin();
        self.action_error = None;
        let expected_source = self.source.clone();
        let handle = window.window_handle();
        let owner = socket.read(cx).call::<FileOwnership>(
            "files",
            "owner",
            json!({"session":session,"expectedSource":expected_source,"expectedOwner":self.owner}),
            None,
        );
        self.action_task = Some(cx.spawn(async move |pane, cx| {
            let fail = |message: String, cx: &mut AsyncApp| {
                let _ = pane.update(cx, |pane, cx| pane.end_action(Some(message), false, cx));
            };
            let owner = match owner.await {
                Outcome::Ok(owner) => owner,
                Outcome::Failed(error) => return fail(error.message, cx),
                Outcome::Aborted => return fail("The owning checkout could not be read.".into(), cx),
            };
            if expected_owner.as_ref().is_some_and(|expected| {
                expected.machine != owner.machine || expected.checkout_root != owner.checkout_root
            }) {
                return fail(
                    "Paste is limited to the same owning checkout. The copied item was not touched.".into(),
                    cx,
                );
            }
            let moving = matches!(kind, "rename" | "move");
            let mut versions: Vec<(DocumentKey, u64)> = Vec::new();
            if moving {
                let Some(target) = destination.as_deref() else {
                    return fail("A move needs a destination.".into(), cx);
                };
                let ready = pane.update(cx, |_, cx| {
                    let ready =
                        CenterPanes::can_rename_file_aliases(&owner.machine, &owner.checkout_root, &source, target, cx)
                            .is_ok();
                    if let Some(docs) = cx.try_global::<DocumentStore>() {
                        versions = docs.versions_under(&owner.machine, &owner.checkout_root, &source);
                    }
                    ready
                });
                if !ready.unwrap_or(false) {
                    return fail(
                        "An open document is busy or the destination already has a draft. Nothing was moved.".into(),
                        cx,
                    );
                }
            }
            let expected = destination.clone().unwrap_or_else(|| source.clone());
            let Ok(call) = pane.update(cx, |_, cx| {
                socket.read(cx).call::<ActionReply>(
                    "files",
                    "action",
                    json!({"session":session,"kind":kind,"path":source,"destination":destination,
                        "expectedSource":expected_source,"expectedOwner":owner}),
                    None,
                )
            }) else {
                return;
            };
            let answer = call.await;
            let _ = handle.update(cx, |_, window, cx| {
                let mut run = ActionRun {
                    state: ActionState::Running,
                    error: None,
                };
                let applied = match answer {
                    Outcome::Ok(reply) => run.complete(&expected, reply),
                    _ => {
                        run.unknown();
                        false
                    }
                };
                if applied && moving {
                    let unchanged = cx
                        .try_global::<DocumentStore>()
                        .map(|docs| docs.versions_under(&owner.machine, &owner.checkout_root, &source))
                        .unwrap_or_default()
                        == versions;
                    if !unchanged
                        || CenterPanes::rename_file_aliases(
                            &owner.machine,
                            &owner.checkout_root,
                            &source,
                            &expected,
                            window,
                            cx,
                        )
                        .is_err()
                    {
                        run.error = Some("The file was moved on disk, but open drafts changed meanwhile. All drafts are preserved at their previous paths; inspect both paths before continuing.".into());
                    }
                }
                let moved = (applied && kind == "move").then_some(expected_owner).flatten();
                let _ = pane.update(cx, |pane, cx| {
                    if moved.as_ref().is_some_and(|key| {
                        cx.try_global::<FileClipboard>().is_some_and(|clipboard| &clipboard.0.key == key)
                    }) {
                        cx.remove_global::<FileClipboard>();
                    }
                    pane.end_action(run.error, applied, cx);
                    if applied && pane.session.as_deref() == Some(session.as_str()) {
                        if kind == "trash" {
                            if pane.selected.as_deref().is_some_and(|picked| {
                                picked == source || picked.starts_with(&format!("{source}/"))
                            }) {
                                pane.selected = None;
                            }
                        } else {
                            pane.selected = Some(expected.clone());
                        }
                    }
                    if pane.session.as_deref() == Some(session.as_str()) {
                        pane.reload_kept(cx);
                    }
                });
            });
        }));
        cx.notify();
    }

    /// The action is over. Applied, the bar closes and any warning goes on the panel's red line; refused, the error
    /// stays in the bar if it's open.
    fn end_action(&mut self, error: Option<String>, applied: bool, cx: &mut Context<Self>) {
        self.action = ActionRun::default();
        self.action_task = None;
        if applied {
            self.entry = None;
            self.action_error = error;
        } else if let Some(error) = error {
            self.entry_failed(error);
        }
        cx.notify();
    }
}
