//! `OpenFile`: open a file in the editor, at a line when given. The session says which machine and folder the path
//! is on.

use gpui_kit::*;

#[derive(Clone, PartialEq, Action)]
#[action(namespace = intents, no_json)]
pub struct OpenFile {
    pub session_id: String,
    pub path: String,
    pub line: Option<u32>,
}
