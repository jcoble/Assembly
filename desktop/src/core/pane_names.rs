//! The pane names: the one spelling the default layout, the saved layout and the kit's panel registry
//! (`register_panel`) all use. Here in core because the features that own these panes may not use the shell's
//! folder. Moved from g0-shape's features/shell/default_layout.rs.

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
