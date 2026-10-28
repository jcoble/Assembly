//! What the Changes tab makes of a `git/branchDiff` answer: each file under its path, and its notes (scm#18, scm#20).

use serde_json::json;

use super::BranchDiff;
use crate::features::changes::diff_lines::Kind;
use crate::features::changes::file_diff::{CUT, Row};

fn branch(diff: &str, truncated: bool) -> BranchDiff {
    serde_json::from_value(json!({ "base": "origin/main", "diff": diff, "truncated": truncated }))
        .expect("a git/branchDiff answer")
}

// Pins: scm#18, scm#20
#[test]
fn each_file_is_under_its_path_and_a_binary_one_says_so() {
    let diff = "diff --git a/src/a.rs b/src/a.rs\nindex 1..2 100644\n--- a/src/a.rs\n+++ b/src/a.rs\n\
                @@ -1 +1 @@\n-a\n+b\n\
                diff --git a/logo.png b/logo.png\nindex 3..4 100644\nBinary files a/logo.png and b/logo.png differ\n\
                diff --git a/old name.rs b/new name.rs\nsimilarity index 90%\nrename from old name.rs\nrename to new name.rs\n";
    let rows = branch(diff, false).rows();
    let labels: Vec<_> = rows
        .iter()
        .filter_map(|row| match row {
            Row::Label(label) => Some(label.as_str()),
            Row::Line(_) => None,
        })
        .collect();
    assert_eq!(labels, ["src/a.rs", "logo.png", "new name.rs"]);
    assert!(matches!(&rows[1], Row::Line(line) if line.kind == Kind::Run));
    assert!(matches!(&rows[5], Row::Line(line)
        if line.kind == Kind::Note && line.text == "Binary files a/logo.png and b/logo.png differ"));
    assert!(matches!(&rows[7], Row::Line(line) if line.text == "rename from old name.rs"));
}

// Pins: scm#20
#[test]
fn an_empty_branch_diff_says_so_and_a_cut_one_says_it_was_cut() {
    let empty = branch("", false);
    assert_eq!(empty.notes(&empty.rows()), ["No changes."]);
    let cut = branch("diff --git a/a b/a\n@@ -1 +1 @@\n-a\n+b\n", true);
    assert_eq!(cut.notes(&cut.rows()), [CUT]);
}
