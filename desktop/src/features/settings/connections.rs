//! The Connections section's machines: this Mac and each remote the backend knows (the `machines` topic), with whether
//! it answered. Each remote has Connect (or Disconnect while it answers), Edit, and Remove after a confirm; under the
//! list a form adds a machine, or edits the one picked. Each goes to the backend (`machines/connect`, `disconnect`,
//! `set`, `remove`), one call at a time, and the list changes only as the topic says, so nothing shows as saved unless
//! the backend took it. A call that fails says why on its machine's row, or under the form.

use gpui_kit::component::button::{Button, ButtonVariant, ButtonVariants as _};
use gpui_kit::component::input::{Input, InputState};
use gpui_kit::component::{ActiveTheme as _, Disableable as _, Sizable as _, WindowExt as _};
use gpui_kit::component::{h_flex, v_flex};
use gpui_kit::prelude::FluentBuilder as _;
use gpui_kit::*;
use serde_json::{Value, json};

use super::machine_edits::{remove_args, set_args};
use super::settings_page::SettingsPage;
use crate::core::socket::client::SocketClient;
use crate::core::state::machine::Machine;
use crate::core::state::machines::Machines;

/// What the form shows in a blank port or token file: the backend's defaults (SavedMachine.cs), which it fills in.
const DEFAULT_PORT: &str = "9797";
const DEFAULT_TOKEN_FILE: &str = ".config/assembly-dotnet/env";

impl SettingsPage {
    /// The form's name, SSH destination, port and token file.
    pub(super) fn machine_fields(
        window: &mut Window,
        cx: &mut Context<Self>,
    ) -> [Entity<InputState>; 4] {
        [
            "Build server",
            "user@hostname or SSH alias",
            DEFAULT_PORT,
            DEFAULT_TOKEN_FILE,
        ]
        .map(|placeholder| cx.new(|cx| InputState::new(window, cx).placeholder(placeholder)))
    }

    pub(super) fn machines(&self, cx: &mut Context<Self>) -> Div {
        let Some(machines) = Machines::try_global(cx) else {
            return v_flex()
                .px_4()
                .py_3()
                .text_sm()
                .child("The backend isn't running.");
        };
        let listed: Vec<Machine> = machines.read(cx).machines().cloned().collect();
        let border = cx.theme().border.opacity(0.6);
        let mut list = v_flex();
        for (ix, machine) in listed.into_iter().enumerate() {
            let row = self.machine_row(machine, cx);
            list = list.child(row.when(ix > 0, |row| row.border_t_1().border_color(border)));
        }
        list.child(self.machine_form(cx).border_t_1().border_color(border))
    }

    fn machine_row(&self, machine: Machine, cx: &mut Context<Self>) -> Div {
        let theme = cx.theme();
        let (dot, status) = if !machine.checked && machine.name.is_some() {
            (theme.muted_foreground, "Checking…".to_string())
        } else if machine.reachable {
            (theme.success, "Connected".to_string())
        } else {
            let status = match &machine.reason {
                Some(reason) => format!("Offline · {reason}"),
                None => "Offline".to_string(),
            };
            (theme.danger, status)
        };
        let reached = match (&machine.name, &machine.ssh) {
            (None, _) => "Local",
            (Some(_), Some(ssh)) => ssh.as_str(),
            (Some(_), None) => "Remote",
        };
        let label = v_flex()
            .gap_0p5()
            .flex_1()
            .min_w_0()
            .child(
                div()
                    .text_sm()
                    .font_weight(FontWeight::SEMIBOLD)
                    .text_color(theme.foreground)
                    .child(machine.name.clone().unwrap_or("This Mac".into())),
            )
            .child(
                h_flex()
                    .gap_1p5()
                    .items_start()
                    .text_xs()
                    .text_color(theme.muted_foreground)
                    .child(div().flex_none().size(px(6.)).rounded_full().bg(dot))
                    .child(
                        div()
                            .flex_1()
                            .min_w_0()
                            .whitespace_normal()
                            .child(format!("{reached} · {status}")),
                    ),
            );
        let failure = machine
            .name
            .as_ref()
            .and_then(|name| self.failure_about(Some(name), cx));
        let actions = machine
            .name
            .is_some()
            .then(|| self.machine_actions(machine, cx));
        v_flex()
            .px_4()
            .py_3()
            .gap_1p5()
            .child(
                h_flex()
                    .gap_4()
                    .justify_between()
                    .child(label)
                    .children(actions),
            )
            .children(failure)
    }

