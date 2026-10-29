//! The line under a turn ("Worked for 42s · Thu 12:02 PM · model · 1.2k in · …", "Working for 9s", "Stopped",
//! "Failed") and the checkout line above a prompt when the run started on another branch or commit. Ported from
//! session/turns.ts.

use std::collections::HashMap;

use chrono::{Local, TimeZone};

use crate::features::session::rows::turn_stats::TurnStats;

/// The line under a turn. A live turn with no stats yet shows only its running time, from the backend clock's `now`
/// (core/clock).
pub fn turn_line(stats: Option<&TurnStats>, live_started_at: Option<i64>, now: i64) -> String {
    let started_at = stats.and_then(|stats| stats.started_at).or(live_started_at);
    let running = stats.map_or(live_started_at.is_some(), |stats| stats.status == "running");
    let head = match stats {
        _ if running => format!(
            "Working for {}",
            worked_for(now - started_at.unwrap_or(now))
        ),
        Some(stats) if stats.status == "failed" => "Failed".to_string(),
        Some(stats) if stats.status == "aborted" => "Stopped".to_string(),
        Some(TurnStats {
            duration_ms: Some(ms),
            ..
        }) => format!("Worked for {}", worked_for(*ms)),
        _ => "Worked".to_string(),
    };
    let Some(stats) = stats else { return head };
    let mut parts = vec![head];
    parts.extend(stats.started_at.map(|at| local(at, "%a %-I:%M %p")));
    parts.extend(stats.model.clone());
    parts.extend(stats.input_tokens.map(|n| format!("{} in", count(n))));
    parts.extend(stats.output_tokens.map(|n| format!("{} out", count(n))));
    parts.extend(
        stats
            .cache_hit_pct
            .map(|pct| format!("{}% cached", pct.round())),
    );
    parts.extend(stats.cost_usd.map(|usd| format!("${usd:.3}")));
    parts.join(" · ")
}

/// "Checkout changed · main@1a2b3c4 → feat@5d6e7f8", or `None` when the run started where the last one left off.
pub fn checkout_line(stats: Option<&TurnStats>) -> Option<String> {
    let stats = stats?;
    let head = stats.head.as_deref().filter(|head| !head.is_empty())?;
    let prev_head = stats.prev_head.as_deref().filter(|head| !head.is_empty())?;
    if head == prev_head && stats.branch == stats.prev_branch {
        return None;
    }
    let from = checkout(stats.prev_branch.as_deref(), prev_head);
    Some(format!(
        "Checkout changed · {from} → {}",
        checkout(stats.branch.as_deref(), head)
    ))
}

/// The branch the newest turn with one started on, for the composer; an empty branch or a detached "HEAD" isn't one.
pub fn latest_branch(stats: &HashMap<String, TurnStats>) -> Option<String> {
    (stats.values())
        .filter(|turn| {
            turn.branch
                .as_deref()
                .is_some_and(|b| !b.is_empty() && b != "HEAD")
        })
        .max_by_key(|turn| turn.started_at)
        .and_then(|turn| turn.branch.clone())
}

/// A prompt's or reply's time: "Thursday 12:02 PM" within the last 7 days, "Oct 17, 12:02 PM" before that.
pub fn message_time(at: i64, now: i64) -> String {
    const WEEK_MS: i64 = 7 * 24 * 60 * 60 * 1000;
    local(
        at,
        if now - at < WEEK_MS {
            "%A %-I:%M %p"
        } else {
            "%b %-d, %-I:%M %p"
        },
    )
}

/// Assembly's "Worked for" duration: 9.5s, 42s, 3m 5s.
pub fn worked_for(ms: i64) -> String {
    let ms = ms.max(0);
    if ms < 10_000 {
        return format!("{:.1}s", ms as f64 / 1000.0);
    }
    let seconds = (ms as f64 / 1000.0).round() as i64;
    if seconds < 60 {
        format!("{seconds}s")
    } else {
        format!("{}m {}s", seconds / 60, seconds % 60)
    }
}

/// A time in ms since the epoch, in the local time zone.
fn local(at: i64, format: &str) -> String {
    Local
        .timestamp_millis_opt(at)
        .single()
        .map(|time| time.format(format).to_string())
        .unwrap_or_default()
}

/// 340, or 12k and 1.2k past a thousand.
fn count(n: i64) -> String {
    if n < 1000 {
        n.to_string()
    } else {
        format!("{}k", (n as f64 / 100.0).round() / 10.0)
    }
}

fn checkout(branch: Option<&str>, head: &str) -> String {
    let short: String = head.chars().take(7).collect();
    match branch {
        None | Some("" | "HEAD") => short,
        Some(branch) => format!("{branch}@{short}"),
    }
}
