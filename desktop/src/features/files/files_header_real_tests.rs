//! The buttons above the tree, against the real backend.
use super::files_real_tests::{settled, shape};
use super::files_reload_real_tests::shown_pane;
use super::panel_message::READ_ONLY;
use crate::core::engine::SessionKey;
use crate::core::socket::row_source::ExpectedRowSource;
use crate::core::state::selected_session::SelectedSession;
use gpui::TestAppContext;
use gpui_kit::VisualTestContext;

/// Opens `session` read only, as a session another machine is working in.
pub(super) fn make_read_only(session: &str, cx: &mut VisualTestContext) {
    cx.update(|_, cx| {
        SelectedSession::global(cx).update(cx, |selected, cx| {
            let key = SessionKey {
                machine: "here".into(),
                id: session.to_string(),
            };
            let source = ExpectedRowSource {
                machine: "here".into(),
                generation: 1,
                route_machine: None,
            };
            selected.commit_open(key, source, true, cx);
        });
    });
}

#[gpui::test]
async fn excluded_files_start_hidden_and_show_dim_when_asked(cx: &mut TestAppContext) {
    let (_backend, _root, _session, pane, cx) =
        shown_pane(cx, &["readme.md", "target/built.bin"]).await;
    pane.read_with(cx, |pane, _| {
        assert_eq!(shape(pane), [".gitignore", "readme.md"]);
    });
    pane.update(cx, |pane, cx| pane.toggle_excluded(cx));
    settled(&pane, cx, |pane| {
        !pane.is_loading() && shape(pane).contains(&"target/ -".into())
    })
    .await;
    pane.read_with(cx, |pane, _| {
        assert_eq!(
            shape(pane),
            [".git/ -", "target/ -", ".gitignore", "readme.md"]
        );
    });
}

#[gpui::test]
async fn sort_keeps_folders_first_and_open_folders_open(cx: &mut TestAppContext) {
    let (_backend, _root, _session, pane, cx) =
        shown_pane(cx, &["alpha/inner.md", "beta/other.md", "zed.md"]).await;
    pane.update(cx, |pane, cx| pane.clicked("alpha".into(), true, cx));
    settled(&pane, cx, |pane| !pane.is_loading()).await;
    pane.update(cx, |pane, cx| pane.toggle_sort(cx));
    settled(&pane, cx, |pane| {
        !pane.is_loading() && shape(pane).first() == Some(&"beta/".to_string())
    })
    .await;
    pane.read_with(cx, |pane, _| {
        assert_eq!(
            shape(pane),
            ["beta/", "alpha/ v", "  inner.md", "zed.md", ".gitignore"]
        );
    });
}

#[gpui::test]
async fn hiding_excluded_removes_an_open_ignored_folder_and_its_children(cx: &mut TestAppContext) {
    let (_backend, _root, _session, pane, cx) =
        shown_pane(cx, &["readme.md", "target/built.bin"]).await;
    pane.update(cx, |pane, cx| pane.toggle_excluded(cx));
    settled(&pane, cx, |pane| {
        !pane.is_loading() && shape(pane).contains(&"target/ -".into())
    })
    .await;
    pane.update(cx, |pane, cx| pane.clicked("target".into(), true, cx));
    settled(&pane, cx, |pane| {
        !pane.is_loading() && shape(pane).contains(&"  built.bin -".into())
    })
    .await;
    pane.update(cx, |pane, cx| pane.toggle_excluded(cx));
    settled(&pane, cx, |pane| {
        !pane.is_loading() && !shape(pane).iter().any(|row| row.contains("target"))
    })
    .await;
    pane.read_with(cx, |pane, _| {
        assert_eq!(shape(pane), [".gitignore", "readme.md"]);
        assert_eq!(pane.selected, None);
    });
}

#[gpui::test]
async fn stop_ends_a_listing_and_keeps_rows(cx: &mut TestAppContext) {
    let (_backend, _root, _session, pane, cx) = shown_pane(cx, &["src/main.rs"]).await;
    let before = pane.read_with(cx, |pane, _| shape(pane));
    pane.update(cx, |pane, cx| {
        pane.toggle_sort(cx);
        assert!(pane.is_loading());
        pane.stop_listing(cx);
        assert!(!pane.is_loading());
    });
    pane.read_with(cx, |pane, _| {
        assert_eq!(shape(pane), before);
        assert_eq!(pane.message(), None);
    });
    // Refresh still works after that, with the sort it was asked for.
    pane.update(cx, |pane, cx| pane.retry(cx));
    settled(&pane, cx, |pane| {
        !pane.is_loading() && shape(pane).first() == Some(&"src/".to_string())
    })
    .await;
}

#[gpui::test]
async fn stop_before_the_first_answer_offers_refresh_and_never_says_empty(cx: &mut TestAppContext) {
    let (_backend, _root, _session, pane, cx) = shown_pane(cx, &["readme.md"]).await;
    let session_rows = pane.read_with(cx, |pane, _| shape(pane));
    pane.update(cx, |pane, cx| {
        pane.tree = Default::default();
        pane.retry(cx);
        pane.stop_listing(cx);
    });
    pane.read_with(cx, |pane, _| {
        assert_eq!(pane.message(), None);
        assert!(shape(pane).is_empty());
    });
    pane.update(cx, |pane, cx| pane.retry(cx));
    settled(&pane, cx, |pane| !pane.is_loading()).await;
    pane.read_with(cx, |pane, _| assert_eq!(shape(pane), session_rows));
}

#[gpui::test]
async fn new_buttons_are_disabled_with_the_reason_in_a_read_only_folder(cx: &mut TestAppContext) {
    let (_backend, _root, session, pane, cx) = shown_pane(cx, &["readme.md"]).await;
    pane.read_with(cx, |pane, cx| {
        assert!(pane.can_create(cx));
        assert_eq!(pane.read_only_reason(cx), None);
    });
    make_read_only(&session, cx);
    pane.read_with(cx, |pane, cx| {
        assert!(!pane.can_create(cx));
        assert_eq!(pane.read_only_reason(cx).as_deref(), Some(READ_ONLY));
    });
}

#[gpui::test]
async fn a_copied_row_carries_its_chip_and_another_row_does_not(cx: &mut TestAppContext) {
    let (_backend, _root, _session, pane, cx) = shown_pane(cx, &["a.md", "b.md"]).await;
    pane.update(cx, |pane, cx| {
        pane.metadata_action("a.md".into(), "copy", cx)
    });
    settled(&pane, cx, |pane| pane.action_idle()).await;
    pane.read_with(cx, |pane, cx| {
        assert_eq!(pane.clipboard_chip("a.md", cx), Some("copy"));
        assert_eq!(pane.clipboard_chip("b.md", cx), None);
    });
    pane.update(cx, |pane, cx| {
        pane.metadata_action("b.md".into(), "cut", cx)
    });
    settled(&pane, cx, |pane| pane.action_idle()).await;
    pane.read_with(cx, |pane, cx| {
        assert_eq!(pane.clipboard_chip("a.md", cx), None);
        assert_eq!(pane.clipboard_chip("b.md", cx), Some("cut"));
    });
}
