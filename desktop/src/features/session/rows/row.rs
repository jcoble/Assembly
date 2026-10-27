//! `Row` is one row of a session, with its kind's payload. Ported from rows/store.ts's `Row` and `Payloads`.

use serde::Deserialize;

use super::ask::AskPayload;
use super::markdown::Block;
use super::plan::PlanItem;
use super::subagent::SubagentPayload;
use super::task::TaskPayload;
use super::tool::ToolPayload;
use super::turn_stats::TurnStats;

#[derive(Clone, Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Row {
    pub session_id: String,
    pub seq: i64,
    pub run_id: Option<String>,
    /// The item the row updates; a row with none is its own item, keyed by its seq.
    pub item_id: Option<String>,
    #[serde(flatten)]
    pub body: RowBody,
    pub created_at: i64,
    /// On page rows only: the seq of the item's first row, which places it.
    pub first_seq: Option<i64>,
}

/// The row's kind and payload. A kind this build doesn't know is `Other`, shown by name so nothing silently
/// disappears.
#[derive(Clone, Debug, Deserialize)]
#[serde(tag = "kind", content = "payload", rename_all = "camelCase")]
pub enum RowBody {
    /// `reason`: why a run the agent started itself started.
    Turn { state: TurnState, reason: Option<String> },
    /// `from`: the session that sent it, when it's a message from another session.
    User { text: String, from: Option<String> },
    /// `text` is only this delta's new raw text; the view draws `blocks` from block `from` on.
    Delta { from: usize, text: String, blocks: Vec<Block> },
    Message { blocks: Vec<Block> },
    Thinking { text: String, done: bool },
    Tool(ToolPayload),
    Plan { items: Vec<PlanItem> },
    Stats(TurnStats),
    Error { message: String, code: Option<String> },
    Ask(AskPayload),
    Task(TaskPayload),
    Subagent(SubagentPayload),
    /// How full the agent's context window is, in tokens: one item per run.
    Context { used: i64, size: i64 },
    #[serde(rename_all = "camelCase")]
    Compaction { trigger: String, pre_tokens: i64, post_tokens: Option<i64> },
    #[serde(other)]
    Other,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Deserialize)]
#[serde(rename_all = "camelCase")]
pub enum TurnState {
    Started,
    Finished,
    Failed,
    Cancelled,
}
