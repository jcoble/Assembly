//! The bottom dock's Terminal tab: a shell for the open local session, run by the backend's `terminal` feature
//! (dotnet/Assembly.Application/Features/Terminal/) and drawn here from an `alacritty_terminal` grid.

pub mod terminal_draw;
pub mod terminal_grid;
pub mod terminal_keys;
pub mod terminal_link;
pub mod terminal_pane;
