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
    assert_eq!(fold.summary, "ran a command · edited a file");
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
    assert_eq!(summary(&edits), "ran 3 commands · edited 2 files");
    let mixed = [
        tool("a", "read", json!({})),
        tool("b", "search", json!({})),
        tool("c", "execute", json!({ "state": "failed" })),
    ];
    assert_eq!(
        summary(&mixed),
        "ran a command · read a file · searched once · 1 call failed"
    );
    // A command stopped mid-run reads failed; it still ran.
    assert_eq!(
        summary(&[tool("a", "execute", json!({ "state": "failed" }))]),
        "ran a command · 1 call failed"
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
        "ran a subagent"
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
        "used 2 tools"
    );
}

// Pins: g0-session#9
#[test]
fn a_denied_command_is_not_counted_as_run_or_failed() {
    let answered = |option: &str| {
        let ask = json!({ "kind": "permission", "state": "answered", "toolCallId": "b",
            "options": [{ "optionId": "yes", "name": "Yes", "kind": "allow_once" },
                        { "optionId": "no", "name": "No", "kind": "reject_once" }],
            "answer": { "optionId": option } });
        item(
            "ask",
            ItemKind::Ask {
                ask_id: "1".into(),
                ask: serde_json::from_value(ask).unwrap(),
            },
        )
    };
    let calls = |option| {
        [
            tool("a", "execute", json!({})),
            tool("b", "execute", json!({ "state": "failed" })),
            answered(option),
        ]
    };
    assert_eq!(summary(&calls("no")), "ran a command");
    assert_eq!(
        summary(&calls("yes")),
        "ran 2 commands · 1 call failed",
        "an allowed call that failed still counts"
    );
}

// Pins: g0-session#13
#[test]
fn an_assembly_helper_tool_keeps_its_words() {
    for kind in [None, Some("execute"), Some("other")] {
        assert_eq!(
            Fold::tool_words("mcp__assembly__start_session", kind, None),
            ("Started a helper".to_string(), None)
        );
    }
    assert_eq!(
        Fold::tool_words("mcp.assembly.message_status", Some("execute"), Some("s1")),
        (
            "Checked a helper's answer".to_string(),
            Some("s1".to_string())
        )
    );
    assert_eq!(
        Fold::tool_words("mcp__other__start_session", None, None),
        ("mcp__other__start_session".to_string(), None)
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
        "ran a command · started a helper · checked answers 2 times · listed sessions"
    );
}

// Pins: g0-session#13
#[test]
fn a_tool_is_titled_by_its_kind() {
    let words = |kind, summary| Fold::tool_words("Bash", kind, summary);
    let titled = |title: &str| (title.to_string(), None);
    assert_eq!(words(Some("execute"), None), titled("Ran"));
    for kind in ["edit", "delete", "move"] {
        assert_eq!(words(Some(kind), None), titled("Edited"), "{kind}");
    }
    assert_eq!(words(Some("read"), None), titled("Read"));
    assert_eq!(words(Some("search"), None), titled("Searched"));
    assert_eq!(words(Some("fetch"), None), titled("Fetched"));
    for kind in [Some("think"), Some("other"), None] {
        assert_eq!(words(kind, None), ("Bash".to_string(), None), "{kind:?}");
    }
    assert_eq!(
        words(Some("execute"), Some("cargo test")),
        ("Ran".to_string(), Some("cargo test".to_string()))
    );
    assert_eq!(
        words(Some("other"), Some("x")),
        ("Bash".to_string(), Some("x".to_string()))
    );
}

// Pins: g0-session#13
#[test]
fn a_multi_line_command_shows_its_first_line() {
    let heredoc = "cat >> src/calc.py <<'EOF'\ndef div(a, b):\n    return a / b\nEOF\n";
    assert_eq!(
        Fold::tool_words("Bash", Some("execute"), Some(heredoc)),
        (
            "Ran".to_string(),
            Some("cat >> src/calc.py <<'EOF'…".to_string())
        )
    );
    assert_eq!(
        Fold::tool_words("Bash", Some("execute"), Some("ls -la\n")),
        ("Ran".to_string(), Some("ls -la".to_string())),
        "a line ending is not more to show"
    );
}

// Pins: g0-session#10
#[test]
fn a_turn_with_no_finished_reply_keeps_its_own_line() {
    let reply_done = |items: &[Item]| Fold::all(items, &stats(), None)["r1"].reply_done(items);
    let tool_only = [tool("t", "execute", json!({}))];
    assert!(
        !reply_done(&tool_only),
        "a tool-only turn has no reply to carry its line"
    );
    let mut unfinished = text("m");
    if let ItemKind::Message { done, .. } = &mut unfinished.kind {
        *done = false;
    }
    assert!(!reply_done(&[tool("t", "execute", json!({})), unfinished]));
    assert!(reply_done(&[tool("t", "execute", json!({})), text("m")]));
}
