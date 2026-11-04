use serde::{Deserialize, Serialize};
use serde_json::Value;

pub use crate::core::intents::open_task::NotionTaskRef;

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct NotionSource {
    pub provider: String,
    pub data_source_id: String,
    pub page_id: String,
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct TaskSnapshot {
    pub key: String,
    pub title: String,
    pub body: String,
    pub url: String,
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct NotionTaskExport {
    pub source: NotionSource,
    pub revision: String,
    pub task: TaskSnapshot,
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct NotionEditReceipt {
    pub page_id: String,
    pub revision: String,
    pub state: String,
    pub error: Option<String>,
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct NotionSettingsView {
    pub data_source_id: String,
    pub has_token: bool,
    pub generation: i64,
    pub state: String,
    pub error: Option<String>,
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct NotionOAuthStart {
    pub operation_id: String,
    pub authorization_url: String,
    pub state: String,
}
#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct NotionBlock {
    pub id: String,
    pub kind: String,
    pub content: Value,
    pub children: Vec<NotionBlock>,
}
#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct DetailPage {
    pub operation_id: String,
    pub revision: String,
    pub properties: Option<Value>,
    pub blocks: Vec<NotionBlock>,
    pub comments: Vec<Value>,
    pub next: Option<String>,
    pub body_complete: bool,
    pub comments_unavailable: bool,
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct NotionQuery {
    pub offset: i32,
    pub limit: i32,
    pub search: Option<String>,
    pub project_id: Option<String>,
    pub statuses: Vec<String>,
    pub priorities: Vec<String>,
    pub sort: String,
    pub direction: String,
}
impl Default for NotionQuery {
    fn default() -> Self {
        Self {
            offset: 0,
            limit: 25,
            search: None,
            project_id: None,
            statuses: vec![],
            priorities: vec![],
            sort: "number".into(),
            direction: "desc".into(),
        }
    }
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct NotionTaskRow {
    pub source_id: String,
    pub page_id: String,
    pub title: String,
    pub key_number: i64,
    pub task_key: String,
    pub project_id: String,
    pub project_title: String,
    pub status: String,
    pub priority: String,
    pub assignee: String,
    pub due: Option<String>,
    pub url: String,
    pub fetched_ms: i64,
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct NotionProjectOption {
    pub id: String,
    pub title: String,
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct NotionTaskPage {
    pub rows: Vec<NotionTaskRow>,
    pub has_more: bool,
    pub statuses: Vec<String>,
    pub priorities: Vec<String>,
    pub projects: Vec<NotionProjectOption>,
    pub fetched_ms: i64,
    pub truncated: bool,
    pub generation: i64,
    pub revision: i64,
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct NotionRefreshReceipt {
    pub count: i32,
    pub fetched_ms: i64,
    pub truncated: bool,
    pub generation: i64,
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct NotionImageLease {
    pub upload_id: String,
    pub chunk_bytes: i32,
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
pub struct NotionEmpty {}
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct NotionError {
    pub code: String,
    pub message: String,
}
