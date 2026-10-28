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
        let listed = (self.session.as_ref())
            .zip(Sessions::try_global(cx))
            .and_then(|(id, sessions)| {
                let items = sessions.read(cx).items();
                items
                    .iter()
                    .find(|item| &item.session_id == id)
                    .map(|item| item.machine.clone())
            });
        let machine = listed
            .unwrap_or_else(|| self.header.read(cx).machine().map(String::from))
            .filter(|machine| !machine.is_empty());
        let offline = machine.filter(|name| Machines::offline(Some(name), cx));
        self.composer
            .update(cx, |composer, cx| composer.set_offline(offline, cx));
    }
}
