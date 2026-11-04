//! What the update check found, from the `machines` topic, and the one button that updates it. Nothing here checks or
//! polls: the backend checks at start and at each remote's hello, and pushes the answer.

use crate::activity::{StatusChip, Tone};
use crate::core::state::machine::{Machine, UpdateItem};
use crate::core::state::machines::Machines;
use crate::core::theme::text::{TextRole, TextRoleExt as _};
use gpui_kit::component::button::{Button, ButtonVariants as _};
use gpui_kit::component::notification::Notification;
use gpui_kit::component::{ActiveTheme as _, Disableable as _, WindowExt as _, h_flex, v_flex};
use gpui_kit::*;

use super::TITLE;
use super::deploy_form::DeployForm;

fn machine_name(machine: &Machine) -> &str {
    machine.name.as_deref().unwrap_or("This machine")
}

fn item_words(item: &UpdateItem) -> String {
    match (&item.current, &item.wanted) {
        (Some(current), Some(wanted)) if current != wanted => {
            format!("{} {current} → {wanted}", item.name)
        }
        (Some(current), _) => format!("{} {current}", item.name),
        (None, Some(wanted)) => format!("{} → {wanted}", item.name),
        (None, None) => item.name.clone(),
    }
}

/// One sentence for the notice at app start, or `None` when nothing is behind.
pub fn behind_words<'a>(machines: impl Iterator<Item = &'a Machine>) -> Option<String> {
    let found: Vec<String> = machines
        .filter(|machine| machine.behind().next().is_some())
        .map(|machine| {
            let names: Vec<&str> = machine.behind().map(|item| item.name.as_str()).collect();
            format!("{}: {}", machine_name(machine), names.join(", "))
        })
        .collect();
    (!found.is_empty()).then(|| format!("Behind on {}.", found.join("; ")))
}

/// Says once what's behind each time that changes, in the place a person sees when the app starts.
pub fn tell_when_behind(cx: &mut App) {
    let Some(machines) = Machines::try_global(cx) else {
        return;
    };
    let mut last = None;
    let heard = cx.observe(&machines, move |machines, cx| {
        let words = behind_words(machines.read(cx).machines());
        if words == last {
            return;
        }
        last = words.clone();
        let (Some(words), Some(window)) = (words, cx.windows().into_iter().next()) else {
            return;
        };
        let text = format!("{words} Use {TITLE} to update them.");
        cx.defer(move |cx| {
            _ = window.update(cx, |_, window, cx| {
                window.push_notification(Notification::warning(text), cx)
            });
        });
    });
    cx.set_global(BehindNotice(heard));
}

/// Holds the notice's subscription for as long as the app runs.
struct BehindNotice(Subscription);

impl Global for BehindNotice {}

impl DeployForm {
    fn machine_updates(&self, machine: &Machine, cx: &App) -> Div {
        let chips = machine.updates.iter().map(|item| {
            let tone = match item.state.as_str() {
                "behind" => Tone::Warning,
                "current" => Tone::Success,
                _ => Tone::Neutral,
            };
            StatusChip::new(item_words(item), tone)
        });
        v_flex()
            .gap_1()
            .child(
                div()
                    .text_role(TextRole::Body, cx)
                    .child(machine_name(machine).to_string()),
            )
            .child(h_flex().flex_wrap().gap_1().children(chips))
            .children(machine.update_note.clone().map(|note| {
                div()
                    .text_role(TextRole::Meta, cx)
                    .text_color(cx.theme().muted_foreground)
                    .child(note)
            }))
    }

    pub(super) fn updates_section(&self, cx: &mut Context<Self>) -> Div {
        let machines: Vec<Machine> = Machines::try_global(cx)
            .map(|machines| machines.read(cx).machines().cloned().collect())
            .unwrap_or_default();
        let updating = machines.iter().any(|machine| machine.updating);
        let behind = machines
            .iter()
            .any(|machine| machine.behind().next().is_some());
        let label = if updating {
            "Updating…"
        } else if behind {
            "Update everything behind"
        } else {
            "Nothing is behind"
        };
        v_flex()
            .gap_2()
            .child(
                div()
                    .text_role(TextRole::Body, cx)
                    .font_weight(FontWeight::MEDIUM)
                    .child("What each machine has"),
            )
            .children(
                machines
                    .iter()
                    .map(|machine| self.machine_updates(machine, cx)),
            )
            .children(self.update_error.clone().map(|error| {
                div()
                    .text_role(TextRole::Body, cx)
                    .text_color(cx.theme().danger)
                    .child(error)
            }))
            .child(
                h_flex().child(
                    Button::new("remote-install:update-everything")
                        .primary()
                        .label(label)
                        .disabled(updating || !behind)
                        .on_click(cx.listener(|form, _, _, cx| form.update_everything(cx))),
                ),
            )
    }
}

#[cfg(test)]
#[path = "updates_tests.rs"]
mod tests;
