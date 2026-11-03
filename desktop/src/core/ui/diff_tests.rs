use super::parse_patch;

const PATCH: &str = "diff --git a/a.rs b/a.rs\n--- a/a.rs\n+++ b/a.rs\n@@ -1 +1 @@\n-old\n+new\n";

// Pins: g9#17, g9#22
#[test]
fn supplied_patch_preserves_source_identity_and_counts() {
    let files = parse_patch(PATCH, false).unwrap();
    assert_eq!(files.len(), 1);
    assert_eq!(files[0].path().as_ref(), "a.rs");
    assert_eq!(files[0].additions(), 1);
    assert_eq!(files[0].deletions(), 1);
}

// Pins: g9#17
#[test]
fn only_a_backend_truncation_relaxes_incomplete_hunks() {
    let cut = "diff --git a/a b/a\n--- a/a\n+++ b/a\n@@ -1,4 +1,4 @@\n same\n-old\n+new\n";
    assert!(parse_patch(cut, false).is_err());
    let files = parse_patch(cut, true).unwrap();
    assert_eq!(files.len(), 1);
    assert!(files[0].is_truncated());
}

// Pins: g9#17
#[test]
fn an_invalid_patch_still_fails_when_backend_says_truncated() {
    let invalid = "diff --git a/a b/a\n--- a/a\n+++ b/a\n@@ -1 +1 @@\n-a\n+b\n+too many\n";
    assert!(parse_patch(invalid, true).is_err());
}

// Pins: scm#16
#[test]
fn a_conflict_s_combined_diff_is_shown_as_git_prints_it() {
    let patch = "diff --cc both.rs\n@@@ -1,1 -1,1 +1,5 @@@\n++<<<<<<< HEAD\n +ours\n";
    match super::supplied_patch(patch, false).unwrap() {
        super::SuppliedPatch::Combined(raw) => assert_eq!(raw, patch),
        _ => panic!(
            "A combined patch must retain its exact raw content, without invented two-side identities"
        ),
    }
    assert!(super::supplied_patch("diff --git a/a b/a\n@@ broken\n", false).is_err());
}

// Pins: scm#18
#[test]
fn quoted_unicode_deleted_and_space_paths_keep_their_git_identity() {
    let patch = "diff --git a/gone.txt b/gone.txt\ndeleted file mode 100644\n--- a/gone.txt\n+++ /dev/null\n@@ -1 +0,0 @@\n-a\ndiff --git a/café/naïve file.md b/café/naïve file.md\n--- a/café/naïve file.md\n+++ b/café/naïve file.md\n@@ -1 +1 @@\n-a\n+b\ndiff --git \"a/tab\\there.txt\" \"b/tab\\there.txt\"\n--- \"a/tab\\there.txt\"\n+++ \"b/tab\\there.txt\"\n@@ -1 +1 @@\n-a\n+b\ndiff --git a/docs/a b/x.md b/docs/a b/x.md\n--- a/docs/a b/x.md\n+++ b/docs/a b/x.md\n@@ -1 +1 @@\n-a\n+b\n";
    let files = parse_patch(patch, false).unwrap();
    assert_eq!(
        files
            .iter()
            .map(|file| file.path().as_ref())
            .collect::<Vec<&str>>(),
        [
            "gone.txt",
            "café/naïve file.md",
            "tab\there.txt",
            "docs/a b/x.md"
        ]
    );
    assert_eq!(files[0].deletions(), 1);
    assert!(files[0].modified_path().is_none());
}
