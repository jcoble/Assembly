use super::*;

#[test]
fn claude_jsonl_usage_records_become_non_zero_events() {
    let lines = vec![r#"{"type":"assistant","uuid":"assistant-1","sessionId":"session-1","timestamp":"2026-08-08T12:34:56Z","cwd":"/tmp/project/workspace","message":{"id":"msg-1","model":"model-a","usage":{"input_tokens":12,"output_tokens":7,"cache_read_input_tokens":3,"cache_creation_input_tokens":2}}}"#.to_string()];
    let events = parse_usage_events_for_source_with_context(
        &lines,
        "claude",
        "local",
        "claude-jsonl",
        "/tmp/session.jsonl",
        &UsageSourceContext::default(),
    );
    assert_eq!(events.len(), 1);
    assert_eq!(events[0].input_tokens, 12);
    assert_eq!(events[0].output_tokens, 7);
    assert_eq!(events[0].cache_read_tokens, 3);
    assert_eq!(events[0].cache_write_tokens, 2);
    assert_eq!(events[0].project_id.as_deref(), Some("workspace"));
    assert_eq!(
        events[0].workspace_id.as_deref(),
        Some("/tmp/project/workspace")
    );
}

fn claude_line(uuid: &str, message_id: Option<&str>, output_tokens: u64) -> String {
    let id = message_id.map_or(String::new(), |id| format!(r#""id":"{id}","#));
    format!(
        r#"{{"type":"assistant","uuid":"{uuid}","sessionId":"session-1","timestamp":"2026-08-08T12:34:56Z","message":{{{id}"model":"model-a","usage":{{"input_tokens":2,"output_tokens":{output_tokens},"cache_read_input_tokens":31538,"cache_creation_input_tokens":40}}}}}}"#
    )
}

fn parse_claude(lines: &[String]) -> Vec<UsageEvent> {
    parse_usage_events_for_source_with_context(
        lines,
        "claude",
        "local",
        "claude-jsonl",
        "/tmp/session.jsonl",
        &UsageSourceContext::default(),
    )
}

#[test]
fn claude_usage_counts_a_message_split_over_lines_once() {
    let events = parse_claude(&[
        claude_line("line-1", Some("msg-1"), 16),
        claude_line("line-2", Some("msg-1"), 214),
    ]);
    assert_eq!(events.len(), 1);
    assert_eq!(events[0].source_event_id, "msg-1");
    assert_eq!(events[0].output_tokens, 214);
    assert_eq!(events[0].cache_read_tokens, 31538);
    assert_eq!(events[0].cache_write_tokens, 40);
}

#[test]
fn claude_usage_falls_back_to_uuid_without_message_id() {
    let events = parse_claude(&[
        claude_line("line-1", None, 16),
        claude_line("line-1", None, 16),
        claude_line("line-2", None, 214),
    ]);
    assert_eq!(events.len(), 2);
    assert_eq!(events[0].source_event_id, "line-1");
    assert_eq!(events[1].source_event_id, "line-2");
}

#[test]
fn claude_usage_dedupes_interleaved_messages_separately() {
    let events = parse_claude(&[
        claude_line("a-1", Some("msg-a"), 5),
        claude_line("b-1", Some("msg-b"), 7),
        claude_line("a-2", Some("msg-a"), 50),
        claude_line("b-2", Some("msg-b"), 70),
    ]);
    assert_eq!(events.len(), 2);
    assert_eq!(events[0].source_event_id, "msg-a");
    assert_eq!(events[0].output_tokens, 50);
    assert_eq!(events[1].source_event_id, "msg-b");
    assert_eq!(events[1].output_tokens, 70);
}

/// The last line is the transcript's final record of the message; taking
/// a per-field maximum would mix snapshots, so a lower later value wins.
#[test]
fn claude_usage_keeps_the_last_line_even_when_its_numbers_are_lower() {
    let events = parse_claude(&[
        claude_line("line-1", Some("msg-1"), 214),
        claude_line("line-2", Some("msg-1"), 16),
    ]);
    assert_eq!(events.len(), 1);
    assert_eq!(events[0].output_tokens, 16);
}
