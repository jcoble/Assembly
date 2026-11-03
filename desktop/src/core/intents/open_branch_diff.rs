//! `OpenBranchDiff`: open everything the session's branch changed since it left its base in the diff view.

use gpui_kit::*;

#[derive(Clone, PartialEq, Action)]
#[action(namespace = intents, no_json)]
pub struct OpenBranchDiff {
    pub session_id: String,
    pub expected_source: Option<crate::core::socket::row_source::ExpectedRowSource>,
}
