use super::{ChangedTree, TreeFile, changed_tree, matches_filter, open_rows, tree_order};
use std::collections::HashSet;

// Pins: old diffRows.ts changedFileTree, moved here from the pull request stack so Changes and PR share it.
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
#[test]
fn a_closed_folder_hides_what_is_under_it_and_only_that() {
    let rows = changed_tree(["a/b/c.rs", "a/b/d/e.rs", "lib/m.rs", "z.rs"]);
    let closed = HashSet::from(["a/b".to_string()]);
    let shown: Vec<(String, bool)> = open_rows(rows, &closed)
        .into_iter()
        .map(|row| (row.label, row.closed))
        .collect();
    assert_eq!(
        shown,
        vec![
            ("a/b".to_string(), true),
            ("lib".to_string(), false),
            ("m.rs".to_string(), false),
            ("z.rs".to_string(), false),
        ]
    );
    let nested = open_rows(
        changed_tree(["a/b/c.rs", "a/b/d/e.rs"]),
        &HashSet::from(["a/b/d".to_string()]),
    );
    assert!(nested.iter().any(|row| row.label == "c.rs"));
    assert!(!nested.iter().any(|row| row.label == "e.rs"));
}
#[test]
fn a_path_listed_for_both_of_its_parts_is_one_row_with_their_counts_added() {
    let mut tree = ChangedTree::new();
    tree.set_files([
        TreeFile {
            path: "a.rs".into(),
            additions: 2,
            deletions: 1,
        },
        TreeFile {
            path: "a.rs".into(),
            additions: 3,
            deletions: 4,
        },
        TreeFile {
            path: "src/b.rs".into(),
            additions: 1,
            deletions: 0,
        },
    ]);
    let files: Vec<&str> = tree
        .rows()
        .iter()
        .filter_map(|row| row.path.as_deref())
        .collect();
    assert_eq!(files, vec!["src/b.rs", "a.rs"]);
    assert_eq!(tree.counts.get("a.rs"), Some(&(5, 5)));
    tree.set_filter("B.RS");
    assert_eq!(
        tree.rows().iter().filter(|row| row.path.is_some()).count(),
        1
    );
}
#[test]
fn the_stack_order_is_the_tree_order() {
    assert_eq!(
        tree_order(["z.rs", "a/b.rs", "c.rs"]),
        vec!["a/b.rs", "c.rs", "z.rs"]
    );
}
