use super::{EntryBar, EntryKind, EntryOutcome, entry_outcome};

fn run(kind: &'static str, path: &str, destination: Option<&str>) -> EntryOutcome {
    EntryOutcome::Run {
        kind,
        path: path.into(),
        destination: destination.map(String::from),
    }
}

#[test]
fn empty_or_a_slash_is_dropped_silently() {
    let bar = EntryBar::new_in(EntryKind::NewFile, "src");
    for typed in ["", "   ", "/", " / "] {
        assert_eq!(
            entry_outcome(&bar, typed),
            EntryOutcome::Dropped,
            "{typed:?}"
        );
    }
}

#[test]
fn new_file_and_folder_name_a_path_in_their_folder() {
    let file = EntryBar::new_in(EntryKind::NewFile, "src");
    assert_eq!(
        entry_outcome(&file, " lib.rs "),
        run("createFile", "src/lib.rs", None)
    );
    let folder = EntryBar::new_in(EntryKind::NewFolder, "");
    assert_eq!(
        entry_outcome(&folder, "docs"),
        run("createFolder", "docs", None)
    );
    assert_eq!(file.placeholder(), "New file name");
    assert_eq!(folder.placeholder(), "New folder name");
    assert_eq!(file.initial(), "");
}

#[test]
fn rename_starts_with_the_name_and_the_same_name_does_nothing() {
    let bar = EntryBar::rename("src/main.rs");
    assert_eq!(bar.initial(), "main.rs");
    assert_eq!(entry_outcome(&bar, "main.rs"), EntryOutcome::Unchanged);
    assert_eq!(
        entry_outcome(&bar, "app.rs"),
        run("rename", "src/main.rs", Some("src/app.rs"))
    );
    let top = EntryBar::rename("readme.md");
    assert_eq!(
        entry_outcome(&top, "README.md"),
        run("rename", "readme.md", Some("README.md"))
    );
}

#[test]
fn a_name_with_a_path_in_it_says_why() {
    let bar = EntryBar::new_in(EntryKind::NewFile, "");
    for typed in ["a/b", "..", "."] {
        assert!(
            matches!(entry_outcome(&bar, typed), EntryOutcome::Invalid(_)),
            "{typed:?}"
        );
    }
}
