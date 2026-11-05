//! The open session's machine, from the header's item and the machines list: while it isn't answering, the composer's
//! Send and Stop wait and say so (sessions-list#11). Followed as either changes.

use gpui_kit::*;

use super::SessionPane;
use crate::core::state::machines::Machines;

impl SessionPane {
    pub(super) fn is_remote(&self) -> bool {
        (self.row_source.as_ref()).is_some_and(|source| source.route_machine.is_some())
    }

    /// Follows the machines list, once the socket client has started it.
    pub(super) fn follow_machines(&mut self, cx: &mut Context<Self>) {
        self._machines = Machines::try_global(cx)
            .map(|machines| cx.observe(&machines, |pane, _, cx| pane.tell_offline(cx)));
        self.tell_offline(cx);
    }

    /// The machine comes from the sessions list, which has it the moment a session opens (the header's own item
    /// arrives later); from the header for a session the list hasn't loaded.
    pub(super) fn tell_offline(&mut self, cx: &mut Context<Self>) {
        if self.read_only {
            return;
        }
        let Some(source) = self.row_source.as_ref() else {
            return;
        };
        let agent = self.active_provider.clone();
        let machine = source.route_machine.clone();
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
        let still_offline = offline.is_some();
        self.composer.update(cx, |composer, cx| {
            composer.set_offline(offline, cx);
            composer.set_owner(agent, machine, route, cx);
        });
        if self.source_lost && !still_offline {
            self.reopen_lost_source(cx);
        }
    }
}
