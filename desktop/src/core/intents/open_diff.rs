//! `OpenDiff`: open a file's changes in the diff view. The session says which machine and folder the path is on.

use gpui_kit::*;

#[derive(Clone, PartialEq, Action)]
#[action(namespace = intents, no_json)]
pub struct OpenDiff {
    pub session_id: String,
    pub path: String,
    /// A staged rename's old path, so its diff shows the rename.
    pub original_path: Option<String>,
}
