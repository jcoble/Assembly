//! The one post or merge a pull request page runs at a time. The confirm dialogs close as soon as they're confirmed, as
//! the old screen's did, so the operation and what it came to live here. An outcome nobody can confirm blocks the next
//! write until the page has read the pull request again, and that read is what lets the person try again.
use super::super::git_operation::GitOperation;
use super::{
    merge_dialog::MergeRequest, post_confirm::PostRequest, pr_owner::PrOwner,
    review_draft::ReviewIdentity,
};
use gpui_kit::*;
use serde_json::Value;

#[derive(Clone, Copy, Debug, PartialEq)]
pub enum WriteKind {
    Post { reply: bool },
    Merge,
    Act(PrAct),
}
/// The writes that move a pull request along without posting anything.
#[derive(Clone, Copy, Debug, PartialEq)]
pub enum PrAct {
    Resolve,
    Reopen,
    Ready,
    BackToDraft,
    UpdateBranch,
    Rerun,
}
impl PrAct {
    fn action(self) -> &'static str {
        match self {
            Self::Resolve | Self::Reopen => "resolveThread",
            Self::Ready | Self::BackToDraft => "ready",
            Self::UpdateBranch => "updateBranch",
            Self::Rerun => "rerunFailed",
        }
    }
    /// What the button says while it runs.
    pub fn doing(self) -> &'static str {
        match self {
            Self::Resolve => "Resolving…",
            Self::Reopen => "Reopening…",
            Self::Ready => "Marking ready…",
            Self::BackToDraft => "Converting…",
            Self::UpdateBranch => "Updating…",
            Self::Rerun => "Starting…",
        }
    }
    /// What the page says once GitHub took it.
    pub fn done(self) -> &'static str {
        match self {
            Self::Resolve => "Thread resolved.",
            Self::Reopen => "Thread reopened.",
            Self::Ready => "Marked ready for review.",
            Self::BackToDraft => "Converted to a draft.",
            Self::UpdateBranch => {
                "GitHub is merging the base into this branch. Checks will run again."
            }
            Self::Rerun => "The failed jobs are running again.",
        }
    }
}
#[derive(Clone, Debug, PartialEq)]
pub enum WriteDone {
    Posted { reply: bool, url: String },
    Merged { url: String },
    Acted { act: PrAct },
}
pub struct PrWrites {
    owner: PrOwner,
    /// The repository and root the shown read reported, sent back unchanged with a write.
    shown: Option<(String, String)>,
    operation: Entity<GitOperation>,
    kind: Option<WriteKind>,
    /// A write was started and its outcome hasn't been taken yet.
    waiting: bool,
    /// GitHub took it but gave no link back, so nobody can say what happened.
    not_confirmed: bool,
    _operation: Subscription,
}
impl EventEmitter<WriteDone> for PrWrites {}
fn github_url(result: Option<&Value>) -> Option<String> {
    result
        .and_then(Value::as_str)
        .filter(|value| {
            url::Url::parse(value)
                .is_ok_and(|url| url.scheme() == "https" && url.host_str() == Some("github.com"))
        })
        .map(String::from)
}
impl PrWrites {
    pub fn new(owner: PrOwner, cx: &mut Context<Self>) -> Self {
        let (operation, heard) = Self::fresh_operation(cx);
        Self {
            owner,
            shown: None,
            operation,
            kind: None,
            waiting: false,
            not_confirmed: false,
            _operation: heard,
        }
    }
    fn fresh_operation(cx: &mut Context<Self>) -> (Entity<GitOperation>, Subscription) {
        let operation = cx.new(|_| GitOperation::default());
        let heard = cx.observe(&operation, |writes: &mut Self, _, cx| {
            writes.operation_changed(cx)
        });
        (operation, heard)
    }
    pub fn show_read(&mut self, repository: String, root: String) {
        self.shown = Some((repository, root));
    }
    fn operation_changed(&mut self, cx: &mut Context<Self>) {
        let operation = self.operation.read(cx);
        // A write with an unknown outcome stays watched: the owner can still report how it really ended.
        let settled = !operation.busy && !operation.unconfirmed;
        let succeeded = operation
            .status
            .as_ref()
            .filter(|status| status.state == "succeeded")
            .map(|status| github_url(status.result.as_ref()));
        if self.waiting && settled {
            self.waiting = false;
            match (succeeded, self.kind) {
                (Some(Some(url)), Some(WriteKind::Post { reply })) => {
                    cx.emit(WriteDone::Posted { reply, url })
                }
                (Some(Some(url)), Some(WriteKind::Merge)) => cx.emit(WriteDone::Merged { url }),
                (Some(Some(_)), Some(WriteKind::Act(act))) => cx.emit(WriteDone::Acted { act }),
                (Some(_), _) => self.not_confirmed = true,
                (None, _) => {}
            }
        }
        cx.notify();
    }
    pub fn post(
        &mut self,
        request: &PostRequest,
        current: &ReviewIdentity,
        cx: &mut Context<Self>,
    ) -> Result<(), String> {
        self.refuse_if_occupied(cx)?;
        let args = request.args(current)?;
        let kind = WriteKind::Post {
            reply: request.is_reply(),
        };
        self.start(kind, request.action(), args, cx)
    }
    pub fn merge(&mut self, request: &MergeRequest, cx: &mut Context<Self>) -> Result<(), String> {
        self.refuse_if_occupied(cx)?;
        let args = request.args()?;
        self.start(WriteKind::Merge, "merge", args, cx)
    }
    /// Resolves a thread, marks ready, updates the branch or re-runs jobs. The args name the pull request and head.
    pub fn act(&mut self, act: PrAct, args: Value, cx: &mut Context<Self>) -> Result<(), String> {
        self.refuse_if_occupied(cx)?;
        self.start(WriteKind::Act(act), act.action(), args, cx)
    }
    /// The act running now, so its own button can say so.
    pub fn acting(&self, cx: &App) -> Option<PrAct> {
        match self.kind {
            Some(WriteKind::Act(act)) if self.busy(cx) => Some(act),
            _ => None,
        }
    }
    fn refuse_if_occupied(&self, cx: &App) -> Result<(), String> {
        if self.free(cx) {
            Ok(())
        } else {
            Err("Refresh this pull request before trying again.".into())
        }
    }
    fn start(
        &mut self,
        kind: WriteKind,
        action: &'static str,
        args: Value,
        cx: &mut Context<Self>,
    ) -> Result<(), String> {
        let shown =
            (self.shown.as_ref()).map(|(repository, root)| (repository.as_str(), root.as_str()));
        let scope =
            (self.owner.scope(shown)).ok_or("Refresh this pull request before trying again.")?;
        self.kind = Some(kind);
        self.waiting = true;
        self.not_confirmed = false;
        self.operation.update(cx, |operation, cx| {
            operation.start_scope(action, scope, args, cx)
        });
        Ok(())
    }
    pub fn cancel(&mut self, cx: &mut Context<Self>) {
        self.operation
            .update(cx, |operation, cx| operation.cancel(cx));
    }
    /// The page read the pull request after this write settled, so what it did is on screen and the slate is clean.
    pub fn page_read(&mut self, cx: &mut Context<Self>) {
        if self.operation.read(cx).busy {
            return;
        }
        let (operation, heard) = Self::fresh_operation(cx);
        self.operation = operation;
        self._operation = heard;
        self.kind = None;
        self.waiting = false;
        self.not_confirmed = false;
        cx.notify();
    }
    pub fn busy(&self, cx: &App) -> bool {
        self.operation.read(cx).busy
    }
    pub fn uncertain(&self, cx: &App) -> bool {
        let operation = self.operation.read(cx);
        !operation.busy && (operation.unconfirmed || self.not_confirmed)
    }
    pub fn free(&self, cx: &App) -> bool {
        !self.operation.read(cx).occupied() && !self.not_confirmed
    }
    pub fn posting(&self, cx: &App) -> bool {
        self.busy(cx) && matches!(self.kind, Some(WriteKind::Post { .. }))
    }
    pub fn merging(&self, cx: &App) -> bool {
        self.busy(cx) && self.kind == Some(WriteKind::Merge)
    }
    pub fn merge_uncertain(&self, cx: &App) -> bool {
        self.uncertain(cx) && self.kind == Some(WriteKind::Merge)
    }
    fn message(&self, cx: &App) -> Option<String> {
        if self.not_confirmed {
            return Some(match self.kind {
                Some(WriteKind::Merge) => {
                    "The merge was not confirmed. Refresh before trying again.".into()
                }
                Some(WriteKind::Act(_)) => {
                    "GitHub didn't confirm that. Refresh before trying again.".into()
                }
                _ => "The post was not confirmed. Refresh before trying again.".into(),
            });
        }
        self.operation.read(cx).error.clone()
    }
    /// What went wrong with a post: the old screen put this at the top of the page.
    pub fn post_error(&self, cx: &App) -> Option<String> {
        matches!(self.kind, Some(WriteKind::Post { .. }))
            .then(|| self.message(cx))
            .flatten()
    }
    /// What went wrong with a thread, draft, branch or re-run; the page shows it at the top as a post's.
    pub fn act_error(&self, cx: &App) -> Option<String> {
        matches!(self.kind, Some(WriteKind::Act(_)))
            .then(|| self.message(cx))
            .flatten()
    }
    /// What went wrong with a merge: the old screen put this in the merge card.
    pub fn merge_error(&self, cx: &App) -> Option<String> {
        (self.kind == Some(WriteKind::Merge))
            .then(|| self.message(cx))
            .flatten()
    }
}
#[cfg(test)]
#[path = "pr_writes_tests.rs"]
mod tests;
