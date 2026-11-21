//! The Home panel's two lists. A provider row shows its icon, name and one state, and grows (the shared
//! `row_drop_down`) into a line per machine: that machine's state, the adapter and CLI versions, the last failure's
//! plain sentence, and the model list, once when every machine has the same list and per machine where they differ.
//! A machine row shows its connection and, when there is something to say, its update. Every word comes from a state
//! the backend pushed (`state_words.rs`).

use gpui_kit::assets::IconName;
use gpui_kit::component::{ActiveTheme as _, Icon, Sizable as _, h_flex, v_flex};
use gpui_kit::prelude::FluentBuilder as _;
use gpui_kit::*;

use super::home_panel::HomePanel;
use super::settings_state::{Machine, Model, Provider, ProviderOnMachine, SettingsState};
use super::state_words::{connection, provider_state, update};
use crate::activity::DotChip;
use crate::core::theme::text::{TextRole, TextRoleExt as _};
use crate::core::ui::access::control_ids::{home_machine, home_provider, home_provider_machine};
use crate::core::ui::access::{ExposedElement as _, say};
use crate::core::ui::provider_name::provider_name;
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
        let name = display_name(provider);
        let state = provider_state(provider.state.as_deref());
        let open = self.drops.read(cx).is_open(&key);
        let label = match state {
            Some((words, _)) => format!("{name}: {words}"),
            None => name.clone(),
        };
        // The grower reads the panel's latest state each time it draws, so a push while it is open shows at once.
        let panel = cx.weak_entity();
        let id = provider.id.clone();
        let body = move |_: &mut Window, cx: &mut App| -> AnyElement {
            let state = panel
                .upgrade()
                .and_then(|panel| panel.read(cx).state.clone());
            let provider = state
                .as_ref()
                .and_then(|state| state.providers.iter().find(|held| held.id == id));
            match (state.as_ref(), provider) {
                (Some(state), Some(provider)) => grower(provider, &state.machines, cx),
                _ => Empty.into_any_element(),
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
            .children(
                provider
                    .glyph
                    .clone()
                    .map(|glyph| Icon::empty().path(glyph).small()),
            )
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

/// A provider's grower: a line per machine, then the model list when every machine has the same one.
fn grower(provider: &Provider, machines: &[Machine], cx: &App) -> AnyElement {
    let shared_models = provider
        .machines
        .windows(2)
        .all(|pair| pair[0].models == pair[1].models);
    let models = shared_models
        .then(|| provider.machines.first())
        .flatten()
        .and_then(|first| models_line(&first.models))
        .map(|words| meta(home_provider(&provider.id, "models"), words, cx));
    v_flex()
        .pl(px(36.))
        .pr_3()
        .pb_2()
        .gap_1p5()
        .children(provider.machines.iter().map(|on| {
            let label = machines
                .iter()
                .find(|machine| machine.key == on.machine_key)
                .map_or(on.machine_key.as_str(), Machine::label);
            machine_line(&provider.id, label, on, !shared_models, cx)
        }))
        .children(models)
        .into_any_element()
}

/// One machine inside a provider: its name and state, versions, the last failure, and its own models if they differ.
fn machine_line(
    provider: &str,
    label: &str,
    on: &ProviderOnMachine,
    own_models: bool,
    cx: &App,
) -> Div {
    let id = |part: &str| home_provider_machine(provider, &on.machine_key, part);
    let versions = [("Adapter", &on.adapter_version), ("CLI", &on.cli_version)]
        .into_iter()
        .filter_map(|(what, version)| version.as_ref().map(|version| format!("{what} {version}")))
        .collect::<Vec<_>>()
        .join(" · ");
    let models = own_models
        .then(|| models_line(&on.models))
        .flatten()
        .map(|words| meta(id("models"), words, cx));
    let failure = on.last_failure.as_ref().map(|failure| {
        say(id("failure"), failure.message.clone())
            .text_role(TextRole::Meta, cx)
            .text_color(cx.theme().danger)
    });
    v_flex()
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
                    provider_state(on.state.as_deref())
                        .map(|(words, tone)| DotChip::new(words, tone).named(id("state"))),
                ),
        )
        .when(!versions.is_empty(), |line| {
            line.child(meta(id("versions"), versions, cx))
        })
        .children(failure)
        .children(models)
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
                machine.label().to_string(),
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
            .map(|model| model.name.as_deref().unwrap_or(&model.id))
            .collect::<Vec<_>>();
        format!("Models: {}", names.join(", "))
    })
}

/// The backend's name for the provider, or its id read as a name.
fn display_name(provider: &Provider) -> String {
    provider
        .name
        .clone()
        .unwrap_or_else(|| provider_name(&provider.id))
}
