//! A finished turn's heading.

use std::collections::HashSet;

use super::heading;
use crate::features::session::model::fold::Fold;

fn fold(summary: &str, duration_ms: Option<i64>) -> Fold {
    Fold {
        run_id: "r1".into(),
        work: HashSet::new(),
        reply: None,
        summary: summary.into(),
        duration_ms,
    }
}

// Pins: g0-session#9
#[test]
fn a_heading_with_a_duration_keeps_its_summary_lower_case() {
    assert_eq!(
        heading(&fold("ran 6 commands", Some(41_000))),
        "Worked for 41s · ran 6 commands"
    );
}

// Pins: g0-session#9
#[test]
fn a_heading_without_a_duration_starts_with_a_capital() {
    assert_eq!(
        heading(&fold("ran 6 commands · edited 4 files", None)),
        "Ran 6 commands · edited 4 files"
    );
}
