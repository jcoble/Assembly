use serde_json::json;

use super::{changed_lines, list_entries, project_label, same_line};
use crate::core::state::session_item::SessionItem;
use crate::features::sessions::list_entry::ListEntry;
use crate::features::sessions::status::Status;

fn session(
    id: &str,
    repo: &str,
    machine: Option<&str>,
    done: Option<i64>,
    settled: Option<i64>,
) -> SessionItem {
    serde_json::from_value(
        json!({ "sessionId": id, "agent": "claude", "repoUrl": repo, "title": null,
        "machine": machine, "parentSessionId": null, "acpSessionId": null, "updatedAt": 1,
        "completedAt": done, "settledAt": settled }),
    )
    .unwrap()
}

fn project(status: Status, folder: &str, machine: Option<&str>) -> ListEntry {
    ListEntry::Project {
        status,
        folder: folder.into(),
        machine: machine.map(Into::into),
    }
}

// Pins: sessions-list#1
#[test]
fn a_session_without_stamps_is_working_and_the_settled_stamp_wins() {
    assert_eq!(
        Status::of(&session("a", "/r", None, None, None)),
        Status::Working
    );
    assert_eq!(
        Status::of(&session("a", "/r", None, Some(1), None)),
        Status::Done
    );
    assert_eq!(
        Status::of(&session("a", "/r", None, Some(1), Some(2))),
        Status::Settled
    );
    // An item from a backend without the stamps reads as working.
    let old: SessionItem = serde_json::from_value(json!({ "sessionId": "a", "agent": "claude", "repoUrl": "/r",
        "title": null, "machine": null, "parentSessionId": null, "acpSessionId": null, "updatedAt": 1 }))
    .unwrap();
    assert_eq!(Status::of(&old), Status::Working);
}

// Pins: sessions-list#1, sessions-list#6
#[test]
fn groups_come_in_order_with_projects_a_to_z_and_sessions_newest_first() {
    // Newest first, as Sessions holds them.
    let items = [
        session("w1", "/home/me/zeta", None, None, None),
        session("d1", "/home/me/alpha", None, Some(5), None),
        session("w2", "https://github.com/o/Beta", None, None, None),
        session("w3", "/home/me/zeta", None, None, None),
    ];
    assert_eq!(
        list_entries(&items, false, true),
        [
            ListEntry::Group {
                status: Status::Working,
                open: true
            },
            project(Status::Working, "https://github.com/o/Beta", None),
            ListEntry::Session("w2".into()),
            project(Status::Working, "/home/me/zeta", None),
            ListEntry::Session("w1".into()),
            ListEntry::Session("w3".into()),
            ListEntry::Group {
                status: Status::Done,
                open: true
            },
            project(Status::Done, "/home/me/alpha", None),
            ListEntry::Session("d1".into()),
            ListEntry::Group {
                status: Status::Settled,
                open: false
            },
        ]
    );
}

// Pins: sessions-list#6
#[test]
fn the_same_folder_on_two_machines_is_two_projects() {
    let items = [
        session("a", "/srv/app", Some("Workbox Test"), None, None),
        session("b", "/srv/app", None, None, None),
    ];
    let entries = list_entries(&items, false, true);
    assert_eq!(
        entries[1..entries.len() - 1],
        [
            project(Status::Working, "/srv/app", None),
            ListEntry::Session("b".into()),
            project(Status::Working, "/srv/app", Some("Workbox Test")),
            ListEntry::Session("a".into()),
        ]
    );
}

// Pins: sessions-list#7
#[test]
fn settled_starts_folded_to_its_header_and_opens() {
    let items = [session("s", "/r", None, Some(1), Some(2))];
    assert_eq!(
        list_entries(&items, false, true),
        [ListEntry::Group {
            status: Status::Settled,
            open: false
        }]
    );
    assert_eq!(
        list_entries(&items, true, true),
        [
            ListEntry::Group {
                status: Status::Settled,
                open: true
            },
            project(Status::Settled, "/r", None),
            ListEntry::Session("s".into()),
        ]
    );
}

