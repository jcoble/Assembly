//! The composer's context ring, ported from app/test/contextMeter.test.ts.

use super::context_meter::ContextMeter;

// Pins: g0-session#28
#[test]
fn nothing_reported_shows_nothing_not_zero() {
    assert_eq!(ContextMeter::of(10, 0), None);
    assert_eq!(ContextMeter::of(2_000, 1_000), None);
    assert_eq!(ContextMeter::of(-1, 1_000), None);
}

// Pins: g0-session#28
#[test]
fn the_popover_and_hint_read_the_rounded_shares() {
    let meter = ContextMeter::of(51_285, 1_000_000).unwrap();
    assert_eq!(meter.detail, "51,285 of 1,000,000 (5%)");
    assert_eq!((meter.left, meter.used), (95, 5));
}

// Pins: g0-session#28
#[test]
fn a_touched_window_paints_at_least_a_tenth_spent_an_untouched_one_is_full() {
    assert_eq!(ContextMeter::of(0, 1_000).unwrap().arc, 100);
    assert_eq!(ContextMeter::of(1, 1_000).unwrap().arc, 90);
    assert_eq!(ContextMeter::of(600, 1_000).unwrap().arc, 40);
}

// Pins: g0-session#28
#[test]
fn warm_at_a_quarter_left_hot_at_a_tenth() {
    let at = |used| {
        let meter = ContextMeter::of(used, 100).unwrap();
        (meter.warm, meter.hot)
    };
    assert_eq!(at(74), (false, false));
    assert_eq!(at(75), (true, false));
    assert_eq!(at(90), (true, true));
}
