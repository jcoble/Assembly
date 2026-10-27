//! What every feature shares: the socket client (`core/socket/`, the only code that opens a socket), the state each
//! fact has one owner for, the theme and the registries. A root module named `core` hides the `core` crate in
//! `main.rs` only: write `::core::` there.

pub mod backend;
pub mod native_views;
pub mod window_root;
