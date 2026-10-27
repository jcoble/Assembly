//! `OpenSession`: open a session in the chat view.

use gpui_kit::*;

#[derive(Clone, PartialEq, Action)]
#[action(namespace = intents, no_json)]
pub struct OpenSession {
    pub id: String,
}
