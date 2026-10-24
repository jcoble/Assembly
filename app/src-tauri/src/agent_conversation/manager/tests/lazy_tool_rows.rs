use super::*;
use super::super::tool_call::{diff_line_counts, read_tool_call};

/// The same diff `conversationTurnStream.test.ts` counts: a `---` line after the hunk is an added
/// or removed line, not a file name.
const EDIT_DIFF: &str = "--- a/notes.txt\n+++ b/notes.txt\n@@ -1,2 +1,3 @@\n-old line\n+new line\n+--- a removed-looking added line\n keep\n";

fn tool(item_id: &str, state: ToolState, summary: Option<&str>, output: Option<&str>, path: Option<&str>, diff: Option<&str>) -> AgentConversationPayload {
    let text = |value: Option<&str>| value.map(str::to_owned);
    AgentConversationPayload::Tool { item_id: item_id.into(), name: "Bash".into(), state, summary: text(summary), output: text(output),
        path: text(path), diff: text(diff), has_detail: false, added: None, removed: None }
}

fn calls() -> Vec<AgentConversationPayload> {
    // Imported history keeps a call's output in a later event with no name or summary.
    let mut output_only = tool("read", ToolState::Completed, None, Some("# Notes\nbody"), None, None);
    if let AgentConversationPayload::Tool { name, .. } = &mut output_only { name.clear(); }
    vec![
        tool("run", ToolState::Started, Some("seq 3"), Some("1\n"), None, None),
        tool("run", ToolState::Completed, Some("seq 3"), Some("1\n2\n3\n"), None, None),
        tool("grep", ToolState::Completed, None, Some("a.ts:1 found\nb.ts:2 found"), None, None),
        tool("edit", ToolState::Completed, None, None, Some("notes.txt"), Some(EDIT_DIFF)),
        tool("empty", ToolState::Completed, Some("nothing"), None, None, None),
        tool("read", ToolState::Completed, Some("notes.md"), None, None, None),
        output_only,
    ]
}

/// The key a remote machine's session is saved under on this Mac (`RemoteHistory::key`).
fn remote_key(profile: &str, owned_id: &str) -> String {
    serde_json::to_string(&(profile, owned_id)).unwrap()
}

/// Saves `payloads` after the newest event of `owned_id`, as the journal or a remote session's saved copy keeps them.
fn save(store: &SessionStore, owned_id: &str, kind: &str, payloads: Vec<AgentConversationPayload>) {
    let first = store.latest_seq(owned_id).unwrap() + 1;
    for (offset, payload) in payloads.into_iter().enumerate() {
        let event = AgentConversationEvent { runtime_state: None, owned_id: owned_id.into(), provider: AgentConversationProvider::Codex,
            generation: 1, sequence: first + offset as i64, timestamp_ms: 1, turn_id: Some("turn".into()), payload };
        store.append_event(&EventRow { owned_id: owned_id.into(), seq: event.sequence, turn_id: event.turn_id.clone(),
            kind: kind.into(), payload_json: serde_json::to_string(&event).unwrap(), created_at_ms: 1 }).unwrap();
    }
}

/// Each tool row a page carries, as (item, summary, has detail, added, removed), after checking it has no output or diff.
fn closed_rows(page: &AgentConversationItemPage) -> Vec<(String, Option<String>, bool, Option<u32>, Option<u32>)> {
    let mut rows: Vec<(String, Option<String>, bool, Option<u32>, Option<u32>)> = Vec::new();
    for event in &page.events {
        if let AgentConversationPayload::Tool { item_id, summary, output, diff, has_detail, added, removed, .. } = &event.payload {
            assert_eq!((output, diff), (&None, &None), "{item_id} must carry no output or diff");
            rows.retain(|row| &row.0 != item_id);
            rows.push((item_id.clone(), summary.clone(), *has_detail, *added, *removed));
        }
    }
    rows
}

