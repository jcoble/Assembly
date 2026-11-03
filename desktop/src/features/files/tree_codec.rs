//! The existing lazy tree owns its view metadata; one workspace codec retains no pane handles.
use super::{
    document_store::{DocumentKey, validate_key},
    files_pane::FilesPane,
};
use crate::core::state::workspace_panes::{PaneCodec, WorkspaceKey, WorkspacePanes};
use gpui_kit::*;
use serde::{Deserialize, Serialize};
use serde_json::Value;
#[cfg(test)]
use serde_json::json;
use std::collections::HashSet;
#[derive(Clone, Default, Deserialize, Serialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub(crate) struct TreeSnapshot {
    pub expanded_paths: Vec<String>,
    #[serde(default)]
    pub selected_path: Option<String>,
    pub scroll: f32,
}
pub struct TreeCodec;
impl PaneCodec for TreeCodec {
    fn name(&self) -> &'static str {
        "tree"
    }
    fn capture(&self, _: &Window, cx: &App) -> Result<Value, String> {
        let Some(key) = WorkspacePanes::key(cx) else {
            return Err("The owning workspace identity is unavailable.".into());
        };
        Ok(FilesPane::workspace_views(cx)
            .into_iter()
            .find_map(|pane| pane.read(cx).workspace_tree(key))
            .map(|snapshot| serde_json::to_value(snapshot).expect("finite tree metadata"))
            .unwrap_or(Value::Null))
    }
    fn validate(
        &self,
        value: &Value,
        key: &WorkspaceKey,
        _: &Value,
        _: &App,
    ) -> Result<(), String> {
        parse(value, key).map(|_| ())
    }
    fn deactivate(&self, _: &mut Window, cx: &mut App) {
        for pane in FilesPane::workspace_views(cx) {
            pane.update(cx, |pane, cx| pane.deactivate_workspace_tree(cx));
        }
    }
    fn restore(&self, value: &Value, _: &mut Window, cx: &mut App) {
        let Some(key) = WorkspacePanes::key(cx).cloned() else {
            return;
        };
        let snapshot = match parse(value, &key) {
            Ok(snapshot) => snapshot,
            Err(error) => {
                WorkspacePanes::refuse_restore(error, cx);
                return;
            }
        };
        for pane in FilesPane::workspace_views(cx) {
            pane.update(cx, |pane, cx| {
                pane.restore_workspace_tree(key.clone(), snapshot.clone(), cx)
            });
        }
    }
}
fn parse(value: &Value, key: &WorkspaceKey) -> Result<TreeSnapshot, String> {
    if value.is_null() {
        return Ok(TreeSnapshot::default());
    }
    let snapshot: TreeSnapshot = serde_json::from_value(value.clone())
        .map_err(|_| "Couldn't restore the saved file tree.".to_string())?;
    if !snapshot.scroll.is_finite() {
        return Err("The saved tree scroll is invalid.".into());
    }
    let mut seen = HashSet::new();
    for path in snapshot
        .expanded_paths
        .iter()
        .chain(snapshot.selected_path.iter())
    {
        validate_key(&DocumentKey {
            machine: key.machine.clone(),
            checkout_root: key.checkout_root.clone(),
            path: path.clone(),
        })
        .map_err(|_| "A saved tree path is outside its checkout.".to_string())?;
    }
    if snapshot
        .expanded_paths
        .iter()
        .any(|path| !seen.insert(path))
    {
        return Err("The saved tree contains duplicate folders.".into());
    }
    Ok(snapshot)
}
#[cfg(test)]
#[path = "tree_codec_tests.rs"]
mod tests;
