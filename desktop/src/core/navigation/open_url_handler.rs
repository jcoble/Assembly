//! Carries out `OpenUrl`.

use gpui_kit::component::WindowExt as _;
use gpui_kit::component::notification::Notification;
use gpui_kit::*;

use super::deep_links::handle_address;
use crate::core::intents::open_url::{OpenUrl, is_web_page};

/// Every link in the app goes through this one handler. The browser tab isn't ported yet, so a web page opens in the
/// person's own browser until that tab takes over here. Links come from untrusted text, and the system would start
/// whatever app claims any other scheme, so nothing else reaches it: an `assembly://` link does what it does from
/// outside, and a file link is `OpenFile`'s, with its session.
pub fn register(cx: &mut App) {
    cx.on_action(|intent: &OpenUrl, cx| {
        if is_web_page(&intent.url) {
            cx.open_url(&intent.url);
        } else if !handle_address(&intent.url, cx)
            && let Some(window) = cx.active_window()
        {
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

#[cfg(test)]
#[path = "open_url_handler_links_tests.rs"]
mod links_tests;
#[cfg(test)]
#[path = "open_url_handler_tests.rs"]
mod tests;
