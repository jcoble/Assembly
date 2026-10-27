//! `Tick` is one `second` tick of the backend's clock: its time, in Unix milliseconds, and when the window got it
//! (the meter measures from it).

use std::time::Instant;

#[derive(Clone, Copy, Debug)]
pub struct Tick {
    pub now: i64,
    pub received: Instant,
}
