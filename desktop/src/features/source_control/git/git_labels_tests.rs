use super::{RefKind, age_at, commit_age_at, ref_pills, split_path};
use chrono::{DateTime, Duration, FixedOffset, TimeZone, Utc};
fn now() -> DateTime<FixedOffset> {
    FixedOffset::west_opt(5 * 3_600)
        .unwrap()
        .with_ymd_and_hms(2026, 11, 3, 14, 0, 0)
        .unwrap()
}
fn ago(duration: Duration) -> String {
    age_at(now().with_timezone(&Utc) - duration, now())
}
// Pins: g9#11
#[test]
fn an_age_is_a_distance_inside_a_day_then_yesterday_then_a_date() {
    assert_eq!(ago(Duration::seconds(-30)), "just now");
    assert_eq!(ago(Duration::seconds(59)), "just now");
    assert_eq!(ago(Duration::minutes(5)), "5m ago");
    assert_eq!(ago(Duration::hours(3)), "3h ago");
    assert_eq!(ago(Duration::hours(30)), "yesterday");
    assert_eq!(ago(Duration::days(10)), "Oct 24, 2:00 PM");
    assert_eq!(ago(Duration::days(400)), "Sep 29, 2025");
}
#[test]
fn a_commit_age_respects_its_own_offset_and_an_unreadable_time_is_left_out() {
    assert_eq!(commit_age_at("2026-11-03T13:00:00-05:00", now()), "1h ago");
    assert_eq!(commit_age_at("not a time", now()), "");
}
#[test]
fn head_branch_and_tag_decorations_become_named_pills_and_head_is_not_repeated() {
    let pills = ref_pills(&[
        "HEAD -> main".into(),
        "main".into(),
        "origin/main".into(),
        "tag: v1.2".into(),
    ]);
    assert_eq!(pills.len(), 3);
    assert_eq!(
        (pills[0].label.as_str(), pills[0].kind),
        ("main", RefKind::Head)
    );
    assert_eq!(pills[1].kind, RefKind::Remote);
    assert_eq!(ref_pills(&["main".into()])[0].kind, RefKind::Branch);
    assert_eq!(
        (pills[2].label.as_str(), pills[2].kind),
        ("v1.2", RefKind::Tag)
    );
    assert_eq!(ref_pills(&["HEAD".into()])[0].kind, RefKind::Head);
}
#[test]
fn a_path_splits_into_its_name_and_folder() {
    assert_eq!(split_path("src/git/panel.rs"), ("panel.rs", "src/git"));
    assert_eq!(split_path("README.md"), ("README.md", ""));
}
