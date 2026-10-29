//! `DrawerButtons` are the drawer's pane buttons at the right of the title row: one 26 px icon button per drawer pane,
//! the shown one lit while the drawer is open, then Terminal, which opens and closes the bottom dock. A pane's button
//! opens the drawer on it, shows it, or closes the drawer when it's the one shown (`press`). The drawer draws no tabs of
//! its own any more, so its panes no longer drag from a tab (accepted for the approved look).

use gpui_kit::assets::IconName;
use gpui_kit::component::dock::{DockArea, DockPlacement, PaneRef, PanelId};
use gpui_kit::component::tooltip::Tooltip;
use gpui_kit::component::{ActiveTheme as _, Icon, h_flex};
use gpui_kit::prelude::FluentBuilder as _;
use gpui_kit::*;

use super::default_layout::{
    AGENTS, CONTEXT, HISTORY, RUN, SOURCE_CONTROL, TASKS, TERMINAL, WORKTREES,
};

/// The approved mock's icon button (`.ib`): 26 px, 8 px corners, a 15 px icon in the muted colour.
const SIZE: Pixels = px(26.);

/// What pressing a drawer pane's button does.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Press {
    /// The drawer is shut: open it on this pane.
    Open,
    /// The drawer shows another pane: show this one.
    Show,
    /// The drawer shows this pane: close it.
    Close,
}

/// What a press does, from whether the drawer is open and the pane is the one it shows.
pub fn press(open: bool, shown: bool) -> Press {
    match (open, shown) {
        (false, _) => Press::Open,
        (true, true) => Press::Close,
        (true, false) => Press::Show,
    }
}

#[derive(IntoElement)]
pub struct DrawerButtons {
    pub dock: Entity<DockArea>,
}

impl RenderOnce for DrawerButtons {
    fn render(self, _: &mut Window, cx: &mut App) -> impl IntoElement {
        let area = self.dock.read(cx);
        let (open, terminal_open) = (
            area.is_dock_open(DockPlacement::Right),
            area.is_dock_open(DockPlacement::Bottom),
        );
        // Each visible drawer pane: its id, its name, and whether its tab group shows it.
        let mut panes: Vec<(PanelId, &'static str, bool)> = Vec::new();
        if let Some(tree) = area.layout(DockPlacement::Right) {
            tree.root().walk(&mut |node| {
                if let PaneRef::Tabs { panels, active_ix } = node.kind() {
                    for (ix, id) in panels.iter().enumerate() {
                        if let Some(panel) = area.panel(*id).filter(|panel| panel.visible(cx)) {
                            panes.push((*id, panel.panel_name(cx), ix == active_ix));
                        }
                    }
                }
            });
        }
        let theme = cx.theme();
        let (lit, color, radius) = (theme.secondary_hover, theme.muted_foreground, px(8.));
        let button = |name: &'static str, glyph: IconName, on: bool| {
            div()
                .id(name)
                .debug_selector(move || format!("drawer-button-{name}"))
                .size(SIZE)
                .flex()
                .items_center()
                .justify_center()
                .rounded(radius)
                .text_color(color)
                .cursor_pointer()
                .when(on, |button| button.bg(lit))
                .hover(|button| button.bg(lit))
                .tooltip(move |window, cx| Tooltip::new(name).build(window, cx))
                .child(Icon::new(glyph).size(px(15.)))
        };
        let panes = panes.into_iter().map(|(id, name, shown)| {
            let dock = self.dock.clone();
            button(name, icon(name), open && shown).on_click(move |_, window, cx| {
                dock.update(cx, |dock, cx| match press(open, shown) {
                    Press::Open => {
                        dock.select_panel(id, window, cx);
                        dock.toggle_dock(DockPlacement::Right, window, cx);
                    }
                    Press::Show => dock.select_panel(id, window, cx),
                    Press::Close => dock.toggle_dock(DockPlacement::Right, window, cx),
                })
            })
        });
        let dock = self.dock.clone();
        let terminal = button(TERMINAL, IconName::SquareTerminal, terminal_open)
            .ml_2()
            .on_click(move |_, window, cx| {
                dock.update(cx, |dock, cx| {
                    dock.toggle_dock(DockPlacement::Bottom, window, cx)
                })
            });
        h_flex().gap_1().children(panes).child(terminal)
    }
}

/// The old app's icon for each drawer pane; a pane it didn't have gets a dot.
fn icon(name: &str) -> IconName {
    match name {
        SOURCE_CONTROL => IconName::GitBranch,
        AGENTS => IconName::Bot,
        TASKS => IconName::ListChecks,
        WORKTREES => IconName::GitFork,
        RUN => IconName::CirclePlay,
        CONTEXT => IconName::Gauge,
        // Lucide's history icon, under the kit's name for it.
        HISTORY => IconName::RotateCcwClock,
        _ => IconName::Dot,
    }
}
