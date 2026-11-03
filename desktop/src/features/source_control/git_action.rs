use super::git_status::GitStatus;
use crate::core::socket::{client::SocketClient, outcome::Outcome};
use gpui_kit::*;
use serde::Deserialize;
use serde_json::Value;

#[derive(Clone, Debug, Deserialize)]
pub struct GitWriteResult {
    pub message: String,
    pub status: GitStatus,
}
#[derive(Default)]
pub struct GitAction {
    pub pending: Option<&'static str>,
    pub session: Option<String>,
    pub message: Option<String>,
    pub failure: Option<String>,
    pub completed: u64,
    pub succeeded: bool,
    task: Option<Task<()>>,
}
impl GitAction {
    pub fn run(
        &mut self,
        session: String,
        method: &'static str,
        args: Value,
        cx: &mut Context<Self>,
    ) -> bool {
        if self.pending.is_some() {
            return false;
        }
        let Some(socket) = SocketClient::try_global(cx) else {
            self.failure = Some("The backend isn't running.".into());
            cx.notify();
            return false;
        };
        let call = socket
            .read(cx)
            .call::<GitWriteResult>("git", method, args, None);
        self.pending = Some(method);
        self.session = Some(session);
        self.message = None;
        self.failure = None;
        self.succeeded = false;
        self.task=Some(cx.spawn(async move|action,cx|{
            let outcome=call.await;
            _=action.update(cx,|action,cx|{
                action.pending=None;action.task=None;action.completed+=1;
                match outcome {
                    Outcome::Ok(result)=>{action.message=Some(result.message);action.succeeded=true;},
                    Outcome::Failed(failure)=>action.failure=Some(failure.message),
                    Outcome::Aborted=>action.failure=Some("This action's reply was not confirmed. Refresh Source Control before trying again.".into()),
                }cx.notify();
            });
        }));
        cx.notify();
        true
    }
}
#[cfg(test)]
#[path = "git_action_tests.rs"]
mod tests;
