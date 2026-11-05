use super::*;

fn refused(code: &str) -> CallFailure {
    CallFailure {
        code: ErrorCode::Refused { code: code.into() },
        message: "the backend's words".into(),
    }
}

fn message(root: RootState<'_>) -> Option<PanelMessage> {
    panel_message(PanelInput {
        subject: true,
        root,
    })
}

#[test]
fn each_state_has_its_message_and_copy() {
    let no_folder = refused("no_folder");
    let not_made = refused("folder_not_made");
    let deleted = refused("checkout_unavailable");
    let cases = [
        (
            panel_message(PanelInput {
                subject: false,
                root: RootState::Waiting,
            }),
            PanelMessage::NoSession,
            "No session selected",
            "Pick a session to view its files.",
        ),
        (
            message(RootState::Failed(&no_folder)),
            PanelMessage::NoFolder,
            "This session has no folder",
            "It has only its conversation, so there are no files to show.",
        ),
        (
            message(RootState::Failed(&not_made)),
            PanelMessage::FolderNotMade,
            "No folder yet",
            "This session's folder is made on its first message.",
        ),
        (
            message(RootState::Failed(&deleted)),
            PanelMessage::CheckoutDeleted,
            "Checkout/Worktree deleted.",
            "The conversation is still available, but this session’s project files no longer exist.",
        ),
        (
            message(RootState::Waiting),
            PanelMessage::Waiting,
            "Loading files…",
            "Reading the selected session's project folder.",
        ),
        (
            message(RootState::Listing),
            PanelMessage::Listing,
            "Listing files…",
            "Reading this project's top-level entries.",
        ),
        (
            message(RootState::Listed { has_rows: false }),
            PanelMessage::EmptyFolder,
            "No source files here",
            "This folder has no files the app can open.",
        ),
    ];
    for (actual, expected, title, body) in cases {
        let actual = actual.expect("a message");
        assert_eq!(actual, expected);
        assert_eq!(actual.title(), title);
        assert_eq!(actual.body(), body);
    }
}

#[test]
fn a_loaded_root_with_rows_shows_no_message() {
    assert_eq!(message(RootState::Listed { has_rows: true }), None);
}

#[test]
fn an_unknown_failure_shows_failed_with_its_text() {
    let refused_other = refused("source_changed");
    let failed = CallFailure {
        code: ErrorCode::Failed,
        message: "the disk is on fire".into(),
    };
    assert_eq!(
        message(RootState::Failed(&refused_other)),
        Some(PanelMessage::Failed("the backend's words".into()))
    );
    let shown = message(RootState::Failed(&failed)).unwrap();
    assert_eq!(shown.title(), "Could not list the files");
    assert_eq!(shown.body(), "the disk is on fire");
}

#[test]
fn not_loaded_does_not_say_empty() {
    assert_eq!(message(RootState::NotLoaded), None);
}
