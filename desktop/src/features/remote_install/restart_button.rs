//! Restart to Update: shown once a new version of the app is ready, always in sight. The app never restarts by itself;
//! this press is the only thing that starts the wait for sessions and the swap.

use gpui_kit::component::button::{Button, ButtonVariants as _};
use gpui_kit::component::{Disableable as _, Sizable as _};
use gpui_kit::*;
use serde_json::{Value, json};

use crate::core::socket::client::SocketClient;
use crate::core::state::machines::{GlobalMachines, Machines};
use crate::core::ui::access::Control as _;
use crate::core::ui::access::control_ids::topbar;

const WORDS: &str = "Restart to Update";

pub struct RestartButton {
    /// Whether this app has an update ready, and whether it's already on its way.
    ready: bool,
    updating: bool,
    _machines: Option<Subscription>,
    _follow: Subscription,
    _call: Option<Task<()>>,
}

impl RestartButton {
    pub fn new(cx: &mut Context<Self>) -> Self {
        let mut button = Self {
            ready: false,
            updating: false,
            _machines: None,
            _follow: cx.observe_global::<GlobalMachines>(Self::follow),
            _call: None,
        };
        button.follow(cx);
        button
    }

    /// The machines list starts on the backend's first ready line, which may be after this button is made.
    fn follow(&mut self, cx: &mut Context<Self>) {
        let machines = Machines::try_global(cx);
        self._machines = machines.as_ref().map(|machines| {
            cx.observe(machines, |button, machines, cx| button.read(&machines, cx))
        });
        if let Some(machines) = machines {
            self.read(&machines, cx);
        }
    }

    fn read(&mut self, machines: &Entity<Machines>, cx: &mut Context<Self>) {
        let here = machines
            .read(cx)
            .machines()
            .find(|machine| machine.name.is_none());
        let ready = here.is_some_and(|machine| machine.restart_ready.is_some());
        let updating = here.is_some_and(|machine| machine.updating);
        if (ready, updating) != (self.ready, self.updating) {
            (self.ready, self.updating) = (ready, updating);
            cx.notify();
        }
    }

    fn press(&mut self, cx: &mut Context<Self>) {
        let call =
            SocketClient::global(cx)
                .read(cx)
                .call::<Value>("updates", "restart", json!({}), None);
        self._call = Some(cx.background_spawn(async move {
            _ = call.await;
        }));
    }
}

impl Render for RestartButton {
    fn render(&mut self, _: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        if !self.ready {
            return div().into_any_element();
        }
        div()
            .flex_none()
            .debug_selector(|| "restart-to-update".into())
            .child(
                Button::new("restart-to-update")
                    .primary()
                    .small()
                    .label(WORDS)
                    .loading(self.updating)
                    .disabled(self.updating)
                    .control(topbar::RESTART_TO_UPDATE, WORDS)
                    .on_click(cx.listener(|button, _, _, cx| button.press(cx))),
            )
            .into_any_element()
    }
}

#[cfg(test)]
#[path = "restart_button_tests.rs"]
mod tests;
