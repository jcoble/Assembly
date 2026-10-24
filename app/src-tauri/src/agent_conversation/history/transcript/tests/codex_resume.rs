use super::*;

fn root() -> PathBuf {
    let root = std::env::temp_dir().join(format!("tsk-1508-codex-history-{}", uuid::Uuid::new_v4()));
    fs::create_dir_all(&root).unwrap();
    root
}

#[test]
fn missing_codex_history_is_not_held() {
    let root = root();
    assert!(!codex::holds_session_at(&root, "missing").unwrap_or(true));
    fs::remove_dir_all(root).unwrap();
}

#[test]
fn unreadable_codex_history_location_is_held() {
    let root = root();
    fs::create_dir_all(root.join(".codex")).unwrap();
    fs::write(root.join(".codex/sessions"), "not a directory").unwrap();
    let held = codex::holds_session_at(&root, "unreadable");
    assert!(held.is_err(), "unreadable storage must not become missing");
    assert!(held.unwrap_or(true));
    fs::remove_dir_all(root).unwrap();
}

#[test]
fn existing_codex_history_with_a_turn_is_held() {
    let root = root();
    let archived = root.join(".codex/archived_sessions/2026/10/19");
    fs::create_dir_all(&archived).unwrap();
    fs::write(archived.join("rollout-existing.jsonl"),
        r#"{"type":"response_item","payload":{"type":"message","role":"user","content":[{"type":"input_text","text":"Keep my history"}]}}"#).unwrap();
    assert!(codex::holds_session_at(&root, "existing").unwrap_or(true));
    fs::remove_dir_all(root).unwrap();
}

#[cfg(unix)]
#[test]
fn valid_codex_history_is_found_beside_a_broken_sibling() {
    let root = root();
    let sessions = root.join(".codex/sessions");
    fs::create_dir_all(sessions.join("valid")).unwrap();
    std::os::unix::fs::symlink(root.join("absent"), sessions.join("broken")).unwrap();
    fs::write(sessions.join("valid/rollout-existing.jsonl"), "{\"type\":\"response_item\"}\n").unwrap();
    assert!(codex::holds_session_at(&root, "existing")
        .expect("a broken sibling must not hide readable native history"));
    fs::remove_dir_all(root).unwrap();
}
