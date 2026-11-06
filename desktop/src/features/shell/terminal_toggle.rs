//! Opening the Terminal, from the top row's button and the "..." menu: one action for both. It finds the Terminal
//! wherever it has been dragged, opens its dock on it, or shuts that dock when it's already showing.

use gpui_kit::assets::IconName;
use gpui_kit::component::button::{Button, ButtonVariants as _};
use gpui_kit::component::dock::{DockArea, DockPlacement, PaneRef, PanelId};
use gpui_kit::component::menu::PopupMenuItem;
use gpui_kit::component::{Icon, Selectable as _};
use gpui_kit::*;

use super::default_layout::TERMINAL;
use crate::core::ui::access::Control as _;
use crate::core::ui::access::control_ids::topbar;
use crate::features::workspaces::workspace_coordinator::WorkspaceCoordinator;

/// The button's size, as the drawer toggle beside it.
const SIZE: Pixels = px(30.);
const GLYPH: Pixels = px(17.);

/// What pressing the Terminal's button or menu item does.
#[derive(Debug, PartialEq)]
pub enum TerminalPress {
    /// Its dock is shut: open it on the Terminal.
    Open(DockPlacement),
    /// Its dock is open on another pane: switch to the Terminal.
    Show(DockPlacement),
    /// It's showing: shut its dock.
    Shut(DockPlacement),
    /// It's showing in the center, which has no dock to shut.
    Nothing,
}

/// What a press does, from where the Terminal is, whether its dock is open and whether it's showing.
pub fn press_terminal(placement: DockPlacement, dock_open: bool, shown: bool) -> TerminalPress {
    match (dock_open, shown) {
        (true, true) if placement == DockPlacement::Center => TerminalPress::Nothing,
        (true, true) => TerminalPress::Shut(placement),
        (true, false) => TerminalPress::Show(placement),
        (false, _) => TerminalPress::Open(placement),
    }
}

/// Where the Terminal is: its placement, its panel, whether its dock is open and whether its tab is the shown one.
/// The center counts as always open, since it has no dock to shut.
fn find(area: &DockArea, cx: &App) -> Option<(DockPlacement, PanelId, bool, bool)> {
    use DockPlacement::{Bottom, Center, Left, Right};
    [Bottom, Right, Left, Center]
        .into_iter()
        .find_map(|placement| {
            let mut found = None;
            area.layout(placement)?.root().walk(&mut |node| {
                if let PaneRef::Tabs { panels, active_ix } = node.kind() {
                    for (ix, id) in panels.iter().enumerate() {
                        if area
                            .panel(*id)
                            .is_some_and(|panel| panel.panel_name(cx) == TERMINAL)
                        {
                            found = Some((*id, ix == active_ix));
                        }
                    }
                }
            });
            let (id, active) = found?;
            let open = placement == Center || area.is_dock_open(placement);
            Some((placement, id, open, active && open))
        })
}

/// Whether the Terminal is showing, wherever it has been dragged.
pub fn terminal_showing(dock: &Entity<DockArea>, cx: &App) -> bool {
    find(dock.read(cx), cx).is_some_and(|(_, _, _, shown)| shown)
}

/// Opens the dock on the Terminal, switches to it, or shuts its dock, and leaves it ready to type when it shows.
pub fn toggle_terminal(dock: &Entity<DockArea>, window: &mut Window, cx: &mut App) {
    let Some((placement, id, open, shown)) = find(dock.read(cx), cx) else {
        return;
    };
    cx.defer(WorkspaceCoordinator::changed);
    let press = press_terminal(placement, open, shown);
    dock.update(cx, |dock, cx| match press {
        TerminalPress::Open(placement) => {
            dock.select_panel(id, window, cx);
            dock.toggle_dock(placement, window, cx);
        }
        TerminalPress::Show(_) => dock.select_panel(id, window, cx),
        TerminalPress::Shut(placement) => dock.toggle_dock(placement, window, cx),
        TerminalPress::Nothing => {}
    });
    if matches!(press, TerminalPress::Open(_) | TerminalPress::Show(_))
        && let Some(panel) = dock.read(cx).panel(id).cloned()
    {
        panel.focus_handle(cx).focus(window, cx);
    }
}

/// The top row's Terminal button, highlighted while the Terminal is showing.
pub fn button(dock: Entity<DockArea>, cx: &App) -> impl IntoElement {
    let showing = terminal_showing(&dock, cx);
    div().debug_selector(|| "terminal-toggle".into()).child(
        Button::new("terminal-toggle")
            .control(topbar::TERMINAL_TOGGLE, TERMINAL)
            .ghost()
            .icon(Icon::new(IconName::SquareTerminal).size(GLYPH))
            .size(SIZE)
            .rounded_full()
            .selected(showing)
            .tooltip(TERMINAL)
            .on_click(move |_, window, cx| toggle_terminal(&dock, window, cx)),
    )
}

/// The "..." menu's Terminal item, checked while the Terminal is showing. It has no icon: the kit draws an item's icon
/// in place of its check mark.
pub fn menu_item(dock: Entity<DockArea>, cx: &App) -> PopupMenuItem {
    PopupMenuItem::new(TERMINAL)
        .checked(terminal_showing(&dock, cx))
        .on_click(move |_, window, cx| toggle_terminal(&dock, window, cx))
}
