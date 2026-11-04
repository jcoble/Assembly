//! A machine's notification mute: read from its own Engine settings, changed through one command, and shown as a line on
//! the board. A mute only stops banners. Its rows and badge stay, and nothing replays when it ends.
use std::rc::Rc;

use chrono::{DateTime, Local, TimeZone as _, Timelike as _};
use gpui_kit::assets::IconName;
use gpui_kit::component::Sizable as _;
use gpui_kit::component::button::{Button, ButtonVariants as _};
use gpui_kit::component::menu::{DropdownMenu as _, PopupMenuItem};
use gpui_kit::*;
use serde_json::json;

use crate::core::engine::commands::WindowCommand;
use crate::core::engine::{EngineFeed, EngineKey};

const SETTINGS_ID: &str = "workflow-settings";
const HOUR_MS: i64 = 3_600_000;

pub fn settings_key(machine: &str) -> EngineKey {
    EngineKey {
        machine: machine.into(),
        kind: "workflow_settings".into(),
        id: SETTINGS_ID.into(),
    }
}

/// When this machine's mute ends, if it's muted now.
pub fn muted_until(feed: &EngineFeed, machine: &str, now_ms: i64) -> Option<i64> {
    feed.item(&settings_key(machine))
        .and_then(|item| item.value["mutedUntil"].as_i64())
        .filter(|until| *until > now_ms)
}

/// The next 8:00 here, today if it's still to come.
pub fn next_eight(now: DateTime<Local>) -> i64 {
    let eight = |day: chrono::NaiveDate| {
        day.and_hms_opt(8, 0, 0)
            .and_then(|time| Local.from_local_datetime(&time).earliest())
    };
    let today = now.date_naive();
    let day = if now.hour() < 8 {
        today
    } else {
        today.succ_opt().unwrap_or(today)
    };
    eight(day).map_or(now.timestamp_millis() + 8 * HOUR_MS, |time| {
        time.timestamp_millis()
    })
}

pub fn muted_line(until_ms: i64) -> String {
    let time = Local.timestamp_millis_opt(until_ms).single();
    match time {
        Some(time) => format!("Muted until {}", time.format("%-H:%M")),
        None => "Muted".to_owned(),
    }
}

/// `None` turns banners back on. The version is the one on screen; a newer one on the way is asked once more.
pub fn mute_command(machine: &str, version: i64, until_ms: Option<i64>) -> WindowCommand {
    WindowCommand {
        command_id: uuid::Uuid::new_v4().to_string(),
        entity_id: SETTINGS_ID.into(),
        expected_version: version,
        epoch: 0,
        machine: Some(machine.into()),
        surface: None,
        command: json!({"kind":"set_mute","untilMs":until_ms}),
    }
}

/// One machine's row in the menu.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct MachineMute {
    pub machine: String,
    pub name: String,
    pub until: Option<i64>,
    pub version: i64,
}

/// Each machine whose settings have arrived.
pub fn machine_mutes(
    feed: &EngineFeed,
    machines: Vec<(String, String)>,
    now_ms: i64,
) -> Vec<MachineMute> {
    machines
        .into_iter()
        .filter_map(|(machine, name)| {
            let version = feed.item(&settings_key(&machine))?.version;
            Some(MachineMute {
                until: muted_until(feed, &machine, now_ms),
                machine,
                name,
                version,
            })
        })
        .collect()
}

/// The board's line about it, naming machines only when there's more than one to tell apart.
pub fn mute_note(mutes: &[MachineMute]) -> Option<String> {
    let named = mutes.len() > 1;
    let lines = mutes
        .iter()
        .filter_map(|mute| {
            let line = muted_line(mute.until?);
            Some(if named {
                format!("{} · {line}", mute.name)
            } else {
                line
            })
        })
        .collect::<Vec<_>>();
    (!lines.is_empty()).then(|| lines.join(", "))
}

pub type OnMute = Rc<dyn Fn(&MachineMute, Option<i64>, &mut App)>;

pub fn mute_button(mutes: Vec<MachineMute>, on_mute: OnMute) -> impl IntoElement {
    let named = mutes.len() > 1;
    Button::new("board-mute")
        .ghost()
        .xsmall()
        .icon(IconName::Bell)
        .accessibility_label("Notifications")
        .dropdown_menu(move |mut menu, _, _| {
            for mute in &mutes {
                if named {
                    menu = menu.label(mute.name.clone());
                }
                let choice = |label: &'static str, until: fn() -> Option<i64>| {
                    let (mute, on_mute) = (mute.clone(), on_mute.clone());
                    PopupMenuItem::new(label).on_click(move |_, _, cx| {
                        on_mute(&mute, until(), cx);
                    })
                };
                menu = menu
                    .item(choice("Mute for an hour", || {
                        Some(Local::now().timestamp_millis() + HOUR_MS)
                    }))
                    .item(choice("Mute until 8:00", || Some(next_eight(Local::now()))));
                if mute.until.is_some() {
                    menu = menu.item(choice("Turn back on", || None));
                }
            }
            menu
        })
}

#[cfg(test)]
#[path = "mute_menu_tests.rs"]
mod tests;
