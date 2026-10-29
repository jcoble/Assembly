//! What the Changes tab makes of a `git/diff` answer: its parts and messages (scm#14, scm#16).

use serde_json::json;

use super::{FileDiff, Row, widest};
use crate::features::changes::diff_lines::Kind;

fn diff(staged: &str, unstaged: &str, binary: bool, truncated: bool) -> FileDiff {
    serde_json::from_value(
        json!({ "path": "a.rs", "staged": staged, "unstaged": unstaged,
        "binary": binary, "truncated": truncated }),
    )
    .expect("a git/diff answer")
}

const EDIT: &str = "@@ -1 +1 @@\n-a\n+b\n";

// Pins: scm#14
#[test]
fn one_part_has_no_label_and_two_parts_are_each_under_theirs() {
    let one = diff("", EDIT, false, false).rows();
    assert!(matches!(one[0], Row::Line(ref line) if line.kind == Kind::Run));
    assert_eq!(one.len(), 3);

    let both = diff(EDIT, EDIT, false, false).rows();
    assert_eq!(both[0], Row::Label("Staged".into()));
    assert_eq!(both[4], Row::Label("Working tree".into()));
    assert_eq!(both.len(), 8);
}

// Pins: scm#14
#[test]
fn a_part_with_no_lines_to_show_has_no_label() {
    // git's header alone, with nothing under it this tab shows.
    let rows = diff("diff --git a/a.rs b/a.rs\nindex 1..2\n", EDIT, false, false).rows();
    assert!(rows.iter().all(|row| !matches!(row, Row::Label(_))));
    assert_eq!(rows.len(), 3);
    assert!(
        diff("diff --git a/a.rs b/a.rs\n", "", false, false)
            .rows()
            .is_empty()
    );
}

// Pins: scm#16
#[test]
fn binary_empty_and_cut_diffs_say_so() {
    const BINARY: &str = "Binary file, so there's no line-by-line comparison.";
    const CUT: &str = "This diff is too long to show in full; the rest is cut.";
    let notes = |d: FileDiff| d.notes(&d.rows());
    assert_eq!(notes(diff("", "", true, false)), [BINARY]);
    assert_eq!(notes(diff(EDIT, "", true, false)), [BINARY]);
    assert_eq!(notes(diff("", "", false, false)), ["No changes."]);
    assert_eq!(
        notes(diff("index 1..2\n", "", false, false)),
        ["No changes."]
    );
    assert!(notes(diff("", EDIT, false, false)).is_empty());
    assert_eq!(notes(diff("", EDIT, false, true)), [CUT]);
}

// Pins: scm#15
#[test]
fn the_widest_row_is_the_one_with_the_longest_text() {
    let long = format!("@@ -1 +1 @@\n-a\n+{}\n", "x".repeat(400));
    let rows = diff("", &long, false, false).rows();
    assert_eq!(widest(&rows), 2);
    assert_eq!(widest(&[]), 0);
}
