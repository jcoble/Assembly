//! `RowPage` is what `rows/page` answers: the newest rows of the items that start below `before`.

use serde::Deserialize;

use super::row::Row;

#[derive(Clone, Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct RowPage {
    pub rows: Vec<Row>,
    pub has_before: bool,
    /// The run going now when the page was read, if any.
    pub active_run_id: Option<String>,
}
