//! The open session's machine, from the header's item and the machines list: while it isn't answering, the composer's
//! Send and Stop wait and say so (sessions-list#11). Followed as either changes.

use gpui_kit::*;

use super::SessionPane;
use crate::core::state::machines::Machines;
use crate::core::state::sessions::Sessions;

impl SessionPane {
    /// Follows the machines list, once the socket client has started it.
    pub(super) fn follow_machines(&mut self, cx: &mut Context<Self>) {
        self._machines = Machines::try_global(cx)
            .map(|machines| cx.observe(&machines, |pane, _, cx| pane.tell_offline(cx)));
        self.tell_offline(cx);
    }

    /// The machine comes from the sessions list, which has it the moment a session opens (the header's own item
    /// arrives later); from the header for a session the list hasn't loaded.
    pub(super) fn tell_offline(&mut self, cx: &mut Context<Self>) {
        let listed = self
            .session
            .as_ref()
            .zip(Sessions::try_global(cx))
            .and_then(|(id, sessions)| {
                sessions
                    .read(cx)
                    .items()
                    .iter()
                    .find(|item| &item.session_id == id)
                    .map(|item| (Some(item.agent.clone()), item.machine.clone()))
            });
        let (agent, machine) = listed.unwrap_or_else(|| {
            let header = self.header.read(cx);
            (
                header.agent().map(String::from),
                header.machine().map(String::from),
            )
        });
        let machine = machine.filter(|machine| !machine.is_empty());
        let route = Machines::try_global(cx)
            .and_then(|machines| {
                machines
                    .read(cx)
                    .machines()
                    .find(|item| item.name == machine)
                    .map(|item| {
                        format!(
                            "{:?}",
                            (&item.ssh, item.port, &item.token_file, item.reachable)
                        )
                    })
            })
            .unwrap_or_default();
        let offline = machine
            .clone()
            .filter(|name| Machines::offline(Some(name), cx));
        self.composer.update(cx, |composer, cx| {
            composer.set_machine(machine.clone(), cx);
            composer.set_offline(offline, cx);
            composer.set_owner(agent, machine, route, cx);
        });
    }
}
