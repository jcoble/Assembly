//! `OpenUrl`: open a web page in the browser, or carry out an `assembly://` link. `core/navigation/open_url_handler.rs`
//! carries it out.

use gpui_kit::*;

#[derive(Clone, PartialEq, Action)]
#[action(namespace = intents, no_json)]
pub struct OpenUrl {
    pub url: String,
}

/// An http or https address, exactly as written. The parser would quietly drop spaces, tabs and newlines the system
/// still sees, so an address holding any of them isn't one.
pub(crate) fn is_web_page(address: &str) -> bool {
    !address.chars().any(|c| c.is_control() || c.is_whitespace())
        && url::Url::parse(address).is_ok_and(|url| matches!(url.scheme(), "http" | "https"))
}
