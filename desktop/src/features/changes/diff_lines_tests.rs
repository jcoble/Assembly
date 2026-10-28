//! How a `git diff` text becomes the Changes tab's numbered lines (scm#15, scm#16).

use super::{DiffLine, Kind, lines_of};

fn line(kind: Kind, old: Option<u32>, new: Option<u32>, text: &str) -> DiffLine {
    DiffLine {
        kind,
        old,
        new,
        text: text.into(),
    }
}

// Pins: scm#15
#[test]
fn each_line_gets_its_old_and_new_numbers_and_the_headers_before_the_first_run_are_left_out() {
    let diff = "diff --git a/a.rs b/a.rs\nindex 1..2 100644\n--- a/a.rs\n+++ b/a.rs\n\
                @@ -3,3 +3,3 @@ fn main()\n keep\n-old\n+new\n tail\n";
    assert_eq!(
        lines_of(diff),
        vec![
            line(Kind::Run, None, None, "@@ -3,3 +3,3 @@ fn main()"),
            line(Kind::Same, Some(3), Some(3), "keep"),
            line(Kind::Removed, Some(4), None, "old"),
            line(Kind::Added, None, Some(4), "new"),
            line(Kind::Same, Some(5), Some(5), "tail"),
        ]
    );
}

// Pins: scm#15
#[test]
fn a_second_run_starts_again_from_its_own_header() {
    let diff = "@@ -1 +1 @@\n-a\n+b\n@@ -10,2 +10 @@\n x\n-y\n";
    let lines = lines_of(diff);
    assert_eq!(lines[3], line(Kind::Run, None, None, "@@ -10,2 +10 @@"));
    assert_eq!(lines[4], line(Kind::Same, Some(10), Some(10), "x"));
    assert_eq!(lines[5], line(Kind::Removed, Some(11), None, "y"));
}

// Pins: scm#14
#[test]
fn a_rename_and_a_new_file_keep_git_s_lines_that_say_so() {
    let diff = "diff --git a/old.rs b/new.rs\nsimilarity index 90%\nrename from old.rs\nrename to new.rs\n";
    assert_eq!(
        lines_of(diff),
        vec![
            line(Kind::Note, None, None, "rename from old.rs"),
            line(Kind::Note, None, None, "rename to new.rs"),
        ]
    );
    let added = lines_of(
        "diff --git a/n b/n\nnew file mode 100644\n--- /dev/null\n+++ b/n\n@@ -0,0 +1 @@\n+hi\n",
    );
    assert_eq!(
        added[0],
        line(Kind::Note, None, None, "new file mode 100644")
    );
    assert_eq!(added[2], line(Kind::Added, None, Some(1), "hi"));
    assert_eq!(
        lines_of("diff --git a/x b/x\nold mode 100644\nnew mode 100755\n"),
        vec![
            line(Kind::Note, None, None, "old mode 100644"),
            line(Kind::Note, None, None, "new mode 100755"),
        ]
    );
}

// Pins: scm#15
#[test]
fn no_newline_at_end_is_a_note() {
    let lines = lines_of("@@ -1 +1 @@\n-a\n\\ No newline at end of file\n+b\n");
    assert_eq!(
        lines[2],
        line(Kind::Note, None, None, "\\ No newline at end of file")
    );
    assert_eq!(lines[3], line(Kind::Added, None, Some(1), "b"));
}

// Pins: scm#16
#[test]
fn a_conflict_s_combined_diff_is_shown_as_git_prints_it() {
    let diff = "diff --cc both.rs\n@@@ -1,1 -1,1 +1,5 @@@\n++<<<<<<< HEAD\n +ours\n";
    assert_eq!(
        lines_of(diff),
        vec![
            line(Kind::Run, None, None, "@@@ -1,1 -1,1 +1,5 @@@"),
            line(Kind::Same, None, None, "++<<<<<<< HEAD"),
            line(Kind::Same, None, None, " +ours"),
        ]
    );
}
