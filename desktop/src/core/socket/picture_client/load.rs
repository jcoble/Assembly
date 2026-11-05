//! One picture's load, following its redirects by hand so each one is checked, all inside one time limit. The asking
//! is passed in, so this runs with no network in its tests.
use super::rules::{
    LARGEST_PAGE_PICTURE, LARGEST_PICTURE, is_picture, may_ask_first, may_follow, may_read_on_page,
    picture_format,
};
use std::time::{Duration, Instant};
use url::Url;

const MOST_ASKS: usize = 3;

/// What one kind of load may ask for and take as an answer.
pub(super) struct Reach {
    may_ask_first: fn(&Url) -> bool,
    may_follow: fn(&Url) -> bool,
    accepts: fn(&str, &[u8]) -> bool,
    /// The whole load's limit, every redirect included, from when the load gets its turn.
    time: Duration,
    /// The longest body read.
    pub(super) largest: u64,
}
pub(super) const PERSON: Reach = Reach {
    may_ask_first,
    may_follow,
    accepts: is_picture,
    time: Duration::from_secs(5),
    largest: LARGEST_PICTURE,
};
/// Only the bytes count: a page's picture is drawn as the kind they say, so it never reaches GPUI's SVG renderer, and
/// file stores don't always name the type.
pub(super) const PAGE: Reach = Reach {
    may_ask_first: may_read_on_page,
    may_follow: may_read_on_page,
    accepts: |_, bytes| picture_format(bytes).is_some(),
    time: Duration::from_secs(30),
    largest: LARGEST_PAGE_PICTURE,
};

pub(super) enum Hop {
    Redirect(String),
    Answer {
        content_type: String,
        bytes: Vec<u8>,
    },
}
/// Why there's no picture. It never holds an address, so nothing downstream can repeat one.
#[derive(Clone, Copy, Debug, PartialEq)]
pub(super) enum NoPicture {
    NotAllowed,
    TooManyRedirects,
    OutOfTime,
    Unreachable,
    BadStatus,
    NotAPicture,
    NoLongerWanted,
}
pub(super) fn load(
    first: &str,
    now: impl Fn() -> Instant,
    wanted: impl Fn() -> bool,
    ask: impl FnMut(&Url, Duration) -> Result<Hop, NoPicture>,
) -> Result<Vec<u8>, NoPicture> {
    load_within(&PERSON, first, now, wanted, ask)
}
pub(super) fn load_within(
    reach: &Reach,
    first: &str,
    now: impl Fn() -> Instant,
    wanted: impl Fn() -> bool,
    mut ask: impl FnMut(&Url, Duration) -> Result<Hop, NoPicture>,
) -> Result<Vec<u8>, NoPicture> {
    let mut url = Url::parse(first).map_err(|_| NoPicture::NotAllowed)?;
    if !(reach.may_ask_first)(&url) {
        return Err(NoPicture::NotAllowed);
    }
    let deadline = now() + reach.time;
    for _ in 0..MOST_ASKS {
        if !wanted() {
            return Err(NoPicture::NoLongerWanted);
        }
        let answer = ask(&url, deadline.saturating_duration_since(now()))?;
        if now() >= deadline {
            return Err(NoPicture::OutOfTime);
        }
        match answer {
            Hop::Redirect(to) => {
                url = url.join(&to).map_err(|_| NoPicture::NotAllowed)?;
                if !(reach.may_follow)(&url) {
                    return Err(NoPicture::NotAllowed);
                }
            }
            Hop::Answer {
                content_type,
                bytes,
            } => {
                return (reach.accepts)(&content_type, &bytes)
                    .then_some(bytes)
                    .ok_or(NoPicture::NotAPicture);
            }
        }
    }
    Err(NoPicture::TooManyRedirects)
}
#[cfg(test)]
#[path = "load_tests.rs"]
mod tests;
