use serde_json::json;

use super::{place, title};
use crate::core::state::session_item::SessionItem;

fn session(title: Option<&str>, repo: &str, machine: Option<&str>) -> SessionItem {
    serde_json::from_value(
        json!({ "sessionId": "s1", "agent": "claude", "repoUrl": repo, "title": title,
        "machine": machine, "parentSessionId": null, "acpSessionId": null, "updatedAt": 1 }),
    )
    .unwrap()
}

// Pins: g0-session#30
#[test]
fn a_row_shows_the_title_else_the_repo_and_where_it_runs() {
    assert_eq!(
        title(&session(
            Some("Fix the bar"),
            "https://github.com/o/r",
            None
        )),
        "Fix the bar"
    );
    assert_eq!(title(&session(None, "https://github.com/o/r", None)), "o/r");
    assert_eq!(
        title(&session(Some("  "), "/home/me/repo", None)),
        "/home/me/repo"
    );
    assert_eq!(place(&session(None, "/r", None)), "claude");
    assert_eq!(
        place(&session(None, "/r", Some("Workbox Test"))),
        "claude · Workbox Test"
    );
}
