//! What every feature shares: the socket client (`core/socket/`, the only code that opens a socket), the state each
//! fact has one owner for, the clock (`core/clock/`: the backend `clock` topic's subscriber and the one named
//! debounce, the only code that waits on time), the theme and the registries. A root module named `core` hides the
//! `core` crate in `main.rs` only: write `::core::` there.

// Unused until g0-host starts the client and the panes read the state.
#[expect(
    dead_code,
    reason = "nothing in the window uses these until g0-host starts the client"
)]
pub mod clock;
#[expect(
    dead_code,
    reason = "nothing in the window uses these until g0-host starts the client"
)]
pub mod socket;
#[expect(
    dead_code,
    reason = "nothing in the window uses these until g0-host starts the client"
)]
pub mod state;
