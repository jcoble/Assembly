//! `OpenUrl`: open a web page in the browser.

use gpui_kit::*;

#[derive(Clone, PartialEq, Action)]
#[action(namespace = intents, no_json)]
pub struct OpenUrl {
    pub url: String,
}

/// Every link in the app goes through this one handler. The browser tab isn't ported yet, so it's the person's own
/// browser until that tab takes over here.
pub fn register(cx: &mut App) {
    cx.on_action(|intent: &OpenUrl, cx| cx.open_url(&intent.url));
}
