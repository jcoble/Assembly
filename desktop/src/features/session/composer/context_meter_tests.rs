//! The composer's context gauge, ported from app/test/contextMeter.test.ts.

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
    assert_eq!(meter.headline, "51,285 of 1,000,000 (5%)");
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

#[test]
fn only_reported_counts_show_and_a_missing_one_is_not_zero() {
    let meter = ContextMeter::of(51_285, 1_000_000).unwrap();
    assert_eq!(
        (meter.input.as_deref(), meter.output.as_deref()),
        (None, None)
    );
    let meter = meter.with_tokens(Some(40_000), None);
    assert_eq!(meter.input.as_deref(), Some("40,000"));
    assert_eq!(meter.output, None);
    let zero = ContextMeter::of(1, 10).unwrap().with_tokens(None, Some(0));
    assert_eq!(zero.output.as_deref(), Some("0"));
}
