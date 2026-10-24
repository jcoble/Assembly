use super::*;

/// codex-acp 2.x no longer sends `rawOutput.formatted_output` or text
/// content for a command. The output arrives only in the terminal meta
/// (frame captured from codex-acp 2.2.2 with `terminal_output_delta`
/// advertised); without it the command row opens onto its input alone.
#[test]
fn a_codex_acp_2_command_update_carries_its_terminal_output() {
    let update = json!({ "update": {
        "sessionUpdate": "tool_call_update",
        "toolCallId": "exec-f67d7a20",
        "status": "completed",
        "_meta": {
            "terminal_output_delta": {
                "data": "Wed Oct 21 18:57:16 EDT 2026\nhello-agent-workbox\n",
                "terminal_id": "exec-f67d7a20"
            },
            "terminal_exit": { "exit_code": 0, "signal": null, "terminal_id": "exec-f67d7a20" }
        }
    } });
    match payload_from_session_update_for_turn(&update, None) {
        Some(AgentConversationPayload::Tool { output, .. }) => {
            assert_eq!(output.as_deref(), Some("Wed Oct 21 18:57:16 EDT 2026\nhello-agent-workbox\n"));
        }
        other => panic!("expected Tool, got {other:?}"),
    }
}

/// A slow command arrives as several chunks, then a finishing frame with only
/// `terminal_exit` (frames captured from codex-acp 2.2.2 running
/// `for i in 1 2 3; do echo line-$i; sleep 1; done`).
#[test]
fn codex_terminal_output_chunks_join_into_the_stored_tool_output() {
    let root = temp_root();
    let manager = AgentRuntimeManager::default();
    let connection = manager.ensure_inner(request(
        root.to_str().unwrap(), "owned-terminal-chunks", AgentConversationProvider::Codex,
    )).unwrap().0;
    let mut sessions = manager.sessions.lock().unwrap();
    let session = current_session_mut(&mut sessions, "owned-terminal-chunks", connection.generation).unwrap();
    let chunk = |data: &str| json!({ "update": {
        "sessionUpdate": "tool_call_update", "toolCallId": "exec-1",
        "_meta": { "terminal_output_delta": { "data": data, "terminal_id": "exec-1" } }
    } });
    let frames = [
        json!({ "update": {
            "sessionUpdate": "tool_call", "toolCallId": "exec-1", "kind": "execute",
            "status": "in_progress", "title": "for i in 1 2 3; do echo line-$i; sleep 1; done",
            "rawInput": { "command": "for i in 1 2 3; do echo line-$i; sleep 1; done" }
        } }),
        chunk("line-2\n"),
        chunk("line-3\n"),
        json!({ "update": {
            "sessionUpdate": "tool_call_update", "toolCallId": "exec-1", "status": "completed",
            "_meta": { "terminal_exit": { "exit_code": 0, "signal": null, "terminal_id": "exec-1" } }
        } }),
        // A second command whose finishing frame carries its last chunk.
        chunk("a\n"),
        json!({ "update": {
            "sessionUpdate": "tool_call_update", "toolCallId": "exec-1", "status": "completed",
            "_meta": { "terminal_output_delta": { "data": "b\n", "terminal_id": "exec-1" } }
        } }),
    ];
    let mut outputs = Vec::new();
    for params in frames {
        let mut payload = payload_from_session_update_for_turn(&params, Some("turn-1")).unwrap();
        // The inbound pump's order: a finishing update drops its live call before its chunk joins.
        let prior = tools::joined_output_so_far(&session.live_tool_calls, &params);
        update_raw_liveness(session, &params);
        tools::join_terminal_output(&mut session.live_tool_calls, prior, &params, &mut payload);
        match record_payload_for_session(session, payload).unwrap().payload {
            AgentConversationPayload::Tool { output, .. } => outputs.push(output),
            other => panic!("expected Tool, got {other:?}"),
        }
    }
    assert_eq!(outputs, [
        None,
        Some("line-2\n".to_string()),
        Some("line-2\nline-3\n".to_string()),
        Some("line-2\nline-3\n".to_string()),
        Some("a\n".to_string()),
        Some("a\nb\n".to_string()),
    ]);
    assert!(session.live_tool_calls.is_empty(), "a finished call keeps no output");
    drop(sessions);
    fs::remove_dir_all(root).unwrap();
}

/// A long stream keeps only its newest output, cut on a character boundary.
#[test]
fn joined_terminal_output_keeps_the_newest_output_within_the_cap() {
    let mut live_tool_calls = HashMap::new();
    let chunk = |data: String| json!({ "update": {
        "sessionUpdate": "tool_call_update", "toolCallId": "exec-1",
        "_meta": { "terminal_output_delta": { "data": data, "terminal_id": "exec-1" } }
    } });
    let mut output = None;
    for data in ["é".repeat(20_000), "tail\n".to_string()] {
        let params = chunk(data);
        let mut payload = payload_from_session_update_for_turn(&params, Some("turn-1")).unwrap();
        tools::join_terminal_output(&mut live_tool_calls, None, &params, &mut payload);
        let AgentConversationPayload::Tool { output: joined, .. } = payload else { panic!("expected Tool") };
        output = joined;
    }
    let output = output.unwrap();
    let cap = crate::agent_conversation::phone::MAX_TOOL_OUTPUT_BYTES;
    assert!(output.len() <= cap && output.len() >= cap - 1, "{}", output.len());
    assert!(output.starts_with('é') && output.ends_with("étail\n"));
}
