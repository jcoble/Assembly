//! The tree shows what's loaded under each open folder, asks for a folder only the first time it opens, and keeps a
//! folder's failure on that folder.

use super::{FileTree, RevealStep, Row};
use crate::features::files::entry::{FileEntry, FileListing};

fn entry(path: &str, folder: bool) -> FileEntry {
    FileEntry {
        path: path.into(),
        name: path.rsplit('/').next().unwrap().into(),
        is_directory: folder,
        excluded: false,
    }
}

fn listing(entries: Vec<FileEntry>) -> Result<FileListing, String> {
    Ok(FileListing {
        entries,
        truncated: false,
        next_cursor: None,
    })
}

fn shape(tree: &FileTree) -> Vec<String> {
    tree.rows()
        .iter()
        .map(|row| match row {
            Row::Entry { entry, depth, open } => format!(
                "{}{}{}",
                "  ".repeat(*depth),
                entry.name,
                match (entry.is_directory, open) {
                    (true, true) => "/ v",
                    (true, false) => "/",
                    _ => "",
                }
            ),
            Row::Loading { depth } => format!("{}…", "  ".repeat(*depth)),
            Row::Failed { depth, message } => format!("{}! {message}", "  ".repeat(*depth)),
            Row::Truncated { depth, .. } => format!("{}+", "  ".repeat(*depth)),
        })
        .collect()
}

#[test]
fn a_new_tree_loads_the_root_first() {
    let mut tree = FileTree::default();
    assert_eq!(tree.start(), "");
    assert_eq!(shape(&tree), ["…"]);
    tree.loaded("", listing(vec![entry("src", true), entry("a.txt", false)]));
    assert_eq!(shape(&tree), ["src/", "a.txt"]);
}

#[test]
fn collapsing_drops_descendants_and_reopening_reads_them_again() {
    let mut tree = FileTree::default();
    tree.start();
    tree.loaded("", listing(vec![entry("src", true), entry("a.txt", false)]));
    assert_eq!(tree.toggle("src"), Some("src".to_string()));
    assert_eq!(shape(&tree), ["src/ v", "  …", "a.txt"]);
    tree.loaded(
        "src",
        listing(vec![entry("src/lib", true), entry("src/main.rs", false)]),
    );
    assert_eq!(shape(&tree), ["src/ v", "  lib/", "  main.rs", "a.txt"]);
    assert_eq!(tree.toggle("src"), None);
    assert_eq!(shape(&tree), ["src/", "a.txt"]);
    assert_eq!(
        tree.toggle("src"),
        Some("src".into()),
        "collapsed descendants are read fresh"
    );
    assert_eq!(shape(&tree), ["src/ v", "  …", "a.txt"]);
    tree.loaded(
        "src",
        listing(vec![entry("src/lib", true), entry("src/main.rs", false)]),
    );
    assert_eq!(shape(&tree), ["src/ v", "  lib/", "  main.rs", "a.txt"]);
}

#[test]
fn closing_a_folder_while_it_loads_forgets_the_load_so_reopening_asks_again() {
    let mut tree = FileTree::default();
    tree.start();
    tree.loaded("", listing(vec![entry("src", true)]));
    assert_eq!(tree.toggle("src"), Some("src".to_string()));
    assert_eq!(tree.toggle("src"), None);
    assert_eq!(tree.toggle("src"), Some("src".to_string()));
}

#[test]
fn a_failed_folder_says_why_under_it_and_opening_it_again_retries() {
    let mut tree = FileTree::default();
    tree.start();
    tree.loaded("", listing(vec![entry("src", true)]));
    tree.toggle("src");
    tree.loaded("src", Err("that path goes through a link".into()));
    assert_eq!(
        shape(&tree),
        ["src/ v", "  ! that path goes through a link"]
    );
    tree.toggle("src");
    assert_eq!(tree.toggle("src"), Some("src".to_string()));
}

#[test]
fn a_truncated_folder_says_so_after_its_entries() {
    let mut tree = FileTree::default();
    tree.start();
    tree.loaded(
        "",
        Ok(FileListing {
            entries: vec![entry("a", false)],
            truncated: true,
            next_cursor: None,
        }),
    );
    assert_eq!(shape(&tree), ["a", "+"]);
}

#[test]
fn an_answer_for_a_folder_nobody_is_loading_is_dropped() {
    let mut tree = FileTree::default();
    tree.start();
    tree.loaded("", listing(vec![entry("src", true)]));
    tree.loaded("src", listing(vec![entry("src/x", false)]));
    assert_eq!(shape(&tree), ["src/"]);
}

