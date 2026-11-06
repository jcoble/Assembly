//! Reveal a folder in the existing owning session's Files tree.
use gpui_kit::*;
#[derive(Clone, PartialEq, Action)]
#[action(namespace = intents, no_json)]
pub struct RevealFileFolder {
    pub session_id: String,
    pub path: String,
    pub expected_source: Option<crate::core::socket::row_source::ExpectedRowSource>,
}
