//! `ConnectionState` is whether the window has the backend: g0-host shows "Disconnected" with its Reconnect button
//! from it.

#[derive(Clone, Debug, PartialEq, Eq)]
pub enum ConnectionState {
    /// Opening the socket, at start or after a drop.
    Connecting,
    /// Welcomed by the backend, which names its machine.
    Connected { machine: String },
    /// The socket dropped and the try to reopen it at once failed: it tries again after a short wait, or on
    /// `SocketClient::reconnect`.
    Disconnected,
    /// The backend refused the hello: 4401, the token isn't its token, or 4400, this client speaks another protocol.
    /// Trying again with the same can't help, so it waits for `SocketClient::reconnect`.
    Refused { code: u16 },
}
