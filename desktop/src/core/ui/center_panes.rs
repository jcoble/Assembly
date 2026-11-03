//! `CenterPanes` opens a pane in the center's tab row, beside Chat, for any feature: a file, a diff, later an editor or
//! a browser. Each pane is opened under a stable key, so opening the same key again brings its tab forward instead of
//! adding a second one. The tab's chip shows the pane's `Panel::title`. The dock holds the pane's only handle, so
//! closing its tab drops the view and the tasks it holds. The shell's Frame sets it up with the window's dock.

use std::collections::HashMap;

use gpui_kit::component::dock::{DockArea, DockPlacement, Panel, PanelId, panel_handle};
use gpui_kit::*;

#[cfg(test)]
#[path = "center_panes_tests.rs"]
mod tests;

pub struct CenterPanes {
    dock: WeakEntity<DockArea>,
    /// The pane last opened under each key; one the dock no longer holds was closed.
    open: HashMap<String, PanelId>,
    file_preview: Option<PanelId>,
}

impl Global for CenterPanes {}

impl CenterPanes {
    pub fn init(dock: &Entity<DockArea>, cx: &mut App) {
        cx.set_global(CenterPanes {
            dock: dock.downgrade(),
            open: HashMap::new(),
            file_preview: None,
        });
    }

    /// Brings forward the pane open under `key`, or builds one with `build` and adds it to the center's first tab row,
    /// in front. Returns the pane's id, or `None` before the window's dock is set up.
    pub fn open<P: Panel>(
        key: impl Into<String>,
        window: &mut Window,
        cx: &mut App,
        build: impl FnOnce(&mut Window, &mut App) -> Entity<P>,
    ) -> Option<PanelId> {
        let key = key.into();
        let panes = cx.try_global::<CenterPanes>()?;
        let dock = panes.dock.upgrade()?;
        if let Some(&id) = panes.open.get(&key)
            && dock.read(cx).panel(id).is_some()
        {
            dock.update(cx, |dock, cx| dock.select_panel(id, window, cx));
            return Some(id);
        }
        let pane = build(window, cx);
        let id = PanelId::from(pane.entity_id());
        dock.update(cx, |dock, cx| {
            dock.add_panel_view(panel_handle(pane), DockPlacement::Center, None, window, cx)
        });
        Self::remember(key, id, cx);
        Some(id)
    }

    /// Files claims its real machine/checkout/path key after the owning backend answers.
    /// Session aliases then bring that document forward through the existing open path.
    pub fn claim_file(
        document_key: String,
        session_alias: String,
        id: PanelId,
        cx: &mut App,
    ) -> PanelId {
        let Some(panes) = cx.try_global::<Self>() else {
            return id;
        };
        let existing = panes.open.get(&document_key).copied().filter(|existing| {
            panes
                .dock
                .upgrade()
                .is_some_and(|dock| dock.read(cx).panel(*existing).is_some())
        });
        let selected = existing.unwrap_or(id);
        let panes = cx.global_mut::<Self>();
        panes.open.insert(document_key, selected);
        panes.open.insert(session_alias, selected);
        selected
    }

    pub fn file_preview(cx: &App) -> Option<PanelId> {
        cx.try_global::<Self>().and_then(|panes| panes.file_preview)
    }

    pub fn set_file_preview(id: PanelId, cx: &mut App) {
        if cx.has_global::<Self>() {
            cx.global_mut::<Self>().file_preview = Some(id);
        }
    }

    pub fn pin_file(id: PanelId, cx: &mut App) {
        if let Some(panes) = cx.try_global::<Self>()
            && panes.file_preview == Some(id)
        {
            cx.global_mut::<Self>().file_preview = None;
        }
    }

    /// The window's dock, once Frame has set it up: the session header's sessions button toggles its left side.
    pub fn dock(cx: &App) -> Option<Entity<DockArea>> {
        cx.try_global::<CenterPanes>()?.dock.upgrade()
    }

    /// Records a pane under its key when it was built some other way, as a pane restored from the saved layout is, so
    /// opening that key finds it.
    pub fn remember(key: impl Into<String>, id: PanelId, cx: &mut App) {
        if cx.has_global::<CenterPanes>() {
            cx.global_mut::<CenterPanes>().open.insert(key.into(), id);
        }
    }
}
