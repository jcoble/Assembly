//! The window's one socket to the backend (plan 5.1), and the only code that opens one. It mirrors the page's
//! app/src/lib/socket.ts: `hello` with the token, `call` answered by `result` or `error`, `cancel`, latest-wins keys,
//! and `sub`/`unsub` for a topic's `state` and `change` frames, plus the `rows` topic with `after` and its seq gap
//! check. The backend's side is dotnet/Assembly.Infrastructure/Socket/.
//!
//! One client thread owns the connection, the calls waiting on an answer and the subscriptions. It blocks on one
//! channel of `Event`s with no timeout: the window's requests, each frame the reader thread read, and the socket
//! closing. When an open socket drops it tries once at once; if that fails it waits for `reconnect`, which the
//! launcher sends on the backend's ready line or after restarting it, and the Reconnect button sends too. There's no
//! backoff loop, timer or sleep.

pub mod call_failure;
pub mod client;
mod client_thread;
mod connection;
pub mod connection_state;
pub mod endpoint;
pub mod error_code;
mod event;
mod in_frame;
mod out_frame;
pub mod outcome;
pub mod pending_call;
pub mod row;
pub mod row_update;
mod rows_feed;
pub mod topic_subscription;
pub mod update;
