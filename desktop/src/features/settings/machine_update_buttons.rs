//! The buttons on a machine's card that update it: the one Update, with the run's figure, Cancel while it downloads, and
//! Restart to Update, which Settings covers in the top bar.

use gpui_kit::component::Disableable as _;
use gpui_kit::component::button::ButtonVariants as _;
use gpui_kit::*;
use serde_json::json;

use super::settings_page::SettingsPage;
use crate::core::state::machine::Machine;
use crate::core::ui::access::control_ids::machine_button as machine_button_id;
use crate::features::remote_install::restart_button::WORDS as RESTART;
use crate::features::remote_install::updates::{update_figure, update_offered};

/// The one button, on each card with something behind. It updates everything behind on every machine, in development and
/// from a release: where it comes from is the backend's to decide.
const UPDATE: &str = "Update everything";
const CANCEL: &str = "Cancel";

impl SettingsPage {
    pub(super) fn update_buttons(
        &self,
        machine: &Machine,
        key: &str,
        cx: &mut Context<Self>,
    ) -> Vec<AnyElement> {
        let mut buttons = Vec::new();
        // Settings covers the top bar's button, and the words on this card say to press it.
        if machine.name.is_none() && machine.restart_ready.is_some() {
            let about = key.to_string();
            let restart = self
                .machine_button(
                    format!("restart-{key}").into(),
                    machine_button_id(key, "restart"),
                    RESTART,
                )
                .primary()
                .loading(machine.restarting)
                .disabled(machine.restarting || machine.updating || self.machine_call.is_some())
                .on_click(cx.listener(move |page, _, window, cx| {
                    page.machine_call(
                        Some(about.clone()),
                        "updates",
                        "restart",
                        json!({}),
                        "Couldn't restart",
                        window,
                        cx,
                    )
                }));
            buttons.push(
                div()
                    .debug_selector(|| "machine-restart".into())
                    .child(restart)
                    .into_any_element(),
            );
        }
        if update_offered(machine) {
            let about = key.to_string();
            let update = self
                .machine_button(
                    format!("start-{key}").into(),
                    machine_button_id(key, "update"),
                    UPDATE,
                )
                .label(update_figure(machine).unwrap_or(UPDATE.to_string()))
                .primary()
                .loading(machine.updating)
                // A press of Restart holds this machine's one place, and would make this do nothing.
                .disabled(machine.updating || machine.restarting || self.machine_call.is_some())
                .on_click(cx.listener(move |page, _, window, cx| {
                    page.machine_call(
                        Some(about.clone()),
                        "updates",
                        "start",
                        json!({}),
                        "Couldn't update",
                        window,
                        cx,
                    )
                }));
            buttons.push(update.into_any_element());
        }
        if machine.updating && machine.update_cancellable {
            let about = key.to_string();
            let cancel = self
                .machine_button(
                    format!("cancel-{key}").into(),
                    machine_button_id(key, "cancel"),
                    CANCEL,
                )
                .outline()
                .on_click(cx.listener(move |page, _, window, cx| {
                    page.machine_call(
                        Some(about.clone()),
                        "updates",
                        "cancel",
                        json!({}),
                        "Couldn't cancel",
                        window,
                        cx,
                    )
                }));
            buttons.push(
                div()
                    .debug_selector(|| "machine-cancel".into())
                    .child(cancel)
                    .into_any_element(),
            );
        }
        buttons
    }
}
