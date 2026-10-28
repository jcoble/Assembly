//! The window's one socket to the backend (plan 5.1), and the only code that opens one. It mirrors the page's
//! app/src/lib/socket.ts: `hello` with the token, `call` answered by `result` or `error`, `cancel`, latest-wins keys,
//! and `sub`/`unsub` for a topic's `state` and `change` frames, plus the `rows` topic with `after` and its seq gap
//! check. The backend's side is dotnet/Assembly.Infrastructure/Socket/.
//!
//! One client thread owns the connection, the calls waiting on an answer and the subscriptions. It blocks on one
//! channel of `Event`s: the window's requests, each frame the reader thread read, and the socket
//! closing. When an open socket drops it tries once at once; if that fails it tries again after a short wait that
//! doubles up to 5 s, or at once on `reconnect`, which the launcher sends on the backend's ready line.
//!
//! The same client, a second time, holds `/socket/bulk`, which alone carries the large calls (`client.rs`).

pub mod call_failure;
pub mod client;
mod client_thread;
#[cfg(test)]
mod client_thread_tests;
mod connection;
pub mod connection_state;
pub mod endpoint;
pub mod error_code;
mod event;
#[cfg(test)]
pub(crate) mod fake_backend;
mod in_frame;
mod open_failure;
mod out_frame;
pub mod outcome;
pub mod pending_call;
#[cfg(test)]
pub(crate) mod real_backend;
#[cfg(test)]
pub(crate) mod real_backend_cancel_tests;
#[cfg(test)]
mod real_backend_terminal_tests;
#[cfg(test)]
mod real_backend_tests;
pub mod row;
pub mod row_update;
mod rows_feed;
#[cfg(test)]
mod rows_tests;
#[cfg(test)]
mod test_client;
#[cfg(test)]
pub(crate) mod test_wait;
pub mod topic_subscription;
pub mod update;
