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
    /// A link's text as written when a line suffix was read from it: a file of that whole name, if there is one, opens
    /// instead of `path` at the line (`file_link_whole`).
    pub whole: Option<String>,
}

/// Whether a path goes to the backend as a path target rather than opening relative to a session: an absolute path,
/// or `~` and `~/...`, the only home paths, as the backend's `FileOwner.ExpandHome` reads them. A session file named
/// `~notes.md` or `~$plan.docx` stays the session's.
pub fn names_its_own_place(path: &str) -> bool {
    std::path::Path::new(path).is_absolute() || path == "~" || path.starts_with("~/")
}

impl OpenFile {
    /// A file link's intent from content shown in `session` (empty with no session selected): any path opens with a
    /// session, and with none only a path that names its own place (absolute or `~`), which the one resolver opens by
    /// machine and path. A `//host/...` path is never a file. A link ending in a line, or a line and column, opens
    /// there, unless a file of the link's whole name exists.
    pub fn link(
        session: &str,
        link: &str,
        expected_source: Option<ExpectedRowSource>,
    ) -> Option<OpenFile> {
        let (path, line, column) = split_line(link);
        let opens = !path.starts_with("//") && (!session.is_empty() || names_its_own_place(path));
        opens.then(|| OpenFile {
            session_id: session.to_string(),
            path: path.to_string(),
            expected_source,
            line,
            column,
            pinned: true,
            outside: None,
            whole: line.map(|_| link.to_string()),
        })
    }
}

/// A link's path and the line it ends in, as the old `splitConversationFileReference` read it: `:N`, `#N` or `#LN`
/// (either case) with no leading zero. `:N:M` is a line and column, as compilers print it. Anything else after the
/// path, such as `:0` or `:x`, stays part of the path.
fn split_line(link: &str) -> (&str, Option<u32>, Option<u32>) {
    let (path, line) = split_number(link, true);
    let Some(column) = line else {
        return (link, None, None);
    };
    match split_number(path, false) {
        (path, Some(line)) => (path, Some(line), Some(column)),
        _ => (path, Some(column), None),
    }
}

/// `text` less a trailing `:N` (or, with `marks`, `#N` or `#LN`), and N.
fn split_number(text: &str, marks: bool) -> (&str, Option<u32>) {
    let digits = text.len() - text.trim_end_matches(|c: char| c.is_ascii_digit()).len();
    let (rest, number) = text.split_at(text.len() - digits);
    let path = (rest.strip_suffix(':'))
        .or_else(|| rest.strip_suffix("#L").filter(|_| marks))
        .or_else(|| rest.strip_suffix("#l").filter(|_| marks))
        .or_else(|| rest.strip_suffix('#').filter(|_| marks));
    match (path, number.parse::<u32>()) {
        (Some(path), Ok(line)) if !number.starts_with('0') => (path, Some(line)),
        _ => (text, None),
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
