//! `SessionChange` is what one item of the sessions list did, so the list can animate it: it arrived, or it changed
//! (and may have moved to the top).

#[derive(Clone, Debug, PartialEq, Eq)]
pub enum SessionChange {
    Added(String),
    Updated(String),
}
