//! `OpenFile`: open a file in the editor, at a line when given. The session says which machine and folder the path
//! is on; an outside file names its machine and real path itself.

use gpui_kit::*;

use crate::core::socket::row_source::ExpectedRowSource;

#[derive(Clone, PartialEq, Action)]
#[action(namespace = intents, no_json)]
pub struct OpenFile {
    pub session_id: String,
    pub path: String,
    pub expected_source: Option<ExpectedRowSource>,
    pub line: Option<u32>,
    pub column: Option<u32>,
    pub pinned: bool,
    /// A file outside any session's checkout, opened by machine and real path.
    pub outside: Option<OutsideFile>,
}

/// Whether a path goes to the backend as a path target rather than opening relative to a session.
pub fn names_its_own_place(path: &str) -> bool {
    std::path::Path::new(path).is_absolute() || path.starts_with('~')
}

impl OpenFile {
    /// A file link's intent from content shown in `session` (empty with no session selected): any path opens with a
    /// session, and with none only a path that names its own place (absolute or `~`), which the one resolver opens by
    /// machine and path. A `//host/...` path is never a file.
    pub fn link(
        session: &str,
        path: &str,
        expected_source: Option<ExpectedRowSource>,
    ) -> Option<OpenFile> {
        let opens = !path.starts_with("//") && (!session.is_empty() || names_its_own_place(path));
        opens.then(|| OpenFile {
            session_id: session.to_string(),
            path: path.to_string(),
            expected_source,
            line: None,
            column: None,
            pinned: true,
            outside: None,
        })
    }
}

/// A file opened by machine and real absolute path rather than through a session.
#[derive(Clone, PartialEq, Debug)]
pub struct OutsideFile {
    /// The route machine name; `None` is this machine.
    pub machine: Option<String>,
    /// The real absolute path the owning machine answered.
    pub path: String,
}
