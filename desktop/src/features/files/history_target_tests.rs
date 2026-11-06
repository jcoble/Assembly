use super::{HistoryTarget, Picked};

fn file(path: &str) -> Picked {
    Picked::File(path.into())
}

#[test]
fn last_changed_wins() {
    let mut target = HistoryTarget::default();
    target.selected(file("a.md"));
    assert_eq!(target.current(), Some("a.md"));
    target.editor(file("b.md"));
    assert_eq!(target.current(), Some("b.md"));
    target.selected(file("c.md"));
    assert_eq!(target.current(), Some("c.md"));
    // The same tab heard again isn't a change.
    target.editor(file("b.md"));
    assert_eq!(target.current(), Some("c.md"));
}

#[test]
fn nothing_falls_back_to_the_other() {
    let mut target = HistoryTarget::default();
    assert_eq!(target.current(), None);
    target.selected(file("a.md"));
    target.editor(file("b.md"));
    target.editor(Picked::Nothing);
    assert_eq!(target.current(), Some("a.md"));
    target.editor(file("b.md"));
    target.selected(Picked::Nothing);
    assert_eq!(target.current(), Some("b.md"));
}

#[test]
fn a_folder_picked_last_gives_none() {
    let mut target = HistoryTarget::default();
    target.editor(file("b.md"));
    target.selected(Picked::Folder);
    assert_eq!(target.current(), None);
}

#[test]
fn another_sessions_file_gives_none() {
    let mut target = HistoryTarget::default();
    target.selected(file("a.md"));
    target.editor(Picked::Elsewhere);
    assert_eq!(target.current(), None);
}
