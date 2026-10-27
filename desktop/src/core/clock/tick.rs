//! `Tick` is one `second` tick of the backend's clock: its time, in Unix milliseconds.

#[derive(Clone, Copy, Debug)]
pub struct Tick {
    pub now: i64,
}
