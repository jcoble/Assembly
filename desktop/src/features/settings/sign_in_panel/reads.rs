use super::challenge::Challenge;
use super::*;
impl SignInPanel {
    pub(super) fn read_provider(&mut self, cx: &mut Context<Self>) {
        if !self.provider_dirty || self._provider.is_some() || !self.projection.ready {
            return;
        }
        let Some(socket) = SocketClient::try_bulk(cx) else {
            return;
        };
        let (generation, revision) = (self.generation, self.read_revision);
        let args = json!({"entityKind":"provider","entityId":self.projection.target.provider_target_id,"machine":self.projection.target.machine});
        let call = socket
            .read(cx)
            .call::<Value>("engine", "entity", args, None);
        self.provider_dirty = false;
        self._provider = Some(cx.spawn(async move |this, cx| {
            let result = call.await;
            _ = this.update(cx, |panel, cx| {
                if !panel.current(generation, cx) || panel.read_revision != revision {
                    return;
                }
                panel._provider = None;
                match result {
                    Outcome::Ok(value) if value.is_null() => panel.projection.provider = None,
                    Outcome::Ok(value) => {
                        if !panel.projection.install_provider(value)
                            && panel.projection.provider.is_none()
                        {
                            panel.error = "The provider status did not match this target.".into();
                        }
                    }
                    _ => panel.error = "Couldn't read provider status.".into(),
                }
                if panel.method_id.as_ref().is_some_and(|id| {
                    panel
                        .projection
                        .provider
                        .as_ref()
                        .and_then(|row| row.facts())
                        .is_none_or(|facts| !facts.methods.iter().any(|m| m.method_id == *id))
                }) {
                    panel.method_id = None;
                }
                panel.read_provider(cx);
                cx.notify();
            });
        }));
    }
    pub(super) fn read_operation(&mut self, cx: &mut Context<Self>) {
        if !self.operation_dirty || self._operation.is_some() || !self.projection.ready {
            return;
        }
        let Some(operation) = self.projection.operation_id.clone() else {
            self.operation_dirty = false;
            return;
        };
        let Some(socket) = SocketClient::try_bulk(cx) else {
            return;
        };
        let (generation, revision) = (self.generation, self.read_revision);
        let call=socket.read(cx).call::<Value>("engine","entity",json!({"entityKind":"sign_in","entityId":operation,"machine":self.projection.target.machine}),None);
        self.operation_dirty = false;
        self._operation = Some(cx.spawn(async move |this, cx| {
            let result = call.await;
            _ = this.update(cx, |panel, cx| {
                if !panel.current(generation, cx)
                    || panel.read_revision != revision
                    || panel.projection.operation_id.as_ref() != Some(&operation)
                {
                    return;
                }
                panel._operation = None;
                match result {
                    Outcome::Ok(value) if value.is_null() => {}
                    Outcome::Ok(value) => {
                        if panel.projection.install_operation(value) {
                            panel.after_operation(cx);
                        }
                    }
                    _ => panel.error = "Couldn't read sign-in status.".into(),
                }
                panel.read_operation(cx);
                cx.notify();
            });
        }));
    }
    pub(super) fn after_operation(&mut self, cx: &mut Context<Self>) {
        let Some(row) = self.projection.operation.as_ref() else {
            return;
        };
        let (id, active, signed_in) = (row.id.clone(), row.active(), row.state == "signed_in");
        if self
            .challenge
            .current
            .as_ref()
            .is_some_and(|challenge| challenge.operation_id != id)
        {
            self._challenge = None;
            self._opening = None;
            self._submit = None;
            self.clear_code(cx);
        }
        self.challenge
            .replace_operation(if active { &id } else { "" });
        if self.retry_command.as_ref().is_some_and(|args| {
            args["entityId"] == id && (args["command"]["kind"] == "start_sign_in" || !active)
        }) {
            self.retry_command = None;
        }
        if active {
            self.challenge_dirty = true;
            self.read_challenge(cx);
        } else {
            self._challenge = None;
            self._opening = None;
            self._submit = None;
            self.clear_code(cx);
            if signed_in && self.announced.as_ref() != Some(&id) {
                self.announced = Some(id);
                self.retry_command = None;
                cx.emit(SignedIn);
            }
        }
    }
    pub(super) fn clear_code(&self, cx: &mut Context<Self>) {
        let (code, window) = (self.code.clone(), self.window);
        cx.defer(move |cx| {
            _ = window.update(cx, |_, window, cx| {
                code.update(cx, |input, cx| input.set_value("", window, cx))
            });
        });
    }
    pub(super) fn read_challenge(&mut self, cx: &mut Context<Self>) {
        if !self.challenge_dirty || self._challenge.is_some() || !self.projection.ready {
            return;
        }
        if !self
            .projection
            .operation
            .as_ref()
            .is_some_and(|row| row.active())
        {
            return;
        }
        let Some(operation) = self.projection.operation_id.clone() else {
            return;
        };
        let Some(socket) = SocketClient::try_global(cx) else {
            return;
        };
        let (generation, revision) = (self.generation, self.read_revision);
        let call = socket.read(cx).call::<Option<Challenge>>(
            "providers",
            "challenge",
            json!({"operationId":operation}),
            None,
        );
        self.challenge_dirty = false;
        self._challenge = Some(cx.spawn(async move |this, cx| {
            let result = call.await;
            _ = this.update(cx, |panel, cx| {
                if !panel.current(generation, cx)
                    || panel.read_revision != revision
                    || panel.projection.operation_id.as_ref() != Some(&operation)
                    || !panel
                        .projection
                        .operation
                        .as_ref()
                        .is_some_and(|row| row.active())
                {
                    return;
                }
                panel._challenge = None;
                match result {
                    Outcome::Ok(Some(challenge)) => {
                        let old = panel.challenge.current.as_ref().map(|c| c.generation);
                        if panel.challenge.install(challenge)
                            && panel.challenge.current.as_ref().map(|c| c.generation) != old
                        {
                            panel._opening = None;
                            panel._submit = None;
                            panel.clear_code(cx);
                        }
                    }
                    Outcome::Ok(None) => {
                        panel.challenge.clear_display();
                        panel._opening = None;
                        panel._submit = None;
                        panel.clear_code(cx);
                    }
                    _ => panel.error = "Couldn't read the sign-in challenge.".into(),
                }
                panel.read_challenge(cx);
                cx.notify();
            });
        }));
    }
    pub(super) fn open_link(
        &mut self,
        expected: super::challenge::DisplayedLink,
        cx: &mut Context<Self>,
    ) {
        self.open_link_using(
            expected,
            |url, cx| cx.foreground_executor().spawn(super::browser::launch(url)),
            cx,
        );
    }
    pub(super) fn open_link_using(
        &mut self,
        expected: super::challenge::DisplayedLink,
        launch: impl FnOnce(String, &App) -> Task<bool>,
        cx: &mut Context<Self>,
    ) {
        if !self.current(self.generation, cx) {
            return;
        }
        if !self.challenge.is_current(&expected) {
            return;
        }
        let Some(link) = self.challenge.begin_click(&self.projection.target.machine) else {
            return;
        };
        let generation = self.generation;
        self.error = Default::default();
        let opening = launch(link.original_url.clone(), cx);
        self._opening = Some(cx.spawn(async move |this, cx| {
            let success = opening.await;
            let call = this
                .update(cx, |panel, cx| {
                    if !panel.current(generation, cx) {
                        return None;
                    }
                    let args = panel.challenge.opened(&link, success);
                    if !success {
                        panel.error =
                            "Couldn't open the sign-in page. Try opening it again.".into();
                        panel._opening = None;
                        cx.notify();
                        return None;
                    }
                    let socket = SocketClient::try_global(cx)?;
                    Some(
                        socket
                            .read(cx)
                            .call::<Value>("providers", "acknowledgeUrl", args?, None),
                    )
                })
                .ok()
                .flatten();
            let Some(call) = call else {
                return;
            };
            let result = call.await;
            _ = this.update(cx, |panel, cx| {
                if !panel.current(generation, cx) || !panel.challenge.is_current(&link) {
                    return;
                }
                panel._opening = None;
                if matches!(result, Outcome::Ok(_)) {
                    panel.challenge.acknowledged(&link);
                } else {
                    panel.challenge.release_click(&link);
                    panel.error =
                        "Couldn't confirm this sign-in link. Checking the current challenge."
                            .into();
                }
                panel.challenge_dirty = true;
                panel.operation_dirty = true;
                panel.read_challenge(cx);
                panel.read_operation(cx);
                cx.notify();
            });
        }));
        cx.notify();
    }
    pub(super) fn submit_code(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        if self._submit.is_some() || !self.current(self.generation, cx) {
            return;
        }
        let Some(challenge) = self.challenge.current.as_ref().filter(|c| c.wants_code) else {
            return;
        };
        let (operation, expected) = (challenge.operation_id.clone(), challenge.generation);
        let code = self.code.read(cx).value().to_string();
        if code.is_empty() {
            return;
        }
        self.code
            .update(cx, |input, cx| input.set_value("", window, cx));
        let Some(socket) = SocketClient::try_global(cx) else {
            return;
        };
        let generation = self.generation;
        let call=socket.read(cx).call::<Value>("providers","submitCode",json!({"operationId":operation,"expectedGeneration":expected,"code":code,"machine":self.projection.target.machine}),None);
        self._submit = Some(cx.spawn(async move |this, cx| {
            let result = call.await;
            _ = this.update(cx, |panel, cx| {
                if !panel.current(generation, cx)
                    || panel.projection.operation_id.as_ref() != Some(&operation)
                {
                    return;
                }
                panel._submit = None;
                if matches!(result, Outcome::Ok(_)) {
                    panel.challenge.clear_display();
                } else {
                    panel.error =
                        "Couldn't submit this code. Checking the current challenge.".into();
                }
                panel.challenge_dirty = true;
                panel.operation_dirty = true;
                panel.read_challenge(cx);
                panel.read_operation(cx);
                cx.notify();
            });
        }));
        cx.notify();
    }
}
