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
