//! `SessionAction` is what a row asks of the session pane, which owns the client: rows are `RenderOnce` and hold only
//! their data and a handler that sends one of these.

use std::rc::Rc;

use gpui_kit::*;
use serde_json::Value;

pub enum SessionAction {
    /// Open or close a tool call; opening reads its detail (`rows/tool`).
    ToggleTool { item_id: String },
    /// Open or close a fold, thinking or a long prompt, by item key or run id.
    Toggle { key: String },
    /// `send/answer`: the option's `{ optionId }` or the form's `{ action, content }`.
    Answer { ask_id: String, answer: Value },
    /// `send/stopTask`: a task, or a subagent by its session id.
    StopTask { task: String },
    /// Opens another session: a subagent's, a parent, or the one that sent a prompt (lane g0-host's intent).
    OpenSession { session_id: String },
    /// Reads an attached image (`attachments/read`) and opens it full size.
    ShowImage { name: String },
}

/// What a row calls with an action; the pane gives each row a clone.
pub type OnAction = Rc<dyn Fn(SessionAction, &mut Window, &mut App)>;
