//! `Row` is one row of a session as the `rows` topic and `rows/page` send it (RowWire.cs), the socket client's.
//! `RowBody` is its kind's payload, read per kind; a kind this build doesn't know, or a payload it can't read, is `Other` with the kind's name, so nothing
//! silently disappears. Ported from rows/store.ts's `Row` and `Payloads`.

use serde::Deserialize;

use super::ask::AskPayload;
use super::markdown::Block;
use super::plan::PlanItem;
use super::subagent::SubagentPayload;
use super::task::TaskPayload;
use super::tool::ToolPayload;
use super::turn_stats::TurnStats;

pub use crate::core::socket::row::Row;

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
