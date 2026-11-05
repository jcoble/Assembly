//! What each tab lists, in the order it draws, and the words above it.
use std::collections::HashSet;

use chrono::{Local, TimeZone};
use gpui_kit::App;

use super::board_prefs::{BoardPrefs, BoardTab};
use super::board_queries::{
    BoardData, answered_today, by_machine, done_runs_today, done_today, live_runs, live_top_level,
    open_owner_questions,
};
use crate::core::engine::{EngineFeed, EngineKey};
use crate::core::socket::client::SocketClient;
use crate::core::state::machines::Machines;
use crate::inline_field::DATE_TIME_SHOWN;

#[derive(Clone, Debug, PartialEq, Eq)]
pub enum Section {
    Open(Vec<EngineKey>),
    Answered(Vec<EngineKey>),
    Live(Vec<(String, Vec<EngineKey>)>),
    Done(Vec<EngineKey>),
}

/// `shown_open` keeps a question answered while the panel is open where it is, until the pointer leaves the list.
pub fn tab_sections(
    data: &BoardData,
    tab: BoardTab,
    midnight_ms: i64,
    local: Option<&str>,
    shown_open: &[EngineKey],
) -> Vec<Section> {
    match tab {
        BoardTab::Needs => {
            let open = open_owner_questions(data);
            let staying = |key: &&EngineKey| data.needs.contains_key(*key) && !open.contains(*key);
            let held = shown_open
                .iter()
                .filter(staying)
                .cloned()
                .collect::<HashSet<_>>();
            let answered = answered_today(data, midnight_ms)
                .into_iter()
                .filter(|key| !held.contains(key))
                .collect();
            let mut listed = shown_open
                .iter()
                .filter(|key| open.contains(*key) || held.contains(*key))
                .cloned()
                .collect::<Vec<_>>();
            listed.extend(
                open.iter()
                    .filter(|key| !shown_open.contains(*key))
                    .cloned(),
            );
            vec![Section::Open(listed), Section::Answered(answered)]
        }
        BoardTab::Workflows => vec![
            Section::Live(by_machine(live_runs(data), local)),
            Section::Done(done_runs_today(data, midnight_ms)),
        ],
        BoardTab::Sessions => vec![
            Section::Live(by_machine(live_top_level(data), local)),
            Section::Done(done_today(data, midnight_ms)),
        ],
    }
}

/// The tab to show on opening: Needs you when any are open, else the last one used.
pub fn first_tab(data: &BoardData, last: BoardTab) -> BoardTab {
    if open_owner_questions(data).is_empty() {
        last
    } else {
        BoardTab::Needs
    }
}

pub fn heading(need_count: usize) -> String {
    match need_count {
        0 => "Nothing needs you".to_owned(),
        count => format!("{count} need you"),
    }
}

pub fn summary((running, waiting, done): (usize, usize, usize)) -> String {
    format!("{running} running · {waiting} waiting · {done} done today")
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub enum Entry {
    /// A section's fold heading, and whether it's folded.
    Fold {
        id: &'static str,
        label: String,
        folded: bool,
    },
    /// A machine's group, with why it can't be reached when it can't.
    Group {
        machine: String,
    },
    Need(EngineKey),
    Run(EngineKey),
    Session(EngineKey),
    Nothing(&'static str),
}

pub const ANSWERED_FOLD: &str = "answered";
const DONE_FOLD: &str = "done";

/// Sections as list rows. Answered and Done start folded, and opening one is what's remembered.
pub fn entries(sections: &[Section], tab: BoardTab, prefs: &BoardPrefs) -> Vec<Entry> {
    let row = |key: &EngineKey| match tab {
        BoardTab::Needs => Entry::Need(key.clone()),
        BoardTab::Workflows => Entry::Run(key.clone()),
        BoardTab::Sessions => Entry::Session(key.clone()),
    };
    let mut rows = Vec::new();
    for section in sections {
        match section {
            Section::Open(keys) => {
                if keys.is_empty() {
                    rows.push(Entry::Nothing("Nothing needs you"));
                }
                rows.extend(keys.iter().map(row));
            }
            Section::Live(groups) => {
                if groups.is_empty() {
                    rows.push(Entry::Nothing(if tab == BoardTab::Workflows {
                        "No workflows running"
                    } else {
                        "No live sessions"
                    }));
                }
                for (machine, keys) in groups {
                    rows.push(Entry::Group {
                        machine: machine.clone(),
                    });
                    rows.extend(keys.iter().map(row));
                }
            }
            Section::Answered(keys) | Section::Done(keys) => {
                if keys.is_empty() {
                    continue;
                }
                let (id, word) = if matches!(section, Section::Answered(_)) {
                    (ANSWERED_FOLD, "Answered today")
                } else {
                    (DONE_FOLD, "Done today")
                };
                let folded = !prefs.unfolded.contains(id);
                rows.push(Entry::Fold {
                    id,
                    label: format!("{word} · {}", keys.len()),
                    folded,
                });
                if !folded {
                    rows.extend(keys.iter().map(row));
                }
            }
        }
    }
    rows
}

/// A machine's heading. Its rows stay listed while it's offline, and nothing about it is guessed: a machine that doesn't
/// say since when is just offline.
pub fn group_heading(name: &str, offline: bool, since_ms: Option<i64>, midnight_ms: i64) -> String {
    if !offline {
        return name.to_owned();
    }
    let since = since_ms.and_then(|ms| Local.timestamp_millis_opt(ms).single().map(|at| (ms, at)));
    match since {
        Some((ms, at)) if ms >= midnight_ms => {
            format!("{name} · offline since {}", at.format("%H:%M"))
        }
        Some((_, at)) => format!("{name} · offline since {}", at.format(DATE_TIME_SHOWN)),
        None => format!("{name} · offline"),
    }
}

/// When a machine's engine stopped answering, as the machines list has it.
pub fn offline_since(machine: &str, feed: &EngineFeed, cx: &App) -> Option<i64> {
    Machines::offline_since(feed.machine_name(machine)?, cx)
}

/// "This Mac" for the local engine, a saved remote's own name, or the engine's id until one is known.
pub fn machine_label(machine: &str, feed: &EngineFeed, cx: &App) -> String {
    let local = SocketClient::try_global(cx).and_then(|socket| socket.read(cx).engine_machine());
    if local.as_deref() == Some(machine) {
        return "This Mac".to_owned();
    }
    feed.machine_name(machine).unwrap_or(machine).to_owned()
}

/// Why a machine's rows can't act now. Only a saved remote can be offline.
pub fn offline_reason(
    machine: &str,
    feed: &EngineFeed,
    cx: &App,
) -> Option<gpui_kit::SharedString> {
    let name = feed.machine_name(machine)?;
    Machines::offline(Some(name), cx).then(|| format!("{name} is offline").into())
}

#[cfg(test)]
#[path = "board_sections_tests.rs"]
mod tests;
