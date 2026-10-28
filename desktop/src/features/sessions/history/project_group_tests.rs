//! How the History tab groups the loaded sessions.

use serde_json::json;

use super::ProjectGroup;
use crate::core::state::session_item::SessionItem;

fn session(id: &str, folder: &str, machine: Option<&str>) -> SessionItem {
    serde_json::from_value(
        json!({ "sessionId": id, "agent": "claude", "repoUrl": folder, "machine": machine,
        "updatedAt": 1 }),
    )
    .expect("a session item")
}

fn ids(group: &ProjectGroup) -> Vec<&str> {
    group.sessions.iter().map(String::as_str).collect()
}

// Pins: g0-shape#15
#[test]
fn sessions_group_by_project_and_machine_newest_project_first() {
    let items = [
        session("s4", "https://github.com/jcoble/Assembly", None),
        session("s3", "/w/notes", Some("Workbox Test")),
        session("s2", "/w/notes", None),
        session("s1", "https://github.com/jcoble/Assembly", None),
        session("s0", "/w/notes", Some("")),
    ];
    let groups = ProjectGroup::all(&items);
    let labels: Vec<String> = groups.iter().map(ProjectGroup::label).collect();
    assert_eq!(labels, ["jcoble/Assembly", "notes · Workbox Test", "notes"]);
    assert_eq!(
        ids(&groups[0]),
        ["s4", "s1"],
        "each project's sessions newest first"
    );
    assert_eq!(
        ids(&groups[2]),
        ["s2", "s0"],
        "an empty machine is this computer"
    );
}
