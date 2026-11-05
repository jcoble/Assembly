//! What each tab lists and the words above it.
use serde_json::{Value, json};

use super::{Entry, Section, entries, first_tab, group_heading, heading, summary, tab_sections};
use crate::core::engine::{EngineItem, EngineKey};
use crate::features::board::board_prefs::{BoardPrefs, BoardTab};
use crate::features::board::board_queries::BoardData;

const MIDNIGHT: i64 = 1000;

fn item(kind: &str, id: &str, mut value: Value) -> EngineItem {
    value["entityKind"] = json!(kind);
    value["machine"] = json!("m");
    value["id"] = json!(id);
    value["version"] = json!(1);
    EngineItem {
        key: EngineKey {
            machine: "m".into(),
            kind: kind.into(),
            id: id.into(),
        },
        version: 1,
        value,
    }
}

fn need(id: &str, state: &str) -> EngineItem {
    item(
        "needs_me",
        id,
        json!({"sessionId":"s","kind":"question","state":state,"createdMs":1,"answeredMs":MIDNIGHT + 1}),
    )
}

fn full() -> BoardData {
    BoardData::of(
        &[need("open", "open"), need("answered", "answered")],
        &[
            item(
                "session",
                "live",
                json!({"provider":"c","folder":"/w","state":"running","activityState":"working","epoch":1}),
            ),
            item(
                "session",
                "over",
                json!({"provider":"c","folder":"/w","state":"ended","activityState":"idle","epoch":1,"updatedMs":MIDNIGHT + 1}),
            ),
        ],
        &[
            item(
                "run",
                "going",
                json!({"state":"running","activeStepIds":[],"paused":false,"reference":false}),
            ),
            item(
                "run",
                "finished",
                json!({"state":"failed","activeStepIds":[],"paused":false,"reference":false,"endedMs":MIDNIGHT + 1}),
            ),
        ],
        &[],
    )
}

fn key(kind: &str, id: &str) -> EngineKey {
    EngineKey {
        machine: "m".into(),
        kind: kind.into(),
        id: id.into(),
    }
}

#[test]
fn tabs_show_their_sections() {
    let data = full();
    assert_eq!(
        tab_sections(&data, BoardTab::Needs, MIDNIGHT, Some("m"), &[]),
        [
            Section::Open(vec![key("needs_me", "open")]),
            Section::Answered(vec![key("needs_me", "answered")])
        ]
    );
    assert_eq!(
        tab_sections(&data, BoardTab::Workflows, MIDNIGHT, Some("m"), &[]),
        [
            Section::Live(vec![("m".into(), vec![key("run", "going")])]),
            Section::Done(vec![key("run", "finished")])
        ]
    );
    assert_eq!(
        tab_sections(&data, BoardTab::Sessions, MIDNIGHT, Some("m"), &[]),
        [
            Section::Live(vec![("m".into(), vec![key("session", "live")])]),
            Section::Done(vec![key("session", "over")])
        ]
    );
}

#[test]
fn answered_row_stays_until_mouse_leaves_or_reopen() {
    let data = full();
    let shown = [key("needs_me", "answered"), key("needs_me", "open")];
    assert_eq!(
        tab_sections(&data, BoardTab::Needs, MIDNIGHT, None, &shown),
        [Section::Open(shown.to_vec()), Section::Answered(vec![])],
        "it stays where it was while the panel holds it"
    );
    assert_eq!(
        tab_sections(&data, BoardTab::Needs, MIDNIGHT, None, &[])[1],
        Section::Answered(vec![key("needs_me", "answered")]),
        "and moves once the panel lets go"
    );
}

#[test]
fn header_text_counts() {
    assert_eq!(heading(3), "3 need you");
    assert_eq!(heading(0), "Nothing needs you");
    assert_eq!(summary((3, 1, 5)), "3 running · 1 waiting · 5 done today");
}

#[test]
fn needs_you_opens_first_when_any_are_open_else_the_last_tab_used() {
    assert_eq!(first_tab(&full(), BoardTab::Sessions), BoardTab::Needs);
    assert_eq!(
        first_tab(&BoardData::default(), BoardTab::Sessions),
        BoardTab::Sessions
    );
}

#[test]
fn answered_and_done_fold_by_default_and_opening_one_shows_its_rows() {
    let sections = tab_sections(&full(), BoardTab::Needs, MIDNIGHT, None, &[]);
    let mut prefs = BoardPrefs::default();
    let shut = entries(&sections, BoardTab::Needs, &prefs);
    assert_eq!(shut.len(), 2);
    assert_eq!(
        shut[1],
        Entry::Fold {
            id: "answered",
            label: "Answered today · 1".into(),
            folded: true
        }
    );
    prefs.unfolded.insert("answered".into());
    let open = entries(&sections, BoardTab::Needs, &prefs);
    assert_eq!(open.last(), Some(&Entry::Need(key("needs_me", "answered"))));
}

#[test]
fn an_empty_tab_says_so_and_machines_head_their_groups() {
    let empty = BoardData::default();
    let prefs = BoardPrefs::default();
    for (tab, words) in [
        (BoardTab::Needs, "Nothing needs you"),
        (BoardTab::Workflows, "No workflows running"),
        (BoardTab::Sessions, "No live sessions"),
    ] {
        assert_eq!(
            entries(&tab_sections(&empty, tab, MIDNIGHT, None, &[]), tab, &prefs),
            [Entry::Nothing(words)]
        );
    }
    let rows = entries(
        &tab_sections(&full(), BoardTab::Sessions, MIDNIGHT, None, &[]),
        BoardTab::Sessions,
        &prefs,
    );
    assert_eq!(
        rows[0],
        Entry::Group {
            machine: "m".into()
        }
    );
    assert_eq!(rows[1], Entry::Session(key("session", "live")));
}

#[test]
fn offline_group_header_and_disabled_actions() {
    use chrono::{Local, TimeZone};
    let at = |day, hour| {
        Local
            .with_ymd_and_hms(2026, 11, day, hour, 2, 0)
            .unwrap()
            .timestamp_millis()
    };
    let midnight = at(4, 0) - 2 * 60 * 1000;
    let heading = |offline, since| group_heading("Workbox Test", offline, since, midnight);
    assert_eq!(
        heading(true, Some(at(4, 14))),
        "Workbox Test · offline since 14:02"
    );
    assert_eq!(
        heading(true, Some(at(3, 14))),
        "Workbox Test · offline since Nov 3, 2026 14:02",
        "not today, so it says which day"
    );
    assert_eq!(heading(true, None), "Workbox Test · offline");
    assert_eq!(heading(false, Some(at(4, 14))), "Workbox Test");
}
