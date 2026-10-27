//! `CallFailure` is why a call failed: its code and the backend's message.

use super::error_code::ErrorCode;

#[derive(Clone, Debug)]
pub struct CallFailure {
    pub code: ErrorCode,
    pub message: String,
}
