//! `BackendReady` is what the launcher hands the socket client's `reconnect()` each time a backend is up: its
//! address and its token. It has no `Debug` or `Display`, so the token can't reach a log.

pub struct BackendReady {
    /// The address from the backend's `listening on <address>` line.
    pub address: String,
    pub token: String,
}
