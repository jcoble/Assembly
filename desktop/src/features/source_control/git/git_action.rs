use super::git_failure::GitFailure;
use super::git_operation::GitOperation;
use super::git_status::GitStatus;
use super::git_target::GitTarget;
use crate::core::socket::{client::SocketClient, outcome::Outcome};
use gpui_kit::*;
use serde::Deserialize;
use serde_json::Value;

#[derive(Clone, Debug, Deserialize)]
pub struct GitWriteResult {
    pub message: String,
    pub status: GitStatus,
}
/// The pane's short writes. With `paired` it also refuses while the pane's long operation is out, so the pane has one
/// write at a time whichever door a dialog came through.
#[derive(Default)]
pub struct GitAction {
    pub pending: Option<&'static str>,
    /// What the running or last write was decided against, so its answer lands only there.
    pub target: Option<GitTarget>,
    pub message: Option<String>,
    pub failure: Option<GitFailure>,
    /// The status the last successful write returned, for showing with no second read.
    pub status: Option<GitStatus>,
    pub completed: u64,
    pub succeeded: bool,
    paired: Option<Entity<GitOperation>>,
    task: Option<Task<()>>,
}
impl GitAction {
    pub fn paired(operation: Entity<GitOperation>) -> Self {
        Self {
            paired: Some(operation),
            ..Self::default()
        }
    }
    pub fn failure_sentence(&self) -> Option<String> {
        self.failure
            .as_ref()
            .map(|failure| failure.sentence.clone())
    }
    pub fn run(
        &mut self,
        target: &GitTarget,
        method: &'static str,
        args: Value,
        cx: &mut Context<Self>,
    ) -> bool {
        let long_running = self
            .paired
            .as_ref()
            .is_some_and(|operation| operation.read(cx).occupied());
        if self.pending.is_some() || long_running {
            return false;
        }
        let Some(socket) = SocketClient::try_global(cx) else {
            self.failure = Some(GitFailure::plain("The backend isn't running."));
            cx.notify();
            return false;
        };
        let call =
            socket
                .read(cx)
                .call::<GitWriteResult>("git", method, target.write_args(args), None);
        self.pending = Some(method);
        self.target = Some(target.clone());
        self.message = None;
        self.failure = None;
        self.status = None;
        self.succeeded = false;
        self.task = Some(cx.spawn(async move |action, cx| {
            let outcome = call.await;
            _ = action.update(cx, |action, cx| {
                action.pending = None;
                action.task = None;
                action.completed += 1;
                match outcome {
                    Outcome::Ok(result) => {
                        action.message = Some(result.message);
                        action.status = Some(result.status);
                        action.succeeded = true;
                    }
                    Outcome::Failed(failure) => {
                        action.failure = Some(GitFailure::from_call(&failure))
                    }
                    Outcome::Aborted => action.failure = Some(GitFailure::lost_reply()),
                }
                cx.notify();
            });
        }));
        cx.notify();
        true
    }
}
/// The pane's one write authority: its short writes and its long operation, handed together to every dialog so a dialog
/// can't start something beside what the pane is already doing.
#[derive(Clone)]
pub struct GitWrites {
    pub action: Entity<GitAction>,
    pub operation: Entity<GitOperation>,
}
impl GitWrites {
    pub fn new(cx: &mut App) -> Self {
        let operation = cx.new(|_| GitOperation::default());
        let paired = operation.clone();
        let action = cx.new(|_| GitAction::paired(paired));
        Self { action, operation }
    }
    pub fn busy(&self, cx: &App) -> bool {
        self.action.read(cx).pending.is_some() || self.operation.read(cx).occupied()
    }
}
#[cfg(test)]
#[path = "git_action_tests.rs"]
mod tests;
