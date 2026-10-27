//! `DefaultLayout` owns the old app's fixed layout, its starting sizes and the pane names the saved layout and the
//! kit's panel registry use. Replaces frame.ts's region setup and size constants (SESSIONS_WIDTH, TOOLS_WIDTH,
//! DOCK_HEIGHT) and RightPanelTabs.svelte's tab list.
//!
//! Left dock: Sessions. Center: Chat. Right dock (the drawer): Files, Source control, Agents, Tasks, Worktrees, Run,
//! Context, History. Bottom dock: Problems, Terminal. Editor, browser and diff tabs aren't in the default: they open
//! beside Chat when the first one opens, as the old tab pane did.

use gpui_kit::component::dock::DockArea;
use gpui_kit::*;

pub const SESSIONS: &str = "Sessions";
pub const CHAT: &str = "Chat";
pub const FILES: &str = "Files";
pub const SOURCE_CONTROL: &str = "Source control";
pub const AGENTS: &str = "Agents";
pub const TASKS: &str = "Tasks";
pub const WORKTREES: &str = "Worktrees";
pub const RUN: &str = "Run";
pub const CONTEXT: &str = "Context";
pub const HISTORY: &str = "History";
pub const PROBLEMS: &str = "Problems";
pub const TERMINAL: &str = "Terminal";

/// Every pane in the default layout, in the order the placeholders are registered.
pub const PANES: [&str; 12] =
    [SESSIONS, CHAT, FILES, SOURCE_CONTROL, AGENTS, TASKS, WORKTREES, RUN, CONTEXT, HISTORY, PROBLEMS, TERMINAL];

/// The sessions column opens so its card is 320 px wide (frame.ts SESSIONS_WIDTH).
pub const SESSIONS_WIDTH: Pixels = px(326.);
/// The drawer opens wide enough for a diff or an editor beside the chat (frame.ts TOOLS_WIDTH).
pub const DRAWER_WIDTH: Pixels = px(720.);
/// What the bottom dock opens at (frame.ts DOCK_HEIGHT).
pub const DOCK_HEIGHT: Pixels = px(180.);

pub struct DefaultLayout;

impl DefaultLayout {
    /// Installs the default layout on an empty DockArea: the four regions, their tabs and their sizes.
    pub fn build(_dock: &mut DockArea, _window: &mut Window, _cx: &mut Context<DockArea>) {
        todo!()
    }
}
