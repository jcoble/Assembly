//! `OpenFailure` is why a socket didn't open: the backend refused the hello with a close code, or anything else.

pub(super) enum OpenFailure {
    Refused(u16),
    Failed(String),
}
