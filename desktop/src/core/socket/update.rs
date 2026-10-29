//! `Update` is what a subscription to a keyed topic gets: its state, then each change, one item at a time. Items stay
//! JSON here; the entity that owns the topic's fact reads them into its type.

use serde_json::Value;

#[derive(Debug)]
pub enum Update {
    /// The topic's items now, at subscribe and again after every reconnect.
    State(Vec<(String, Value)>),
    /// One item changed. A topic that removes items (`machines`) sends a null item for one removed.
    Change { key: String, item: Value },
}
