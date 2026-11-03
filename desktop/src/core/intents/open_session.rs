//! `OpenSession`: open a session in the chat view.

use gpui_kit::*;

#[derive(Clone, PartialEq, Action)]
#[action(namespace = intents, no_json)]
pub struct OpenSession {
    pub id: String,
    /// Explicit current route metadata, resolved before selection.
    pub route_machine: Option<String>,
    pub request_id: String,
    pub historical: bool,
}
