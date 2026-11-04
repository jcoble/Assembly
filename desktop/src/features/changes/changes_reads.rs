//! How the Changes tab reads: each status answer starts a round, at most four files at once, and a reply counts only
//! for the round it was asked in. A commit's one file is read on its own.
use super::{ChangesPane, CombinedView, CommitPick, Picked, Target};
use crate::core::socket::{
    checkout_selector::CheckoutSelector, client::SocketClient, outcome::Outcome,
};
use crate::core::state::{inspected_checkout::InspectedCheckout, session_read::Shown};
use crate::core::ui::{
    center_panes::CenterPanes,
    changed_tree::{TreeFile, matches_filter, tree_order},
};
use crate::features::changes::{
    branch_diff::BranchDiff,
    file_diff::FileDiff,
    working_copy::{Scope, Selection, read_branch, read_file, wanted_files},
};
use crate::features::source_control::{git_checkouts::checkout_gone, git_status::GitStatus};
use gpui_kit::component::diff::DiffFile;
use gpui_kit::*;
use serde_json::json;
use std::collections::HashSet;
use std::rc::Rc;

const NO_BACKEND: &str = "The backend isn't running.";

impl ChangesPane {
    pub(super) fn show(&mut self, picked: Picked, cx: &mut Context<Self>) {
        match picked {
            Picked::File(path) => self.show_file(path, cx),
            Picked::Commit(pick) => self.show_commit(pick, cx),
        }
    }
    /// A file picked in Source control: its place in the working copy.
    fn show_file(&mut self, path: String, cx: &mut Context<Self>) {
        let was_commit = matches!(self.target, Target::Commit(_));
        self.target = Target::Stack;
        self.commit_read = None;
        self.commit_waiting = false;
        self.commit_failure = None;
        if was_commit || self.scope != Scope::Uncommitted {
            self.scope = Scope::Uncommitted;
            self.forget_stack(cx);
            self.focus_path = Some(path);
            CenterPanes::save_layout(cx);
            if self.active {
                if was_commit {
                    self.start_status(cx);
                } else {
                    self.status.update(cx, |status, cx| status.refresh(cx));
                }
            }
            return;
        }
        self.working.focus_first(&path);
        // Not read yet, it waits for the round that reads it.
        let here = self.working.answers().contains_key(&path);
        self.focus_path = Some(path);
        if here {
            self.jump(cx);
        }
    }
    fn show_commit(&mut self, pick: CommitPick, cx: &mut Context<Self>) {
        self.stop_status(cx);
        self.forget_stack(cx);
        self.selection = None;
        self.focus_path = None;
        self.owner = Some((pick.session.clone(), pick.expected_source.clone()));
        self.target = Target::Commit(pick);
        self.commit_failure = None;
        self.commit_read = None;
        self.commit_waiting = true;
        if self.active {
            self.read_commit(cx);
        }
        cx.notify();
    }
    pub(super) fn activate(&mut self, active: bool, cx: &mut Context<Self>) {
        if self.active == active {
            return;
        }
        self.active = active;
        match (&self.target, active) {
            (Target::Stack, true) => self.start_status(cx),
            (Target::Commit(_), true) => {
                if self.commit_waiting {
                    self.read_commit(cx);
                }
            }
            (_, false) => {
                self.stop_status(cx);
                if self.commit_read.take().is_some() {
                    self.commit_waiting = true;
                }
            }
        }
        cx.notify();
    }
    /// Whatever status is already here isn't proof of the checkout now: only the answer this read brings starts reads.
    fn start_status(&mut self, cx: &mut Context<Self>) {
        self.started_for = Some(self.status.read(cx).answers());
        self.status
            .update(cx, |status, cx| status.set_active(true, cx));
        self.sync_inspected(false, cx);
    }
    fn stop_status(&mut self, cx: &mut Context<Self>) {
        self.working.stop();
        self.reads.clear();
        self.branch_read = None;
        self.started_for = None;
        self.status
            .update(cx, |status, cx| status.set_active(false, cx));
    }
    pub(super) fn refresh(&mut self, cx: &mut Context<Self>) {
        self.status.update(cx, |status, cx| status.refresh(cx));
    }
    pub(super) fn set_scope(&mut self, scope: Scope, cx: &mut Context<Self>) {
        if self.scope == scope {
            return;
        }
        self.scope = scope;
        self.forget_stack(cx);
        self.refresh(cx);
        CenterPanes::save_layout(cx);
        cx.notify();
    }
    /// Follows the checkout Source control has read for this session. A status it read moves this tab's too.
    pub(super) fn sync_inspected(&mut self, read_again: bool, cx: &mut Context<Self>) {
        if !self.active || self.target != Target::Stack {
            return;
        }
        let current = InspectedCheckout::global(cx).read(cx).current.clone();
        let status = self.status.read(cx);
        let same_session = current.filter(|inspected| {
            status.session() == Some(inspected.session.as_str())
                && status.source() == inspected.source.as_ref()
        });
        let checkout = same_session
            .as_ref()
            .and_then(|inspected| inspected.checkout.clone());
        let moved = status.checkout() != checkout.as_ref();
        let stamp = same_session.map(|inspected| (inspected.fingerprint, inspected.answers));
        let newer = read_again && stamp.is_some() && stamp != self.published;
        self.published = stamp;
        if moved {
            self.status
                .update(cx, |status, cx| status.set_checkout(checkout, cx));
        } else if newer {
            self.refresh(cx);
        }
    }
    pub(super) fn status_changed(&mut self, cx: &mut Context<Self>) {
        // A hidden tab forgets the session its read stopped for, which isn't the selection changing.
        if !self.active || self.target != Target::Stack {
            return;
        }
        let read = self.status.read(cx);
        let selection = read.session().map(|session| Selection {
            session: session.to_string(),
            source: read.source().cloned(),
            checkout: read.checkout().cloned(),
        });
        let answers = read.answers();
        let answer = match read.shown() {
            Shown::Read(status) => Ok(Some(status.clone())),
            Shown::Failed(_) => {
                Err(read.failure_code().is_some_and(checkout_gone) && read.checkout().is_some())
            }
            _ => Ok(None),
        };
        if selection != self.selection {
            let moved = self.selection.is_some();
            let new_session = self.selection.as_ref().map(|selection| &selection.session)
                != selection.as_ref().map(|selection| &selection.session);
            self.selection = selection.clone();
            self.forget_stack(cx);
            if moved {
                self.focus_path = None;
            }
            // Only for another session: a deleted checkout must not be picked straight back up.
            if new_session {
                self.sync_inspected(false, cx);
            }
        }
        if let Some(selection) = &selection {
            match &mut self.owner {
                None => self.owner = Some((selection.session.clone(), selection.source.clone())),
                Some((session, source)) if *session == selection.session => {
                    *source = selection.source.clone();
                }
                Some(_) => {}
            }
        }
        if self.started_for != Some(answers) {
            match answer {
                Ok(Some(status)) => {
                    self.started_for = Some(answers);
                    self.start_round(&status, cx);
                }
                Err(gone) => {
                    self.started_for = Some(answers);
                    self.working.stop();
                    self.reads.clear();
                    self.branch_read = None;
                    if gone {
                        self.status
                            .update(cx, |status, cx| status.set_checkout(None, cx));
                    }
                }
                Ok(None) => {}
            }
        }
        cx.notify();
    }
    fn start_round(&mut self, status: &GitStatus, cx: &mut Context<Self>) {
        self.reads.clear();
        self.branch_read = None;
        self.branch_failure = None;
        self.more_line = status.more_line();
        let branch = self.scope == Scope::Branch;
        let wanted = wanted_files(status, self.scope);
        let round = self
            .working
            .begin(wanted, branch, self.focus_path.as_deref());
        if branch {
            self.read_branch_diff(round, cx);
        }
        self.pump(cx);
        self.show_answers(cx);
    }
    fn pump(&mut self, cx: &mut Context<Self>) {
        let Some(selection) = self.selection.clone() else {
            return;
        };
        let Some(socket) = SocketClient::try_global(cx) else {
            return;
        };
        let round = self.working.round();
        while let Some(next) = self.working.next_read() {
            let args = selection.args(json!({"path":next.path,"originalPath":next.original_path}));
            let call = socket.read(cx).call::<FileDiff>("git", "diff", args, None);
            let path = next.path.clone();
            let task = cx.spawn(async move |pane, cx| {
                let outcome = call.await;
                _ = pane.update(cx, |pane, cx| pane.file_answered(round, path, outcome, cx));
            });
            self.reads.insert(next.path, task);
        }
    }
    fn file_answered(
        &mut self,
        round: u64,
        path: String,
        outcome: Outcome<FileDiff>,
        cx: &mut Context<Self>,
    ) {
        let file = match outcome {
            Outcome::Ok(diff) => read_file(&diff).ok(),
            _ => None,
        };
        if self.working.answered(round, &path, file) {
            self.reads.remove(&path);
            self.pump(cx);
            self.show_answers(cx);
        }
    }
    fn read_branch_diff(&mut self, round: u64, cx: &mut Context<Self>) {
        let (Some(selection), Some(socket)) =
            (self.selection.clone(), SocketClient::try_global(cx))
        else {
            self.branch_failure = Some(NO_BACKEND.into());
            self.working.branch_answered(round, None);
            return;
        };
        let call = socket.read(cx).call::<BranchDiff>(
            "git",
            "branchDiff",
            selection.args(json!({})),
            None,
        );
        self.branch_read = Some(cx.spawn(async move |pane, cx| {
            let outcome = call.await;
            _ = pane.update(cx, |pane, cx| pane.branch_answered(round, outcome, cx));
        }));
    }
    fn branch_answered(
        &mut self,
        round: u64,
        outcome: Outcome<BranchDiff>,
        cx: &mut Context<Self>,
    ) {
        if round != self.working.round() {
            return;
        }
        self.branch_read = None;
        let read = match outcome {
            Outcome::Ok(branch) => {
                self.base = Some(branch.base.clone());
                Some(read_branch(&branch))
            }
            Outcome::Failed(failure) => {
                self.branch_failure = Some(failure.message);
                None
            }
            Outcome::Aborted => {
                self.branch_failure = Some("The connection dropped.".into());
                None
            }
        };
        self.working.branch_answered(round, read);
        self.show_answers(cx);
    }
    pub(super) fn read_commit(&mut self, cx: &mut Context<Self>) {
        let Target::Commit(pick) = &self.target else {
            return;
        };
        self.commit_waiting = false;
        let Some(socket) = SocketClient::try_global(cx) else {
            self.commit_failure = Some(NO_BACKEND.into());
            cx.notify();
            return;
        };
        let args = CheckoutSelector::applied(
            pick.checkout.as_ref(),
            json!({"session":pick.session,"sha":pick.sha,"path":pick.path,"expectedSource":pick.expected_source}),
        );
        let path = pick.path.clone();
        let call = socket
            .read(cx)
            .call::<FileDiff>("git", "commitDiff", args, None);
        self.commit_read = Some(cx.spawn(async move |pane, cx| {
            let outcome = call.await;
            _ = pane.update(cx, |pane, cx| pane.commit_answered(path, outcome, cx));
        }));
        cx.notify();
    }
    fn commit_answered(
        &mut self,
        path: String,
        outcome: Outcome<FileDiff>,
        cx: &mut Context<Self>,
    ) {
        self.commit_read = None;
        let read = match outcome {
            Outcome::Ok(diff) => read_file(&diff),
            Outcome::Failed(failure) => Err(failure.message),
            Outcome::Aborted => Err("The connection dropped.".into()),
        };
        match read {
            Ok(mut file) => {
                for patch in &mut file.combined {
                    patch.label = "Commit";
                }
                self.commit_failure = None;
                self.working.set_only(path, file);
                self.show_answers(cx);
            }
            Err(failure) => self.commit_failure = Some(failure),
        }
        cx.notify();
    }
    /// A different selection or scope: nothing on screen is about it.
    pub(super) fn forget_stack(&mut self, cx: &mut Context<Self>) {
        self.working.clear();
        self.reads.clear();
        self.branch_read = None;
        self.base = None;
        self.branch_failure = None;
        self.more_line = None;
        self.viewed.clear();
        self.both = Rc::new(HashSet::new());
        self.combined.clear();
        self.notes.clear();
        self.totals = (0, 0);
        self.active_file = None;
        self.line_notice = None;
        self.tree.set_files(Vec::new());
        self.state
            .update(cx, |state, cx| state.set_files(Vec::new(), cx));
        cx.notify();
    }
    /// Redraws only when what would be shown differs, because redrawing the stack resets its scroll.
    pub(super) fn show_answers(&mut self, cx: &mut Context<Self>) {
        if self.working.should_show() && self.working.shown_changed() {
            let order = tree_order(self.working.answers().keys().map(String::as_str));
            let mut tree = Vec::with_capacity(order.len());
            let mut both = HashSet::new();
            let mut notes = Vec::new();
            let mut combined: Vec<CombinedView> = Vec::new();
            let mut totals = (0, 0);
            for path in order {
                let file = &self.working.answers()[&path];
                let additions: usize = file.files.iter().map(DiffFile::additions).sum();
                let deletions: usize = file.files.iter().map(DiffFile::deletions).sum();
                totals = (totals.0 + additions, totals.1 + deletions);
                if file.both {
                    both.insert(path.clone());
                }
                notes.extend(file.notes.iter().map(|note| (path.clone(), *note)));
                for patch in &file.combined {
                    let kept = self
                        .combined
                        .iter()
                        .position(|view| view.path == path && view.patch == *patch);
                    combined.push(match kept {
                        Some(at) => self.combined.swap_remove(at),
                        None => CombinedView {
                            path: path.clone(),
                            patch: patch.clone(),
                            editor: None,
                        },
                    });
                }
                tree.push(TreeFile {
                    path,
                    additions,
                    deletions,
                });
            }
            self.tree.set_files(tree);
            self.both = Rc::new(both);
            self.notes = notes;
            self.combined = combined;
            self.totals = totals;
            self.show_filtered(cx);
        }
        self.jump(cx);
        cx.notify();
    }
    pub(super) fn show_filtered(&mut self, cx: &mut Context<Self>) {
        self.tree.set_filter(&self.filter);
        let answers = self.working.answers();
        let shown: Vec<DiffFile> = tree_order(answers.keys().map(String::as_str))
            .iter()
            .filter(|path| matches_filter(path, &self.filter))
            .flat_map(|path| answers[path].files.iter().cloned())
            .collect();
        let viewed = self.viewed.clone();
        self.state.update(cx, |state, cx| {
            state.set_files(shown, cx);
            for path in &viewed {
                state.set_file_collapsed(path, true, cx);
            }
        });
        cx.notify();
    }
    /// Goes to the picked file once its round has finished, when nothing above it can still move it.
    fn jump(&mut self, cx: &mut Context<Self>) {
        if self.working.pending() > 0 {
            return;
        }
        let Some(path) = self.focus_path.take() else {
            return;
        };
        if self.working.answers().contains_key(&path) {
            self.state
                .update(cx, |state, cx| state.scroll_to_file(&path, cx));
            self.active_file = Some(path);
            cx.notify();
        }
    }
    pub(super) fn toggle_viewed(&mut self, path: String, cx: &mut Context<Self>) {
        let viewed = !self.viewed.remove(&path);
        if viewed {
            self.viewed.insert(path.clone());
        }
        self.state
            .update(cx, |state, cx| state.set_file_collapsed(&path, viewed, cx));
        cx.notify();
    }
    pub(super) fn pick_in_tree(&mut self, path: String, cx: &mut Context<Self>) {
        self.state
            .update(cx, |state, cx| state.scroll_to_file(&path, cx));
        self.active_file = Some(path);
        cx.notify();
    }
}