    /// A remote's buttons: Connect or Disconnect, Edit, and Remove after a confirm.
    fn machine_actions(&self, machine: Machine, cx: &mut Context<Self>) -> Div {
        let name = machine.name.clone().unwrap_or_default();
        let editing = (self.machine_edits.editing.as_ref())
            .is_some_and(|edited| edited.name.as_ref() == Some(&name));
        let (method, label, args, doing) = if machine.reachable {
            (
                "disconnect",
                "Disconnect",
                json!({ "name": name }),
                "Couldn't disconnect",
            )
        } else {
            (
                "connect",
                "Connect",
                json!({ "machine": name }),
                "Couldn't connect",
            )
        };
        let about = Some(name.clone());
        let connection = self
            .machine_button(format!("{method}-{name}").into(), label)
            .outline()
            .on_click(cx.listener(move |page, _, window, cx| {
                let (about, args) = (about.clone(), args.clone());
                page.machine_call(about, method, args, doing, window, cx)
            }));
        let edit = self
            .machine_button(format!("edit-{name}").into(), "Edit")
            .ghost()
            .disabled(self.machine_call.is_some() || editing)
            .on_click(
                cx.listener(move |page, _, window, cx| page.edit_machine(&machine, window, cx)),
            );
        let remove = self
            .machine_button(format!("remove-{name}").into(), "Remove")
            .ghost()
            .disabled(self.machine_call.is_some() || editing)
            .on_click(
                cx.listener(move |_, _, window, cx| confirm_remove(name.clone(), window, cx)),
            );
        h_flex()
            .gap_1()
            .flex_none()
            .child(connection)
            .child(edit)
            .child(remove)
    }

    /// Adds a machine, or saves the one being edited, whose name stays as it is.
    fn machine_form(&self, cx: &mut Context<Self>) -> Div {
        let [name, ssh, port, token_file] = &self.machine_fields;
        // What's typed during the form's save would be emptied when it's taken, never sent.
        let saving = (self.machine_call.as_ref()).is_some_and(|(id, _)| id == "set");
        let editing = (self.machine_edits.editing.as_ref()).and_then(|edited| edited.name.clone());
        let failure = self.failure_about(None, cx);
        let title = match &editing {
            Some(name) => format!("Edit {name}"),
            None => "Add a machine".into(),
        };
        let save_label = if editing.is_some() {
            "Save"
        } else {
            "Add machine"
        };
        let save = self
            .machine_button("set".into(), save_label)
            .primary()
            .on_click(cx.listener(|page, _, window, cx| page.save_machine(window, cx)));
        let cancel = editing.is_some().then(|| {
            self.machine_button("cancel-edit".into(), "Cancel")
                .ghost()
                .on_click(cx.listener(|page, _, window, cx| {
                    page.machine_edits.editing = None;
                    page.machine_edits.failure = None;
                    page.clear_machine_fields(window, cx);
                }))
        });
        let theme = cx.theme();
        v_flex()
            .px_4()
            .py_3()
            .gap_3()
            .child(
                div()
                    .text_sm()
                    .font_weight(FontWeight::SEMIBOLD)
                    .text_color(theme.foreground)
                    .child(title),
            )
            .when(editing.is_none(), |form| {
                form.child(field("Machine name", name, saving, cx))
            })
            .child(field("SSH destination", ssh, saving, cx))
            .child(
                h_flex()
                    .gap_3()
                    .child(div().w(px(120.)).child(field("Port", port, saving, cx)))
                    .child(div().flex_1().child(field(
                        "Token file on the machine",
                        token_file,
                        saving,
                        cx,
                    ))),
            )
            .child(
                div()
                    .text_xs()
                    .text_color(theme.muted_foreground)
                    .child("A port or token file left empty takes the default shown."),
            )
            .children(failure)
            .child(h_flex().justify_end().gap_2().children(cancel).child(save))
    }

    /// The last failed call's reason, in red, when it came from machine `about`'s row, or from the form (`None`).
    fn failure_about(&self, about: Option<&String>, cx: &App) -> Option<Div> {
        let (from, why) = self.machine_edits.failure.as_ref()?;
        (from.as_ref() == about).then(|| {
            div()
                .text_xs()
                .text_color(cx.theme().danger)
                .child(why.clone())
        })
    }

    /// A small button that shows its call running, and waits while any machine call runs.
    fn machine_button(&self, id: SharedString, label: &'static str) -> Button {
        let running = self.machine_call.as_ref().map(|(running, _)| running);
        Button::new(id.clone())
            .small()
            .label(label)
            .loading(running == Some(&id))
            .disabled(running.is_some())
    }

