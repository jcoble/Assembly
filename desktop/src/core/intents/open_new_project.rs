use gpui_kit::*;
#[derive(Clone, PartialEq, Action)]
#[action(namespace=intents,no_json)]
pub struct OpenNewProject {
    pub machine: Option<String>,
}
