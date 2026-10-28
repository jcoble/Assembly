//! What `files/list` and `files/search` answer (dotnet/Assembly.Infrastructure/Chat/Files/). Paths are from the
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
    pub next_cursor: Option<u32>,
    pub complete: bool,
}
