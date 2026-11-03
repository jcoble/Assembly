//! Engine display updates on the existing socket and global machine cursor.
use serde::{Deserialize, Serialize};
use serde_json::Value;

#[derive(Clone, Debug, Deserialize, Serialize, PartialEq)]
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
}

pub(super) struct EngineFeed {
    pub handle: u64,
    pub message: String,
    pub machine: Option<String>,
    pub through: Option<i64>,
    pub at: Option<CursorAt>,
    pub snapshot_cut: Option<i64>,
    pub updates: futures_channel::mpsc::UnboundedSender<EngineUpdate>,
}
