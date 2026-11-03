use super::{FileDiff, CUT};

const EDIT: &str = "diff --git a/a.rs b/a.rs\n--- a/a.rs\n+++ b/a.rs\n@@ -1 +1 @@\n-a\n+b\n";
fn diff(staged: &str, unstaged: &str, binary: bool, truncated: bool) -> FileDiff {
    FileDiff { path: "a.rs".into(), staged: staged.into(), unstaged: unstaged.into(), binary, truncated }
}

// Pins: scm#14
#[test]
fn staged_and_working_patches_keep_their_identity() {
    let one = diff("", EDIT, false, false).parts();
    assert_eq!(one.len(), 1);
    assert_eq!(one[0].0, "Working tree");
    let both = diff(EDIT, EDIT, false, false).parts();
    assert_eq!(both.iter().map(|part| part.0).collect::<Vec<_>>(), ["Staged", "Working tree"]);
    assert_eq!(both[0].1, EDIT);
}

// Pins: scm#16
#[test]
fn binary_empty_and_cut_diffs_say_so_without_losing_a_text_side() {
    assert_eq!(diff("", "", true, false).notes(), ["Binary file, so there's no line-by-line comparison."]);
    assert_eq!(diff("", "", false, false).notes(), ["No changes."]);
    assert_eq!(diff("", EDIT, false, true).notes(), [CUT]);
    let mixed = diff(EDIT, "", true, false);
    assert_eq!(mixed.parts().len(), 1);
    assert_eq!(mixed.parts()[0].1, EDIT);
}
