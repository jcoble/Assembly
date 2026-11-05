//! Carries out `OpenUrl` and `OpenInBrowser`.

use gpui_kit::component::WindowExt as _;
use gpui_kit::component::notification::Notification;
use gpui_kit::*;

use super::deep_links::handle_address;
use super::web_links::{notion_page, pull_request};
use crate::core::intents::open_pull_requests::OpenPullRequests;
use crate::core::intents::open_url::{OpenInBrowser, OpenUrl, is_mail_link, is_web_page};
use crate::features::tasks::tasks_pane;

/// Every link in the app goes through this one handler, in the old app's order: an `assembly://` link does what it does
/// from outside, a Notion page opens its task, a GitHub pull request opens in the Pull requests tab, and any other web
/// page opens in the person's own browser until the browser tab is ported. An email link goes to the mail app. Links
/// come from untrusted text, and the system would start whatever app claims any other scheme, so nothing else reaches
/// it; a file link is `OpenFile`'s, with its session.
pub fn register(cx: &mut App) {
    cx.on_action(|intent: &OpenUrl, cx| follow(&intent.url, cx));
    cx.on_action(|intent: &OpenInBrowser, cx| {
        if is_web_page(&intent.url) {
            cx.open_url(&intent.url);
        } else {
            refuse(cx);
        }
    });
}

fn follow(address: &str, cx: &mut App) {
    if handle_address(address, cx) {
        return;
    }
    if let Some(url) = url::Url::parse(address)
        .ok()
        .filter(|_| is_web_page(address))
    {
        if notion_page(&url).is_some() {
            return tasks_pane::open_notion_page(address.to_string(), cx);
        }
        if let Some((repo, number)) = pull_request(&url) {
            let intent = OpenPullRequests {
                session_id: None,
                repo: Some(repo),
                number: Some(number),
            };
            // An action runs while its window is being updated, so a second one waits until that's done.
            return cx.defer(move |cx| cx.dispatch_action(&intent));
        }
        return cx.open_url(address);
    }
    if is_mail_link(address) {
        return cx.open_url(address);
    }
    refuse(cx);
}

fn refuse(cx: &mut App) {
    if let Some(window) = cx.active_window() {
        // An action runs while its window is being updated, so the window is updated once that's done.
        cx.defer(move |cx| {
            _ = window.update(cx, |_, window, cx| {
                window.push_notification(Notification::warning(NOT_A_WEB_PAGE), cx)
            });
        });
    }
}

const NOT_A_WEB_PAGE: &str = "This link isn't a web page, so it wasn't opened.";

#[cfg(test)]
#[path = "open_url_handler_links_tests.rs"]
mod links_tests;
#[cfg(test)]
#[path = "open_url_handler_tests.rs"]
mod tests;
