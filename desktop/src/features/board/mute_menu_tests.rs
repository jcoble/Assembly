//! What the menu asks for and what the board says about it.
use chrono::{Local, TimeZone as _};
use gpui::TestAppContext;
use serde_json::json;

use super::{
    MachineMute, machine_mutes, mute_command, mute_note, muted_line, muted_until, next_eight,
};
use crate::core::engine::{EngineFeed, EngineScope};

fn at(hour: u32, minute: u32) -> chrono::DateTime<Local> {
    Local
        .with_ymd_and_hms(2026, 3, 10, hour, minute, 0)
        .single()
        .expect("a plain local time")
}

#[test]
fn eight_oclock_is_today_only_while_it_is_still_to_come() {
    assert_eq!(next_eight(at(7, 59)), at(8, 0).timestamp_millis());
    let tomorrow = Local
        .with_ymd_and_hms(2026, 3, 11, 8, 0, 0)
        .single()
        .expect("a plain local time")
        .timestamp_millis();
    assert_eq!(next_eight(at(8, 0)), tomorrow);
    assert_eq!(next_eight(at(23, 30)), tomorrow);
}

#[test]
fn the_line_reads_the_way_the_spec_says() {
    assert_eq!(muted_line(at(8, 0).timestamp_millis()), "Muted until 8:00");
}

#[test]
fn the_command_targets_the_settings_at_the_version_on_screen() {
    let command = mute_command("box", 4, Some(99));
    assert_eq!(command.entity_id, "workflow-settings");
    assert_eq!(command.expected_version, 4);
    assert_eq!(command.epoch, 0);
    assert_eq!(command.machine.as_deref(), Some("box"));
    assert_eq!(command.command, json!({"kind":"set_mute","untilMs":99}));
    assert_eq!(
        mute_command("box", 4, None).command,
        json!({"kind":"set_mute","untilMs":null})
    );
}

fn mute(name: &str, until: Option<i64>) -> MachineMute {
    MachineMute {
        machine: name.into(),
        name: name.into(),
        until,
        version: 1,
    }
}

#[test]
fn the_note_names_machines_only_when_there_are_several() {
    let eight = at(8, 0).timestamp_millis();
    assert_eq!(mute_note(&[mute("Mac", None)]), None);
    assert_eq!(
        mute_note(&[mute("Mac", Some(eight))]).as_deref(),
        Some("Muted until 8:00")
    );
    assert_eq!(
        mute_note(&[mute("Mac", None), mute("Box", Some(eight))]).as_deref(),
        Some("Box · Muted until 8:00")
    );
}

#[gpui::test]
fn a_mute_that_has_ended_no_longer_counts(cx: &mut TestAppContext) {
    let feed = cx.update(EngineFeed::global);
    let scope = EngineScope {
        machine: "box".into(),
        topic: "runs".into(),
        key: None,
    };
    let settings = json!({"entityKind":"workflow_settings","id":"workflow-settings","machine":"box","version":3,"mutedUntil":100});
    feed.update(cx, |feed, cx| feed.test_snapshot(scope, vec![settings], cx));
    cx.read(|cx| {
        let feed = feed.read(cx);
        assert_eq!(muted_until(feed, "box", 99), Some(100));
        assert_eq!(muted_until(feed, "box", 100), None);
        let listed = machine_mutes(
            feed,
            vec![("box".into(), "Box".into()), ("none".into(), "None".into())],
            99,
        );
        assert_eq!(
            listed.len(),
            1,
            "a machine with no settings yet offers nothing"
        );
        assert_eq!(listed[0].version, 3);
    });
}
