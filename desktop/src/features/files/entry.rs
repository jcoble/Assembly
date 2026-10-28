//! What `files/list`, `files/search` and `files/read` answer (dotnet/Assembly.Infrastructure/Chat/Files/). Paths are from the
//! session's folder, with `/` between names.

use serde::Deserialize;

#[derive(Clone, Debug, Deserialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct FileEntry {
    pub path: String,
    pub name: String,
    pub is_directory: bool,
    /// Ignored by git (or `.git` itself): drawn dimmer.
    pub excluded: bool,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct FileListing {
    pub entries: Vec<FileEntry>,
    /// The folder held more than the backend answers at once.
    pub truncated: bool,
}

#[derive(Clone, Debug, Deserialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct NameMatch {
    pub path: String,
    pub name: String,
    pub is_directory: bool,
    pub relative_path: String,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SearchPage {
    pub matches: Vec<NameMatch>,
    /// Null once the search is complete.
    pub next_cursor: Option<u32>,
}

/// A part of a file's text from where it was asked: the whole file's size, revision (SHA-256) and line count, and where
/// the next part starts, or `None` at the end. A binary file has no content.
#[derive(Clone, Debug, Deserialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct FileChunk {
    pub content: String,
    pub size: u64,
    pub revision: String,
    pub line_count: usize,
    pub binary: bool,
    pub next_offset: Option<u64>,
}
