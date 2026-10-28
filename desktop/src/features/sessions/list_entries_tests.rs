use serde_json::json;

use super::{list_entries, project_label};
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
        list_entries(&items, false),
        [
            ListEntry::Group {
                status: Status::Working,
                open: true
            },
            project(Status::Working, "https://github.com/o/Beta", None),
            ListEntry::Session(2),
            project(Status::Working, "/home/me/zeta", None),
            ListEntry::Session(0),
            ListEntry::Session(3),
            ListEntry::Group {
                status: Status::Done,
                open: true
            },
            project(Status::Done, "/home/me/alpha", None),
            ListEntry::Session(1),
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
    let entries = list_entries(&items, false);
    assert_eq!(
        entries[1..],
        [
            project(Status::Working, "/srv/app", None),
            ListEntry::Session(1),
            project(Status::Working, "/srv/app", Some("Workbox Test")),
            ListEntry::Session(0),
        ]
    );
}

// Pins: sessions-list#7
#[test]
fn settled_starts_folded_to_its_header_and_opens() {
    let items = [session("s", "/r", None, Some(1), Some(2))];
    assert_eq!(
        list_entries(&items, false),
        [ListEntry::Group {
            status: Status::Settled,
            open: false
        }]
    );
    assert_eq!(
        list_entries(&items, true),
        [
            ListEntry::Group {
                status: Status::Settled,
                open: true
            },
            project(Status::Settled, "/r", None),
            ListEntry::Session(0),
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
