use std::time::Duration;

use super::progress;

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
