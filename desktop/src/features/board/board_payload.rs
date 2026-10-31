//! The Board's wire records, as the backend names them
//! (dotnet/Assembly.Application/Features/Workflows/Contracts, camelCase by WorkflowJson.cs). Only the fields the
//! window reads or sends are here; the others are skipped.

use serde::{Deserialize, Serialize};

#[derive(Clone, Debug, Deserialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct BoardTask {
    pub machine_id: String,
    pub task_key: String,
    pub creator_session_id: String,
    pub project: String,
    pub title: String,
    pub summary: String,
    pub state: String,
    pub task_url: Option<String>,
    pub pr_url: Option<String>,
    pub started_at: i64,
    pub ended_at: Option<i64>,
}

#[derive(Clone, Debug, Deserialize, Serialize, PartialEq)]
pub struct BoardAnswerValue {
    pub kind: String,
    pub text: Option<String>,
}

#[derive(Clone, Debug, Deserialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct BoardQuestion {
    pub machine_id: String,
    pub task_key: String,
    pub question_key: String,
    pub generation: i32,
    pub kind: String,
    pub prompt: String,
    pub recommendation: Option<String>,
    pub answer: Option<BoardAnswerValue>,
    pub delivery_state: String,
}

#[derive(Clone, Debug, Deserialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct BoardEvent {
    pub kind: String,
    pub occurred_at: i64,
    pub task: BoardTask,
    pub questions: Vec<BoardQuestion>,
}

#[derive(Clone, Debug, Deserialize, PartialEq)]
pub struct BoardMetrics {
    pub files: Option<i64>,
}

#[derive(Clone, Debug, Deserialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct BoardSummary {
    pub task: BoardTask,
    pub attention_group: i32,
    pub question_count: i64,
    pub metrics: BoardMetrics,
}

#[derive(Clone, Debug, Deserialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct BoardCounts {
    pub total: i64,
    pub running: i64,
    pub needs_you: i64,
    pub terminal: i64,
}

#[derive(Debug, Deserialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct BoardPageResult {
    pub items: Vec<BoardSummary>,
    pub counts: BoardCounts,
    pub next_cursor: Option<String>,
}

#[derive(Debug, Deserialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct BoardTaskResult {
    #[serde(default)]
    pub steps: Vec<BoardStep>,
    pub questions: Vec<BoardQuestion>,
    pub feed: Vec<BoardEvent>,
    pub next_before_seq: Option<i64>,
    pub next_question_cursor: Option<String>,
}

#[derive(Clone, Debug, Deserialize, Serialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct BoardStep {
    pub index: i32,
    pub text: String,
    pub tool: Option<String>,
    pub detail: Option<String>,
    pub state: String,
    pub badge: Option<String>,
    pub explanation: Option<String>,
    pub progress: Option<i32>,
    pub started_at: Option<String>,
    pub ended_at: Option<String>,
    pub attempt: Option<i32>,
    pub max_attempts: Option<i32>,
    pub question_key: Option<String>,
    pub required_scope: Option<String>,
    pub scope_granted: Option<bool>,
}

/// The `board` topic's item: which card changed. It carries no page; the window reads one.
#[derive(Debug, Deserialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct BoardChange {
    pub machine_id: String,
    pub task_key: String,
}

/// `project` is always sent, as null in PR 1: the backend requires every argument.
#[derive(Clone, Debug, Serialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct BoardPageArgs {
    pub project: Option<String>,
    pub state: Option<String>,
    pub cursor: Option<String>,
}

#[derive(Clone, Debug, Serialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct BoardTaskArgs {
    pub machine_id: String,
    pub task_key: String,
    pub before_seq: Option<i64>,
    pub question_cursor: Option<String>,
}

#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct BoardAnswerArgs {
    pub machine_id: String,
    pub task_key: String,
    pub question_key: String,
    pub generation: i32,
    pub answer_key: String,
    pub answer: BoardAnswerValue,
}

/// The answer's acceptance. The window reads none of its fields; decoding it checks the call answered with one.
#[derive(Debug, Deserialize)]
pub struct BoardAnswerResult {}

#[cfg(test)]
#[path = "board_payload_tests.rs"]
mod tests;
