//! Shared source-qualified Engine display state. The socket owns replay and cursors;
//! this cache keeps complete display rows, never reconstructing them from sparse events.
pub use crate::core::socket::engine_update::CursorAt as EventFingerprint;
use serde_json::Value;
mod engine_feed;
mod engine_store;
mod entity_batch;
pub use engine_feed::{EngineChanged, EngineFeed, EngineWatch};
pub use engine_store::EngineStore;

#[derive(Clone, Debug, PartialEq, Eq, Hash)]
pub struct SessionKey {
    pub machine: String,
    pub id: String,
}
#[derive(Clone, Debug, PartialEq, Eq, Hash, PartialOrd, Ord)]
pub struct EngineKey {
    pub machine: String,
    pub kind: String,
    pub id: String,
}
#[derive(Clone, Debug, PartialEq, Eq, Hash)]
pub struct EngineScope {
    pub machine: String,
    pub topic: String,
    pub key: Option<String>,
}
#[derive(Clone, Debug)]
pub struct EngineCursor {
    pub through: i64,
    pub at: Option<EventFingerprint>,
}
#[derive(Clone, Debug)]
pub struct EngineItem {
    pub key: EngineKey,
    pub version: i64,
    pub value: Value,
}
#[derive(Clone, Debug)]
pub struct EngineRefusal {
    pub code: String,
    pub message: String,
}

#[cfg(test)]
mod engine_store_tests;

#[cfg(test)]
mod engine_feed_tests;

pub mod engine_items;
#[cfg(test)]
mod engine_items_tests;

pub mod commands;
#[cfg(test)]
mod commands_tests;

mod native_activity;
pub use native_activity::{NativeWatch, SubagentItem};
#[cfg(test)]
mod native_activity_tests;

pub mod workflow_items;
#[cfg(test)]
mod workflow_items_tests;
pub mod workflow_read;
#[cfg(test)]
mod workflow_read_tests;
