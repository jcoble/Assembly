//! The History tab's short ages, as the old app's formatAge printed them.

use super::{age, elapsed_text};

const MINUTE: i64 = 60_000;
const DAY: i64 = 24 * 60 * MINUTE;

// Pins: g0-shape#15
#[test]
fn an_age_is_the_largest_whole_unit_that_has_passed() {
    let now = 1_000 * DAY;
    let ago = |elapsed: i64| age(now, now - elapsed);
    assert_eq!(ago(59_999), "now");
    assert_eq!(
        ago(-5 * MINUTE),
        "now",
        "a stamp ahead of the clock is now, not negative"
    );
    assert_eq!(ago(5 * MINUTE), "5m");
    assert_eq!(ago(3 * 60 * MINUTE + 59 * MINUTE), "3h");
    assert_eq!(ago(2 * DAY), "2d");
    assert_eq!(ago(13 * DAY), "1w");
    assert_eq!(ago(45 * DAY), "1mo");
    assert_eq!(ago(800 * DAY), "2y");
}

const SECOND: i64 = 1_000;

#[test]
fn elapsed_text_counts_minutes_and_seconds() {
    let since = 1_000 * DAY;
    let after = |elapsed: i64| elapsed_text(since + elapsed, since);
    assert_eq!(after(0), "0:00");
    assert_eq!(after(7 * SECOND + 999), "0:07");
    assert_eq!(after(12 * MINUTE + 41 * SECOND), "12:41");
    assert_eq!(after(59 * MINUTE + 59 * SECOND), "59:59");
    assert_eq!(
        after(-5 * SECOND),
        "0:00",
        "a start ahead of the clock is no time yet"
    );
}

#[test]
fn elapsed_text_shows_hours_past_an_hour() {
    let since = 1_000 * DAY;
    let after = |elapsed: i64| elapsed_text(since + elapsed, since);
    assert_eq!(after(60 * MINUTE), "1:00:00");
    assert_eq!(after(62 * MINUTE + 3 * SECOND), "1:02:03");
    assert_eq!(after(DAY + 5 * MINUTE), "24:05:00");
}
