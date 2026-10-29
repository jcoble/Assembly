//! `Fold` is a finished turn's work folded under one "Worked for" heading above its final reply. The prompt, the
//! final reply, errors and notes stay out, and so does anything still waiting on the user or still running. Ported
//! from session/fold.ts (turnFolds, workSummary, toolTitle).

use std::collections::{HashMap, HashSet};

use super::item::{Item, ItemKind};
use crate::features::session::rows::tool::ToolState;
use crate::features::session::rows::turn_stats::TurnStats;

#[derive(Clone, Debug, PartialEq)]
pub struct Fold {
    pub run_id: String,
    /// The keys of the items the heading hides while closed.
    pub work: HashSet<String>,
    /// The final reply's key: its copy button and time go under it.
    pub reply: Option<String>,
    /// "Edited 2 files, ran 3 commands".
    pub summary: String,
    pub duration_ms: Option<i64>,
}

impl Fold {
    /// Each finished run's fold, by run id. A run is finished once its stats are in and it isn't the one running.
    pub fn all(
        items: &[Item],
        stats: &HashMap<String, TurnStats>,
        active_run: Option<&str>,
    ) -> HashMap<String, Fold> {
        let mut by_run: HashMap<&str, Vec<&Item>> = HashMap::new();
        for item in items {
            let Some(run) = item.run_id.as_deref() else {
                continue;
            };
            if Some(run) != active_run && stats.contains_key(run) {
                by_run.entry(run).or_default().push(item);
            }
        }
        let mut folds = HashMap::new();
        for (run, run_items) in by_run {
            let reply = run_items
                .iter()
                .rev()
                .find(|item| matches!(item.kind, ItemKind::Message { .. }))
                .map(|item| item.key.clone());
            let work: Vec<&Item> = run_items
                .into_iter()
                .filter(|item| is_work(item) && Some(&item.key) != reply.as_ref())
                .collect();
            if work.is_empty() && reply.is_none() {
                continue;
            }
            let fold = Fold {
                run_id: run.to_string(),
                work: work.iter().map(|item| item.key.clone()).collect(),
                reply,
                summary: if work.is_empty() {
                    String::new()
                } else {
                    Fold::summary(&work)
                },
                duration_ms: if work.is_empty() {
                    None
                } else {
                    stats[run].duration_ms
                },
            };
            folds.insert(run.to_string(), fold);
        }
        folds
    }

    /// What the hidden work did, by ACP tool kind, with the Assembly helper tools in words.
    pub fn summary(work: &[&Item]) -> String {
        let mut edited = HashSet::new();
        let (mut edits, mut reads, mut commands, mut searches, mut fetches, mut others) =
            (0, 0, 0, 0, 0, 0);
        let (mut failed, mut thinking, mut subagents) = (0, 0, 0);
        let mut helpers = [0; HELPERS.len()];
        for item in work {
            let tool = match &item.kind {
                ItemKind::Thinking { .. } => {
                    thinking += 1;
                    continue;
                }
                ItemKind::Subagent { subagent, .. } => {
                    if !subagent.helper {
                        subagents += 1;
                    }
                    continue;
                }
                ItemKind::Tool { tool, .. } => tool,
                _ => continue,
            };
            // A failed call still counts as what it was (a stopped command is still a command run).
            if tool.state == ToolState::Failed {
                failed += 1;
            }
            if let Some(helper) = helper_of(&tool.name) {
                helpers[helper] += 1;
                continue;
            }
            match tool.kind.as_deref() {
                Some("edit" | "delete" | "move") => {
                    let new_path = tool
                        .path
                        .as_ref()
                        .is_none_or(|path| edited.insert(path.clone()));
                    if new_path {
                        edits += 1;
                    }
                }
                Some("read") => reads += 1,
                Some("execute") => commands += 1,
                Some("search") => searches += 1,
                Some("fetch") => fetches += 1,
                Some("think") => {}
                _ => others += 1,
            }
        }
        let [started, sent, checked, listed] = helpers;
        let parts: Vec<String> = [
            counted(edits, "edited a file", "edited {} files"),
            counted(reads, "read a file", "read {} files"),
            counted(commands, "ran a command", "ran {} commands"),
            counted(searches, "searched once", "searched {} times"),
            counted(fetches, "fetched a page", "fetched {} pages"),
            counted(others, "used a tool", "used {} tools"),
            counted(subagents, "ran a subagent", "ran {} subagents"),
            counted(started, "started a session", "started {} sessions"),
            counted(sent, "sent a message", "sent {} messages"),
            counted(
                checked,
                "checked a session's answer",
                "checked answers {} times",
            ),
            counted(listed, "listed sessions", "listed sessions {} times"),
            counted(failed, "1 call failed", "{} calls failed"),
        ]
        .into_iter()
        .flatten()
        .collect();
        if parts.is_empty() {
            return if thinking > 0 { "Thought" } else { "Worked" }.to_string();
        }
        let line = parts.join(", ");
        let mut chars = line.chars();
        chars
            .next()
            .map(|first| first.to_uppercase().chain(chars).collect())
            .unwrap_or_default()
    }

    /// A tool row's title: an Assembly helper tool's in words (`mcp__assembly__start_session` is "Started a
    /// helper"), any other's name.
    pub fn tool_title(name: &str) -> String {
        helper_of(name).map_or_else(|| name.to_string(), |helper| HELPERS[helper].1.to_string())
    }
}

/// The Assembly MCP server's tools, with their row titles: Claude Code calls them `mcp__assembly__<tool>`, Codex
/// `mcp.assembly.<tool>`. The summary counts them in this order.
const HELPERS: [(&str, &str); 4] = [
    ("start_session", "Started a session"),
    ("send_message", "Sent a message"),
    ("message_status", "Checked a session's answer"),
    ("list_sessions", "Listed sessions"),
];

fn helper_of(name: &str) -> Option<usize> {
    let tool = ["mcp__assembly__", "mcp.assembly."]
        .iter()
        .find_map(|prefix| name.strip_prefix(prefix))?;
    HELPERS.iter().position(|(helper, _)| *helper == tool)
}

fn is_work(item: &Item) -> bool {
    match &item.kind {
        ItemKind::Tool { .. } | ItemKind::Thinking { .. } | ItemKind::Message { .. } => true,
        ItemKind::Task { task, .. } => task.state != "running",
        ItemKind::Subagent { subagent, .. } => subagent.state == "idle",
        ItemKind::Ask { ask, .. } => ask.state != "open",
        _ => false,
    }
}

/// `one` for a count of one, `many` with the count in place of `{}`, nothing for none.
fn counted(count: usize, one: &str, many: &str) -> Option<String> {
    match count {
        0 => None,
        1 => Some(one.to_string()),
        _ => Some(many.replace("{}", &count.to_string())),
    }
}
