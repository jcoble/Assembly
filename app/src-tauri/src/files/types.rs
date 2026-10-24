use std::collections::HashMap;

#[derive(Debug, Clone, serde::Deserialize, serde::Serialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct SourceRecord {
    pub(crate) path: String,
    pub(crate) relative_path: String,
    pub(crate) file_name: String,
    pub(crate) language: String,
    pub(crate) byte_count: u64,
}

#[derive(Debug, Clone, serde::Serialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct SourceDirectoryEntry {
    pub(crate) path: String,
    pub(crate) name: String,
    pub(crate) is_directory: bool,
    pub(crate) excluded: bool,
}

#[derive(Debug, serde::Serialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct SourceScanResult {
    pub(crate) records: Vec<SourceRecord>,
    pub(crate) limit: usize,
    pub(crate) truncated: bool,
    pub(crate) stats: SourceScanStats,
}

/// How many lines mention each requested symbol, across the whole project.
///
/// `approximate` is true when the count stopped early — either the deadline ran
/// out or the project has more files than one walk collects. An approximate
/// result is still worth drawing for symbols that were found, but a zero in an
/// approximate result means "not seen yet", not "nowhere in the project".
#[derive(Debug, serde::Serialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct SourceReferenceCountResult {
    pub(crate) counts: HashMap<String, u32>,
    pub(crate) approximate: bool,
    pub(crate) scanned_files: usize,
    pub(crate) elapsed_ms: u64,
}

#[derive(Clone, Debug, Default, serde::Serialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct SourceScanStats {
    pub(crate) visited_entries: usize,
    pub(crate) matched_files: usize,
    pub(crate) skipped_directories: usize,
    pub(crate) unsupported_files: usize,
    pub(crate) unreadable_entries: usize,
    pub(crate) requested_limit: usize,
    pub(crate) returned_files: usize,
    pub(crate) collection_limit: usize,
    pub(crate) collection_limit_reached: bool,
    pub(crate) skipped_directory_samples: Vec<SourceSkippedDirectory>,
}

#[derive(Clone, Debug, serde::Serialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct SourceSkippedDirectory {
    pub(crate) path: String,
    pub(crate) name: String,
    pub(crate) reason: String,
}

#[derive(Debug, serde::Serialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct ProjectRootValidationResult {
    pub(crate) path: String,
    pub(crate) exists: bool,
    pub(crate) is_directory: bool,
    pub(crate) is_git_repository: bool,
    pub(crate) git_root: Option<String>,
    pub(crate) message: String,
}

#[derive(Clone, serde::Serialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct SourceScanProgressEvent {
    pub(crate) scan_id: String,
    pub(crate) visited_entries: usize,
    pub(crate) matched_files: usize,
}

#[derive(Debug, serde::Serialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct SourcePreview {
    pub(crate) path: String,
    pub(crate) relative_path: String,
    pub(crate) file_name: String,
    pub(crate) language: String,
    pub(crate) byte_count: u64,
    pub(crate) revision: String,
    pub(crate) content: String,
    pub(crate) line_count: usize,
}

#[derive(Debug, serde::Serialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct SourceSearchMatch {
    pub(crate) path: String,
    pub(crate) relative_path: String,
    pub(crate) file_name: String,
    pub(crate) language: String,
    pub(crate) byte_count: u64,
    pub(crate) line: usize,
    pub(crate) column: usize,
    pub(crate) excerpt: String,
}

#[derive(Debug, serde::Serialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct SourceTreeSearchMatch {
    pub(crate) path: String,
    pub(crate) relative_path: String,
    pub(crate) name: String,
    pub(crate) is_directory: bool,
    pub(crate) excluded: bool,
}

#[derive(Debug, serde::Serialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct SourceTreeSearchPage {
    pub(crate) matches: Vec<SourceTreeSearchMatch>,
    pub(crate) next_cursor: Option<usize>,
    pub(crate) complete: bool,
}

#[derive(Debug, serde::Serialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct SourceDefinitionTarget {
    pub(crate) path: String,
    pub(crate) relative_path: String,
    pub(crate) file_name: String,
    pub(crate) language: String,
    pub(crate) byte_count: u64,
    pub(crate) symbol_name: String,
    pub(crate) kind: String,
    pub(crate) line: usize,
    pub(crate) column: usize,
    pub(crate) detail: String,
}

#[derive(Debug, serde::Serialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct SourceReferenceTarget {
    pub(crate) path: String,
    pub(crate) relative_path: String,
    pub(crate) file_name: String,
    pub(crate) language: String,
    pub(crate) byte_count: u64,
    pub(crate) symbol_name: String,
    pub(crate) line: usize,
    pub(crate) column: usize,
    pub(crate) excerpt: String,
}
