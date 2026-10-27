//! `OpenUrl`: open a web page in the browser.

use gpui_kit::*;

#[derive(Clone, PartialEq, Action)]
#[action(namespace = intents, no_json)]
pub struct OpenUrl {
    pub url: String,
}