    fn edit_machine(&mut self, machine: &Machine, window: &mut Window, cx: &mut Context<Self>) {
        self.machine_edits.editing = Some(machine.clone());
        self.machine_edits.failure = None;
        let values = [
            machine.name.clone(),
            machine.ssh.clone(),
            machine.port.map(|port| port.to_string()),
            machine.token_file.clone(),
        ];
        for (field, value) in self.machine_fields.iter().zip(values) {
            field.update(cx, |field, cx| {
                field.set_value(value.unwrap_or_default(), window, cx)
            });
        }
        cx.notify();
    }

    pub(super) fn clear_machine_fields(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        for field in &self.machine_fields {
            field.update(cx, |field, cx| field.set_value("", window, cx));
        }
        cx.notify();
    }

    fn save_machine(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        let [name, ssh, port, token_file] = self
            .machine_fields
            .each_ref()
            .map(|field| field.read(cx).value());
        let edited = (self.machine_edits.editing.as_ref()).and_then(|edited| edited.name.clone());
        if edited.is_none()
            && Machines::try_global(cx).is_some_and(|machines| {
                machines
                    .read(cx)
                    .machines()
                    .any(|machine| machine.name.as_deref() == Some(name.trim()))
            })
        {
            self.machine_edits.failure = Some((
                None,
                format!(
                    "A machine named \"{}\" already exists. Edit it instead.",
                    name.trim()
                ),
            ));
            cx.notify();
            return;
        }
        let name = edited.unwrap_or(name.to_string());
        match set_args(&name, &ssh, &port, &token_file) {
            Ok(args) => self.machine_call(None, "set", args, "Not saved", window, cx),
            Err(why) => {
                self.machine_edits.failure = Some((None, why));
                cx.notify();
            }
        }
    }

    fn remove_machine(&mut self, name: String, window: &mut Window, cx: &mut Context<Self>) {
        let about = Some(name.clone());
        match remove_args(&name) {
            Ok(args) => self.machine_call(about, "remove", args, "Not removed", window, cx),
            Err(why) => {
                self.machine_edits.failure = Some((about, why));
                cx.notify();
            }
        }
    }

    /// Sends `machines/<method>` for the row of machine `about`, or the form (`None`); the button that sent it is
    /// `<method>-<name>`, or the form's `set`. Once the backend takes the form's save, the form empties.
    fn machine_call(
        &mut self,
        about: Option<String>,
        method: &'static str,
        args: Value,
        doing: &'static str,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        self.machine_edits.failure = None;
        let Some(socket) = SocketClient::try_global(cx) else {
            let why = format!("{doing}: the backend isn't running.");
            self.machine_edits.failure = Some((about, why));
            cx.notify();
            return;
        };
        let call = socket
            .read(cx)
            .call::<Value>("machines", method, args, None);
        let id = match &about {
            Some(name) => format!("{method}-{name}").into(),
            None => method.into(),
        };
        let task = cx.spawn_in(window, async move |page, cx| {
            let outcome = call.await;
            _ = page.update_in(cx, |page, window, cx| {
                page.machine_call = None;
                if page.machine_edits.settle(about.clone(), doing, outcome) && about.is_none() {
                    page.clear_machine_fields(window, cx);
                }
                cx.notify();
            });
        });
        self.machine_call = Some((id, task));
        cx.notify();
    }
}

/// Asks before removing machine `name`; Remove sends it.
fn confirm_remove(name: String, window: &mut Window, cx: &mut Context<SettingsPage>) {
    let page = cx.entity().downgrade();
    window.open_alert_dialog(cx, move |alert, _, _| {
        let (page, removed) = (page.clone(), name.clone());
        alert
            .confirm()
            .title(format!("Remove {name}?"))
            .description(
                "Assembly stops connecting to it and forgets how to reach it. Its sessions stay in the list.",
            )
            .ok_text("Remove")
            .ok_variant(ButtonVariant::Danger)
            .on_ok(move |_, window, cx| {
                _ = page.update(cx, |page, cx| {
                    page.remove_machine(removed.clone(), window, cx)
                });
                true
            })
    });
}

/// One of the form's inputs, under its label; a `locked` one takes no typing.
fn field(label: &'static str, state: &Entity<InputState>, locked: bool, cx: &App) -> Div {
    v_flex()
        .gap_1()
        .child(
            div()
                .text_xs()
                .text_color(cx.theme().muted_foreground)
                .child(label),
        )
        .child(Input::new(state).small().disabled(locked))
}

#[cfg(test)]
#[path = "connections_tests.rs"]
mod tests;
