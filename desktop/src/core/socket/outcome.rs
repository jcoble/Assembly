//! `Outcome` is what a call comes back with, as in socket.ts. `Aborted` (a newer call with the same key took its
//! place, or the socket closed while it waited) isn't an error and shows nothing. A call the socket dropped is never
//! sent again: it may have reached the backend.

use super::call_failure::CallFailure;

#[derive(Debug)]
pub enum Outcome<T> {
    Ok(T),
    Failed(CallFailure),
    Aborted,
}
