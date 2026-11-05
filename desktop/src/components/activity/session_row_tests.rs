use gpui_kit::SharedString;

use super::joined;

#[test]
fn a_line_names_only_what_the_session_has() {
    let (task, pr) = (
        Some(SharedString::from("TSK-1")),
        Some(SharedString::from("PR 12")),
    );
    assert_eq!(joined([&task, &pr]), SharedString::from("TSK-1 · PR 12"));
    assert_eq!(joined([&None, &pr]), SharedString::from("PR 12"));
    assert_eq!(joined([&None, &None]), SharedString::from(""));
}

#[test]
fn a_word_both_parts_share_reads_once() {
    let project = Some(SharedString::from("repo"));
    let same_folder = Some(SharedString::from("repo"));
    let other_folder = Some(SharedString::from("tsk-1656-board-gaps"));
    assert_eq!(joined([&project, &same_folder]), SharedString::from("repo"));
    assert_eq!(
        joined([&project, &other_folder]),
        SharedString::from("repo · tsk-1656-board-gaps")
    );
}
