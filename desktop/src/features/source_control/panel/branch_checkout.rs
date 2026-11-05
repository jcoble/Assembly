//! Switching branch or folder is the Engine's `ChangeCheckout`, the same command every other surface sends. A receipt
//! only says it was accepted: the outcome is the pushed session row carrying our operation id, and that row can arrive
//! before the receipt, so the wait starts before the command goes.
use super::{PaneResult, SourceControlPane};
use crate::core::engine::commands::WindowCommand;
use crate::core::engine::engine_items::SessionItem;
use crate::core::engine::{
    EngineChanged, EngineFeed, EngineItem, EngineKey, EngineScope, EngineWatch, SessionKey,
};
use crate::core::state::selected_session::SelectedSession;
use crate::core::state::viewed_project::{Viewed, ViewedProject, is_view};
use crate::features::source_control::git_failure::{GitFailure, Recovery};
use crate::features::workspaces::workspace_coordinator::WorkspaceCoordinator;
use gpui_kit::*;
use serde_json::{Value, json};

/// The backend requires every field of its command, so the ones that don't apply go as null.
pub(super) fn branch_wire(branch: &str, create: bool, head: &str) -> Value {
    json!({"kind":"change_checkout","checkoutKind":"branch","branch":branch,"base":null,"folder":null,"createBranch":create,"expectedHead":head})
}
pub(super) fn folder_wire(folder: &str) -> Value {
    json!({"kind":"change_checkout","checkoutKind":"folder","branch":null,"base":null,"folder":folder})
}

/// A checkout command on its way, holding the Engine scope that will tell us how it ended.
pub(super) struct PendingCheckout {
    key: SessionKey,
    operation: String,
    created: Option<String>,
    done: String,
    _watch: EngineWatch,
    _heard: Subscription,
}
#[derive(Debug, PartialEq)]
pub(super) enum CheckoutSeen {
    Waiting,
    Ready,
    Refused(Option<String>),
}
/// How the session row says our command ended. Another operation's outcome isn't ours.
pub(super) fn observe_checkout(value: &Value, operation: &str) -> CheckoutSeen {
    if value["checkoutOperationId"].as_str() != Some(operation) {
        return CheckoutSeen::Waiting;
    }
    match value["checkoutState"].as_str() {
        Some("ready") => CheckoutSeen::Ready,
        Some("refused") => {
            CheckoutSeen::Refused(value["checkoutReason"].as_str().map(String::from))
        }
        _ => CheckoutSeen::Waiting,
    }
}
/// The plain sentence for a refusal code; anything else is already the backend's sentence.
const VERSION_CONFLICT: &str = "version_conflict";

