//! The sessions column: every session, newest first, from the sessions list state lane g0-socket owns. Picking one
//! sets the selected session there; the session view follows it. Replaces the session list in routes/+page.svelte
//! and the spike's sessions/panel.rs.

pub mod new_session_form;
pub mod session_row;
pub mod sessions_pane;
