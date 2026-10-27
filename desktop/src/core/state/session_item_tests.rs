use serde_json::json;

use super::SessionItem;

pub(crate) fn session(title: Option<&str>, repo: &str, machine: Option<&str>) -> SessionItem {
    serde_json::from_value(
        json!({ "sessionId": "s1", "agent": "claude", "repoUrl": repo, "title": title,
        "machine": machine, "parentSessionId": null, "acpSessionId": null, "updatedAt": 1 }),
    )
    .unwrap()
}

// Pins: g0-session#30
#[test]
fn a_session_shows_the_title_else_the_repo_and_where_it_runs() {
    assert_eq!(
        session(Some("Fix the bar"), "https://github.com/o/r", None).title(),
        "Fix the bar"
    );
    assert_eq!(session(None, "https://github.com/o/r", None).title(), "o/r");
    assert_eq!(
        session(Some("  "), "/home/me/repo", None).title(),
        "/home/me/repo"
    );
    assert_eq!(session(None, "/r", None).place(), "claude");
    assert_eq!(
        session(None, "/r", Some("Workbox Test")).place(),
        "claude · Workbox Test"
    );
}
