//! `ErrorCode` is an `error` frame's `code` (dotnet/Assembly.Infrastructure/Socket/SocketFrames.cs, `ErrorFrame`),
//! less `cancelled`, which settles a call `Aborted`.

#[derive(Clone, Debug, PartialEq, Eq)]
pub enum ErrorCode {
    NotFound,
    BadArgs,
    /// Also a call made while `Disconnected`: it fails at once, as on the page.
    Failed,
    Timeout,
    /// The machine the call went to didn't answer. For a send, create or stop the outcome is unknown (plan 5.10):
    /// the `rows` and `sessions` topics tell what happened.
    Unreachable {
        machine: String,
    },
}
