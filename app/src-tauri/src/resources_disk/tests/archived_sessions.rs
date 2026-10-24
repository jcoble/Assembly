use super::*;
use std::process::Command;

#[test]
fn app_stores_include_archived_codex_sessions() {
    if std::env::var_os("MCB_DISK_FIXTURE_CHILD").is_some() {
        let section = measure_app_store_section(None);
        let entries = section.entries;
        assert_eq!(entries.len(), 3, "all transcript stores must be measured");
        for (relative, bytes) in [(".claude/projects", 7), (".codex/sessions", 11), (".codex/archived_sessions", 23)] {
            let path = PathBuf::from(std::env::var_os("HOME").unwrap()).join(relative);
            let entry = entries.iter().find(|entry| entry.path == path.to_string_lossy()).expect(relative);
            assert_eq!(entry.bytes, bytes);
            assert_eq!(entry.category, DiskUsageCategory::TranscriptStore);
            assert!(!entry.reclaimable);
        }
        assert_eq!(section.bytes, 41);
        return;
    }
    let home = fixture_root("archived-home");
    write(&home.join(".claude/projects/session.jsonl"), 7);
    write(&home.join(".codex/sessions/active.jsonl"), 11);
    write(&home.join(".codex/archived_sessions/archived.jsonl"), 23);
    let output = Command::new(std::env::current_exe().unwrap())
        .args(["--exact", "resources_disk::tests::archived_sessions::app_stores_include_archived_codex_sessions", "--nocapture"])
        .env("HOME", &home)
        .env("MCB_DISK_FIXTURE_CHILD", "1")
        .output().expect("fixture subprocess must start");
    fs::remove_dir_all(home).expect("fixture home must be removed");
    assert!(output.status.success(), "fixture failed: {}{}", String::from_utf8_lossy(&output.stdout), String::from_utf8_lossy(&output.stderr));
    assert!(String::from_utf8_lossy(&output.stdout).contains("1 passed"), "fixture test must run: {}", String::from_utf8_lossy(&output.stdout));
}
