//! Pure old-layout cutover preflight. The caller must hold confirmed canonical saves and fresh live captures.
use crate::features::files::file_view::{FileView, LegacyFileCapture, saved_alias};
use gpui_kit::component::dock::{DockAreaState, DockState, PanelInfo, PanelState};
use std::collections::HashSet;

pub(super) fn prune(
    state: &DockAreaState,
    captures: &[LegacyFileCapture],
) -> Result<DockAreaState, String> {
    let refs: Vec<_> = captures
        .iter()
        .map(|capture| FileView::session_tab_key(&capture.legacy_session, &capture.path))
        .collect();
    // Old layouts hold session files only; an outside file opened with no workspace isn't kept.
    prune_refs(&strip_outside(state)?, &refs)
}
/// Drops the File panes whose tab keys are confirmed in `refs`, failing on any other File pane.
pub(super) fn prune_refs(state: &DockAreaState, refs: &[String]) -> Result<DockAreaState, String> {
    let confirmed: HashSet<_> = refs.iter().map(String::as_str).collect();
    if confirmed.len() != refs.len() {
        return Err("Duplicate file references prevent layout migration.".into());
    }
    let mut seen = HashSet::new();
    rebuild(state, &mut |state| {
        confirmed_file(state, &confirmed, &mut seen)
    })
}
/// Drops the outside File panes, which belong to no workspace when none is open, so they aren't saved.
pub(super) fn strip_outside(state: &DockAreaState) -> Result<DockAreaState, String> {
    rebuild(state, &mut |state| {
        Ok(matches!(&state.info, PanelInfo::Panel(info) if info["outside"] == true))
    })
}
/// The layout without the File panes `drop` says to drop. `drop` sees every File pane.
fn rebuild(
    state: &DockAreaState,
    drop: &mut dyn FnMut(&PanelState) -> Result<bool, String>,
) -> Result<DockAreaState, String> {
    let mut panel = |state: &PanelState| {
        prune_panel(state, drop)?.ok_or_else(|| {
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
/// Whether a File pane is a confirmed, once-seen tab: `Ok(true)` drops it.
fn confirmed_file(
    state: &PanelState,
    confirmed: &HashSet<&str>,
    seen: &mut HashSet<String>,
) -> Result<bool, String> {
    let PanelInfo::Panel(info) = &state.info else {
        return Err("An old File has invalid metadata; the saved layout was preserved.".into());
    };
    let fields = info
        .as_object()
        .ok_or("An old File has no metadata; the saved layout was preserved.")?;
    if !state.children.is_empty() {
        return Err(
            "An old File contains additional unmigrated metadata; the saved layout was preserved."
                .into(),
        );
    }
    if fields.get("outside").is_none() {
        if fields
            .keys()
            .any(|key| !matches!(key.as_str(), "session" | "path"))
        {
            return Err("An old File contains additional unmigrated metadata; the saved layout was preserved.".into());
        }
        fields
            .get("session")
            .and_then(serde_json::Value::as_str)
            .ok_or("An old File has no session reference.")?;
        fields
            .get("path")
            .and_then(serde_json::Value::as_str)
            .ok_or("An old File has no path reference.")?;
    }
    let alias = saved_alias(fields).ok_or(
        "An old File contains additional unmigrated metadata; the saved layout was preserved.",
    )?;
    if !confirmed.contains(alias.as_str()) || !seen.insert(alias) {
        return Err(
            "An old File is unconfirmed or duplicated; the saved layout was preserved.".into(),
        );
    }
    Ok(true)
}
fn prune_panel(
    state: &PanelState,
    drop: &mut dyn FnMut(&PanelState) -> Result<bool, String>,
) -> Result<Option<PanelState>, String> {
    if state.panel_name == "File" {
        return Ok((!drop(state)?).then(|| state.clone()));
    }
    let mut retained = Vec::new();
    let mut children = Vec::new();
    for (index, child) in state.children.iter().enumerate() {
        if let Some(child) = prune_panel(child, drop)? {
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
