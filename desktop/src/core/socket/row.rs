//! `Row` is a saved row as the `rows` topic sends it (dotnet/Assembly.Application/Features/Chat/Rows/RowWire.cs).

use serde::Deserialize;
use serde_json::Value;

#[derive(Clone, Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Row {
    pub session_id: String,
    pub seq: i64,
    pub run_id: Option<String>,
    pub item_id: Option<String>,
    pub kind: String,
    pub payload: Value,
    pub created_at: i64,
    /// On page rows: places the item.
    pub first_seq: Option<i64>,
    /// On live and catch-up rows: the seq before it, which proves none was missed.
    pub prev_seq: Option<i64>,
}
