//! `ToolPayload` is a tool call's row; `ToolDetail` is what `rows/tool` answers for an opened one.

use serde::Deserialize;

#[derive(Clone, Debug, PartialEq, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ToolPayload {
    pub name: String,
    pub state: ToolState,
    /// Left out when it's the tool's name.
    pub summary: Option<String>,
    pub path: Option<String>,
    pub added: Option<i64>,
    pub removed: Option<i64>,
    pub has_detail: bool,
    /// ACP's tool kind: read, edit, delete, move, search, execute, think, fetch or other.
    pub kind: Option<String>,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Deserialize)]
#[serde(rename_all = "camelCase")]
pub enum ToolState {
    Started,
    Completed,
    Failed,
}

/// `input` is the call's arguments as JSON text. Output and diff are cut at 1 MB each, with `truncated` set and the
/// full sizes in `output_bytes` and `diff_bytes` (RowsFeature.ToolDetail).
#[derive(Clone, Debug, PartialEq, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ToolDetail {
    pub summary: Option<String>,
    pub input: Option<String>,
    pub output: Option<String>,
    pub diff: Option<String>,
    pub path: Option<String>,
    pub truncated: bool,
    pub output_bytes: i64,
    pub diff_bytes: i64,
}
