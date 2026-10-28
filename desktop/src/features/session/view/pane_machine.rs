//! The open session's machine, from the header's item and the machines list: while it isn't answering, the composer's
//! Send and Stop wait and say so (sessions-list#11). Followed as either changes.

use gpui_kit::*;

use super::SessionPane;
use crate::core::state::machines::Machines;

impl SessionPane {
    /// Follows the machines list, once the socket client has started it.
    pub(super) fn follow_machines(&mut self, cx: &mut Context<Self>) {
        self._machines = Machines::try_global(cx)
            .map(|machines| cx.observe(&machines, |pane, _, cx| pane.tell_offline(cx)));
        self.tell_offline(cx);
    }

    pub(super) fn tell_offline(&mut self, cx: &mut Context<Self>) {
        let machine = self.header.read(cx).machine().map(String::from);
        let offline = machine.filter(|name| Machines::offline(Some(name), cx));
        self.composer
            .update(cx, |composer, cx| composer.set_offline(offline, cx));
    }
}
