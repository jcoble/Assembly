//! `OpenFile`: open a file in the editor, at a line when given. The session says which machine and folder the path
//! is on; an outside file names its machine and real path itself.

use gpui_kit::*;

#[derive(Clone, PartialEq, Action)]
#[action(namespace = intents, no_json)]
pub struct OpenFile {
    pub session_id: String,
    pub path: String,
    pub expected_source: Option<crate::core::socket::row_source::ExpectedRowSource>,
    pub line: Option<u32>,
    pub column: Option<u32>,
    pub pinned: bool,
    /// A file outside any session's checkout, opened by machine and real path.
    pub outside: Option<OutsideFile>,
}

/// A file opened by machine and real absolute path rather than through a session.
#[derive(Clone, PartialEq, Debug)]
pub struct OutsideFile {
    /// The route machine name; `None` is this machine.
    pub machine: Option<String>,
    /// The real absolute path the owning machine answered.
    pub path: String,
}
