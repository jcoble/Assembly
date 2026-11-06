use gpui_kit::*;

#[derive(Clone, PartialEq, Action)]
#[action(namespace = intents, no_json)]
pub struct OpenCommitDiff {
    pub session_id: String,
    pub expected_source: Option<crate::core::socket::row_source::ExpectedRowSource>,
    pub sha: String,
    pub path: String,
    pub title: String,
    /// A linked checkout to read instead of the session's own.
    pub checkout: Option<crate::core::socket::checkout_selector::CheckoutSelector>,
}
