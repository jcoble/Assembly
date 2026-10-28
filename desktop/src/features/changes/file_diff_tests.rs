//! What the Changes tab makes of a `git/diff` answer: its parts and messages (scm#14, scm#16).

use serde_json::json;

use super::{FileDiff, Row};
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
    assert_eq!(both[0], Row::Label("Staged"));
    assert_eq!(both[4], Row::Label("Working tree"));
    assert_eq!(both.len(), 8);
}

// Pins: scm#16
#[test]
fn binary_empty_and_cut_diffs_say_so() {
    assert_eq!(
        diff("", "", true, false).message(),
        Some("Binary file, so there's no line-by-line comparison.")
    );
    assert_eq!(diff("", "", false, false).message(), Some("No changes."));
    assert_eq!(diff("", EDIT, false, false).message(), None);
    assert_eq!(
        diff("", EDIT, false, true).cut_line(),
        Some("This diff is cut at 1 MiB.")
    );
    assert_eq!(diff("", EDIT, false, false).cut_line(), None);
}
