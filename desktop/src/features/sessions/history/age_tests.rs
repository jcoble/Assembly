//! The History tab's short ages, as the old app's formatAge printed them.

use super::age;

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
