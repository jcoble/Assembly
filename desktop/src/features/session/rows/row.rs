//! `Row` is one row of a session as the `rows` topic and `rows/page` send it (RowWire.cs), the same envelope as the
//! socket client's (lane g0-socket), which replaces this one when it lands. `RowBody` is its kind's payload, read
//! per kind; a kind this build doesn't know, or a payload it can't read, is `Other` with the kind's name, so nothing
//! silently disappears. Ported from rows/store.ts's `Row` and `Payloads`.

use serde::Deserialize;
use serde_json::Value;

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
    pub kind: String,
    pub payload: Value,
    pub created_at: i64,
    /// On page rows only: the seq of the item's first row, which places it.
    pub first_seq: Option<i64>,
    /// On live rows: the seq before it (the socket client's gap check).
    pub prev_seq: Option<i64>,
}

/// Read as `{kind, payload}`, so each variant's name is its kind.
#[derive(Clone, Debug, PartialEq, Deserialize)]
#[serde(tag = "kind", content = "payload", rename_all = "camelCase")]
pub enum RowBody {
    /// `reason`: why a run the agent started itself started.
    Turn {
        state: TurnState,
        reason: Option<String>,
    },
    /// `from`: the session that sent it, when it's a message from another session.
    User {
        text: String,
        from: Option<String>,
    },
    /// `text` is only this delta's new raw text; the view draws `blocks` from block `from` on.
    Delta {
        from: usize,
        text: String,
        blocks: Vec<Block>,
    },
    Message {
        blocks: Vec<Block>,
    },
    Thinking {
        text: String,
        done: bool,
    },
    Tool(ToolPayload),
    Plan {
        items: Vec<PlanItem>,
    },
    Stats(TurnStats),
    Error {
        message: String,
        code: Option<String>,
    },
    Ask(AskPayload),
    Task(TaskPayload),
    Subagent(SubagentPayload),
    /// How full the agent's context window is, in tokens: one item per run.
    Context {
        used: i64,
        size: i64,
    },
    #[serde(skip)]
    Other {
        kind: String,
    },
}

impl RowBody {
    pub fn of(row: &Row) -> RowBody {
        let tagged = serde_json::json!({ "kind": row.kind, "payload": row.payload });
        serde_json::from_value(tagged).unwrap_or_else(|_| RowBody::Other {
            kind: row.kind.clone(),
        })
    }
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Deserialize)]
#[serde(rename_all = "camelCase")]
pub enum TurnState {
    Started,
    Finished,
    Failed,
    Cancelled,
}
