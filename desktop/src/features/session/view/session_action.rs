//! `SessionAction` is what a row asks of the session pane, which owns the client: rows are `RenderOnce` and hold only
//! their data and a handler that sends one of these.

use std::rc::Rc;

use gpui_kit::*;
use serde_json::Value;

#[derive(Clone)]
pub enum SessionAction {
    /// Open or close a finished turn's fold, by its run id.
    ToggleFold { run_id: String },
    /// Open or close a tool call, thinking or a long prompt, by item key; opening a tool call reads its detail
    /// (`rows/tool`).
    Toggle { key: String },
    /// Load the first page again after it failed.
    RetryPage,
    /// Open or close the plan's list of steps.
    TogglePlan,
    /// Show an open tool call's whole diff, by item key.
    ShowAllDiff { key: String },
    /// Pick a choice in an open question's form: the field's one choice, or one more of many (picked again, it's
    /// taken off).
    Pick {
        ask_id: String,
        field: String,
        value: String,
        many: bool,
    },
    /// Send an open question's form as it's filled in.
    SubmitAsk { ask_id: String },
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
