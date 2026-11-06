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

#[test]
fn session_checkout_paths_keep_the_owning_machine_values() {
    let item: SessionItem = serde_json::from_value(json!({
        "sessionId":"remote-session", "agent":"codex", "repoUrl":"https://github.com/o/r",
        "machine":"Workbox Test", "updatedAt":1,
        "root":null, "checkoutRoot":"/home/test/worktrees/actual-checkout"
    }))
    .unwrap();
    assert_eq!(item.root, None);
    assert_eq!(
        item.checkout_root.as_deref(),
        Some("/home/test/worktrees/actual-checkout")
    );
    let local = session(None, "/repo", None);
    assert_eq!(local.root, None);
    assert_eq!(local.checkout_root, None);
}

#[test]
fn untitled_bare_sessions_never_show_the_owning_folder() {
    for machine in [None, Some("Workbox Test")] {
        let mut item = session(None, "/other-machine/home/assembly-bare", machine);
        item.is_bare = true;
        assert_eq!(item.title(), "Untitled session");
        item.title = Some("  ".into());
        assert_eq!(item.title(), "Untitled session");
        item.title = Some("Clean storage".into());
        assert_eq!(item.title(), "Clean storage");
    }
}
