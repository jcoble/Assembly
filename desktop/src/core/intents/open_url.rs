//! `OpenUrl`: open a web page in the browser.

use gpui_kit::component::WindowExt as _;
use gpui_kit::component::notification::Notification;
use gpui_kit::*;

#[derive(Clone, PartialEq, Action)]
#[action(namespace = intents, no_json)]
pub struct OpenUrl {
    pub url: String,
}

/// Every link in the app goes through this one handler. The browser tab isn't ported yet, so a web page opens in the
/// person's own browser until that tab takes over here. Links come from untrusted text, and the system would start
/// whatever app claims any other scheme, so nothing else reaches it: a file link is `OpenFile`'s, with its session.
pub fn register(cx: &mut App) {
    cx.on_action(|intent: &OpenUrl, cx| {
        if is_web_page(&intent.url) {
            cx.open_url(&intent.url);
        } else if let Some(window) = cx.active_window() {
            // An action runs while its window is being updated, so the window is updated once that's done.
            cx.defer(move |cx| {
                _ = window.update(cx, |_, window, cx| {
                    window.push_notification(Notification::warning(NOT_A_WEB_PAGE), cx)
                });
            });
        }
    });
}

const NOT_A_WEB_PAGE: &str = "This link isn't a web page, so it wasn't opened.";

/// An http or https address, exactly as written. The parser would quietly drop spaces, tabs and newlines the system
/// still sees, so an address holding any of them isn't one.
fn is_web_page(address: &str) -> bool {
    !address.chars().any(|c| c.is_control() || c.is_whitespace())
        && url::Url::parse(address).is_ok_and(|url| matches!(url.scheme(), "http" | "https"))
}

#[cfg(test)]
#[path = "open_url_links_tests.rs"]
mod links_tests;
#[cfg(test)]
#[path = "open_url_tests.rs"]
mod tests;
