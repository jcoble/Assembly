use super::*;

// Expected values come from TanStack AI's own `canonicalInterruptJson` and
// `defaultInterruptHash` (ai/src/interrupt-serialization.ts at 1318bbc), run
// with Node on the same JSON text.
#[test]
fn the_response_schema_hash_matches_tanstacks_own() {
    let approval: Value = serde_json::from_str(
        r#"{"type":"object","properties":{"decision":{"type":"string","enum":["accept","decline"]}},"required":["decision"]}"#,
    ).unwrap();
    assert_eq!(
        response_schema_hash(&approval),
        "sha256:a295546c82ccf07af5c4c0cc933f99d2c672dab5492ca8160d4ad0dadffc231e"
    );
    // Non-integers, numbers past 2^53 and 1e21, string escapes, and keys whose
    // UTF-16 order differs from their UTF-8 order.
    let awkward: Value = serde_json::from_str(
        r#"{"zeta":{"enum":[0.1,1e21,12345678901234567890,-0.5,1.5e-7,100,2.5e-6,-0]},"alpha":"tab\tquote\"nl\nctl\u0001","｡":1,"😀":2,"é":[true,false,null],"10":"ten","9":"nine"}"#,
    ).unwrap();
    assert_eq!(
        canonical_json(&awkward),
        r#"{"10":"ten","9":"nine","alpha":"tab\tquote\"nl\nctl\u0001","zeta":{"enum":[0.1,1e+21,12345678901234567000,-0.5,1.5e-7,100,0.0000025,0]},"é":[true,false,null],"😀":2,"｡":1}"#
    );
    assert_eq!(
        response_schema_hash(&awkward),
        "sha256:246bd1c2df38ea3be4d009d69136f1920d14dcbc8b6890271fca5da09c2e3172"
    );
    // serde_json alone would print 100.0 and 1e16 here; JavaScript prints 100 and the digits.
    let floats: Value = serde_json::from_str(
        r#"{"type":"object","properties":{"n":{"type":"number","enum":[100.0,2.5,1e16,1.5e20,9007199254740993,1e21,0.000001,1e-7]}}}"#,
    ).unwrap();
    assert_eq!(
        canonical_json(&floats),
        r#"{"properties":{"n":{"enum":[100,2.5,10000000000000000,150000000000000000000,9007199254740992,1e+21,0.000001,1e-7],"type":"number"}},"type":"object"}"#
    );
    assert_eq!(
        response_schema_hash(&floats),
        "sha256:f4cbf6d01a24d391833b05189f76d12867520938a6c6a89a17a9208e540f2559"
    );
}

#[test]
fn numbers_are_written_the_way_javascript_writes_them() {
    for (number, text) in [
        (1.0, "1"), (-2.0, "-2"), (0.5, "0.5"), (123.456, "123.456"), (1e20, "100000000000000000000"),
        (1e21, "1e+21"), (1.5e300, "1.5e+300"), (0.000001, "0.000001"), (1e-7, "1e-7"), (-1.25e-9, "-1.25e-9"),
    ] {
        assert_eq!(js_number(number), text, "{number}");
    }
}

#[test]
fn a_bound_interrupt_carries_the_binding_object_tanstack_reads() {
    let schema = json!({ "type": "object" });
    let interrupt = bound_interrupt("perm-1", "permission", "Write a file", &schema, "run-7", 3, Map::new());
    assert_eq!(interrupt["metadata"]["tanstack:interruptBinding"], json!({
        "v": 1, "kind": "generic", "interruptId": "perm-1", "interruptedRunId": "run-7",
        "generation": 3, "responseSchemaHash": response_schema_hash(&schema),
    }));
    assert_eq!(interrupt["responseSchema"], schema);
}

#[test]
fn a_run_without_a_parent_has_no_parent_field_at_all() {
    let first = run_started("thread", "run-1", None);
    assert!(first.get("parentRunId").is_none(), "{first}");
    assert_eq!(run_started("thread", "run-2", Some("run-1"))["parentRunId"], "run-1");
}

#[test]
fn run_input_reads_resume_answers_and_ignores_what_assembly_does_not_use() {
    let input: RunAgentInput = serde_json::from_value(json!({
        "threadId": "s", "runId": "run-2", "parentRunId": "run-1", "messages": [], "tools": [],
        "forwardedProps": { "x": 1 },
        "resume": [{ "interruptId": "perm-1", "status": "resolved", "payload": { "decision": "accept" } }],
    })).unwrap();
    assert_eq!(input.parent_run_id.as_deref(), Some("run-1"));
    assert_eq!(input.resume[0].status, ResumeStatus::Resolved);
    assert_eq!(input.resume[0].payload["decision"], "accept");
    let fresh: RunAgentInput = serde_json::from_value(json!({ "threadId": "s", "runId": "run-3", "messages": [] })).unwrap();
    assert!(fresh.parent_run_id.is_none() && fresh.resume.is_empty());
}

#[test]
fn a_page_keeps_the_native_paging_fields_and_carries_its_events_as_chunks() {
    let page: AgentConversationItemPage = serde_json::from_value(json!({
        "items": [{ "stableId": "s-1", "itemId": "user-1", "selectionMode": "whole", "firstSequence": 4, "firstTimestampMs": 50,
            "lastSequence": 4, "authoritySeq": null, "turnId": "turn-1", "completed": true, "prefixComplete": true,
            "positionKnown": true, "requiredBytes": 30 }],
        "events": [{ "ownedId": "s", "provider": "claude", "generation": 1, "sequence": 4, "timestampMs": 50,
            "payload": { "kind": "userMessage", "itemId": "user-1", "text": "hi", "completed": true } }],
        "turns": [{ "turnId": "turn-1", "startedAtMs": 50, "endedAtMs": null, "terminalState": null, "finalAssistantItemId": null }],
        "beforeCursor": 4, "afterCursor": 4, "hasBefore": true, "hasEarlierTranscript": false, "hasAfter": true,
        "watermark": 9, "transferBytes": 30, "oversized": false, "coverage": { "low": 4, "high": 9, "startComplete": false },
    })).unwrap();
    let wire = serde_json::to_value(AguiPage::from(page)).unwrap();
    for key in ["items", "turns", "beforeCursor", "afterCursor", "hasBefore", "hasEarlierTranscript", "hasAfter", "watermark", "coverage"] {
        assert!(wire.get(key).is_some_and(|value| !value.is_null()), "{key} in {wire}");
    }
    assert_eq!(wire["events"], json!([]), "the events travel once, as chunks");
    assert_eq!(wire["chunks"][0]["type"], "TEXT_MESSAGE_START");
    assert_eq!(wire["chunks"][1]["delta"], "hi");
}

#[test]
fn run_input_carries_the_message_the_run_sends() {
    let input: RunAgentInput = serde_json::from_value(json!({
        "threadId": "s", "runId": "run-1", "messageId": "client-1",
        "message": { "ownedId": "s", "generation": 2, "text": "hello", "attachmentIds": ["shot-1"] },
    })).unwrap();
    let message = input.message.unwrap();
    assert_eq!((message.text.as_str(), message.generation, input.message_id.as_deref()), ("hello", 2, Some("client-1")));
    assert_eq!(message.attachment_ids, ["shot-1"]);
}
