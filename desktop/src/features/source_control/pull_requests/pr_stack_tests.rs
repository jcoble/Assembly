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
#[test]
fn the_filter_ignores_case_and_an_empty_one_keeps_everything() {
    assert!(matches_filter("src/Main.rs", " main "));
    assert!(matches_filter("src/Main.rs", ""));
    assert!(!matches_filter("src/Main.rs", "lib"));
}
#[test]
fn the_tree_lists_folders_first_and_merges_single_folder_chains() {
    let rows = changed_tree([
        "z.rs",
        "a/b/c.rs",
        "a/b/d/e.rs",
        "lib/m.rs",
        "lib/n/o.rs",
        "b.rs",
    ]);
    let shape: Vec<(usize, &str, bool)> = rows
        .iter()
        .map(|row| (row.depth, row.label.as_str(), row.path.is_some()))
        .collect();
    assert_eq!(
        shape,
        vec![
            (0, "a/b", false),
            (1, "d", false),
            (2, "e.rs", true),
            (1, "c.rs", true),
            (0, "lib", false),
            (1, "n", false),
            (2, "o.rs", true),
            (1, "m.rs", true),
            (0, "b.rs", true),
            (0, "z.rs", true),
        ]
    );
    assert_eq!(rows[2].path.as_deref(), Some("a/b/d/e.rs"));
}
