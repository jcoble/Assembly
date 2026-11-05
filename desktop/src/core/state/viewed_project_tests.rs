use super::{SelectedSession, Viewed, ViewedProject, overlaps};

fn viewed() -> Viewed {
    Viewed {
        project_id: "p1".into(),
        machine: "local".into(),
        folder: "/w/other".into(),
        title: "other".into(),
    }
}

#[test]
fn its_id_is_what_the_backend_reads() {
    assert_eq!(viewed().id(), "view:7031:2f772f6f74686572");
}

#[test]
fn a_session_works_in_the_folder_it_is_in_or_around_but_not_beside() {
    assert!(overlaps("/w/other", "/w/other/"));
    assert!(overlaps("/w/other/sub", "/w/other"));
    assert!(overlaps("/w", "/w/other"));
    assert!(!overlaps("/w/other-two", "/w/other"));
}

#[gpui::test]
fn another_sessions_selection_does_not_see_it_and_back_forgets_it(cx: &mut gpui::TestAppContext) {
    cx.update(|cx| {
        gpui_kit::init(cx);
        SelectedSession::init(cx);
        let selected = SelectedSession::global(cx);
        selected.update(cx, |selected, cx| selected.select(Some("s1".into()), cx));
        ViewedProject::set(Some("s1".into()), Some(viewed()), cx);
        assert_eq!(ViewedProject::viewed(cx), Some(viewed()));
        assert_eq!(ViewedProject::subject(cx).unwrap().0, viewed().id());
        selected.update(cx, |selected, cx| selected.select(Some("s2".into()), cx));
        assert_eq!(ViewedProject::viewed(cx), None);
        assert_eq!(ViewedProject::subject(cx).unwrap().0, "s2");
        selected.update(cx, |selected, cx| selected.select(Some("s1".into()), cx));
        assert_eq!(ViewedProject::viewed(cx), Some(viewed()));
        ViewedProject::set(Some("s1".into()), None, cx);
        assert_eq!(ViewedProject::subject(cx).unwrap().0, "s1");
    });
}
