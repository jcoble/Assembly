//! `OpenUrl`: follow a link: an `assembly://` link, a page the app opens itself (a Notion task, a GitHub pull request),
//! an email link, or else a web page in the browser. `OpenInBrowser`: a web page in the browser, never in the app, for
//! "Open on GitHub" and "Open in Notion". `core/navigation/open_url_handler.rs` carries out both.

use gpui_kit::*;

#[derive(Clone, PartialEq, Action)]
#[action(namespace = intents, no_json)]
pub struct OpenUrl {
    pub url: String,
}

#[derive(Clone, PartialEq, Action)]
#[action(namespace = intents, no_json)]
pub struct OpenInBrowser {
    pub url: String,
}

/// An http or https address, exactly as written. The parser would quietly drop spaces, tabs and newlines the system
/// still sees, so an address holding any of them isn't one.
pub(crate) fn is_web_page(address: &str) -> bool {
    has_scheme(address, &["http", "https"])
}

/// A `mailto:` address, exactly as written, for the mail app.
pub(crate) fn is_mail_link(address: &str) -> bool {
    has_scheme(address, &["mailto"])
}

fn has_scheme(address: &str, schemes: &[&str]) -> bool {
    !address.chars().any(|c| c.is_control() || c.is_whitespace())
        && url::Url::parse(address).is_ok_and(|url| schemes.contains(&url.scheme()))
}
