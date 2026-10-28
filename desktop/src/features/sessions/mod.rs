//! The sessions column: every session, newest first, from the sessions list state lane g0-socket owns. Picking one
//! sets the selected session there; the session view follows it. Replaces the session list in routes/+page.svelte
//! and the spike's sessions/panel.rs.

pub mod group_header;
pub mod history;
pub mod list_entries;
pub mod list_entry;
pub mod new_session_form;
mod pane_actions;
mod pane_lines;
pub mod presence;
pub mod project_header;
pub mod session_row;
pub mod sessions_pane;
pub mod status;
