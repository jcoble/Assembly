use gpui_kit::*;
/// The exact registry copy chosen by the user; never a provider or session identity.
#[derive(Clone, PartialEq, Action)]
#[action(namespace=intents,no_json)]
pub struct OpenProject {
    pub project_id: String,
    pub machine: String,
    pub root_path: String,
}
