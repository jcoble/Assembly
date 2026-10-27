//! What every feature shares: the socket client (`core/socket/`, the only code that opens a socket), the state each
//! fact has one owner for, the backend clock's subscriber (`core/clock/`), the theme and the registries. A root module
//! named `core` hides the `core` crate in `main.rs` only: write `::core::` there.

pub mod backend;
// Unused until the panes read the state.
#[expect(
    dead_code,
    reason = "nothing in the window uses these until the panes read the state"
)]
pub mod clock;
pub mod commands;
pub mod intents;
pub mod native_views;
#[expect(
    dead_code,
    reason = "nothing in the window uses these until the panes read the state"
)]
pub mod socket;
#[expect(
    dead_code,
    reason = "nothing in the window uses these until the panes read the state"
)]
pub mod state;
pub mod window_root;
