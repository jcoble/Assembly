//! One picture's load, following its redirects by hand so each one is checked, all inside one time limit. The asking
//! is passed in, so this runs with no network in its tests.
use super::rules::{is_picture, may_ask_first, may_follow};
use std::time::{Duration, Instant};
use url::Url;

/// The whole load's limit, every redirect included, from when the load gets its turn.
const LOAD_TIME: Duration = Duration::from_secs(5);
const MOST_ASKS: usize = 3;

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
    mut ask: impl FnMut(&Url, Duration) -> Result<Hop, NoPicture>,
) -> Result<Vec<u8>, NoPicture> {
    let mut url = Url::parse(first).map_err(|_| NoPicture::NotAllowed)?;
    if !may_ask_first(&url) {
        return Err(NoPicture::NotAllowed);
    }
    let deadline = now() + LOAD_TIME;
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
                if !may_follow(&url) {
                    return Err(NoPicture::NotAllowed);
                }
            }
            Hop::Answer {
                content_type,
                bytes,
            } => {
                return is_picture(&content_type, &bytes)
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
