//! `ErrorCode` is an `error` frame's `code` (dotnet/Assembly.Infrastructure/Socket/SocketFrames.cs, `ErrorFrame`),
//! less `cancelled`, which settles a call `Aborted`.

#[derive(Clone, Debug, PartialEq, Eq)]
pub enum ErrorCode {
    NotFound,
    BadArgs,
    /// Also a call made while `Disconnected`: it fails at once, as on the page.
    Failed,
    Timeout,
    /// An actual allowlisted session target refusal; the backend code remains inspectable.
    Refused {
        code: String,
    },
    /// SocketDispatcher emits this when completion must be reconciled before retry.
    Uncertain {
        machine: String,
    },
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
            "source_changed"
            | "native_card_changed"
            | "native_history_unavailable"
            | "owner_validation_required"
            | "unknown_engine_session"
            | "unknown_session"
            | "nonresumable" => ErrorCode::Refused { code: code.into() },
            "uncertain" => ErrorCode::Uncertain {
                machine: machine.unwrap_or_default(),
            },
            "unreachable" => ErrorCode::Unreachable {
                machine: machine.unwrap_or_default(),
            },
            _ => ErrorCode::Failed,
        }
    }
}

#[cfg(test)]
#[path = "tests/error_code_tests.rs"]
mod tests;
