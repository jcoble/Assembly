//! `OpenDiff`: open a file's changes in the diff view. The session says which machine and folder the path is on.

use gpui_kit::*;

#[derive(Clone, PartialEq, Action)]
#[action(namespace = intents, no_json)]
pub struct OpenDiff {
    pub session_id: String,
    pub expected_source: Option<crate::core::socket::row_source::ExpectedRowSource>,
    pub path: String,
    /// A staged rename's old path, so its diff shows the rename.
    pub original_path: Option<String>,
    /// A linked checkout to read instead of the session's own.
    pub checkout: Option<crate::core::socket::checkout_selector::CheckoutSelector>,
}
