use std::time::{Duration, Instant};

use gpui::TestAppContext;

use super::{progress, toggled, turn};

const EASE: Duration = Duration::from_millis(200);

// Pins: g0-session#34
#[test]
fn a_toggle_eases_to_its_end_then_stays_still() {
    assert_eq!(
        progress(true, None, EASE),
        (1.0, false),
        "never toggled: still"
    );
    assert_eq!(progress(false, None, EASE), (0.0, false));
    assert_eq!(progress(true, Some(Duration::ZERO), EASE), (0.0, true));
    assert_eq!(progress(false, Some(Duration::ZERO), EASE), (1.0, true));
    let (half_open, moving) = progress(true, Some(EASE / 2), EASE);
    assert!(
        moving && half_open > 0.5 && half_open < 1.0,
        "eased out: {half_open}"
    );
    let (half_shut, _) = progress(false, Some(EASE / 2), EASE);
    assert!((half_open + half_shut - 1.0).abs() < 1e-6);
    assert_eq!(
        progress(true, Some(EASE), EASE),
        (1.0, false),
        "done: no more frames"
    );
    assert_eq!(progress(false, Some(EASE * 3), EASE), (0.0, false));
}

// Shut while it's still opening, it turns back from where it is; a toggle after an ease ended starts fresh.
// Pins: g0-session#34
#[test]
fn a_reversal_turns_back_from_where_it_is() {
    let now = Instant::now();
    let opened = now - EASE / 4;
    let (was, _) = progress(true, Some(EASE / 4), EASE);
    let shut = toggled(false, Some(opened), now, EASE);
    let (from, moving) = progress(false, Some(now - shut), EASE);
    assert!(moving && (from - was).abs() < 1e-3, "{was} then {from}");
    let reopened = toggled(true, Some(shut), now, EASE);
    let (back, _) = progress(true, Some(now - reopened), EASE);
    assert!((back - was).abs() < 1e-3, "{was} then {back}");
    assert_eq!(toggled(false, Some(now - EASE * 2), now, EASE), now);
    assert_eq!(toggled(true, None, now, EASE), now);
}

// With Reduce Motion on, a toggle a moment ago is already at its end.
// Pins: g0-session#34
#[gpui::test]
fn reduce_motion_puts_a_toggle_at_its_end(cx: &mut TestAppContext) {
    cx.update(gpui_kit::init);
    let window = cx.add_empty_window();
    let just = Some(Instant::now());
    window.update(|window, cx| {
        assert!(
            turn(true, just, window, cx) < 0.5,
            "it eases with motion on"
        );
        cx.set_reduce_motion(true);
        assert_eq!(turn(true, just, window, cx), 1.0);
        assert_eq!(turn(false, just, window, cx), 0.0);
    });
}
