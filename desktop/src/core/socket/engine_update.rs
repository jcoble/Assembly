//! Engine display updates on the existing socket and global machine cursor.
use serde::{Deserialize, Serialize};
use serde_json::Value;

#[derive(Clone, Debug, Deserialize, Serialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct CursorAt {
    pub kind: String,
    pub entity_id: String,
    pub version: i64,
}

#[derive(Debug)]
pub enum EngineUpdate {
    Snapshot(Vec<Value>),
    Events(Vec<Value>),
    /// A generic scope, published only after all pages at one snapshot cut arrived.
    Scoped(EnginePage),
}

#[derive(Debug)]
pub struct EnginePage {
    pub wire: u64,
    pub machine: String,
    pub through: i64,
    pub at: Option<CursorAt>,
    pub snapshot: bool,
    pub items: Vec<Value>,
    pub events: Vec<Value>,
}

pub(super) struct EngineFeed {
    pub handle: u64,
    pub topic: String,
    pub key: Option<String>,
    pub scoped: bool,
    pub machine: Option<String>,
    pub through: Option<i64>,
    pub acknowledged: Option<(i64, Option<CursorAt>)>,
    pub at: Option<CursorAt>,
    pub snapshot_cut: Option<i64>,
    pub snapshot_items: Vec<Value>,
    pub updates: futures_channel::mpsc::UnboundedSender<EngineUpdate>,
}
