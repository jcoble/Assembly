//! Which saved machine the form deploys to, and which Engine that machine answers as on this connection.

use gpui_kit::*;
use serde_json::Value;

use super::{DeployForm, Target};
use crate::core::socket::client::SocketClient;
use crate::core::socket::outcome::Outcome;
use crate::core::state::machines::Machines;
use crate::features::session::client::source_resolution;

impl DeployForm {
    /// The saved remote machines.
    pub(in crate::features::remote_install) fn machines(&self, cx: &App) -> Vec<String> {
        Machines::try_global(cx).map_or_else(Vec::new, |machines| {
            (machines.read(cx).machines())
                .filter_map(|machine| machine.name.clone())
                .collect()
        })
    }

    pub(super) fn machines_changed(&mut self, cx: &mut Context<Self>) {
        let machines = self.machines(cx);
        if self
            .machine
            .as_ref()
            .is_some_and(|machine| !machines.contains(machine))
            && !self.followed()
        {
            self.machine = None;
            self.target = None;
        }
        if self.machine.is_none() && machines.len() == 1 {
            self.pick_machine(machines[0].clone(), cx);
        } else if self.machine.is_some()
            && self.target.is_none()
            && !self.resolving
            && !self.followed()
        {
            self.resolve_target(cx);
        }
        cx.notify();
    }

    /// An install is being shown, so the form follows its machine, not the list.
    pub(super) fn followed(&self) -> bool {
        self.expected.is_some()
    }

    pub(in crate::features::remote_install) fn pick_machine(
        &mut self,
        machine: String,
        cx: &mut Context<Self>,
    ) {
        if self.machine.as_deref() == Some(&machine) && self.target.is_some() {
            return;
        }
        self.machine = Some(machine);
        self.resolve_target(cx);
    }

    /// Asks which Engine the machine answers as. A display name is never taken for one.
    pub(in crate::features::remote_install) fn resolve_target(&mut self, cx: &mut Context<Self>) {
        self.target = None;
        self.target_error = None;
        let Some(machine) = self.machine.clone() else {
            return;
        };
        let Some(socket) = SocketClient::try_global(cx) else {
            self.target_error = Some("The backend isn't connected.".into());
            return;
        };
        let (generation, local) = {
            let socket = socket.read(cx);
            (socket.connection_generation(), socket.engine_machine())
        };
        let args = match source_resolution::build_args(Some(&machine)) {
            Ok(args) => args,
            Err(error) => {
                self.target_error = Some(error.message.into());
                return;
            }
        };
        let call = socket.read(cx).call::<Value>(
            "sessions",
            "resolve_source",
            args,
            Some("remoteInstall:target"),
        );
        self.resolving = true;
        self._resolve = Some(cx.spawn(async move |form, cx| {
            let answer = call.await;
            _ = form.update(cx, |form, cx| {
                form.resolving = false;
                if form.machine.as_deref() != Some(&machine)
                    || form.current_generation(cx) != generation
                {
                    return;
                }
                let read = match answer {
                    Outcome::Ok(value) => {
                        source_resolution::parse_reply(value, Some(&machine), local.as_deref())
                            .map_err(|error| SharedString::from(error.message))
                    }
                    Outcome::Failed(error) => Err(error.message.into()),
                    Outcome::Aborted => return,
                };
                match (read, generation) {
                    (Ok(source), Some(generation)) => {
                        form.target = Some(Target {
                            engine: source.machine,
                            generation,
                        })
                    }
                    (Err(why), _) => form.target_error = Some(why),
                    (Ok(_), None) => form.target_error = Some("The backend isn't ready.".into()),
                }
                cx.notify();
            });
        }));
        cx.notify();
    }

    pub(super) fn current_generation(&self, cx: &App) -> Option<u64> {
        SocketClient::try_global(cx).and_then(|socket| socket.read(cx).connection_generation())
    }

    /// A new connection may be a different backend, so what the old one said no longer holds. A followed install hears
    /// about the reconnect on its own subscription.
    pub(super) fn connection_changed(&mut self, cx: &mut Context<Self>) {
        let generation = self.current_generation(cx);
        if generation == self.generation {
            return;
        }
        self.generation = generation;
        self.facts = None;
        self.target = None;
        if generation.is_some() && self.machine.is_some() && !self.followed() {
            self.resolve_target(cx);
        }
        cx.notify();
    }
}
