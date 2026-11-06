//! Reads the owner's actual center dock order and focus selection; keeps no second tab list.
use super::center_tabs::{Pane, Tabs, pane_key};
use crate::core::state::viewed_project::is_view;
use crate::core::{state::workspace_panes::WorkspaceKey, ui::center_panes::CenterPanes};
use crate::features::files::file_view::FileView;
use gpui_kit::component::dock::{DockPlacement, PaneRef, PanelHandle};
use gpui_kit::*;

pub(super) fn capture(key: &WorkspaceKey, window: &Window, cx: &App) -> Result<Tabs, String> {
    let dock = CenterPanes::dock(cx).ok_or("The center dock is unavailable.")?;
    let dock = dock.read(cx);
    let tree = dock
        .layout(DockPlacement::Center)
        .ok_or("The center layout is unavailable.")?;
    let retained = FileView::captured_workspace_identities(key, cx);
    let mut order = Vec::new();
    let mut panes = Vec::new();
    let mut choices = Vec::new();
    let mut failure = None;
    tree.root().walk(&mut |node| {
        let PaneRef::Tabs { panels, active_ix } = node.kind() else {
            return;
        };
        for (index, id) in panels.iter().enumerate() {
            let Some(panel) = dock.panel(*id).filter(|panel| panel.visible(cx)) else {
                continue;
            };
            let shown = index == active_ix;
            let focused = shown && panel.focus_handle(cx).contains_focused(window, cx);
            let tab = match panel.panel_name(cx) {
                "Chat" => Some("Chat".to_string()),
                "File" => {
                    let file = PanelHandle::of(panel)
                        .and_then(|panel| panel.view().downcast::<FileView>().ok());
                    match file {
                        // A viewed project's file isn't this workspace's, and stays open until the app closes.
                        Some(file) if is_view(file.read(cx).session()) => None,
                        Some(file) => match file.read(cx).workspace_identity(key) {
                            // A second tab on one document waits for its claim; the first stands for it.
                            Ok(Some(identity))
                                if retained.contains(&identity) && !order.contains(&identity) =>
                            {
                                Some(identity)
                            }
                            // A clean file excluded by the sole Files retention limit, a second tab on one document,
                            // or an outside file that belongs to no workspace.
                            Ok(_) => None,
                            Err(error) => {
                                failure = Some(error);
                                None
                            }
                        },
                        None => {
                            failure = Some("The file panel has no owning file view.".to_string());
                            None
                        }
                    }
                }
                "Changes" | "Git history" | "Pull requests" => {
                    let state = panel.dump(cx);
                    match pane_key(&state, key) {
                        Ok(key) => {
                            panes.push(Pane {
                                key: key.clone(),
                                state,
                            });
                            Some(key)
                        }
                        Err(error) => {
                            failure = Some(error);
                            None
                        }
                    }
                }
                _ => None, // Other generic panels remain dock-owned; this codec does not invent metadata for them.
            };
            if let Some(key) = &tab {
                order.push(key.clone());
            }
            choices.push((tab, shown, focused));
        }
    });
    if let Some(error) = failure {
        return Err(error);
    }
    // Identical to the owner's top_tabs focus, first-shown, then first-visible fallback.
    let selected = choices
        .iter()
        .position(|(_, _, focused)| *focused)
        .or_else(|| choices.iter().position(|(_, shown, _)| *shown))
        .unwrap_or(0);
    let active_key = choices.get(selected).and_then(|(key, _, _)| key.clone());
    Ok(Tabs {
        order,
        active_key,
        panes,
    })
}