pub(super) fn checkout_sentence(reason: &str) -> String {
    match reason {
        "forbidden" => "This session can't change checkout from here.",
        "bad_args" => "That branch or folder can't be used.",
        "busy" => "The session is busy. Try again when it's idle.",
        "writer_elsewhere" => {
            "Another window is driving this session, so it can't change checkout here."
        }
        "checkout_pending" | "checkout_busy" => "A checkout change is already in progress.",
        "checkout_conflict" => {
            "Uncommitted changes would be overwritten. Commit or stash them first."
        }
        "checkout_not_applied" => "The checkout didn't change.",
        "checkout_outcome_unknown" => "The checkout may have changed. Refresh to see where it is.",
        "checkout_root_changed" => "The session's folder changed. Refresh and try again.",
        "folder_unavailable" => "That folder isn't available.",
        "head_changed" => "The branch moved since this was read. Refresh and try again.",
        "invalid_branch" => "That isn't a valid branch name.",
        "invalid_base" => "That base can't be used for a new branch.",
        "not_git" => "This folder isn't a git repository.",
        VERSION_CONFLICT => "The session just changed. Try again.",
        "unsupported_resume" => {
            "This session's agent can't keep its conversation in another checkout."
        }
        "worktree_collision"
        | "worktree_refused"
        | "worktree_root_unavailable"
        | "worktrees_unavailable" => "A worktree couldn't be set up for that branch.",
        other => return other.to_string(),
    }
    .to_string()
}
impl SourceControlPane {
    /// Whether the pane is on a viewed folder, whose branch has no session or Engine to tell.
    pub(super) fn viewing(&self, cx: &App) -> bool {
        self.target(cx)
            .is_some_and(|target| is_view(&target.session))
    }
    fn switch_in_view(
        &mut self,
        name: String,
        create: bool,
        head: Option<String>,
        cx: &mut Context<Self>,
    ) {
        if let Some(target) = self.target(cx) {
            let args =
                json!({"session":target.session,"name":name,"create":create,"expectedHead":head});
            self.write_to(target, "switchBranch", args, cx);
        }
    }
    pub(super) fn switch_branch(&mut self, name: String, cx: &mut Context<Self>) {
        if self.viewing(cx) {
            self.switch_in_view(name, false, None, cx);
            return;
        }
        let Some(head) = self.shown_status(cx).and_then(|status| status.head) else {
            self.fail(
                "This branch has no commits yet, so there is nothing to switch from.",
                cx,
            );
            return;
        };
        let done = format!("Switched to {name}.");
        let wire = branch_wire(&name, false, &head);
        self.change_checkout(wire, None, done, cx);
    }
    pub(super) fn create_branch(&mut self, name: String, cx: &mut Context<Self>) {
        let Some(head) = self.shown_status(cx).and_then(|status| status.head) else {
            let sentence = "Make a first commit before creating a branch.".to_string();
            self.branch_menu
                .update(cx, |menu, cx| menu.set_refused(Some(sentence), cx));
            return;
        };
        if self.viewing(cx) {
            self.switch_in_view(name, true, Some(head), cx);
            return;
        }
        let done = format!("Created and switched to {name}.");
        let wire = branch_wire(&name, true, &head);
        self.change_checkout(wire, Some(name), done, cx);
    }
    pub(super) fn move_to_folder(&mut self, folder: String, cx: &mut Context<Self>) {
        // A viewed project's branch held in another worktree: look at that worktree instead.
        if let Some(viewed) = ViewedProject::viewed(cx) {
            let title = folder.rsplit('/').next().unwrap_or(&folder).to_string();
            let session = SelectedSession::global(cx)
                .read(cx)
                .session_id()
                .map(String::from);
            ViewedProject::set(
                session,
                Some(Viewed {
                    folder,
                    title,
                    ..viewed
                }),
                cx,
            );
            WorkspaceCoordinator::changed(cx);
            return;
        }
        let done = format!("Moved to {folder}.");
        let wire = folder_wire(&folder);
        self.change_checkout(wire, None, done, cx);
    }
    fn change_checkout(
        &mut self,
        wire: Value,
        created: Option<String>,
        done: String,
        cx: &mut Context<Self>,
    ) {
        if self.busy(cx) {
            return;
        }
        // The session's checkout is the Engine's; which of them the pane is reading has no say in it.
        let Some(target) = self.shown_target(cx) else {
            return;
        };
        let Some(machine) = target
            .source
            .map(|source| source.machine)
            .filter(|machine| !machine.is_empty())
        else {
            self.fail(
                "This session's machine isn't known yet. Refresh and try again.",
                cx,
            );
            return;
        };
        let key = SessionKey {
            machine,
            id: target.session,
        };
        let feed = EngineFeed::global(cx);
        let scope = EngineScope {
            machine: key.machine.clone(),
            topic: "sessions".into(),
            key: None,
        };
        let watch = match feed.update(cx, |feed, cx| feed.watch(scope, cx)) {
            Ok(watch) => watch,
            Err(refusal) => {
                self.fail(&refusal.message, cx);
                return;
            }
        };
        let watched = key.clone();
        let heard = cx.subscribe(&feed, move |pane, _, event: &EngineChanged, cx| {
            if event.sessions.contains(&watched)
                || event
                    .scopes
                    .iter()
                    .any(|scope| scope.machine == watched.machine && scope.topic == "sessions")
            {
                pane.checkout_observed(cx);
            }
        });
        let operation = uuid::Uuid::new_v4().to_string();
        self.result = None;
        self.checkout = Some(PendingCheckout {
            key: key.clone(),
            operation: operation.clone(),
            created,
            done,
            _watch: watch,
            _heard: heard,
        });
        self.push_branch_menu(cx);
        let row = feed.read(cx).session(&key).is_none().then(|| {
            feed.update(cx, |feed, cx| {
                feed.ensure_item(
                    EngineKey {
                        machine: key.machine.clone(),
                        kind: "session".into(),
                        id: key.id.clone(),
                    },
                    cx,
                )
            })
        });
        self.checkout_task = Some(cx.spawn(async move |pane, cx| {
            let read = match row {
                Some(row) => match row.await {
                    Ok(read) => read,
                    Err(refusal) => {
                        _ = pane.update(cx, |pane, cx| {
                            pane.checkout_refused(&refusal.message, false, cx)
                        });
                        return;
                    }
                },
                None => None,
            };
            let Ok(Some(submitted)) = pane.update(cx, |pane, cx| {
                pane.submit_checkout(wire.clone(), operation, read.clone(), None, cx)
            }) else {
                return;
            };
            let mut answer = submitted.await;
            // The row moved while this was on its way, which rarely bears on what this person asked. The Engine still
            // checks the command against the row as it is now, so it's asked once more at the version it named.
            if let Ok(receipt) = &answer
                && receipt.error.as_deref() == Some(VERSION_CONFLICT)
            {
                let version = receipt.version;
                let operation = uuid::Uuid::new_v4().to_string();
                let Ok(Some(again)) = pane.update(cx, |pane, cx| {
                    pane.submit_checkout(wire, operation, read, Some(version), cx)
                }) else {
                    return;
                };
                answer = again.await;
            }
            _ = pane.update(cx, |pane, cx| match answer {
                Ok(receipt) if receipt.applied => pane.checkout_observed(cx),
                Ok(receipt) => {
                    let reason = receipt
                        .error
                        .unwrap_or_else(|| "checkout_not_applied".into());
                    pane.checkout_refused(&reason, false, cx);
                }
                Err(refusal) => {
                    let known = checkout_sentence(&refusal.code);
                    let sentence = if known == refusal.code {
                        refusal.message
                    } else {
                        known
                    };
                    pane.checkout_refused(&sentence, true, cx);
                }
            });
        }));
        cx.notify();
    }
    fn submit_checkout(
        &mut self,
        wire: Value,
        operation: String,
        read: Option<EngineItem>,
        version: Option<i64>,
        cx: &mut Context<Self>,
    ) -> Option<
        Task<
            Result<
                crate::core::engine::commands::CommandReceipt,
                crate::core::engine::EngineRefusal,
            >,
        >,
    > {
        let pending = self.checkout.as_mut()?;
        pending.operation = operation.clone();
        let key = pending.key.clone();
        let feed = EngineFeed::global(cx);
        // The feed keeps a row it's asked for only once its subscription holds it, which may not have happened yet.
        let held = feed.read(cx).session(&key);
        let Some(session) = held.or_else(|| read.as_ref().and_then(SessionItem::of)) else {
            self.checkout_refused("This session isn't available.", false, cx);
            return None;
        };
        let command = WindowCommand {
            command_id: operation,
            entity_id: key.id,
            expected_version: version.unwrap_or(session.version),
            epoch: session.epoch,
            machine: Some(key.machine),
            surface: None,
            command: wire,
        };
        Some(feed.update(cx, |feed, cx| feed.submit(command, cx)))
    }
    fn checkout_observed(&mut self, cx: &mut Context<Self>) {
        let Some(pending) = &self.checkout else {
            return;
        };
        let key = EngineKey {
            machine: pending.key.machine.clone(),
            kind: "session".into(),
            id: pending.key.id.clone(),
        };
        let operation = pending.operation.clone();
        let feed = EngineFeed::global(cx);
        let seen = match feed.read(cx).item(&key) {
            Some(item) => observe_checkout(&item.value, &operation),
            None => CheckoutSeen::Waiting,
        };
        match seen {
            CheckoutSeen::Waiting => {}
            CheckoutSeen::Ready => self.checkout_ready(cx),
            CheckoutSeen::Refused(reason) => {
                let reason = reason.unwrap_or_else(|| "checkout_not_applied".into());
                self.checkout_refused(&reason, false, cx);
            }
        }
    }
    fn checkout_ready(&mut self, cx: &mut Context<Self>) {
        let Some(pending) = self.checkout.take() else {
            return;
        };
        self.checkout_task = None;
        self.result = Some(PaneResult::Done(pending.done));
        if pending.created.is_some() {
            self.branch_open = false;
            let menu = self.branch_menu.clone();
            let handle = self.window;
            cx.defer(move |cx| {
                _ = handle.update(cx, |_, window, cx| {
                    menu.update(cx, |menu, cx| menu.clear(window, cx))
                });
            });
        }
        self.read.update(cx, |read, cx| read.refresh(cx));
        self.read_branches(cx);
        self.result_answers = self.read.read(cx).answers() + 1;
        cx.notify();
    }
    /// A create keeps its menu open and says why under the name; everything else says it in the pane.
    fn checkout_refused(&mut self, reason: &str, uncertain: bool, cx: &mut Context<Self>) {
        let Some(pending) = self.checkout.take() else {
            return;
        };
        self.checkout_task = None;
        let sentence = checkout_sentence(reason);
        if pending.created.is_some() {
            self.branch_menu
                .update(cx, |menu, cx| menu.set_refused(Some(sentence), cx));
        } else {
            self.result = Some(PaneResult::Failed(GitFailure {
                recovery: uncertain.then_some(Recovery::Refresh),
                ..GitFailure::plain(&sentence)
            }));
        }
        self.push_branch_menu(cx);
        cx.notify();
    }
}
#[cfg(test)]
#[path = "branch_checkout_tests.rs"]
mod tests;
