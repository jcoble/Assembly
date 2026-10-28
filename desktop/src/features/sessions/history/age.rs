//! `age` is how long ago a stamp was, short: "now", "5m", "3h", "2d", "1w", "1mo", "1y", as the old app's formatAge
//! (relativeTime.ts) printed it beside each past session.

const MINUTE: i64 = 60_000;
const HOUR: i64 = 60 * MINUTE;
const DAY: i64 = 24 * HOUR;

/// `now` and `then` are Unix ms. A stamp ahead of the clock is "now": the clocks disagree.
pub fn age(now: i64, then: i64) -> String {
    let elapsed = now - then;
    match elapsed {
        _ if elapsed < MINUTE => "now".into(),
        _ if elapsed < HOUR => format!("{}m", elapsed / MINUTE),
        _ if elapsed < DAY => format!("{}h", elapsed / HOUR),
        _ if elapsed < 7 * DAY => format!("{}d", elapsed / DAY),
        _ if elapsed < 30 * DAY => format!("{}w", elapsed / (7 * DAY)),
        _ if elapsed < 365 * DAY => format!("{}mo", elapsed / (30 * DAY)),
        _ => format!("{}y", elapsed / (365 * DAY)),
    }
}

#[cfg(test)]
#[path = "age_tests.rs"]
mod tests;
