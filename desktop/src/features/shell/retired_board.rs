//! Saved layouts from when the Board was a center pane: its tabs go, and everything else stays as it was.
use gpui_kit::component::dock::{DockAreaState, DockState, PanelInfo, PanelState};

/// The retired saved pane's name.
pub const BOARD: &str = "Build Board";

/// Removes only the retired Board tabs from an otherwise compatible saved layout.
pub fn remove_saved_panel(state: &mut DockAreaState) {
    fn prune(mut panel: PanelState) -> Option<PanelState> {
        if panel.panel_name == BOARD {
            return None;
        }
        let old_len = panel.children.len();
        let mut kept = Vec::new();
        panel.children = panel
            .children
            .into_iter()
            .enumerate()
            .filter_map(|(index, child)| prune(child).inspect(|_| kept.push(index)))
            .collect();
        if kept.len() != old_len {
            match &mut panel.info {
                PanelInfo::Tabs { active_index } => {
                    *active_index = kept
                        .iter()
                        .position(|index| *index == *active_index)
                        .unwrap_or_else(|| {
                            kept.iter()
                                .take_while(|index| **index < *active_index)
                                .count()
                                .min(kept.len().saturating_sub(1))
                        });
                }
                PanelInfo::Stack { sizes, .. } => {
                    *sizes = kept
                        .iter()
                        .filter_map(|index| sizes.get(*index).copied())
                        .collect();
                }
                PanelInfo::Panel(_) => {}
            }
            if kept.is_empty()
                && matches!(panel.info, PanelInfo::Tabs { .. } | PanelInfo::Stack { .. })
            {
                return None;
            }
        }
        Some(panel)
    }
    state.center = prune(std::mem::take(&mut state.center)).unwrap_or_else(|| PanelState {
        panel_name: "TabPanel".into(),
        children: Vec::new(),
        info: PanelInfo::tabs(0),
    });
    for dock in [
        &mut state.left_dock,
        &mut state.right_dock,
        &mut state.bottom_dock,
    ] {
        *dock = dock.take().and_then(|saved| {
            prune(saved.panel().clone())
                .map(|panel| DockState::new(panel, saved.placement(), saved.size(), saved.open()))
        });
    }
}

#[cfg(test)]
#[path = "retired_board_tests.rs"]
mod tests;
