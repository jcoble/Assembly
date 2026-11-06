//! `OpenTask`: open a Notion task in the Tasks tab.

use gpui_kit::*;
use serde::{Deserialize, Serialize};

/// A task by its data source and page, both in Notion's canonical form.
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct NotionTaskRef {
    pub data_source_id: String,
    pub page_id: String,
}

#[derive(Clone, PartialEq, Action)]
#[action(namespace = intents, no_json)]
pub struct OpenTask {
    pub source: NotionTaskRef,
}

/// A link clicked inside a task page; the Tasks tab decides where it goes.
#[derive(Clone, PartialEq, Action)]
#[action(namespace = intents, no_json)]
pub struct OpenTaskLink {
    pub url: String,
}
