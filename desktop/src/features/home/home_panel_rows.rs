//! The Home panel's two lists. A provider row shows its icon, name and one state, and grows (the shared
//! `row_drop_down`) into a line per machine: that machine's state, the adapter and CLI versions, the last failure's
//! plain sentence with "Try again" and a "Details" grower (the same shared row), the model list — once when the
//! backend sends one for the provider, else per machine — and an "Advanced" grower, drawn by
//! `home_panel_advanced.rs`. A machine row shows its connection and, when there is something to say, its update.
//! Every word comes from a state the backend pushed (`state_words.rs`).

use gpui_kit::assets::IconName;
use gpui_kit::component::button::{Button, ButtonVariants as _};
use gpui_kit::component::{ActiveTheme as _, Disableable as _, Icon, Sizable as _, h_flex, v_flex};
use gpui_kit::prelude::FluentBuilder as _;
use gpui_kit::*;

use super::home_panel::HomePanel;
use super::settings_state::{Machine, Model, Provider, ProviderOnMachine, SettingsState};
use super::state_words::{connection, machine_state, provider_state, update};
use crate::activity::DotChip;
use crate::core::theme::text::{TextRole, TextRoleExt as _};
use crate::core::ui::access::control_ids::{home_machine, home_provider, home_provider_machine};
use crate::core::ui::access::{Control as _, ExposedElement as _, say};
use crate::core::ui::row_drop_down::GrowingBody;
use crate::core::ui::three_line_row::row_height;

impl HomePanel {
    /// "Providers", then a row for each with its grower under it.
    pub(super) fn providers(&self, state: &SettingsState, cx: &mut Context<Self>) -> Div {
        let mut list = v_flex().child(heading("Providers", cx));
        for provider in &state.providers {
            let key: SharedString = format!("provider:{}", provider.id).into();
            list = list
                .child(self.provider_row(provider, key.clone(), cx))
                .child(GrowingBody::new(key, self.drops.clone(), row_height(cx)).max_rows(None));
        }
        list
    }

    /// One provider: icon, name and state. A click opens or shuts its grower.
    fn provider_row(
        &self,
        provider: &Provider,
        key: SharedString,
        cx: &mut Context<Self>,
    ) -> Stateful<Div> {
        let theme = cx.theme();
        let name = provider.name.clone();
        let state = provider_state(provider.state.as_deref());
        let open = self.drops.read(cx).is_open(&key);
        let label = match state {
            Some((words, _)) => format!("{name}: {words}"),
            None => name.clone(),
        };
        // The grower reads the panel's latest state each time it draws, so a push while it is open shows at once.
        let panel = cx.weak_entity();
        let id = provider.id.clone();
        let body = move |window: &mut Window, cx: &mut App| -> AnyElement {
            match panel.upgrade() {
                Some(panel) => panel.update(cx, |panel, cx| panel.provider_body(&id, window, cx)),
                None => Empty.into_any_element(),
            }
        };
        h_flex()
            .id(key.clone())
            .debug_selector({
                let selector = format!("home-{}", key.replace(':', "-"));
                move || selector.clone()
            })
            .exposed(Role::Button, home_provider(&provider.id, "row"), label)
            .px_3()
            .py_1p5()
            .gap_2()
            .cursor_pointer()
            .hover(|row| row.bg(theme.muted))
            .child(Icon::empty().path(provider.glyph.clone()).small())
            .child(
                div()
                    .flex_1()
                    .min_w_0()
                    .text_role(TextRole::Body, cx)
                    .font_weight(FontWeight::MEDIUM)
                    .child(name),
            )
            .children(state.map(|(words, tone)| {
                DotChip::new(words, tone).named(home_provider(&provider.id, "state"))
            }))
            .child(
                Icon::new(if open {
                    IconName::ChevronDown
                } else {
                    IconName::ChevronRight
                })
                .small()
                .text_color(theme.muted_foreground),
            )
            .on_click(cx.listener(move |panel, _, _, cx| {
                let (key, body) = (key.clone(), body.clone());
                panel
                    .drops
                    .update(cx, |drops, cx| drops.toggle(key, body, cx));
            }))
    }

    /// "Connections", then a row for each machine.
    pub(super) fn machines(&self, state: &SettingsState, cx: &mut Context<Self>) -> Div {
        v_flex().pt_2().child(heading("Connections", cx)).children(
            state
                .machines
                .iter()
                .map(|machine| machine_row(machine, cx)),
        )
    }
}

