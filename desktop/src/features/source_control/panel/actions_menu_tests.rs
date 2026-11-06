use super::{
    MenuAction, MenuContext, PublishRow, READ_ONLY_SCOPE, STILL_RUNNING, WriteState, publish_row,
    rows,
};
use crate::features::source_control::git_status::GitStatus;
fn status() -> GitStatus {
    GitStatus {
        branch: Some("main".into()),
        head: Some("a".repeat(40)),
        ahead: 0,
        behind: 0,
        has_upstream: true,
        files: vec![],
        total_files: 2,
        total_staged: Some(0),
        fingerprint: "f".into(),
        checkout_root: None,
    }
}
fn context<'a>(status: &'a GitStatus, remotes: &'a [String]) -> MenuContext<'a> {
    MenuContext {
        has_session: true,
        reading: false,
        status: Some(status),
        remotes: Some(remotes),
        write: WriteState::Idle,
    }
}
fn reason(context: &MenuContext, action: MenuAction) -> Option<String> {
    rows(context)
        .into_iter()
        .find(|row| row.action == action)
        .unwrap()
        .reason
}
// Pins: g9#4
#[test]
fn every_row_stays_listed_in_the_old_order_with_nothing_blocked_when_all_is_well() {
    let current = status();
    let remotes = ["origin".to_string()];
    let listed = rows(&context(&current, &remotes));
    assert_eq!(
        listed.iter().map(|row| row.label).collect::<Vec<_>>(),
        [
            "Refresh",
            "Discard all…",
            "Fetch",
            "Pull",
            "Push",
            "Add remote…",
            "Open history"
        ]
    );
    assert!(listed.iter().all(|row| row.reason.is_none()));
}
#[test]
fn push_without_upstream_is_disabled_with_its_reason() {
    let unpublished = GitStatus {
        has_upstream: false,
        ..status()
    };
    let remotes = ["origin".to_string()];
    let open = context(&unpublished, &remotes);
    assert!(
        reason(&open, MenuAction::Push)
            .unwrap()
            .starts_with("This branch has no upstream")
    );
    assert!(
        reason(&open, MenuAction::Pull)
            .unwrap()
            .starts_with("This branch has no upstream")
    );
    assert_eq!(reason(&open, MenuAction::Fetch), None);
}
#[test]
fn fetch_without_upstream_waits_for_the_remotes_and_says_when_there_are_none() {
    let unpublished = GitStatus {
        has_upstream: false,
        ..status()
    };
    let reading = MenuContext {
        remotes: None,
        ..context(&unpublished, &[])
    };
    assert_eq!(
        reason(&reading, MenuAction::Fetch).as_deref(),
        Some("Reading this repository’s remotes…")
    );
    assert_eq!(
        reason(&context(&unpublished, &[]), MenuAction::Fetch).as_deref(),
        Some("This repository has no remote. Add one to fetch.")
    );
}
#[test]
fn a_running_action_blocks_every_write_but_refresh_still_reads() {
    let current = status();
    let remotes = ["origin".to_string()];
    let running = MenuContext {
        write: WriteState::Running,
        ..context(&current, &remotes)
    };
    for row in rows(&running) {
        match row.action {
            MenuAction::Refresh | MenuAction::OpenHistory => {
                assert_eq!(row.reason, None)
            }
            _ => assert_eq!(row.reason.as_deref(), Some(STILL_RUNNING)),
        }
    }
}
#[test]
fn discard_all_says_when_there_is_nothing_to_discard() {
    let clean = GitStatus {
        total_files: 0,
        ..status()
    };
    assert_eq!(
        reason(&context(&clean, &[]), MenuAction::DiscardAll).as_deref(),
        Some("There are no changes to discard.")
    );
}
// Pins: g9#5
#[test]
fn publish_is_hidden_with_an_upstream_and_with_two_remotes_asks_which() {
    let current = status();
    assert_eq!(publish_row(&context(&current, &[])), PublishRow::Hidden);
    let unpublished = GitStatus {
        has_upstream: false,
        ..status()
    };
    let one = ["origin".to_string()];
    assert_eq!(
        publish_row(&context(&unpublished, &one)),
        PublishRow::One("origin".into())
    );
    let two = ["origin".to_string(), "fork".to_string()];
    assert_eq!(
        publish_row(&context(&unpublished, &two)),
        PublishRow::Many(two.to_vec())
    );
    assert!(matches!(
        publish_row(&context(&unpublished, &[])),
        PublishRow::Blocked(_)
    ));
    let detached = GitStatus {
        branch: None,
        has_upstream: false,
        ..status()
    };
    assert_eq!(
        publish_row(&context(&detached, &one)),
        PublishRow::Blocked("Choose a branch before publishing.".into())
    );
}
#[test]
fn another_checkout_blocks_every_write_and_leaves_reading_open() {
    let mut current = status();
    current.has_upstream = false;
    let remotes = ["origin".to_string()];
    let mut shown = context(&current, &remotes);
    shown.write = WriteState::ReadOnly;
    for action in [
        MenuAction::DiscardAll,
        MenuAction::Fetch,
        MenuAction::Pull,
        MenuAction::Push,
        MenuAction::AddRemote,
    ] {
        assert_eq!(
            reason(&shown, action).as_deref(),
            Some(READ_ONLY_SCOPE),
            "{action:?}"
        );
    }
    assert_eq!(
        publish_row(&shown),
        PublishRow::Blocked(READ_ONLY_SCOPE.into())
    );
    for action in [MenuAction::Refresh, MenuAction::OpenHistory] {
        assert_eq!(reason(&shown, action), None, "{action:?}");
    }
}