#[test]
fn a_project_is_named_by_its_repo_or_its_folders_last_name() {
    assert_eq!(project_label("https://github.com/o/r"), "o/r");
    assert_eq!(project_label("/home/me/dev/Assembly/"), "Assembly");
    assert_eq!(project_label("/"), "/");
}

// Pins: sessions-list#2
#[test]
fn the_row_menu_moves_a_session_to_the_other_two_groups() {
    for status in Status::ALL {
        let targets: Vec<Status> = status.moves().iter().map(|(_, to)| *to).collect();
        assert!(!targets.contains(&status));
        assert_eq!(targets.len(), 2);
    }
    assert_eq!(Status::Settled.moves()[0], ("Unsettle", Status::Done));
}

fn line(id: &str) -> ListEntry {
    ListEntry::Session(id.into())
}

#[test]
fn only_the_lines_between_what_both_share_change() {
    let old = [line("a"), line("b"), line("c"), line("d")];
    assert_eq!(changed_lines(&old, &old), (4..4, 0));
    // "c" moved up past "b": only those two lines change.
    assert_eq!(
        changed_lines(&old, &[line("a"), line("c"), line("b"), line("d")]),
        (1..3, 2)
    );
    // One added at the end, one removed at the start.
    assert_eq!(
        changed_lines(
            &old,
            &[line("a"), line("b"), line("c"), line("d"), line("e")]
        ),
        (4..4, 1)
    );
    assert_eq!(
        changed_lines(&old, &[line("b"), line("c"), line("d")]),
        (0..1, 0)
    );
    assert_eq!(changed_lines(&[], &old), (0..0, 4));
}

// Pins: sessions-list#9
#[test]
fn the_line_at_the_top_is_found_again_when_lines_change_around_it() {
    // An older page adds a session above the reader's line and one below it: the whole span is replaced.
    let old = [line("a"), line("b"), line("c")];
    let new = [line("a"), line("a2"), line("b"), line("c"), line("c2")];
    let (changed, _) = changed_lines(&old, &new);
    assert_eq!(changed, 1..3);
    assert_eq!(same_line(&old, &new, &changed, 1), Some(2));
    // Outside the changed lines the splice keeps the place itself; a line that's gone isn't found.
    assert_eq!(same_line(&old, &new, &changed, 0), None);
    assert_eq!(same_line(&old, &[line("a"), line("c")], &(1..2), 1), None);
}

#[test]
fn settleds_header_is_there_before_any_settled_session_has_loaded() {
    let items = [session("w", "/r", None, None, None)];
    assert_eq!(
        list_entries(&items, false, true).last(),
        Some(&ListEntry::Group {
            status: Status::Settled,
            open: false
        })
    );
    // With nothing open, Settled is still there to open; before the list loads, nothing is.
    assert_eq!(
        list_entries(&[], false, true),
        [ListEntry::Group {
            status: Status::Settled,
            open: false
        }]
    );
    assert_eq!(list_entries(&[], false, false), []);
}

fn summary(state: &str, run: Option<&str>, unread: bool) -> SessionItem {
    serde_json::from_value(
        json!({ "sessionId": "a", "agent": "claude", "repoUrl": "/r", "title": null,
        "machine": null, "parentSessionId": null, "acpSessionId": null, "updatedAt": 1,
        "state": state, "activeRunId": run, "unread": unread }),
    )
    .unwrap()
}

#[test]
fn a_row_shows_needs_you_first_then_working_failed_and_unread() {
    use crate::features::sessions::presence::Presence;
    // An open ask comes while a run goes: it needs you.
    assert_eq!(
        Presence::of(&summary("waiting", Some("r"), true), false),
        Presence::NeedsYou
    );
    assert_eq!(
        Presence::of(&summary("running", Some("r"), true), false),
        Presence::Working
    );
    assert_eq!(
        Presence::of(&summary("failed", None, true), false),
        Presence::Failed
    );
    assert_eq!(
        Presence::of(&summary("idle", None, true), false),
        Presence::Unread
    );
    assert_eq!(
        Presence::of(&summary("idle", None, false), false),
        Presence::Idle
    );
    assert_eq!(Presence::NeedsYou.word(), Some("needs you"));
    assert_eq!(Presence::Unread.word(), None);
}
