use gpui_kit::*;
#[derive(Clone, PartialEq, Action)]
#[action(namespace = intents, no_json)]
pub struct OpenGitHistory {
    pub session_id: String,
    pub expected_source: Option<crate::core::socket::row_source::ExpectedRowSource>,
    pub path: Option<String>,
    /// The checkout the panel that sent this was reading. History checks it against its own fresh list. `None` asks for
    /// nothing, and an open History tab stays on the folder it's picked.
    pub checkout: Option<crate::core::socket::checkout_selector::CheckoutSelector>,
}
