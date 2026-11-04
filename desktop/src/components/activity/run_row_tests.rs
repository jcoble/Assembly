use super::percent;

#[test]
fn a_run_with_no_steps_shows_no_progress() {
    assert_eq!(percent(0, 0), 0.);
    assert_eq!(percent(1, 4), 25.);
    assert_eq!(percent(4, 4), 100.);
}
