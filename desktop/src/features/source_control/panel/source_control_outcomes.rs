//! What the pane does when the read or a write comes back: results land only on what they were decided against, a
//! returned status needs no second read, and an operation's terminal state is taken once.
use super::{PaneResult, RunningOperation, SourceControlPane};
use crate::core::socket::{client::SocketClient, outcome::Outcome};
use crate::core::state::inspected_checkout::{Inspected, InspectedCheckout};
use crate::core::state::session_read::Shown;
use crate::features::source_control::commit_timeline::CommitTimeline;
use crate::features::source_control::git_branches::GitBranchList;
use crate::features::source_control::git_failure::{GitFailure, Recovery};
use crate::features::source_control::git_status::GitStatus;
use crate::features::source_control::pull_requests::pr_section::PrSection;
use crate::features::source_control::stash_dialogs::Stash;
use gpui_kit::*;
use serde_json::{Value, json};

/// The words for an operation on its way.
pub(super) fn running_label(action: &str) -> &'static str {
    match action {
        "fetch" => "Fetching…",
        "pull" => "Pulling…",
        "push" => "Pushing…",
        "publish" => "Publishing…",
        "draftCommitMessage" | "draftPr" => "Drafting…",
        "createPr" => "Creating pull request…",
        _ => "Working…",
    }
}
fn finished_label(action: &str) -> &'static str {
    match action {
        "fetch" => "Fetched.",
        "pull" => "Pulled.",
        "push" => "Pushed.",
        "publish" => "Published.",
        _ => "Done.",
    }
}
impl SourceControlPane {
    pub(super) fn read_changed(&mut self, cx: &mut Context<Self>) {
        let (session, cleared, answers) = {
            let read = self.read.read(cx);
            (
                read.session().map(String::from),
                matches!(read.shown(), Shown::NoSession),
                read.answers(),
            )
        };
        // A hidden tab forgets the session its read stopped for, which isn't the session changing.
        let changed = match &session {
            Some(session) => self.last_session.as_ref() != Some(session),
            None => cleared && self.last_session.is_some(),
        };
        if changed {
            self.session_changed(session.clone(), cx);
        }
        self.publish_inspected(cx);
        self.settle_place(cx);
        if matches!(self.result, Some(PaneResult::Done(_))) && answers != self.result_answers {
            self.result = None;
        }
        self.sync_children(cx);
        let target = self.target(cx);
        if self.branches_target.is_some() && self.branches_target != target {
            self.forget_branches();
        }
        let is_repository = matches!(self.read.read(cx).shown(), Shown::Read(_));
        if self.active && is_repository && self.branches_target.is_none() {
            self.read_branches(cx);
        }
        self.push_branch_menu(cx);
        cx.notify();
    }
    /// Tells the Changes tab what this panel has actually read.
    fn publish_inspected(&self, cx: &mut Context<Self>) {
        let read = self.read.read(cx);
        if let (Shown::Read(status), Some(session)) = (read.shown(), read.session()) {
            let inspected = Inspected {
                session: session.to_string(),
                source: read.source().cloned(),
                fingerprint: status.fingerprint.clone(),
                answers: read.answers(),
            };
            InspectedCheckout::publish(Some(inspected), cx);
        }
    }
    pub(super) fn session_changed(&mut self, session: Option<String>, cx: &mut Context<Self>) {
        // A restored workspace has already put its own view in place for the session it's bound to.
        let bound = self
            .workspace
            .as_ref()
            .map(|workspace| workspace.session_id.as_str());
        if bound != session.as_deref() {
            self.open_sections.clear();
            self.scroll.set_offset(Point::default());
            self.pending_place = None;
        }
        self.last_session = session;
        self.result = None;
        self.branches = None;
        self.forget_branches();
        self.branch_open = false;
        self.checkout = None;
        self.checkout_task = None;
        self.hovered_section = None;
        self.hovered_file = None;
        self.selected_file = None;
        self.amend = false;
        self.running = None;
        self.seen_head = None;
        self.set_message(String::new(), cx);
    }
    /// The commit list and the pull requests belong to one session. A plain folder reads no history.
    pub(super) fn sync_children(&mut self, cx: &mut Context<Self>) {
        let session = self.last_session.clone();
        let target = self.target(cx);
        let shown = self.shown_target(cx);
        let source = shown.as_ref().and_then(|shown| shown.source.clone());
        // The commit list's labels change on a switch to a branch at the same commit, too.
        let head = self.shown_status(cx).and_then(|status| {
            status
                .head
                .as_ref()
                .map(|head| format!("{head} {}", status.branch_name()))
        });
        let is_repository = matches!(self.read.read(cx).shown(), Shown::Read(_));
        // Pull requests stay on the session's folder while a project is viewed.
        let own = self.session_target(cx);
        let viewing = own.is_some();
        let pr_session = own
            .as_ref()
            .map(|own| own.session.clone())
            .or_else(|| session.clone());
        let pr_target = own.or_else(|| target.clone());
        if self.pull_requests.as_ref().map(|(key, _)| key) != pr_session.as_ref() {
            self.pull_requests = pr_session.as_ref().map(|session| {
                let target = pr_target.clone().unwrap_or_else(|| {
                    crate::features::source_control::git_target::GitTarget::new(session.clone())
                });
                let writes = self.writes.clone();
                let active = self.active;
                (
                    session.clone(),
                    cx.new(|cx| PrSection::new(target, writes, active, cx)),
                )
            });
        }
        if let (Some((_, section)), Some(target)) = (&self.pull_requests, &pr_target) {
            let target = target.clone();
            let branch = self
                .shown_status(cx)
                .filter(|_| !viewing)
                .map(|status| status.branch_name());
            section.update(cx, |section, cx| section.set_target(target, branch, cx));
        }
        if self.timeline.as_ref().map(|(key, _)| key) != session.as_ref() {
            self.timeline_active = false;
            self.timeline = session.as_ref().map(|session| {
                let source = source.clone();
                (
                    session.clone(),
                    cx.new(|cx| CommitTimeline::new_inline(session.clone(), source, false, cx)),
                )
            });
        }
        let Some((_, timeline)) = self.timeline.clone() else {
            return;
        };
        let wanted = self.active && is_repository;
        if wanted != self.timeline_active {
            self.timeline_active = wanted;
            self.seen_head = head;
            timeline.update(cx, |timeline, cx| {
                timeline.set_target(None, source, cx);
                timeline.set_active(wanted, cx)
            });
        } else if wanted {
            let moved = head.is_some() && head != self.seen_head;
            self.seen_head = head;
            timeline.update(cx, |timeline, cx| {
                timeline.set_target(None, source, cx);
                if moved {
                    timeline.refresh(cx);
                }
            });
        }
    }
    pub(super) fn action_changed(&mut self, cx: &mut Context<Self>) {
        let action = self.writes.action.read(cx);
        if action.completed <= self.action_revision {
            self.push_branch_menu(cx);
            cx.notify();
            return;
        }
        self.action_revision = action.completed;
        let decided_against = action.target.clone();
        let succeeded = action.succeeded;
        let message = action.message.clone();
        let failure = action.failure.clone();
        let status: Option<GitStatus> = action.status.clone();
        let submitted = self.submitted_revision.take();
        // A reply for the checkout the pane has since left never paints over what it's showing now.
        if decided_against != self.target(cx) {
            self.push_branch_menu(cx);
            cx.notify();
            return;
        }
        self.details_open = false;
        if succeeded {
            self.result = message.map(PaneResult::Done);
            if let Some(status) = status {
                self.read.update(cx, |read, cx| read.show(status, cx));
            }
            self.result_answers = self.read.read(cx).answers();
            self.read_branches(cx);
            if submitted == Some(self.edit_revision) {
                self.amend = false;
                self.set_message(String::new(), cx);
            }
        } else {
            self.result = failure.map(PaneResult::Failed);
            self.read.update(cx, |read, cx| read.refresh(cx));
        }
        self.push_branch_menu(cx);
        cx.notify();
    }
    pub(super) fn operation_changed(&mut self, cx: &mut Context<Self>) {
        self.push_branch_menu(cx);
        cx.notify();
        let operation = self.writes.operation.read(cx);
        let error = operation.error.clone();
        let unreachable = operation.unreachable.clone();
        let started_by = operation.action;
        let busy = operation.busy;
        if operation.unconfirmed {
            let sentence = error.unwrap_or_else(|| {
                "This may have completed. Reconcile this operation before trying again.".into()
            });
            self.running = None;
            self.result = Some(PaneResult::Failed(GitFailure {
                recovery: Some(Recovery::ReconcileOperation),
                ..GitFailure::plain(&sentence)
            }));
            return;
        }
        if busy {
            return;
        }
        let Some(status) = operation.status.clone() else {
            let running = self.running.take();
            let current =
                running.is_some_and(|running| self.target(cx).as_ref() == Some(&running.target));
            if let (true, Some(error)) = (current, error) {
                self.result = Some(PaneResult::Failed(GitFailure {
                    recovery: unreachable.map(Recovery::Connect),
                    ..GitFailure::plain(&error)
                }));
            }
            return;
        };
        if !status.terminal() || self.finished_op.as_deref() == Some(status.operation_id.as_str()) {
            return;
        }
        self.finished_op = Some(status.operation_id.clone());
        let Some(running) = self.running.take() else {
            // A dialog ran it and shows its own outcome; the pane only catches up.
            if matches!(&self.result, Some(PaneResult::Failed(failure)) if failure.recovery == Some(Recovery::ReconcileOperation))
            {
                self.result = None;
            }
            if started_by == Some("createPr") && status.state == "succeeded" {
                self.after_sync(None, cx);
            }
            return;
        };
        // An ordinary fetch, pull or push that lands after the session moved stays the write
        // authority's to settle; its status and refreshes never reach the current selection.
        if self.target(cx).as_ref() != Some(&running.target) {
            return;
        }
        self.details_open = false;
        match status.state.as_str() {
            "succeeded" => self.operation_succeeded(running, status.result, cx),
            "cancelled" => {
                self.result = Some(PaneResult::Done("Cancelled.".into()));
                self.result_answers = self.read.read(cx).answers();
            }
            _ => {
                let sentence = error.unwrap_or_else(|| "The operation failed.".into());
                self.result = Some(PaneResult::Failed(GitFailure {
                    recovery: unreachable.map(Recovery::Connect),
                    ..GitFailure::plain(&sentence)
                }));
            }
        }
    }
    fn operation_succeeded(
        &mut self,
        running: RunningOperation,
        result: Option<Value>,
        cx: &mut Context<Self>,
    ) {
        let RunningOperation {
            action,
            edit_revision,
            ..
        } = running;
        if action == "draftCommitMessage" {
            // A generated message lands only on the box it was asked for; anything else leaves the text alone.
            let current = edit_revision == self.edit_revision;
            if let (true, Some(Value::String(text))) = (current, result) {
                self.set_message(text, cx);
            }
            return;
        }
        let result = result.unwrap_or(Value::Null);
        let message = result["message"]
            .as_str()
            .unwrap_or(finished_label(action))
            .to_string();
        self.result = Some(PaneResult::Done(message));
        let status = serde_json::from_value::<GitStatus>(result["status"].clone()).ok();
        self.after_sync(status, cx);
    }
    /// A sync moved refs the status doesn't show, so the branches and commits read again whatever the head did.
    fn after_sync(&mut self, status: Option<GitStatus>, cx: &mut Context<Self>) {
        match status {
            Some(status) => {
                self.read.update(cx, |read, cx| read.show(status, cx));
                self.result_answers = self.read.read(cx).answers();
            }
            None => {
                self.read.update(cx, |read, cx| read.refresh(cx));
                self.result_answers = self.read.read(cx).answers() + 1;
            }
        }
        self.read_branches(cx);
        if let Some((_, timeline)) = &self.timeline {
            timeline.update(cx, |timeline, cx| timeline.refresh(cx));
        }
    }
    fn forget_branches(&mut self) {
        self.branches = None;
        self.branches_failure = None;
        self.branches_read = None;
        self.branches_target = None;
    }
    pub(super) fn read_branches(&mut self, cx: &mut Context<Self>) {
        let Some(target) = self.target(cx) else {
            return;
        };
        let Some(socket) = SocketClient::try_global(cx) else {
            return;
        };
        let args = target.read_args(json!({"session":target.session}));
        let stash_args = args.clone();
        let call = socket
            .read(cx)
            .call::<GitBranchList>("git", "branches", args, None);
        self.branches_failure = None;
        self.branches_target = Some(target.clone());
        self.branches_read = Some(cx.spawn(async move |pane, cx| {
            let answer = call.await;
            _ = pane.update(cx, |pane, cx| {
                if pane.branches_target.as_ref() != Some(&target) {
                    return;
                }
                pane.branches_read = None;
                match answer {
                    Outcome::Ok(list) => pane.branches = Some(list),
                    Outcome::Failed(failure) => pane.branches_failure = Some(failure.message),
                    Outcome::Aborted => {
                        pane.branches_failure = Some("The branch list didn't answer.".into())
                    }
                }
                pane.push_branch_menu(cx);
                cx.notify();
            });
        }));
        let call = socket
            .read(cx)
            .call::<Vec<Stash>>("git", "stashes", stash_args, None);
        self.stashes_read = Some(cx.spawn(async move |pane, cx| {
            // Without the list the menu just offers no Pop; Stashes… says why when opened.
            let answer = call.await;
            _ = pane.update(cx, |pane, cx| {
                pane.stashes = match answer {
                    Outcome::Ok(items) => items,
                    _ => Vec::new(),
                };
                pane.push_branch_menu(cx);
            });
        }));
        self.push_branch_menu(cx);
    }
    pub(super) fn push_branch_menu(&mut self, cx: &mut Context<Self>) {
        let branches = self
            .branches
            .as_ref()
            .map(|list| list.branches.clone())
            .unwrap_or_default();
        let reading = self.branches_read.is_some();
        let failure = self.branches_failure.clone();
        // A read-only folder still opens the menu; only its rows are off.
        let busy = self.busy(cx) || self.read_only(cx);
        let stashes = self.stashes.clone();
        let has_changes = self
            .shown_status(cx)
            .is_some_and(|status| status.total_files > 0);
        self.branch_menu.update(cx, |menu, cx| {
            menu.set_stashes(stashes, cx);
            menu.set_state(branches, reading, failure, busy, has_changes, cx)
        });
    }
    pub(super) fn connect(&mut self, machine: String, cx: &mut Context<Self>) {
        let Some(socket) = SocketClient::try_global(cx) else {
            return;
        };
        let call =
            socket
                .read(cx)
                .call::<Value>("machines", "connect", json!({"machine":machine}), None);
        self.connecting = Some(cx.spawn(async move |pane, cx| {
            let answer = call.await;
            _ = pane.update(cx, |pane, cx| {
                pane.connecting = None;
                match answer {
                    Outcome::Ok(_) => {
                        if matches!(&pane.result, Some(PaneResult::Failed(failure)) if matches!(failure.recovery, Some(Recovery::Connect(_)))) {
                            pane.result = None;
                        }
                        pane.read.update(cx, |read, cx| read.refresh(cx));
                    }
                    Outcome::Failed(failure) => pane.fail(&failure.message, cx),
                    Outcome::Aborted => {}
                }
                cx.notify();
            });
        }));
        cx.notify();
    }
    /// The message box is edited only from a deferred turn with its window in hand.
    pub(super) fn set_message(&self, text: String, cx: &mut Context<Self>) {
        let input = self.message.clone();
        let handle = self.window;
        cx.defer(move |cx| {
            _ = handle.update(cx, |_, window, cx| {
                input.update(cx, |input, cx| input.set_value(text, window, cx))
            });
        });
    }
}
