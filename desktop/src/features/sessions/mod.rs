//! The sessions column: filtered history with optional status and project groups, as `sessions/rail` lists it.
//! Picking one sets the selected session; the session view follows it. Replaces the session list in routes/+page.svelte
//! and the spike's sessions/panel.rs.

pub mod history;
pub mod list_entries;
pub mod list_entry;
pub mod new_session_form;
mod pane_actions;
mod pane_lines;
pub mod presence;
pub mod project_header;
pub mod rail_page;
pub mod session_row;
pub mod sessions_pane;
pub mod status;
pub mod view_options;
