use gpui_kit::*;
#[derive(Clone, PartialEq, Action)]
#[action(namespace = intents, no_json)]
pub struct OpenGitHistory {
    pub session_id: String,
    pub expected_source: Option<crate::core::socket::row_source::ExpectedRowSource>,
    pub path: Option<String>,
}
