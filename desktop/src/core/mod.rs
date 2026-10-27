//! What every feature shares: the socket client (`core/socket/`, the only code that opens a socket), the state each
//! fact has one owner for, the clock (`core/clock/`: the backend `clock` topic's subscriber and the one named
//! debounce, the only code that waits on time), the theme and the registries. A root module named `core` hides the
//! `core` crate in `main.rs` only: write `::core::` there.

// Files only until step 2 of lane g0-socket fills them in and the window uses them.
#[expect(
    dead_code,
    reason = "lane g0-socket step 1: files, names and types, no logic"
)]
pub mod clock;
#[expect(
    dead_code,
    reason = "lane g0-socket step 1: files, names and types, no logic"
)]
pub mod socket;
#[expect(
    dead_code,
    reason = "lane g0-socket step 1: files, names and types, no logic"
)]
pub mod state;
