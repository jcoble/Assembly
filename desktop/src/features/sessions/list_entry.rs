//! `ListEntry` is one line of the grouped sessions list: a group's header, a project's header inside it, or a
//! session (by its id).

use super::status::Status;

#[derive(Clone, Debug, PartialEq, Eq)]
pub enum ListEntry {
    /// `open` is false only for a folded Settled.
    Group {
        status: Status,
        open: bool,
    },
    /// A project inside a group: its folder (the session's repo or folder until sessions carry a project) on its
    /// machine (`None` for this computer).
    Project {
        status: Status,
        folder: String,
        machine: Option<String>,
    },
    Session(String),
}
