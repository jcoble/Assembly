//! The reads a pull request page runs through its owner as Git operations: a failed job's log and the merge's
//! conflicts. Each is read when the person asks for it and kept until a read of the pull request shows a new head.
use super::super::{
    pr_owner::PrOwner,
    pull_request::{PrCheckLog, PrConflicts},
};
use super::*;
use serde::de::DeserializeOwned;
use std::collections::HashMap;

#[derive(Clone, Debug, PartialEq)]
pub(super) enum Looked<T> {
    Reading,
    Failed(String),
    Found(T),
}
struct Read {
    operation: Entity<GitOperation>,
    _heard: Subscription,
}
pub(super) struct PrLookups {
    owner: PrOwner,
    /// The repository and root the shown read reported, and its head.
    shown: Option<(String, String, String)>,
    logs: HashMap<u64, Read>,
    conflicts: Option<Read>,
}
fn looked<T: DeserializeOwned>(operation: &GitOperation) -> Looked<T> {
    match operation.status.as_ref().filter(|status| status.terminal()) {
        Some(status) if status.state == "succeeded" => status
            .result
            .clone()
            .and_then(|result| serde_json::from_value(result).ok())
            .map_or_else(
                || Looked::Failed("The answer couldn't be read.".into()),
                Looked::Found,
            ),
        Some(status) => Looked::Failed(
            (status.error.clone())
                .or_else(|| operation.error.clone())
                .unwrap_or_else(|| "The read didn't finish.".into()),
        ),
        None if !operation.busy && operation.error.is_some() => {
            Looked::Failed(operation.error.clone().unwrap_or_default())
        }
        None => Looked::Reading,
    }
}
impl PrLookups {
    pub(super) fn new(owner: PrOwner) -> Self {
        Self {
            owner,
            shown: None,
            logs: HashMap::new(),
            conflicts: None,
        }
    }
    /// A read of the pull request arrived; what was read against another head no longer describes it.
    pub(super) fn show_read(&mut self, detail: &PullRequestDetail, cx: &mut Context<Self>) {
        let moved = (self.shown.as_ref()).is_some_and(|(_, _, head)| *head != detail.head_sha);
        if moved {
            self.logs.clear();
            self.conflicts = None;
            cx.notify();
        }
        self.shown = Some((
            detail.repository.clone(),
            detail.local_root.clone(),
            detail.head_sha.clone(),
        ));
    }
    fn start(&self, action: &'static str, args: Value, cx: &mut Context<Self>) -> Option<Read> {
        let shown =
            (self.shown.as_ref()).map(|(repository, root, _)| (repository.as_str(), root.as_str()));
        let scope = self.owner.scope(shown)?;
        let operation = cx.new(|_| GitOperation::default());
        let heard = cx.observe(&operation, |_, _, cx| cx.notify());
        operation.update(cx, |operation, cx| {
            operation.start_scope(action, scope, args, cx)
        });
        Some(Read {
            operation,
            _heard: heard,
        })
    }
    fn settled_badly(read: Option<&Read>, cx: &App) -> bool {
        read.is_none_or(|read| {
            matches!(looked::<Value>(read.operation.read(cx)), Looked::Failed(_))
        })
    }
    /// Reads a job's log once; a read that failed is tried again.
    pub(super) fn read_log(&mut self, number: u64, job: u64, cx: &mut Context<Self>) {
        if !Self::settled_badly(self.logs.get(&job), cx) {
            return;
        }
        if let Some(read) = self.start("checkLog", json!({"number":number,"jobId":job}), cx) {
            self.logs.insert(job, read);
            cx.notify();
        }
    }
    pub(super) fn log(&self, job: u64, cx: &App) -> Option<Looked<PrCheckLog>> {
        Some(looked(self.logs.get(&job)?.operation.read(cx)))
    }
    pub(super) fn read_conflicts(&mut self, number: u64, cx: &mut Context<Self>) {
        if !Self::settled_badly(self.conflicts.as_ref(), cx) {
            return;
        }
        let Some(head) = self.shown.as_ref().map(|(_, _, head)| head.clone()) else {
            return;
        };
        self.conflicts = self.start(
            "conflicts",
            json!({"number":number,"expectedHead":head}),
            cx,
        );
        cx.notify();
    }
    pub(super) fn conflicts(&self, cx: &App) -> Option<Looked<PrConflicts>> {
        Some(looked(self.conflicts.as_ref()?.operation.read(cx)))
    }
}
