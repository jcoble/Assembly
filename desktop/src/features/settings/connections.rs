//! The Connections section's machines: this Mac and each remote the backend knows (the `machines` topic), with whether
//! it answered, and Connect for a remote that didn't, which asks the backend to reach it now (`machines/connect`, up
//! to ten seconds). One that answers comes back through the machines list. Adding, editing and removing machines
//! wait on the backend calls for them.

use gpui_kit::component::button::Button;
use gpui_kit::component::{ActiveTheme as _, Disableable as _, Sizable as _};
use gpui_kit::component::{h_flex, v_flex};
use gpui_kit::prelude::FluentBuilder as _;
use gpui_kit::*;
use serde_json::json;

use super::settings_page::SettingsPage;
use crate::core::socket::client::SocketClient;
use crate::core::state::machine::Machine;
use crate::core::state::machines::Machines;

impl SettingsPage {
    pub(super) fn machines(&self, cx: &mut Context<Self>) -> Div {
        let Some(machines) = Machines::try_global(cx) else {
            return v_flex()
                .px_4()
                .py_3()
                .text_sm()
                .child("The backend isn't running.");
        };
        let listed: Vec<Machine> = machines.read(cx).machines().cloned().collect();
        let theme = cx.theme();
        let mut list = v_flex();
        for (ix, machine) in listed.into_iter().enumerate() {
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
            let title = machine.name.clone().unwrap_or("This Mac".into());
            let connecting = self
                .connecting
                .as_ref()
                .is_some_and(|(name, _)| Some(name) == machine.name.as_ref());
            let action = machine
                .name
                .clone()
                .filter(|_| machine.didnt_answer())
                .map(|name| {
                    Button::new(SharedString::from(format!("connect-{name}")))
                        .outline()
                        .small()
                        .label(if connecting {
                            "Connecting…"
                        } else {
                            "Connect"
                        })
                        .disabled(self.connecting.is_some())
                        .on_click(cx.listener(move |page, _, _, cx| page.connect(name.clone(), cx)))
                });
            list = list.child(
                h_flex()
                    .px_4()
                    .py_3()
                    .gap_4()
                    .justify_between()
                    .when(ix > 0, |row| {
                        row.border_t_1().border_color(theme.border.opacity(0.6))
                    })
                    .child(
                        v_flex()
                            .gap_0p5()
                            .min_w_0()
                            .child(
                                div()
                                    .text_sm()
                                    .font_weight(FontWeight::SEMIBOLD)
                                    .text_color(theme.foreground)
                                    .child(title),
                            )
                            .child(
                                h_flex()
                                    .gap_1p5()
                                    .text_xs()
                                    .text_color(theme.muted_foreground)
                                    .child(div().size(px(6.)).rounded_full().bg(dot))
                                    .child(format!("{} · {status}", kind(&machine))),
                            ),
                    )
                    .children(action),
            );
        }
        list
    }

    fn connect(&mut self, name: String, cx: &mut Context<Self>) {
        let Some(socket) = SocketClient::try_global(cx) else {
            return;
        };
        let call = socket.read(cx).call::<serde_json::Value>(
            "machines",
            "connect",
            json!({ "machine": name }),
            None,
        );
        let task = cx.spawn(async move |page, cx| {
            _ = call.await;
            _ = page.update(cx, |page, cx| {
                page.connecting = None;
                cx.notify();
            });
        });
        self.connecting = Some((name, task));
        cx.notify();
    }
}

fn kind(machine: &Machine) -> &str {
    if machine.kind == "local" {
        "Local"
    } else {
        "Remote"
    }
}
