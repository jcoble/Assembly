use gpui_kit::*;
#[derive(Clone, PartialEq, Action)]
#[action(namespace = intents, no_json)]
pub struct OpenGitHistory { pub session_id: String, pub path: Option<String> }
