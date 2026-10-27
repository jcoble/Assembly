//! `Reveal` shows a streaming reply's newest text smoothly, like typing: one step per frame, from the characters
//! received but not yet shown. Display only: it cuts the backend's blocks short and never parses text. The session
//! pane steps it on each frame while it's behind and stops asking for frames once it has caught up. Ported from
//! rows/reveal.ts.

use crate::features::session::rows::markdown::Block;

/// The steady pace while little is waiting, in characters a second.
pub const MIN_CPS: f64 = 90.0;
/// The most the reveal is behind: what's waiting always shows within this, so the pace grows with what's waiting.
pub const MAX_LAG_MS: f64 = 250.0;

/// The streaming item being revealed, and how many of its characters show.
#[derive(Clone, Debug, PartialEq)]
pub struct Reveal {
    pub key: String,
    pub shown: usize,
}

impl Reveal {
    /// How many characters show after a frame `dt_ms` long, out of `target`.
    pub fn step(_shown: usize, _target: usize, _dt_ms: f64) -> usize {
        todo!()
    }

    /// The first `chars` characters of the blocks, keeping whole blocks as they are.
    pub fn clip(_blocks: &[Block], _chars: usize) -> Vec<Block> {
        todo!()
    }

    /// The characters the blocks show.
    pub fn chars(_blocks: &[Block]) -> usize {
        todo!()
    }
}
