use super::BranchDiff;
use crate::core::ui::diff::parse_patch;
use crate::features::changes::file_diff::CUT;

// Pins: scm#18, scm#20
#[test]
fn branch_paths_binary_and_rename_are_preserved_by_the_kit_document() {
    let patch = "diff --git a/a.rs b/a.rs\n--- a/a.rs\n+++ b/a.rs\n@@ -1 +1 @@\n-a\n+b\ndiff --git a/logo.png b/logo.png\nBinary files a/logo.png and b/logo.png differ\ndiff --git a/old.rs b/new.rs\nsimilarity index 100%\nrename from old.rs\nrename to new.rs\n";
    let files = parse_patch(patch, false).unwrap();
    assert_eq!(files.iter().map(|file| file.path().as_ref()).collect::<Vec<&str>>(), ["a.rs", "logo.png", "new.rs"]);
    assert!(files[1].is_binary());
    assert_eq!(files[2].original_path().unwrap().as_ref(), "old.rs");
}

// Pins: scm#20
#[test]
fn empty_and_cut_branch_diffs_have_visible_notes() {
    assert_eq!(BranchDiff { base: "main".into(), diff: "".into(), truncated: false }.notes(), ["No changes."]);
    assert_eq!(BranchDiff { base: "main".into(), diff: "patch".into(), truncated: true }.notes(), [CUT]);
}
