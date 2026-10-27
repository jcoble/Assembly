//! `default_state` owns the old app's fixed layout, its starting sizes and the pane names the saved layout and the
//! kit's panel registry use. Replaces frame.ts's region setup and size constants (SESSIONS_WIDTH, DOCK_HEIGHT),
//! routes/+page.svelte's drawer width (lines 144-147) and RightPanelTabs.svelte's tab list.
//!
//! Left dock: Sessions, open. Center: Chat. Right dock (the drawer): Files, Source control, Agents, Tasks, Worktrees,
//! Run, Context, History, closed at launch as in the old app. Bottom dock: Problems, Terminal, closed. Editor, browser
//! and diff tabs aren't in the default: they open beside Chat when the first one opens, as the old tab pane did.

use gpui_kit::component::dock::DockAreaState;
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

/// Every pane in the default layout.
pub const PANES: [&str; 12] =
    [SESSIONS, CHAT, FILES, SOURCE_CONTROL, AGENTS, TASKS, WORKTREES, RUN, CONTEXT, HISTORY, PROBLEMS, TERMINAL];

/// The sessions column opens so its card is 320 px wide (frame.ts SESSIONS_WIDTH).
pub const SESSIONS_WIDTH: Pixels = px(326.);
/// The drawer's card width (routes/+page.svelte drawerWidth).
pub const DRAWER_WIDTH: Pixels = px(320.);
/// What the bottom dock opens at (frame.ts DOCK_HEIGHT).
pub const DOCK_HEIGHT: Pixels = px(180.);

/// The default layout as the kit's saved-layout value, so the window loads it the same way as a saved one.
pub fn default_state() -> DockAreaState {
    todo!()
}
