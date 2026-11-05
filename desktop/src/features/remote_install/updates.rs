//! What the update check found, from the `machines` topic, and the one button that updates it. Nothing here checks or
//! polls: the backend checks at start and at each remote's hello, and pushes the answer.

use crate::core::state::machine::{Machine, UpdateItem};
use crate::core::state::machines::Machines;
use gpui_kit::component::WindowExt as _;
use gpui_kit::component::notification::Notification;
use gpui_kit::*;

fn machine_name(machine: &Machine) -> &str {
    machine.name.as_deref().unwrap_or("This machine")
}

/// What's ahead of what this app wants, which nothing here downgrades.
pub(crate) fn newer_words(item: &UpdateItem) -> Option<String> {
    let (current, wanted) = (item.current.as_ref()?, item.wanted.as_ref()?);
    (item.state == "newer").then(|| {
        let side = if item.kind == "backend" {
            "this app's "
        } else {
            ""
        };
        format!("{} {current}, newer than {side}{wanted}", item.name)
    })
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

/// The notice's whole text, naming the button every app has: its card in Settings.
pub fn behind_notice(words: &str) -> String {
    format!("{words} Use Update everything in Settings to update them.")
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
        let text = behind_notice(&words);
        cx.defer(move |cx| {
            _ = window.update(cx, |_, window, cx| {
                window.push_notification(Notification::warning(text), cx)
            });
        });
    });
    cx.set_global(BehindNotice { _heard: heard });
}

/// Holds the notice's subscription for as long as the app runs.
struct BehindNotice {
    _heard: Subscription,
}

impl Global for BehindNotice {}

#[cfg(test)]
#[path = "updates_tests.rs"]
mod tests;
