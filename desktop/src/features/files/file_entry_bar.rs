//! The line above the tree that names a new file or folder, or renames one, as the old panel's did: what a typed
//! name does once it's committed.
use super::file_actions::named_target;

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum EntryKind {
    NewFile,
    NewFolder,
    Rename,
}

/// What the bar is naming: something new in `parent`, or `original` renamed in place.
#[derive(Clone, Debug, PartialEq)]
pub struct EntryBar {
    pub kind: EntryKind,
    pub parent: String,
    pub original: Option<String>,
}

#[derive(Debug, PartialEq)]
pub enum EntryOutcome {
    /// Nothing typed, or only `/`: the bar closes and nothing runs.
    Dropped,
    /// A rename to the same name: the bar closes and nothing runs.
    Unchanged,
    /// A name the backend would refuse; the bar stays and says why.
    Invalid(&'static str),
    Run {
        kind: &'static str,
        path: String,
        destination: Option<String>,
    },
}

/// The last part of a relative path.
pub fn leaf(path: &str) -> &str {
    path.rsplit('/').next().unwrap_or(path)
}

/// The folder holding a relative path; the root is "".
pub fn parent_of(path: &str) -> &str {
    path.rsplit_once('/').map_or("", |(parent, _)| parent)
}

impl EntryBar {
    pub fn new_in(kind: EntryKind, folder: &str) -> Self {
        Self {
            kind,
            parent: folder.to_string(),
            original: None,
        }
    }

    pub fn rename(path: &str) -> Self {
        Self {
            kind: EntryKind::Rename,
            parent: parent_of(path).to_string(),
            original: Some(path.to_string()),
        }
    }

    pub fn placeholder(&self) -> &'static str {
        match self.kind {
            EntryKind::NewFolder => "New folder name",
            _ => "New file name",
        }
    }

    /// What the field starts with: the name being renamed, or nothing.
    pub fn initial(&self) -> &str {
        self.original.as_deref().map_or("", leaf)
    }
}

pub fn entry_outcome(bar: &EntryBar, input: &str) -> EntryOutcome {
    let name = input.trim();
    if name.is_empty() || name == "/" {
        return EntryOutcome::Dropped;
    }
    if bar.kind == EntryKind::Rename && name == bar.initial() {
        return EntryOutcome::Unchanged;
    }
    let target = match named_target(&bar.parent, name) {
        Ok(target) => target,
        Err(reason) => return EntryOutcome::Invalid(reason),
    };
    match (bar.kind, &bar.original) {
        (EntryKind::Rename, Some(original)) => EntryOutcome::Run {
            kind: "rename",
            path: original.clone(),
            destination: Some(target),
        },
        (EntryKind::NewFolder, _) => EntryOutcome::Run {
            kind: "createFolder",
            path: target,
            destination: None,
        },
        _ => EntryOutcome::Run {
            kind: "createFile",
            path: target,
            destination: None,
        },
    }
}

#[cfg(test)]
#[path = "file_entry_bar_tests.rs"]
mod tests;
