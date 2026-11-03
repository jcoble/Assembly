use super::*;
use serde::Deserialize;
#[derive(Deserialize)]
struct CommandResult {
    applied: bool,
    error: Option<String>,
}
impl SignInPanel {
    pub(super) fn check(&mut self, cx: &mut Context<Self>) {
        let version = self
            .projection
            .provider
            .as_ref()
            .map_or(0, |row| row.version);
        self.command(
            self.projection.target.provider_target_id.clone(),
            version,
            json!({"kind":"check_provider"}),
            cx,
        );
    }
    pub(super) fn start(&mut self, cx: &mut Context<Self>) {
        if self
            .projection
            .operation
            .as_ref()
            .is_some_and(|row| row.active())
        {
            return;
        }
        let Some(facts) = self
            .projection
            .provider
            .as_ref()
            .and_then(|row| row.facts())
        else {
            return;
        };
        if !facts
            .methods
            .iter()
            .any(|method| matches!(method.kind.as_str(), "agent" | "terminal"))
        {
            return;
        }
        if self
            .method_id
            .as_ref()
            .is_some_and(|id| !facts.methods.iter().any(|method| method.method_id == *id))
        {
            return;
        }
        self.command(uuid::Uuid::new_v4().simple().to_string(),0,json!({"kind":"start_sign_in","provider":self.projection.target.provider,
            "accountContext":self.projection.target.account_context,"methodId":self.method_id,"originNeedId":null}),cx);
    }
    pub(super) fn cancel(&mut self, cx: &mut Context<Self>) {
        let Some(row) = self
            .projection
            .operation
            .as_ref()
            .filter(|row| row.active())
        else {
            return;
        };
        self.command(
            row.id.clone(),
            row.version,
            json!({"kind":"cancel_sign_in"}),
            cx,
        );
    }
    fn command(&mut self, entity: String, version: i64, command: Value, cx: &mut Context<Self>) {
        if self.retry_command.is_some() {
            return;
        }
        let args = json!({"commandId":uuid::Uuid::new_v4().to_string(),"entityId":entity,"expectedVersion":version,"epoch":0,
            "command":command,"machine":self.projection.target.machine});
        self.send_command(args, cx);
    }
    pub(super) fn retry(&mut self, cx: &mut Context<Self>) {
        if let Some(args) = self.retry_command.clone() {
            self.send_command(args, cx);
        }
    }
    fn send_command(&mut self, args: Value, cx: &mut Context<Self>) {
        if self._command.is_some() || !self.projection.ready || !self.current(self.generation, cx) {
            return;
        }
        let Some(socket) = SocketClient::try_global(cx) else {
            return;
        };
        let (generation, retained) = (self.generation, args.clone());
        let call = socket
            .read(cx)
            .call::<CommandResult>("engine", "command", args, None);
        self.error = Default::default();
        self.retry_command = Some(retained.clone());
        self._command=Some(cx.spawn(async move |this,cx| {
            let result=call.await;
            _=this.update(cx,|panel,cx| {
                if !panel.current(generation,cx) { return; }
                panel._command=None;
                match result {
                    Outcome::Ok(result)=>{
                        panel.retry_command=None;
                        if result.applied {
                            if retained["command"]["kind"]=="start_sign_in"
                                && let Some(operation)=retained["entityId"].as_str() {
                                    panel._operation=None; panel._challenge=None; panel._opening=None; panel._submit=None;
                                    panel.challenge.replace_operation(operation); panel.projection.retain_operation(operation);
                            }
                        } else {
                            let reason=result.error.as_deref();
                            panel.error=match reason {
                                Some("already_running")=>"A sign-in is already running. Loading its current state.",
                                Some("version_conflict")=>"The status changed. Review the current state before trying again.",
                                Some("unavailable")=>"This provider is unavailable.",
                                Some("forbidden")=>"This sign-in action isn't available on this connection.",
                                Some("invalid_transition")=>"This action doesn't apply to the current sign-in state.",
                                _=>"Couldn't apply this sign-in action.",
                            }.into();
                            if reason==Some("already_running") { panel.request_snapshot(cx); }
                        }
                    }
                    _=>{
                        panel.retry_command=Some(retained);
                        panel.error="The reply was lost. The action may have completed. Review its status or retry the same request.".into();
                        panel.request_snapshot(cx);
                    }
                }
                panel.provider_dirty=true; panel.operation_dirty=true;
                panel.read_provider(cx); panel.read_operation(cx); cx.notify();
            });
        }));
        cx.notify();
    }
}
