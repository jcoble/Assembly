use gpui_kit::*;
#[derive(Clone, PartialEq, Action)]
#[action(namespace=intents,no_json)]
pub struct OpenPullRequests {
    pub session_id: Option<String>,
    pub repo: Option<String>,
    pub number: Option<u64>,
}
