//! The line under a turn and the checkout line, ported from app/test/turns.test.ts.

use chrono::{Local, TimeZone};
use serde_json::{Value, json};

use std::collections::HashMap;

use super::turn_line::{checkout_line, latest_branch, message_time, turn_line, worked_for};
use crate::features::session::rows::turn_stats::TurnStats;

fn stats(over: Value) -> TurnStats {
    let mut stats = json!({ "runId": "r1", "status": "completed", "startedAt": null, "durationMs": 12_400, "firstTokenMs": 900,
        "model": "claude-opus-4-8", "inputTokens": 12_000, "outputTokens": 340, "cacheHitPct": 81.5, "costUsd": 0.0412,
        "toolCalls": 2, "branch": "main", "head": "a".repeat(40), "prevBranch": "main", "prevHead": "a".repeat(40) });
    stats
        .as_object_mut()
        .unwrap()
        .extend(over.as_object().unwrap().clone());
    serde_json::from_value(stats).unwrap()
}

#[test]
fn worked_for_reads_like_assembly() {
    assert_eq!(worked_for(3_240), "3.2s");
    assert_eq!(worked_for(42_400), "42s");
    assert_eq!(worked_for(125_000), "2m 5s");
    assert_eq!(worked_for(-5), "0.0s");
}

// Pins: g0-session#10
#[test]
fn a_finished_turn_shows_its_time_model_tokens_cache_and_cost() {
    assert_eq!(
        turn_line(Some(&stats(json!({}))), None, 0),
        "Worked for 12s · claude-opus-4-8 · 12k in · 340 out · 82% cached · $0.041"
    );
}

// Pins: g0-session#12
#[test]
fn a_live_turn_shows_what_is_known_so_far() {
    assert_eq!(turn_line(None, Some(1_000), 6_000), "Working for 5.0s");
    let running =
        stats(json!({ "status": "running", "durationMs": null, "startedAt": 1_000, "model": "m" }));
    assert!(turn_line(Some(&running), Some(1_000), 31_000).starts_with("Working for 30s · "));
}

// Pins: g0-session#10
#[test]
fn a_failed_or_stopped_turn_says_so() {
    let failed = stats(
        json!({ "status": "failed", "durationMs": null, "inputTokens": null, "outputTokens": null, "costUsd": null, "cacheHitPct": null }),
    );
    assert_eq!(
        turn_line(Some(&failed), None, 0),
        "Failed · claude-opus-4-8"
    );
    assert!(
        turn_line(Some(&stats(json!({ "status": "aborted" }))), None, 0).starts_with("Stopped · ")
    );
}

// Pins: g0-session#11
#[test]
fn the_checkout_line_shows_only_when_the_checkout_moved_between_turns() {
    assert_eq!(checkout_line(None), None);
    assert_eq!(checkout_line(Some(&stats(json!({})))), None);
    assert_eq!(
        checkout_line(Some(&stats(
            json!({ "prevHead": null, "prevBranch": null })
        ))),
        None
    );
    assert_eq!(
        checkout_line(Some(&stats(json!({ "branch": "feature" })))).as_deref(),
        Some("Checkout changed · main@aaaaaaa → feature@aaaaaaa")
    );
    assert_eq!(
        checkout_line(Some(&stats(
            json!({ "branch": "HEAD", "head": "b".repeat(40) })
        )))
        .as_deref(),
        Some("Checkout changed · main@aaaaaaa → bbbbbbb")
    );
    assert_eq!(checkout_line(Some(&stats(json!({ "head": "" })))), None);
    assert_eq!(
        checkout_line(Some(&stats(
            json!({ "branch": "", "head": "b".repeat(40) })
        )))
        .as_deref(),
        Some("Checkout changed · main@aaaaaaa → bbbbbbb")
    );
}

fn local(day: u32, hour: u32, minute: u32) -> i64 {
    Local
        .with_ymd_and_hms(2026, 10, day, hour, minute, 0)
        .unwrap()
        .timestamp_millis()
}

// Pins: g0-session#10
#[test]
fn a_turn_with_a_start_time_shows_its_weekday_and_time_after_how_long_it_worked() {
    let line = turn_line(
        Some(&stats(
            json!({ "startedAt": local(22, 12, 2), "model": null, "inputTokens": null, "outputTokens": null, "cacheHitPct": null, "costUsd": null }),
        )),
        None,
        0,
    );
    assert_eq!(line, "Worked for 12s · Thu 12:02 PM");
}

// Pins: g0-session#21
#[test]
fn a_time_within_the_week_says_its_weekday_an_older_one_its_date() {
    let at = local(22, 12, 2);
    assert_eq!(message_time(at, at + 60_000), "Thursday 12:02 PM");
    assert_eq!(
        message_time(at, at + 8 * 24 * 3_600_000),
        "Oct 22, 12:02 PM"
    );
    assert_eq!(
        message_time(local(5, 9, 30), local(5, 10, 0)),
        "Monday 9:30 AM"
    );
}

fn turns(branches: &[(&str, i64, Value)]) -> HashMap<String, TurnStats> {
    (branches.iter())
        .map(|(run, at, branch)| {
            let turn = stats(json!({ "runId": run, "startedAt": at, "branch": branch }));
            (run.to_string(), turn)
        })
        .collect()
}

#[test]
fn the_branch_is_the_newest_turns_that_has_one() {
    let newer_on_feat = turns(&[("r1", 1_000, json!("main")), ("r2", 2_000, json!("feat"))]);
    assert_eq!(latest_branch(&newer_on_feat).as_deref(), Some("feat"));
    let newer_without = turns(&[("r1", 1_000, json!("main")), ("r2", 2_000, Value::Null)]);
    assert_eq!(latest_branch(&newer_without).as_deref(), Some("main"));
}

#[test]
fn an_empty_or_head_branch_is_not_shown() {
    let detached = turns(&[("r1", 1_000, json!("")), ("r2", 2_000, json!("HEAD"))]);
    assert_eq!(latest_branch(&detached), None);
    assert_eq!(latest_branch(&HashMap::new()), None);
}
