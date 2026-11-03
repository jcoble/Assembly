//! The existing backend operation owns execution. This view owns one subscription, never a replay or poller.
use super::git_target::GitTarget;
use crate::core::socket::{
    client::SocketClient, error_code::ErrorCode, outcome::Outcome, update::Update,
};
use gpui_kit::*;
use serde::Deserialize;
use serde_json::{Value, json};
use uuid::Uuid;

#[derive(Clone, Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
struct Host {
    instance_id: String,
    machine: String,
}
#[derive(Clone, Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct OperationStatus {
    pub operation_id: String,
    pub instance_id: String,
    pub machine: String,
    pub version: u64,
    pub state: String,
    pub result: Option<Value>,
    pub error: Option<String>,
}
impl OperationStatus {
    pub fn terminal(&self) -> bool {
        matches!(
            self.state.as_str(),
            "succeeded" | "failed" | "cancelled" | "uncertain"
        )
    }
}
#[derive(Clone)]
struct Owner {
    host: Host,
    id: String,
    session: Option<String>,
    action: &'static str,
    target: Option<GitTarget>,
}
impl Owner {
    /// Reconcile and cancel forward the source captured at start; they never take a new one.
    fn args(&self) -> Value {
        let args = json!({"operationId":self.id,"instanceId":self.host.instance_id,"session":self.session,"action":self.action});
        match &self.target {
            Some(target) => target.read_args(args),
            None => args,
        }
    }
}
#[derive(Default)]
pub struct GitOperation {
    pub status: Option<OperationStatus>,
    pub error: Option<String>,
    pub busy: bool,
    pub unconfirmed: bool,
    /// The machine that didn't answer, when that is why it failed, for Connect.
    pub unreachable: Option<String>,
    /// What the running or last operation was asked to do.
    pub action: Option<&'static str>,
    owner: Option<Owner>,
    observing: Option<Task<()>>,
    cancelling: Option<Task<()>>,
    reconciling: Option<Task<()>>,
}
impl GitOperation {
    /// Running, or finished with an outcome nobody has confirmed: either way no other write may start.
    pub fn occupied(&self) -> bool {
        self.busy || self.unconfirmed
    }
    pub fn start(
        &mut self,
        action: &'static str,
        session: Option<String>,
        args: Value,
        cx: &mut Context<Self>,
    ) {
        self.launch(action, session, None, args, cx);
    }
    /// Start for a read the pane showed: host, start, reconcile and cancel all forward the captured target.
    pub fn start_target(
        &mut self,
        action: &'static str,
        target: &GitTarget,
        args: Value,
        cx: &mut Context<Self>,
    ) {
        let session = Some(target.session.clone());
        self.launch(action, session, Some(target.clone()), args, cx);
    }
    fn launch(
        &mut self,
        action: &'static str,
        session: Option<String>,
        target: Option<GitTarget>,
        args: Value,
        cx: &mut Context<Self>,
    ) {
        if self.occupied() {
            return;
        }
        let Some(socket) = SocketClient::try_global(cx) else {
            self.error = Some("The backend isn't running.".into());
            cx.notify();
            return;
        };
        let host_args = json!({"session":session,"action":action});
        let host_call = socket.read(cx).call::<Host>(
            "git",
            "operation_host",
            match &target {
                Some(target) => target.read_args(host_args),
                None => host_args,
            },
            None,
        );
        let id = Uuid::new_v4().to_string();
        self.status = None;
        self.error = None;
        self.owner = None;
        self.unreachable = None;
        self.action = Some(action);
        self.busy = true;
        self.unconfirmed = false;
        self.observing=Some(cx.spawn(async move|operation,cx|{
            let host=match host_call.await {
                Outcome::Ok(host)=>host,
                Outcome::Failed(error)=>{
                    if let ErrorCode::Unreachable{machine}=&error.code {let machine=machine.clone();_=operation.update(cx,|operation,_|operation.unreachable=Some(machine));}
                    Self::fail(&operation,error.message,false,false,cx);return;
                },
                Outcome::Aborted=>{Self::fail(&operation,"The operation owner did not answer.".into(),false,false,cx);return;},
            };
            if host.instance_id.is_empty()||host.machine.is_empty(){Self::fail(&operation,"Invalid operation owner.".into(),false,false,cx);return;}
            let owner=Owner{host,id,session,action,target};
            let key=format!("{}/{}",owner.host.machine,owner.id);
            let mut subscription=cx.update(|cx|socket.read(cx).subscribe("gitOperation",Some(&key)));
            // The first state is the server's acknowledgement that the watcher exists, even with no operation yet.
            loop {
                match subscription.updates.recv().await {
                    Ok(Update::State(_))=>break,
                    Ok(_)=>{},
                    Err(_)=>{Self::fail(&operation,"The operation subscription was refused.".into(),false,false,cx);return;},
                }
            }
            if operation.update(cx,|operation,cx|{operation.owner=Some(owner.clone());cx.notify();}).is_err(){return;}
            let start_args=json!({"instanceId":owner.host.instance_id,"operationId":owner.id,"session":owner.session,"action":action,"args":args});
            let start_args=match &owner.target {Some(target)=>target.write_args(start_args),None=>start_args};
            let start=cx.update(|cx|socket.read(cx).call::<OperationStatus>("git","start",start_args,None));
            match start.await {
                Outcome::Ok(status)=>{if Self::accept(&operation,&owner,status,cx){return;}},
                Outcome::Failed(error)=>{
                    let unknown=matches!(error.code,ErrorCode::Uncertain{..}|ErrorCode::Timeout|ErrorCode::Unreachable{..});
                    Self::fail(&operation,error.message,false,unknown,cx);
                    if !unknown{return;}
                },
                Outcome::Aborted=>Self::fail(&operation,"This may have completed. Reconcile this operation before trying again.".into(),false,true,cx),
            }
            while let Ok(update)=subscription.updates.recv().await {
                let values=match update {
                    Update::State(items)=>{
                        if items.is_empty() {
                            // A reconnect gets the exact retained id once; an old/missing instance never starts new work.
                            let read=cx.update(|cx|socket.read(cx).call::<OperationStatus>("git","operation",owner.args(),None));
                            match read.await {
                                Outcome::Ok(status)=>{if Self::accept(&operation,&owner,status,cx){return;}},
                                Outcome::Failed(error)=>{Self::fail(&operation,format!("Operation outcome is uncertain. {}",error.message),false,true,cx);return;},
                                Outcome::Aborted=>{Self::fail(&operation,"Operation outcome is uncertain. The owner did not answer.".into(),true,true,cx);},
                            }
                        }
                        items.into_iter().map(|(_,item)|item).collect::<Vec<_>>()
                    },
                    Update::Change{item,..}=>vec![item],
                };
                for item in values {
                    match serde_json::from_value(item) {
                        Ok(status)=>{if Self::accept(&operation,&owner,status,cx){return;}},
                        Err(_)=>{Self::fail(&operation,"The operation result was invalid. Refresh before trying again.".into(),false,true,cx);return;},
                    }
                }
            }
            Self::fail(&operation,"Operation outcome is uncertain. The subscription ended.".into(),false,true,cx);
        }));
        cx.notify();
    }
    fn accept(
        operation: &WeakEntity<Self>,
        owner: &Owner,
        status: OperationStatus,
        cx: &mut AsyncApp,
    ) -> bool {
        if status.operation_id != owner.id
            || status.instance_id != owner.host.instance_id
            || status.machine != owner.host.machine
        {
            Self::fail(
                operation,
                "The operation owner changed. Its outcome is uncertain.".into(),
                false,
                true,
                cx,
            );
            return true;
        }
        let terminal = status.terminal();
        let mut finished = false;
        let gone = operation
            .update(cx, |operation, cx| {
                if operation
                    .status
                    .as_ref()
                    .is_some_and(|prior| prior.terminal() && prior.state != "uncertain")
                {
                    finished = true;
                    return;
                }
                if operation
                    .status
                    .as_ref()
                    .is_some_and(|prior| prior.version > status.version)
                {
                    return;
                }
                operation.busy = !terminal;
                operation.unconfirmed = status.state == "uncertain";
                operation.error = status.error.clone();
                operation.status = Some(status);
                finished = terminal;
                cx.notify();
            })
            .is_err();
        gone || finished
    }
    fn fail(
        operation: &WeakEntity<Self>,
        error: String,
        keep_observing: bool,
        unconfirmed: bool,
        cx: &mut AsyncApp,
    ) {
        _ = operation.update(cx, |operation, cx| {
            if operation
                .status
                .as_ref()
                .is_some_and(|status| status.terminal() && status.state != "uncertain")
            {
                return;
            }
            operation.error = Some(error);
            operation.busy = keep_observing;
            operation.unconfirmed = unconfirmed;
            if !unconfirmed && !keep_observing {
                operation.owner = None;
            }
            cx.notify();
        });
    }
    /// Reads only the captured retained operation. It cannot launch or replay the write.
    pub fn reconcile(&mut self, cx: &mut Context<Self>) {
        if self.reconciling.is_some() {
            return;
        }
        let Some(owner) = self.owner.clone() else {
            return;
        };
        let Some(socket) = SocketClient::try_global(cx) else {
            return;
        };
        let read = socket
            .read(cx)
            .call::<OperationStatus>("git", "operation", owner.args(), None);
        self.reconciling=Some(cx.spawn(async move|operation,cx|{
            let answer=read.await;
            _=operation.update(cx,|operation,_|operation.reconciling=None);
            match answer{
                Outcome::Ok(status)=>{if Self::accept(&operation,&owner,status,cx){_=operation.update(cx,|operation,_|operation.observing=None);}},
                Outcome::Failed(error)=>{_=operation.update(cx,|operation,cx|{if operation.status.as_ref().is_some_and(|status|status.terminal()&&status.state!="uncertain"){return;}operation.error=Some(error.message);operation.unconfirmed=true;cx.notify();});},
                Outcome::Aborted=>{_=operation.update(cx,|operation,cx|{if operation.status.as_ref().is_some_and(|status|status.terminal()&&status.state!="uncertain"){return;}operation.error=Some("The outcome is still unknown. Refresh this operation before starting another.".into());operation.unconfirmed=true;cx.notify();});},
            }
        }));
    }
    /// An explicit Stop action targets the captured operation; closing its dialog only drops this view.
    pub fn cancel(&mut self, cx: &mut Context<Self>) {
        if self.cancelling.is_some() {
            return;
        }
        let Some(owner) = self.owner.clone() else {
            return;
        };
        let Some(socket) = SocketClient::try_global(cx) else {
            return;
        };
        let call = socket
            .read(cx)
            .call::<Value>("git", "cancel", owner.args(), None);
        self.cancelling = Some(cx.spawn(async move |operation, cx| {
            let outcome = call.await;
            _ = operation.update(cx, |operation, cx| {
                operation.cancelling = None;
                operation.error = Some(match outcome {
                    Outcome::Ok(_) => {
                        "Cancellation requested; waiting for the actual outcome.".into()
                    }
                    Outcome::Failed(error) => error.message,
                    Outcome::Aborted => {
                        "Cancellation outcome is uncertain; the operation may still finish.".into()
                    }
                });
                cx.notify();
            });
        }));
        cx.notify();
    }
}
#[cfg(test)]
#[path = "git_operation_tests.rs"]
mod tests;