impl HomePanel {
    /// A provider's grower: a line per machine, then the model list when the backend says every machine
    /// agrees on one. Drawn from the latest pushed state each time, so a push while it is open shows at once.
    pub(super) fn provider_body(
        &mut self,
        provider_id: &str,
        _window: &mut Window,
        cx: &mut Context<Self>,
    ) -> AnyElement {
        let Some(state) = self.state.clone() else {
            return Empty.into_any_element();
        };
        let Some(provider) = state.providers.iter().find(|held| held.id == provider_id) else {
            return Empty.into_any_element();
        };
        let provider = provider.clone();
        let shared = provider.models.is_some();
        let models = (provider.models.as_deref())
            .and_then(models_line)
            .map(|words| meta(home_provider(&provider.id, "models"), words, cx));
        let mut body = v_flex().pl(px(36.)).pr_3().pb_2().gap_1p5();
        for on in &provider.machines {
            let label = state
                .machines
                .iter()
                .find(|machine| machine.key == on.machine_key)
                .map_or(on.machine_key.as_str(), |machine| {
                    machine.display_name.as_str()
                });
            body = body.child(self.machine_line(&provider, label, on, !shared, cx));
        }
        body.children(models).into_any_element()
    }

    /// One machine inside a provider: its name and state, versions, the last failure with "Try again" and a
    /// "Details" grower, its own models if they differ, and an "Advanced" grower with the four overrides.
    /// Both growers are the shared expanding row the provider rows use, under their own keys.
    fn machine_line(
        &mut self,
        provider: &Provider,
        label: &str,
        on: &ProviderOnMachine,
        own_models: bool,
        cx: &mut Context<Self>,
    ) -> Div {
        let id = |part: &str| home_provider_machine(&provider.id, &on.machine_key, part);
        let versions = [("Adapter", &on.adapter_version), ("CLI", &on.cli_version)]
            .into_iter()
            .filter_map(|(what, version)| version.as_ref().map(|version| format!("{what} {version}")))
            .collect::<Vec<_>>()
            .join(" · ");
        let models = own_models
            .then(|| on.models.as_deref().and_then(models_line))
            .flatten()
            .map(|words| meta(id("models"), words, cx));
        let mut line = v_flex()
            .gap_0p5()
            .child(
                h_flex()
                    .gap_2()
                    .child(
                        div()
                            .flex_1()
                            .min_w_0()
                            .text_role(TextRole::Body, cx)
                            .child(label.to_string()),
                    )
                    .children(
                        machine_state(on.state.as_deref())
                            .map(|(words, tone)| DotChip::new(words, tone).named(id("state"))),
                    ),
            )
            .when(!versions.is_empty(), |line| {
                line.child(meta(id("versions"), versions, cx))
            })
            .children(models);
        if let Some(failure) = &on.last_failure {
            line = line.child(
                say(id("failure"), failure.message.clone())
                    .text_role(TextRole::Meta, cx)
                    .text_color(cx.theme().danger),
            );
            let mut actions = h_flex().gap_2();
            if on.provider_target_id.is_some() && on.engine_machine.is_some() {
                let on_click = on.clone();
                actions = actions.child(
                    small_button(
                        format!("home-try-again-{}-{}", provider.id, on.machine_key),
                        id("try-again"),
                        "Try again",
                    )
                    .on_click(cx.listener(move |panel, _, _, cx| panel.try_again(&on_click, cx))),
                );
            }
            let details_key: SharedString =
                format!("provider:{}:machine:{}:details", provider.id, on.machine_key).into();
            actions = actions.child(self.grower_button(
                details_key.clone(),
                id("details"),
                "Details",
                cx,
            ));
            line = line.child(actions).child(
                GrowingBody::new(details_key, self.sub_drops.clone(), row_height(cx))
                    .max_rows(None),
            );
        }
        // "Check again" on this machine's line re-runs the local check and reconcile.
        if on.machine_key == "local" {
            line = line.child(
                h_flex().pt_0p5().child(
                    Button::new(SharedString::from(format!(
                        "check-again:{}:local",
                        provider.id
                    )))
                    .ghost()
                    .xsmall()
                    .label(if self.checking {
                        "Checking…"
                    } else {
                        "Check again"
                    })
                    .disabled(self.checking)
                    .control(id("check-again"), "Check again")
                    .on_click(cx.listener(|panel, _, _, cx| panel.check_again(cx))),
                ),
            );
        }
        let advanced_key: SharedString =
            format!("provider:{}:machine:{}:advanced", provider.id, on.machine_key).into();
        line = line
            .child(h_flex().child(self.grower_button(
                advanced_key.clone(),
                id("advanced"),
                "Advanced",
                cx,
            )))
            .child(
                GrowingBody::new(advanced_key, self.sub_drops.clone(), row_height(cx))
                    .max_rows(None),
            );
        line
    }

