use super::*;
use crate::core::ui::diff::parse_patch;

fn file(path: &str, status: &str, patch: Option<&str>) -> PullRequestFile {
    PullRequestFile {
        path: path.into(),
        previous_path: None,
        status: status.into(),
        additions: 1,
        deletions: 1,
        patch: patch.map(String::from),
        patch_omitted: false,
    }
}
// Pins: old MultiFileDiff.svelte and diffRows.ts changedFileTree on the Changes page.
#[test]
fn a_changed_file_parses_under_its_own_path() {
    let patch = file_patch(&file(
        "src/a.rs",
        "modified",
        Some("@@ -1 +1 @@\n-old\n+new"),
    ))
    .unwrap();
    let parsed = parse_patch(&patch, false).unwrap();
    assert_eq!(parsed.len(), 1);
    assert_eq!(parsed[0].path().to_string(), "src/a.rs");
    assert_eq!(parsed[0].additions(), 1);
}
#[test]
fn added_removed_and_renamed_files_keep_their_real_paths() {
    let added = file_patch(&file("new.rs", "added", Some("@@ -0,0 +1 @@\n+new"))).unwrap();
    assert!(added.contains("--- /dev/null\n+++ b/new.rs\n"));
    assert_eq!(
        parse_patch(&added, false).unwrap()[0].path().to_string(),
        "new.rs"
    );
    let removed = file_patch(&file("old.rs", "removed", Some("@@ -1 +0,0 @@\n-old"))).unwrap();
    assert!(removed.contains("--- a/old.rs\n+++ /dev/null\n"));
    let mut renamed = file("b.rs", "renamed", Some("@@ -1 +1 @@\n-old\n+new"));
    renamed.previous_path = Some("a.rs".into());
    let renamed = file_patch(&renamed).unwrap();
    assert!(renamed.contains("rename from a.rs\nrename to b.rs\n--- a/a.rs\n+++ b/b.rs\n"));
    assert_eq!(
        parse_patch(&renamed, false).unwrap()[0].path().to_string(),
        "b.rs"
    );
}
#[test]
fn a_file_with_no_lines_says_why() {
    assert_eq!(file_patch(&file("a.png", "modified", None)), None);
    assert_eq!(file_patch(&file("a.png", "modified", Some("  "))), None);
    let mut binary = file("a.png", "added", None);
    binary.additions = 0;
    binary.deletions = 0;
    assert!(no_lines_note(&binary).starts_with("Binary file"));
    let mut moved = binary;
    moved.status = "renamed".into();
    assert_eq!(no_lines_note(&moved), "No line changes to show.");
    moved.patch_omitted = true;
    assert!(no_lines_note(&moved).contains("left this file's changes out"));
}

// Pins: a file's comments gather under the last line the patch shows, on whichever side that line is.
#[test]
fn the_end_of_a_patch_is_its_last_shown_line_on_that_lines_side() {
    let ends_in_context = "--- a/a.rs\n+++ b/a.rs\n@@ -1,3 +1,3 @@\n keep\n-old\n+new\n tail";
    assert_eq!(patch_end(ends_in_context), Some((DiffSide::Modified, 3)));
    let ends_in_deletion = "@@ -8,2 +8 @@\n keep\n-gone\n\\ No newline at end of file";
    assert_eq!(patch_end(ends_in_deletion), Some((DiffSide::Original, 9)));
    let second_hunk = "@@ -1 +1 @@\n-a\n+b\n@@ -40,2 +40,3 @@\n keep\n+one\n+two";
    assert_eq!(patch_end(second_hunk), Some((DiffSide::Modified, 42)));
    assert_eq!(patch_end(""), None);
}
