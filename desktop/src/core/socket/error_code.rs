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

impl ErrorCode {
    /// An `error` frame's code; one this client doesn't know is `Failed`.
    pub fn from_wire(code: &str, machine: Option<String>) -> Self {
        match code {
            "not_found" => ErrorCode::NotFound,
            "bad_args" => ErrorCode::BadArgs,
            "timeout" => ErrorCode::Timeout,
            "unreachable" => ErrorCode::Unreachable {
                machine: machine.unwrap_or_default(),
            },
            _ => ErrorCode::Failed,
        }
    }
}
