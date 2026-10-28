//! The tree shows what's loaded under each open folder, asks for a folder only the first time it opens, and keeps a
//! folder's failure on that folder.

use super::{FileTree, Row};
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
            Row::Truncated { depth } => format!("{}+", "  ".repeat(*depth)),
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
fn opening_a_folder_loads_it_once_and_closing_hides_it() {
    let mut tree = FileTree::default();
    tree.start();
    tree.loaded("", listing(vec![entry("src", true), entry("a.txt", false)]));
    assert_eq!(tree.toggle("src"), Some("src".to_string()));
    assert_eq!(shape(&tree), ["src/ v", "  …", "a.txt"]);
    tree.loaded("src", listing(vec![entry("src/lib", true), entry("src/main.rs", false)]));
    assert_eq!(shape(&tree), ["src/ v", "  lib/", "  main.rs", "a.txt"]);
    assert_eq!(tree.toggle("src"), None);
    assert_eq!(shape(&tree), ["src/", "a.txt"]);
    assert_eq!(tree.toggle("src"), None, "already loaded");
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
    assert_eq!(shape(&tree), ["src/ v", "  ! that path goes through a link"]);
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
