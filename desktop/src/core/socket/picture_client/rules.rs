//! What may be asked for and what counts as an answer: a person's public GitHub picture and nothing else.
use url::Url;

/// The largest picture read; a longer body fails the load.
pub(super) const LARGEST_PICTURE: u64 = 1024 * 1024;
const ASKED_SIZE: &str = "size=40";

/// The address of a person's picture, the old screen's: 40 pixels, for a 20 point circle on a sharp display.
pub fn picture_url(login: &str) -> Option<String> {
    if login.is_empty() {
        return None;
    }
    let encoded = url::form_urlencoded::byte_serialize(login.as_bytes())
        .collect::<String>()
        .replace('+', "%20");
    Some(format!("https://github.com/{encoded}.png?{ASKED_SIZE}"))
}
fn plain_https(url: &Url) -> bool {
    url.scheme() == "https"
        && url.username().is_empty()
        && url.password().is_none()
        && url.port().is_none()
}
/// Only the shape `picture_url` makes: anything else the window tries to load, such as an image in markdown, is refused.
pub(super) fn may_ask_first(url: &Url) -> bool {
    let one_picture = url.path_segments().is_some_and(|mut segments| {
        let first = segments.next();
        segments.next().is_none()
            && first
                .and_then(|segment| segment.strip_suffix(".png"))
                .is_some_and(|stem| !stem.is_empty())
    });
    plain_https(url)
        && url.host_str() == Some("github.com")
        && url.query() == Some(ASKED_SIZE)
        && url.fragment().is_none()
        && one_picture
}
/// GitHub answers a person's picture by sending the reader to its avatar host.
pub(super) fn may_follow(url: &Url) -> bool {
    may_ask_first(url)
        || (plain_https(url) && url.host_str() == Some("avatars.githubusercontent.com"))
}
fn starts_like_a_picture(bytes: &[u8]) -> bool {
    bytes.starts_with(b"\x89PNG\r\n\x1a\n")
        || bytes.starts_with(&[0xff, 0xd8, 0xff])
        || bytes.starts_with(b"GIF87a")
        || bytes.starts_with(b"GIF89a")
        || (bytes.len() >= 12 && bytes.starts_with(b"RIFF") && &bytes[8..12] == b"WEBP")
}
/// GPUI hands anything it doesn't recognize to its SVG renderer, so only pixels get past here: the type and the bytes
/// must both say so.
pub(super) fn is_picture(content_type: &str, bytes: &[u8]) -> bool {
    let kind = content_type.split(';').next().unwrap_or("").trim();
    ["image/png", "image/jpeg", "image/gif", "image/webp"]
        .iter()
        .any(|allowed| kind.eq_ignore_ascii_case(allowed))
        && bytes.len() as u64 <= LARGEST_PICTURE
        && starts_like_a_picture(bytes)
}
#[cfg(test)]
#[path = "rules_tests.rs"]
mod tests;