fn expected_rows() -> Vec<(String, Option<String>, bool, Option<u32>, Option<u32>)> {
    vec![
        ("run".into(), Some("seq 3".into()), true, None, None),
        // A call with no summary keeps its output's first line as the one line its closed row shows.
        ("grep".into(), Some("a.ts:1 found".into()), true, None, None),
        ("edit".into(), None, true, Some(2), Some(1)),
        ("empty".into(), Some("nothing".into()), false, None, None),
        // The output-only event adds no summary, so the row keeps showing its call's "notes.md".
        ("read".into(), None, true, None, None),
    ]
}

/// A local session with the tool calls saved, a sub-agent of it with the same calls, and a remote
/// session's saved copy with them too. Returns the manager, root folder and the three ids.
fn sessions() -> (AgentRuntimeManager, std::path::PathBuf, String, String, String) {
    let root = temp_root();
    let manager = AgentRuntimeManager::new(ProviderRegistry::default());
    let parent = manager.ensure_inner(request(root.to_str().unwrap(), "lazy-parent", AgentConversationProvider::Codex)).unwrap().0.owned_id;
    let mut row = manager.store.get_session(&parent).unwrap().unwrap();
    row.native_session_id = Some("native-lazy-parent".into());
    manager.store.upsert_session(&row).unwrap();
    let child = super::super::super::history::ensure_child_import(&manager.store, &parent, None, "lazy-child").unwrap();
    let remote = remote_key("workbox-test", "remote-session");
    row.owned_id = remote.clone();
    manager.store.upsert_session(&row).unwrap();
    save(&manager.store, &parent, "item.updated", calls());
    save(&manager.store, &child, "item.updated", calls());
    save(&manager.store, &remote, "remote.cached", calls());
    (manager, root, parent, child, remote)
}

#[test]
fn diff_line_counts_match_the_frontend_rules() {
    assert_eq!(diff_line_counts(EDIT_DIFF), (2, 1));
    assert_eq!(diff_line_counts(""), (0, 0));
}

#[test]
fn history_pages_carry_tool_rows_without_output_or_diff() {
    let (manager, root, parent, child, _) = sessions();
    let page = manager.list_items_before(&parent, i64::MAX, 1 << 20).unwrap();
    assert_eq!(closed_rows(&page), expected_rows());
    // A sub-agent's transcript is paged the same way.
    let child_page = manager.list_items_before(&child, i64::MAX, 1 << 20).unwrap();
    assert_eq!(closed_rows(&child_page), expected_rows());
    // The saved rows keep everything.
    let saved = manager.store.list_events(&parent, 0, 100).unwrap();
    assert!(saved.iter().any(|row| row.payload_json.contains("1\\n2\\n3\\n")));
    fs::remove_dir_all(root).unwrap();
}

#[test]
fn an_opened_row_reads_the_whole_call_for_local_sub_agent_and_remote_sessions() {
    let (manager, root, parent, child, remote) = sessions();
    for owned_id in [&parent, &child, &remote] {
        let run = read_tool_call(&manager.store, owned_id, "run").unwrap();
        assert_eq!((run.summary.as_deref(), run.output.as_deref(), run.diff), (Some("seq 3"), Some("1\n2\n3\n"), None), "{owned_id}");
        let edit = read_tool_call(&manager.store, owned_id, "edit").unwrap();
        assert_eq!((edit.path.as_deref(), edit.diff.as_deref()), (Some("notes.txt"), Some(EDIT_DIFF)), "{owned_id}");
        let read = read_tool_call(&manager.store, owned_id, "read").unwrap();
        assert_eq!((read.summary.as_deref(), read.output.as_deref()), (Some("notes.md"), Some("# Notes\nbody")), "{owned_id}");
    }
    assert_eq!(read_tool_call(&manager.store, &remote_key("workbox-test", "never-saved"), "run").unwrap_err(),
        "This part of the conversation is not saved on this Mac yet");
    fs::remove_dir_all(root).unwrap();
}