#[test]
fn subsequent_directory_pages_append_unique_entries_and_keep_the_continuation() {
    use crate::features::files::entry::PageCursor;
    let mut tree = FileTree::default();
    tree.start();
    let cursor = PageCursor {
        generation: "g".into(),
        after: "f:a".into(),
    };
    tree.loaded(
        "",
        Ok(FileListing {
            entries: vec![entry("a", false)],
            truncated: true,
            next_cursor: Some(cursor.clone()),
        }),
    );
    assert_eq!(tree.more(""), Some(cursor));
    tree.loaded(
        "",
        Ok(FileListing {
            entries: vec![entry("a", false), entry("b", false)],
            truncated: false,
            next_cursor: None,
        }),
    );
    assert_eq!(shape(&tree), ["a", "b"]);
    assert_eq!(tree.more(""), None);
}

#[test]
fn collapse_refuses_a_late_descendant_answer() {
    let mut tree = FileTree::default();
    tree.start();
    tree.loaded("", listing(vec![entry("src", true)]));
    tree.toggle("src");
    tree.loaded("src", listing(vec![entry("src/lib", true)]));
    tree.toggle("src/lib");
    tree.toggle("src");
    tree.loaded("src/lib", listing(vec![entry("src/lib/stale", false)]));
    assert_eq!(shape(&tree), ["src/"]);
    assert_eq!(tree.toggle("src"), Some("src".into()));
}

#[test]
fn revealing_a_folder_loads_its_ancestors_and_the_needed_continuation_without_closing_them() {
    use super::RevealStep;
    use crate::features::files::entry::PageCursor;
    let mut tree = FileTree::default();
    tree.start();
    let cursor = PageCursor {
        generation: "g".into(),
        after: "first-page".into(),
    };
    tree.loaded(
        "",
        Ok(FileListing {
            entries: vec![entry("earlier", true)],
            truncated: true,
            next_cursor: Some(cursor.clone()),
        }),
    );
    assert!(
        matches!(tree.reveal("dir/nested"), RevealStep::Load { dir, cursor: Some(_) } if dir.is_empty())
    );
    tree.loaded("", listing(vec![entry("dir", true)]));
    assert!(
        matches!(tree.reveal("dir/nested"), RevealStep::Load { dir, cursor: None } if dir == "dir")
    );
    tree.loaded("dir", listing(vec![entry("dir/nested", true)]));
    assert!(
        matches!(tree.reveal("dir/nested"), RevealStep::Load { dir, cursor: None } if dir == "dir/nested")
    );
    tree.loaded("dir/nested", listing(vec![]));
    assert!(matches!(tree.reveal("dir/nested"), RevealStep::Ready));
    assert!(shape(&tree).contains(&"  nested/ v".into()));
    assert!(matches!(tree.reveal("missing"), RevealStep::Missing));
}

#[test]
fn keyboard_navigation_visits_visible_entries_and_skips_loading_rows() {
    let mut tree = FileTree::default();
    tree.start();
    tree.loaded("", listing(vec![entry("dir", true), entry("last", false)]));
    tree.toggle("dir");
    assert_eq!(tree.adjacent(None, 1).unwrap().0, "dir");
    assert_eq!(tree.adjacent(Some("dir"), 1).unwrap().0, "last");
    tree.loaded("dir", listing(vec![entry("dir/first", false)]));
    assert_eq!(tree.adjacent(Some("dir"), 1).unwrap().0, "dir/first");
    assert_eq!(tree.adjacent(Some("last"), -1).unwrap().0, "dir/first");
    assert_eq!(tree.adjacent(Some("dir"), -1).unwrap().0, "dir");
}

#[test]
fn a_failed_reveal_directory_does_not_automatically_replay_the_read() {
    let mut tree = FileTree::default();
    tree.start();
    tree.loaded(
        "",
        Ok(FileListing {
            entries: vec![FileEntry {
                path: "blocked".into(),
                name: "blocked".into(),
                is_directory: true,
                excluded: false,
            }],
            truncated: false,
            next_cursor: None,
        }),
    );
    assert!(matches!(tree.reveal("blocked"), RevealStep::Load { .. }));
    tree.loaded("blocked", Err("Traversal denied".into()));
    assert!(matches!(tree.reveal("blocked"), RevealStep::Missing));
    assert!(matches!(tree.reveal("blocked"), RevealStep::Missing));
}

#[test]
fn revealing_a_file_uses_existing_pages_without_loading_it_as_a_directory() {
    let mut tree = FileTree::default();
    tree.start();
    tree.loaded(
        "",
        Ok(FileListing {
            entries: vec![entry("src", true)],
            truncated: false,
            next_cursor: None,
        }),
    );
    assert!(matches!(tree.reveal_entry("src/λ.rs"),RevealStep::Load {dir,..} if dir=="src"));
    tree.loaded(
        "src",
        Ok(FileListing {
            entries: vec![entry("src/λ.rs", false)],
            truncated: false,
            next_cursor: None,
        }),
    );
    assert!(matches!(tree.reveal_entry("src/λ.rs"), RevealStep::Ready));
    assert_eq!(tree.expanded_paths(), vec!["src"]);
    assert!(!tree.reveal_failed("src/λ.rs"));
}
