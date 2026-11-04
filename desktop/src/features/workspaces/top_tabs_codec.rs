//! Session center tabs use the real registered factories after pure metadata admission; Chat remains the fixed entity.
use super::center_tabs::{self, Tabs};
use crate::core::{
    state::workspace_panes::{PaneCodec, WorkspaceKey, WorkspacePanes},
    ui::center_panes::CenterPanes,
};
use crate::features::{
    changes::changes_pane::ChangesPane,
    source_control::{history_pane::HistoryPane, pull_requests::pr_pane::PrPane},
};
use gpui_kit::component::dock::{
    DockArea, DockPlacement, InsertTarget, NodeId, PaneRef, Panel, PanelBuildContext, PanelHandle,
    PanelId, PanelRegistry,
};
use gpui_kit::*;
use serde_json::Value;
use std::collections::{HashMap, HashSet};
pub struct TopTabsCodec;
impl PaneCodec for TopTabsCodec {
    fn name(&self) -> &'static str {
        "topTabs"
    }
    fn capture(&self, window: &Window, cx: &App) -> Result<Value, String> {
        let key =
            WorkspacePanes::key(cx).ok_or("The workspace checkout identity is unavailable.")?;
        serde_json::to_value(super::center_tabs_capture::capture(key, window, cx)?)
            .map_err(|error| error.to_string())
    }
    fn validate(
        &self,
        value: &Value,
        key: &WorkspaceKey,
        snapshot: &Value,
        cx: &App,
    ) -> Result<(), String> {
        let tabs = center_tabs::parse(value, key, snapshot)?;
        self.validate_deactivate(cx)?;
        let dock = CenterPanes::dock(cx).ok_or("The center dock is unavailable.")?;
        if tabs
            .as_ref()
            .is_some_and(|tabs| tabs.order.iter().any(|key| key == "Chat"))
            && !center_ids(&dock, cx).iter().any(|id| {
                dock.read(cx)
                    .panel(*id)
                    .is_some_and(|pane| pane.panel_name(cx) == "Chat")
            })
        {
            return Err(
                "The fixed Chat pane is unavailable; the current workspace was preserved.".into(),
            );
        }
        // Production startup registers Changes, Git history and Pull requests before registering this codec
        // and initializing the sole coordinator. No placeholder/presence flag or mutating preflight is used.
        Ok(())
    }
    fn restore(&self, value: &Value, window: &mut Window, cx: &mut App) {
        if let Err(error) = restore(value, window, cx) {
            WorkspacePanes::refuse_restore(error, cx);
        }
    }
    fn validate_deactivate(&self, cx: &App) -> Result<(), String> {
        if let Some(dock) = CenterPanes::dock(cx)
            && groups(&dock, cx).len() > 1
        {
            return Err("This split layout cannot yet be restored without changing its geometry. Its tabs and drafts remain open.".into());
        }
        Ok(())
    }
    fn deactivate(&self, window: &mut Window, cx: &mut App) {
        if let Some(dock) = CenterPanes::dock(cx) {
            remove_owned(&dock, window, cx);
        }
    }
}
fn groups(dock: &Entity<DockArea>, cx: &App) -> Vec<(NodeId, Vec<PanelId>)> {
    let mut groups = Vec::new();
    if let Some(tree) = dock.read(cx).layout(DockPlacement::Center) {
        tree.root().walk(&mut |node| {
            if let PaneRef::Tabs { panels, .. } = node.kind() {
                groups.push((node.id(), panels.to_vec()));
            }
        });
    }
    groups
}
fn center_ids(dock: &Entity<DockArea>, cx: &App) -> Vec<PanelId> {
    groups(dock, cx)
        .into_iter()
        .flat_map(|(_, ids)| ids)
        .collect()
}
fn remove<P: Panel>(id: PanelId, dock: &Entity<DockArea>, window: &mut Window, cx: &mut App) {
    let pane = dock
        .read(cx)
        .panel(id)
        .and_then(PanelHandle::of)
        .and_then(|pane| pane.view().downcast::<P>().ok());
    if let Some(pane) = pane {
        dock.update(cx, |dock, cx| dock.remove_panel(pane, window, cx));
    }
}
fn remove_owned(dock: &Entity<DockArea>, window: &mut Window, cx: &mut App) {
    for id in center_ids(dock, cx) {
        let name = dock.read(cx).panel(id).map(|panel| panel.panel_name(cx));
        match name {
            Some("Changes") => remove::<ChangesPane>(id, dock, window, cx),
            Some("Git history") => remove::<HistoryPane>(id, dock, window, cx),
            Some("Pull requests") => remove::<PrPane>(id, dock, window, cx),
            _ => {}
        }
    }
}
fn restore(value: &Value, window: &mut Window, cx: &mut App) -> Result<(), String> {
    let key = WorkspacePanes::key(cx)
        .cloned()
        .ok_or("The workspace checkout identity is unavailable.")?;
    let dock = CenterPanes::dock(cx).ok_or("The center dock is unavailable.")?;
    if value.is_null() {
        remove_owned(&dock, window, cx);
        return Ok(());
    }
    let tabs: Tabs =
        serde_json::from_value(value.clone()).map_err(|_| "The saved center tabs are invalid.")?;
    let mut built = Vec::new();
    for pane in &tabs.panes {
        let context = PanelBuildContext::new(dock.downgrade(), &pane.state, &pane.state.info);
        let panel = PanelRegistry::build_panel(&pane.state.panel_name, context, window, cx)
            .ok_or_else(|| {
                format!(
                    "The registered {} pane is unavailable.",
                    pane.state.panel_name
                )
            })?;
        built.push((pane.key.clone(), panel));
    }
    remove_owned(&dock, window, cx);
    let mut identities = HashMap::new();
    for (key, panel) in built {
        let id = panel.panel_id(cx);
        dock.update(cx, |dock, cx| {
            dock.add_panel_view(panel, DockPlacement::Center, None, window, cx)
        });
        identities.insert(key, id);
    }
    for id in center_ids(&dock, cx) {
        if dock
            .read(cx)
            .panel(id)
            .is_some_and(|pane| pane.panel_name(cx) == "Chat")
        {
            identities.insert("Chat".into(), id);
        }
    }
    for file in CenterPanes::file_views(cx) {
        if let Some(path) = file.read(cx).restored_workspace_path(&key) {
            let name = format!(
                "document:{}",
                serde_json::to_string(&(&key.machine, &key.checkout_root, path))
                    .expect("document identity")
            );
            identities.insert(name, PanelId::from(file.entity_id()));
        }
    }
    let order = tabs
        .order
        .iter()
        .map(|key| {
            identities
                .get(key)
                .copied()
                .ok_or_else(|| format!("The saved center tab {key} could not be restored."))
        })
        .collect::<Result<Vec<_>, _>>()?;
    let rank: HashMap<_, _> = order
        .iter()
        .enumerate()
        .map(|(rank, id)| (*id, rank))
        .collect();
    // Reorder the existing slots within each actual group. Never flatten device-local split geometry.
    for (node, ids) in groups(&dock, cx) {
        let slots: Vec<_> = ids
            .iter()
            .enumerate()
            .filter(|(_, id)| rank.contains_key(*id))
            .map(|(index, id)| (index, *id))
            .collect();
        let mut wanted: Vec<_> = slots.iter().map(|(_, id)| *id).collect();
        wanted.sort_by_key(|id| rank[id]);
        for ((index, _), id) in slots.into_iter().zip(wanted) {
            dock.update(cx, |dock, cx| {
                dock.move_panel(
                    id,
                    InsertTarget::Tabs {
                        node,
                        ix: Some(index),
                        activate: false,
                    },
                    window,
                    cx,
                )
            });
        }
    }
    let wanted: HashSet<_> = order.iter().copied().collect();
    let actual: Vec<_> = center_ids(&dock, cx)
        .into_iter()
        .filter(|id| wanted.contains(id))
        .collect();
    if actual != order {
        return Err(
            "The saved order crosses existing split groups; device-local geometry was preserved."
                .into(),
        );
    }
    if let Some(active) = tabs
        .active_key
        .and_then(|key| identities.get(&key).copied())
    {
        dock.update(cx, |dock, cx| dock.select_panel(active, window, cx));
    }
    Ok(())
}
#[cfg(test)]
#[path = "top_tabs_codec_tests.rs"]
mod tests;
