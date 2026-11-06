//! The Context usage card's figures from what the session reported, with the old app's running-total rule.

use super::{Usage, usage};

// Pins: g0-shape#17
#[test]
fn a_running_total_reported_as_used_becomes_the_session_total() {
    // The old app's test figures: used far above the window while in plus out fit it.
    assert_eq!(
        usage(
            Some(2_091_674),
            Some(237_500),
            Some(81_896),
            Some(418),
            None,
            true
        ),
        Usage {
            used: Some(82_314),
            window: Some(237_500),
            percent: Some(35),
            input: Some(81_896),
            output: Some(418),
            total: Some(2_091_674),
        }
    );
}

// Pins: g0-shape#17
#[test]
fn the_rule_waits_until_the_total_has_been_read() {
    let held = usage(
        Some(2_091_674),
        Some(237_500),
        Some(81_896),
        Some(418),
        None,
        false,
    );
    assert_eq!((held.used, held.total), (Some(2_091_674), None));
    let reported = usage(
        Some(2_091_674),
        Some(237_500),
        Some(81_896),
        Some(418),
        Some(5_000_000),
        true,
    );
    assert_eq!(
        (reported.used, reported.total),
        (Some(2_091_674), Some(5_000_000)),
        "a reported total leaves used as it is"
    );
}

// Pins: g0-shape#17
#[test]
fn a_quarter_of_the_window_reads_25() {
    let quarter = usage(Some(50_000), Some(200_000), None, None, None, true);
    assert_eq!(quarter.percent, Some(25));
}

// Pins: g0-shape#17
#[test]
fn half_a_pair_gives_no_percent() {
    assert_eq!(
        usage(Some(50_000), None, None, None, None, true).percent,
        None
    );
    assert_eq!(
        usage(None, Some(200_000), None, None, None, true).percent,
        None
    );
    let used_only = usage(Some(50_000), None, Some(10), None, None, true);
    assert_eq!((used_only.input, used_only.output), (Some(10), None));
}

// Pins: g0-shape#17
#[test]
fn used_above_the_window_reads_at_most_100() {
    let over = usage(Some(300_000), Some(200_000), None, None, None, true);
    assert_eq!((over.used, over.percent), (Some(300_000), Some(100)));
    // In plus out don't fit, so the rule leaves it.
    let wide = usage(
        Some(300_000),
        Some(200_000),
        Some(150_000),
        Some(60_000),
        None,
        true,
    );
    assert_eq!(
        (wide.used, wide.total, wide.percent),
        (Some(300_000), None, Some(100))
    );
}

// Pins: g0-shape#17
#[test]
fn zeros_read_as_not_reported() {
    let zeros = usage(Some(0), Some(0), Some(0), Some(0), Some(0), true);
    assert_eq!(
        zeros,
        Usage {
            used: Some(0),
            window: None,
            percent: None,
            input: None,
            output: None,
            total: None,
        }
    );
    assert_eq!(
        usage(Some(0), Some(1_000), None, None, None, true).percent,
        Some(0)
    );
    assert_eq!(
        usage(Some(-1), Some(1_000), Some(-5), None, None, true).used,
        None
    );
}
