//! What `files/list`, `files/search` and `files/read` answer (dotnet/Assembly.Infrastructure/Chat/Files/). Paths are from the
//! session's folder, with `/` between names.

use serde::{Deserialize, Serialize};

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
    pub next_cursor: Option<PageCursor>,
}

#[derive(Clone, Debug, Deserialize, Serialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct PageCursor {
    pub generation: String,
    pub after: String,
}

#[derive(Debug, Deserialize)]
pub struct ObservedRoot {
    pub lease: String,
}

#[derive(Clone, Debug, Deserialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct NameMatch {
    pub path: String,
    pub name: String,
    pub is_directory: bool,
    pub relative_path: String,
}

#[derive(Clone, Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ContentMatch {
    pub path: String,
    pub line: u32,
    pub column: u32,
    pub excerpt: String,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SearchResult {
    pub search_id: String,
    pub names: Vec<NameMatch>,
    pub content: Vec<ContentMatch>,
    pub more: bool,
    pub skipped: usize,
    pub truncated: bool,
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

#[derive(Clone, Debug, Deserialize, serde::Serialize)]
#[serde(rename_all = "camelCase")]
pub struct FileOwnership {
    pub machine: String,
    pub checkout_root: String,
    pub remote_session_id: String,
}
