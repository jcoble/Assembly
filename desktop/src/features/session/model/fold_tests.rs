//! A finished turn's fold and its summary, ported from app/test/fold.test.ts.

use std::collections::{HashMap, HashSet};

use serde_json::{Value, json};

use super::fold::Fold;
use super::item::{Item, ItemKind};
use crate::features::session::rows::markdown::{Block, Inline};
use crate::features::session::rows::turn_stats::TurnStats;

fn item(key: &str, kind: ItemKind) -> Item {
    Item {
        key: key.into(),
        run_id: Some("r1".into()),
        kind,
        at: 0,
    }
}

fn tool(key: &str, kind: &str, extra: Value) -> Item {
    let mut tool = json!({ "name": kind, "state": "completed", "summary": "", "hasDetail": false, "kind": kind });
    tool.as_object_mut()
        .unwrap()
        .extend(extra.as_object().unwrap().clone());
    item(
        key,
        ItemKind::Tool {
            item_id: key.into(),
            tool: serde_json::from_value(tool).unwrap(),
        },
    )
}

fn text(key: &str) -> Item {
    let blocks = vec![Block::Paragraph {
        parts: vec![Inline::Text { value: key.into() }],
    }];
    item(
        key,
        ItemKind::Message {
            blocks,
            done: true,
            at: 1,
        },
    )
}

fn stats() -> HashMap<String, TurnStats> {
    let stats = json!({ "runId": "r1", "status": "completed", "startedAt": null, "durationMs": 12_000, "firstTokenMs": null,
        "model": null, "inputTokens": null, "outputTokens": null, "cacheHitPct": null, "costUsd": null, "toolCalls": 0,
        "branch": null, "head": null, "prevBranch": null, "prevHead": null });
    HashMap::from([("r1".into(), serde_json::from_value(stats).unwrap())])
}

fn turn() -> Vec<Item> {
    vec![
        item(
            "u",
            ItemKind::User {
                text: "go".into(),
                at: 1,
                from: None,
            },
        ),
        item(
            "th",
            ItemKind::Thinking {
                text: "hm".into(),
                done: true,
            },
        ),
        text("before"),
        tool("t1", "edit", json!({ "path": "a.ts" })),
        tool("t2", "edit", json!({ "path": "a.ts" })),
        tool("t3", "execute", json!({})),
        item(
            "ask",
            ItemKind::Ask {
                ask_id: "1".into(),
                ask: serde_json::from_value(json!({ "kind": "permission", "state": "open" }))
                    .unwrap(),
            },
        ),
        item(
            "task",
            ItemKind::Task {
                task_id: "b1".into(),
                task: serde_json::from_value(
                    json!({ "name": "build", "state": "running", "canStop": false }),
                )
                .unwrap(),
            },
        ),
        text("reply"),
    ]
}

fn summary(work: &[Item]) -> String {
    Fold::summary(&work.iter().collect::<Vec<_>>())
}

// Pins: g0-session#9
#[test]
fn a_finished_turn_folds_its_work_not_its_prompt_reply_open_card_or_running_task() {
    let fold = &Fold::all(&turn(), &stats(), None)["r1"];
    let work: HashSet<String> = ["th", "before", "t1", "t2", "t3"].map(String::from).into();
    assert_eq!(fold.work, work);
    assert_eq!(fold.reply.as_deref(), Some("reply"));
    assert_eq!(fold.summary, "Edited a file, ran a command");
    assert_eq!(fold.duration_ms, Some(12_000));
}

// Pins: g0-session#9
#[test]
fn a_running_turn_or_one_without_stats_yet_never_folds() {
    assert!(Fold::all(&turn(), &stats(), Some("r1")).is_empty());
    assert!(Fold::all(&turn(), &HashMap::new(), None).is_empty());
}

#[test]
fn a_turn_that_is_only_its_reply_has_no_work_to_fold_but_its_reply_is_known() {
    let fold = &Fold::all(&[text("reply")], &stats(), None)["r1"];
    assert!(fold.work.is_empty());
    assert_eq!(fold.reply.as_deref(), Some("reply"));
}

// Pins: g0-session#9
#[test]
fn the_summary_counts_work_by_kind_distinct_files_and_failures() {
    let edits = [
        tool("a", "edit", json!({ "path": "x" })),
        tool("b", "edit", json!({ "path": "y" })),
        tool("c", "execute", json!({})),
        tool("d", "execute", json!({})),
        tool("e", "execute", json!({})),
    ];
    assert_eq!(summary(&edits), "Edited 2 files, ran 3 commands");
    let mixed = [
        tool("a", "read", json!({})),
        tool("b", "search", json!({})),
        tool("c", "execute", json!({ "state": "failed" })),
    ];
    assert_eq!(
        summary(&mixed),
        "Read a file, ran a command, searched once, 1 call failed"
    );
    // A command stopped mid-run reads failed; it still ran.
    assert_eq!(
        summary(&[tool("a", "execute", json!({ "state": "failed" }))]),
        "Ran a command, 1 call failed"
    );
    let subagent = serde_json::from_value(
        json!({ "sessionId": "c1", "title": "Explore", "state": "idle", "canStop": false }),
    )
    .unwrap();
    assert_eq!(
        summary(&[item(
            "s",
            ItemKind::Subagent {
                acp_session_id: "s1".into(),
                subagent
            }
        )]),
        "Ran a subagent"
    );
    assert_eq!(
        summary(&[item(
            "t",
            ItemKind::Thinking {
                text: String::new(),
                done: true
            }
        )]),
        "Thought"
    );
    assert_eq!(
        summary(&[tool("a", "other", json!({})), tool("b", "other", json!({}))]),
        "Used 2 tools"
    );
}

#[test]
fn a_started_session_is_not_counted_as_a_provider_subagent() {
    let mut payload = json!({ "sessionId": "c1", "title": "Review", "state": "idle", "canStop": false, "helper": true });
    let started = item(
        "s",
        ItemKind::Subagent {
            acp_session_id: "s1".into(),
            subagent: serde_json::from_value(payload.clone()).unwrap(),
        },
    );
    assert_eq!(summary(&[started.clone()]), "Worked");
    payload.as_object_mut().unwrap().remove("helper");
    let provider = item(
        "p",
        ItemKind::Subagent {
            acp_session_id: "p1".into(),
            subagent: serde_json::from_value(payload).unwrap(),
        },
    );
    assert_eq!(summary(&[started, provider]), "Ran a subagent");
}

#[test]
fn the_assembly_mcp_server_tools_read_as_words_in_the_row_and_the_summary() {
    assert_eq!(
        Fold::tool_title("mcp__assembly__start_session"),
        "Started a session"
    );
    assert_eq!(
        Fold::tool_title("mcp.assembly.message_status"),
        "Checked a session's answer"
    );
    assert_eq!(
        Fold::tool_title("mcp__other__start_session"),
        "mcp__other__start_session"
    );
    let calls = [
        tool(
            "a",
            "other",
            json!({ "name": "mcp__assembly__start_session" }),
        ),
        tool(
            "b",
            "execute",
            json!({ "name": "mcp.assembly.message_status" }),
        ),
        tool(
            "c",
            "execute",
            json!({ "name": "mcp.assembly.message_status" }),
        ),
        tool(
            "d",
            "execute",
            json!({ "name": "mcp.assembly.list_sessions" }),
        ),
        tool("e", "execute", json!({})),
    ];
    assert_eq!(
        summary(&calls),
        "Ran a command, started a session, checked answers 2 times, listed sessions"
    );
}
