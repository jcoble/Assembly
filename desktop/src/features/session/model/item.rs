//! `Item` is one thing the timeline draws, keyed by its row's item (or, for a row with none, by its seq). Deltas and
//! the final message share one item; a tool's, an ask's, a task's or a subagent's rows update its one item.

use crate::features::session::rows::ask::AskPayload;
use crate::features::session::rows::markdown::Block;
use crate::features::session::rows::subagent::SubagentPayload;
use crate::features::session::rows::task::TaskPayload;
use crate::features::session::rows::tool::ToolPayload;
use crate::features::session::rows::turn_stats::TurnStats;

#[derive(Clone, Debug, PartialEq)]
pub struct Item {
    pub key: String,
    pub run_id: Option<String>,
    pub kind: ItemKind,
}

#[derive(Clone, Debug, PartialEq)]
pub enum ItemKind {
    /// `at`: when it was sent. `from`: the session that sent it, when another session did.
    User {
        text: String,
        at: i64,
        from: Option<String>,
    },
    /// `at`: when the reply was last written.
    Message {
        blocks: Vec<Block>,
        done: bool,
        at: i64,
    },
    Thinking {
        text: String,
        done: bool,
    },
    Tool {
        item_id: String,
        tool: ToolPayload,
    },
    Error {
        message: String,
        code: Option<String>,
    },
    Ask {
        ask_id: String,
        ask: AskPayload,
    },
    Task {
        task_id: String,
        task: TaskPayload,
    },
    /// `acp_session_id`: the subagent's ACP session, from its item id; Stop sends it as the task.
    Subagent {
        acp_session_id: String,
        subagent: SubagentPayload,
    },
    /// A run the agent started itself, with its reason standing in for a prompt.
    Note {
        text: String,
    },
    Stats {
        stats: TurnStats,
    },
    /// A kind this build doesn't draw, by name.
    Other {
        kind: String,
    },
}
