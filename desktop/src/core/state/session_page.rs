//! `SessionPage` is `sessions/page`'s answer: older sessions, and where the next page starts (none at the end).

use serde::Deserialize;

use super::session_item::SessionItem;

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SessionPage {
    pub sessions: Vec<SessionItem>,
    pub next_before: Option<i64>,
}
