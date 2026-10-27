//! `ConnectionState` is whether the window has the backend: g0-host shows "Disconnected" with its Reconnect button
//! from it.

#[derive(Clone, Debug, PartialEq, Eq)]
pub enum ConnectionState {
    /// Opening the socket, at start or after a drop.
    Connecting,
    /// Welcomed by the backend, which names its machine.
    Connected { machine: String },
    /// The socket dropped and the one try to reopen it failed: it waits for `SocketClient::reconnect`.
    Disconnected,
}