    /// A small text button that opens or shuts one of the panel's sub-growers (Advanced, Details). Its body
    /// is drawn by the panel from the latest pushed state, like the provider growers above it.
    fn grower_button(
        &self,
        key: SharedString,
        tree_id: String,
        label: &'static str,
        cx: &mut Context<Self>,
    ) -> Button {
        let weak = cx.weak_entity();
        let (provider_id, machine_key, kind) = parse_sub_key(&key);
        small_button(
            format!("home-{kind}-{provider_id}-{machine_key}"),
            tree_id,
            label,
        )
        .on_click(cx.listener(move |this, _, _, cx| {
            let (key, kind, provider_id, machine_key, weak) = (
                key.clone(),
                kind.clone(),
                provider_id.clone(),
                machine_key.clone(),
                weak.clone(),
            );
            let body = move |window: &mut Window, cx: &mut App| -> AnyElement {
                let Some(panel) = weak.upgrade() else {
                    return Empty.into_any_element();
                };
                panel.update(cx, |panel, cx| {
                    panel.sub_body(&kind, &provider_id, &machine_key, window, cx)
                })
            };
            this.sub_drops
                .update(cx, |drops, cx| drops.toggle(key, body, cx));
        }))
    }

    /// One sub-grower's body, from the latest pushed state: a machine line's Advanced fields, or its
    /// failure's raw detail.
    fn sub_body(
        &mut self,
        kind: &str,
        provider_id: &str,
        machine_key: &str,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) -> AnyElement {
        let on = self.state.as_ref().and_then(|state| {
            state
                .providers
                .iter()
                .find(|provider| provider.id == provider_id)
                .and_then(|provider| {
                    provider
                        .machines
                        .iter()
                        .find(|on| on.machine_key == machine_key)
                })
                .cloned()
        });
        let Some(on) = on else {
            return Empty.into_any_element();
        };
        match kind {
            "advanced" => {
                // An offline machine can't answer the read, and a forwarded call to one waits
                // indefinitely, so the grower says so from the pushed connection state instead.
                let offline = self.state.as_ref().is_some_and(|state| {
                    state
                        .machines
                        .iter()
                        .find(|machine| machine.key == machine_key)
                        .is_some_and(|machine| {
                            matches!(
                                machine.connection_state.as_deref(),
                                Some("offline" | "disconnected")
                            )
                        })
                });
                self.advanced_body(provider_id, &on, offline, window, cx)
            }
            _ => match &on.last_failure {
                Some(failure) => div()
                    .py_1()
                    .text_role(TextRole::Meta, cx)
                    .text_color(cx.theme().muted_foreground)
                    .child(failure.detail.clone())
                    .into_any_element(),
                None => Empty.into_any_element(),
            },
        }
    }
}

/// The three parts of a sub-grower key (`provider:<id>:machine:<key>:<kind>`), for its button and body.
fn parse_sub_key(key: &SharedString) -> (String, String, String) {
    let parts: Vec<&str> = key.split(':').collect();
    (
        parts.get(1).unwrap_or(&"").to_string(),
        parts.get(3).unwrap_or(&"").to_string(),
        parts.get(4).unwrap_or(&"").to_string(),
    )
}

/// A small ghost text button, as the machine lines' actions are drawn.
fn small_button(element: String, tree_id: String, label: &'static str) -> Button {
    Button::new(element)
        .small()
        .ghost()
        .label(label)
        .control(tree_id, label)
}

/// One machine: its name, its connection, and its update when there is one to mention.
fn machine_row(machine: &Machine, cx: &App) -> Div {
    let chip = |state: Option<(&'static str, _)>, part: &str| {
        state.map(|(words, tone)| DotChip::new(words, tone).named(home_machine(&machine.key, part)))
    };
    h_flex()
        .px_3()
        .py_1p5()
        .gap_2()
        .child(
            say(
                home_machine(&machine.key, "name"),
                machine.display_name.clone(),
            )
            .flex_1()
            .min_w_0()
            .text_role(TextRole::Body, cx)
            .font_weight(FontWeight::MEDIUM),
        )
        .children(chip(update(machine.update_state.as_deref()), "update"))
        .children(chip(
            connection(machine.connection_state.as_deref()),
            "state",
        ))
}

/// A list's small muted title.
fn heading(words: &'static str, cx: &App) -> Div {
    div()
        .px_3()
        .pt_1()
        .pb_0p5()
        .text_role(TextRole::Meta, cx)
        .font_weight(FontWeight::SEMIBOLD)
        .text_color(cx.theme().muted_foreground)
        .child(words)
}

/// A muted line a script can read.
fn meta(id: String, words: String, cx: &App) -> Stateful<Div> {
    say(id, words)
        .text_role(TextRole::Meta, cx)
        .text_color(cx.theme().muted_foreground)
}

/// "Models: A, B", or nothing for an empty list.
fn models_line(models: &[Model]) -> Option<String> {
    (!models.is_empty()).then(|| {
        let names = models
            .iter()
            .map(|model| model.name.as_str())
            .collect::<Vec<_>>();
        format!("Models: {}", names.join(", "))
    })
}
