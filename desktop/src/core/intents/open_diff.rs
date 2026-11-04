//! `OpenDiff`: go to a file in the Changes tab of the selected session. The tab decides what it reads from its own
//! accepted status, never from this.

use gpui_kit::*;

#[derive(Clone, PartialEq, Action)]
#[action(namespace = intents, no_json)]
pub struct OpenDiff {
    pub session_id: String,
    pub path: String,
}
