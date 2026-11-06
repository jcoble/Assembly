//! Pure center-tab metadata over actual File document identities and the existing E2 registered leaf codecs.
use crate::core::state::workspace_panes::WorkspaceKey;
use crate::features::files::document_store::DocumentKey;
use crate::features::{
    changes::changes_pane::ChangesPane,
    source_control::{
        git_checkouts::CheckoutHint, history_pane::HistoryPane, pull_requests::pr_pane::PrPane,
    },
};
use gpui_kit::component::dock::{PanelInfo, PanelState};
use serde::{Deserialize, Serialize};
use serde_json::Value;
#[cfg(test)]
use serde_json::json;
use std::collections::{HashMap, HashSet};
#[derive(Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub(super) struct Tabs {
    pub order: Vec<String>,
    pub active_key: Option<String>,
    pub panes: Vec<Pane>,
}
#[derive(Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub(super) struct Pane {
    pub key: String,
    pub state: PanelState,
}
pub(super) fn pane_key(state: &PanelState, key: &WorkspaceKey) -> Result<String, String> {
    if !state.children.is_empty() {
        return Err("Only existing center leaf panes belong to a workspace.".into());
    }
    let PanelInfo::Panel(info) = &state.info else {
        return Err("The saved pane is not leaf metadata.".into());
    };
    let fields: &[&str] = match state.panel_name.as_str() {
        "Changes" => &[
            "session",
            "path",
            "originalPath",
            "sha",
            "title",
            "split",
            "scope",
            "expectedSource",
        ],
        "Git history" => &["session", "path", "expectedSource", "checkoutHint"],
        "Pull requests" => &["session"],
        _ => return Err("Unknown saved center pane.".into()),
    };
    if let Some(info) = info.as_object() {
        if info.keys().any(|field| !fields.contains(&field.as_str())) {
            return Err("Operational or unknown fields cannot be saved as pane metadata.".into());
        }
        if info.iter().any(|(field, value)| {
            !matches!(field.as_str(), "split" | "expectedSource" | "checkoutHint")
                && !value.is_null()
                && !value.is_string()
        }) {
            return Err("Saved pane text metadata must be actual strings.".into());
        }
    }
    // This is the pane's retained source assertion, never refreshed or treated as filesystem authority.
    if let Some(source) = info
        .get("expectedSource")
        .filter(|source| !source.is_null())
    {
        serde_json::from_value::<crate::core::socket::row_source::ExpectedRowSource>(
            source.clone(),
        )
        .map_err(|_| "Invalid saved pane source assertion.".to_string())?;
    }
    // Only names what to look for: nothing reads it until a fresh `git/checkouts` answer still offers that checkout.
    if let Some(hint) = info.get("checkoutHint").filter(|hint| !hint.is_null()) {
        let source = info
            .get("expectedSource")
            .cloned()
            .and_then(|source| serde_json::from_value(source).ok());
        let admitted = serde_json::from_value::<CheckoutHint>(hint.clone())
            .is_ok_and(|hint| source.is_some_and(|source| hint.belongs_to(key, &source)));
        if !admitted {
            return Err("A saved checkout hint contradicts its pane or workspace.".into());
        }
    }
    match state.panel_name.as_str() {
        "Changes" => ChangesPane::workspace_key(state, &key.session_id),
        "Git history" => HistoryPane::workspace_key(state, &key.session_id),
        "Pull requests" => PrPane::workspace_key(state, &key.session_id),
        _ => unreachable!("fixed leaf allowlist"),
    }
}
pub(super) fn parse(
    value: &Value,
    key: &WorkspaceKey,
    snapshot: &Value,
) -> Result<Option<Tabs>, String> {
    if value.is_null() {
        return Ok(None);
    }
    // PanelState's public persisted schema ignores extra keys; admit only its actual fields before decoding it.
    if let Some(panes) = value.get("panes").and_then(Value::as_array) {
        for pane in panes {
            let state = pane
                .get("state")
                .and_then(Value::as_object)
                .ok_or("Invalid saved pane metadata.")?;
            if state
                .keys()
                .any(|field| !matches!(field.as_str(), "panel_name" | "children" | "info"))
            {
                return Err("Unknown saved pane fields.".into());
            }
        }
    }
    let tabs: Tabs = serde_json::from_value(value.clone())
        .map_err(|_| "Invalid saved center tabs.".to_string())?;
    let mut panes = HashMap::new();
    for pane in &tabs.panes {
        if pane_key(&pane.state, key)? != pane.key
            || panes.insert(pane.key.as_str(), &pane.state).is_some()
        {
            return Err(
                "Saved pane identities contradict their actual registered metadata.".into(),
            );
        }
    }
    // A session file's identity is under this checkout; an outside file's, under its own engine and real parent.
    let files = crate::features::files::file_view::FileView::saved_identities(snapshot, key);
    let mut order = HashSet::new();
    for tab in &tabs.order {
        if !order.insert(tab.as_str()) {
            return Err("The saved tab order contains duplicate identities.".into());
        }
        if tab == "Chat" || panes.contains_key(tab.as_str()) {
            continue;
        }
        let Some(document) = tab.strip_prefix("document:") else {
            return Err("The saved tab order contains an unknown identity.".into());
        };
        let (machine, root, path): (String, String, String) = serde_json::from_str(document)
            .map_err(|_| "Invalid saved file document identity.".to_string())?;
        let actual = DocumentKey {
            machine,
            checkout_root: root,
            path,
        }
        .identity();
        if &actual != tab || !files.contains(tab) {
            return Err("A saved tab refers to a file outside the sole Files snapshot or a noncanonical alias.".into());
        }
    }
    if tabs
        .active_key
        .as_ref()
        .is_some_and(|key| !order.contains(key.as_str()))
        || panes.keys().any(|key| !order.contains(key))
    {
        return Err("Saved active or leaf panes contradict the tab order.".into());
    }
    Ok(Some(tabs))
}
#[cfg(test)]
#[path = "center_tabs_tests.rs"]
mod tests;
