use serde_json::json;

use super::create_args;

// Pins: g0-session#31
#[test]
fn a_new_session_needs_an_agent_and_a_folder_and_names_its_machine() {
    assert_eq!(create_args(None, "/repo", None), None);
    assert_eq!(create_args(Some("claude"), "  ", None), None);
    assert_eq!(
        create_args(Some("claude"), " /repo ", None),
        Some(json!({ "agent": "claude", "repoUrl": "/repo" }))
    );
    assert_eq!(
        create_args(Some("codex"), "/repo", Some("Workbox Test")),
        Some(json!({ "agent": "codex", "repoUrl": "/repo", "machine": "Workbox Test" }))
    );
}
