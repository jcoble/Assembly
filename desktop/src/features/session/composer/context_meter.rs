//! `ContextMeter` is how full the agent's context window is, for the composer's ring and its hint ("51,285 of
//! 1,000,000 (5%)"), from the newest `context` row. Not the window's performance meter (lane g0-host). Replaces
//! contextMeter.ts.

#[derive(Clone, Debug, PartialEq)]
pub struct ContextMeter {
    /// Shares of the window, rounded: what's left and what's spent.
    pub left: u8,
    pub used: u8,
    /// What the ring paints: what's left, but never more than 90 once anything is spent.
    pub arc: u8,
    pub warm: bool,
    pub hot: bool,
    pub detail: String,
}

impl ContextMeter {
    /// `None` when the agent hasn't reported, or reports more used than the window holds.
    pub fn of(_used: i64, _size: i64) -> Option<ContextMeter> {
        todo!()
    }
}
