//! Pure old-layout cutover preflight. The caller must hold confirmed canonical saves and fresh live captures.
use crate::features::files::file_view::LegacyFileCapture;
use gpui_kit::component::dock::{DockAreaState, DockState, PanelInfo, PanelState};
use std::collections::HashSet;

pub(super) fn prune(
    state: &DockAreaState,
    captures: &[LegacyFileCapture],
) -> Result<DockAreaState, String> {
    let refs: Vec<_> = captures
        .iter()
        .map(|capture| (capture.legacy_session.as_str(), capture.path.as_str()))
        .collect();
    prune_refs(state, &refs)
}
pub(super) fn prune_refs(
    state: &DockAreaState,
    refs: &[(&str, &str)],
) -> Result<DockAreaState, String> {
    let confirmed: HashSet<_> = refs.iter().copied().collect();
    if confirmed.len() != refs.len() {
        return Err("Duplicate file references prevent layout migration.".into());
    }
    let mut seen = HashSet::new();
    let mut panel = |state: &PanelState| {
        prune_panel(state, &confirmed, &mut seen)?.ok_or_else(|| {
            "An old File occupies a dock root; its metadata was preserved.".to_string()
        })
    };
    let center = panel(&state.center)?;
    let mut dock = |state: &Option<DockState>| -> Result<Option<DockState>, String> {
        state
            .as_ref()
            .map(|state| {
                Ok(DockState::new(
                    panel(state.panel())?,
                    state.placement(),
                    state.size(),
                    state.open(),
                ))
            })
            .transpose()
    };
    Ok(DockAreaState {
        version: state.version,
        center,
        left_dock: dock(&state.left_dock)?,
        right_dock: dock(&state.right_dock)?,
        bottom_dock: dock(&state.bottom_dock)?,
    })
}
fn prune_panel(
    state: &PanelState,
    confirmed: &HashSet<(&str, &str)>,
    seen: &mut HashSet<(String, String)>,
) -> Result<Option<PanelState>, String> {
    if state.panel_name == "File" {
        let PanelInfo::Panel(info) = &state.info else {
            return Err("An old File has invalid metadata; the saved layout was preserved.".into());
        };
        let fields = info
            .as_object()
            .ok_or("An old File has no metadata; the saved layout was preserved.")?;
        if !state.children.is_empty()
            || fields
                .keys()
                .any(|key| !matches!(key.as_str(), "session" | "path"))
        {
            return Err("An old File contains additional unmigrated metadata; the saved layout was preserved.".into());
        }
        let session = fields
            .get("session")
            .and_then(serde_json::Value::as_str)
            .ok_or("An old File has no session reference.")?;
        let path = fields
            .get("path")
            .and_then(serde_json::Value::as_str)
            .ok_or("An old File has no path reference.")?;
        if !confirmed.contains(&(session, path)) || !seen.insert((session.into(), path.into())) {
            return Err(
                "An old File is unconfirmed or duplicated; the saved layout was preserved.".into(),
            );
        }
        return Ok(None);
    }
    let mut retained = Vec::new();
    let mut children = Vec::new();
    for (index, child) in state.children.iter().enumerate() {
        if let Some(child) = prune_panel(child, confirmed, seen)? {
            retained.push(index);
            children.push(child);
        }
    }
    let mut result = state.clone();
    if children.len() != state.children.len() {
        match &mut result.info {
            PanelInfo::Tabs { active_index } => {
                *active_index = retained
                    .iter()
                    .position(|index| *index >= *active_index)
                    .unwrap_or_else(|| retained.len().saturating_sub(1))
            }
            PanelInfo::Stack { sizes, .. } => {
                if sizes.len() != state.children.len() {
                    return Err("Old split sizes contradict their saved children; the layout was preserved.".into());
                }
                *sizes = retained.iter().map(|index| sizes[*index]).collect();
            }
            PanelInfo::Panel(_) => {
                return Err(
                    "An old File is nested in unknown pane metadata; the layout was preserved."
                        .into(),
                );
            }
        }
    }
    result.children = children;
    Ok(Some(result))
}
#[cfg(test)]
#[path = "legacy_layout_tests.rs"]
mod tests;
