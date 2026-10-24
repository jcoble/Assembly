use std::path::{Path, PathBuf};
use crate::files::discovery::{
    DEFAULT_SOURCE_LIST_LIMIT, MAX_SOURCE_LIST_LIMIT, SourceScanCancellation, SourceScanWalkProgress,
};
use crate::files::discovery::paths::{
    compare_source_records, compare_source_walk_entries, detect_language, is_source_file, normalized_relative_source_path, skip_dir_reason, source_collection_limit, source_file_matches_query,
};
use crate::files::types::{
    SourceDirectoryEntry, SourceRecord, SourceScanResult,
};

#[cfg(test)]
pub(crate) fn list_source_files_sync(
    root: PathBuf,
    limit: usize,
    query: Option<String>,
) -> Result<SourceScanResult, String> {
    list_source_files_sync_with_cancellation(root, limit, query, SourceScanCancellation::none())
}

pub(crate) fn list_source_files_sync_with_cancellation(
    root: PathBuf,
    limit: usize,
    query: Option<String>,
    cancellation: SourceScanCancellation,
) -> Result<SourceScanResult, String> {
    cancellation.ensure_active()?;
    let metadata = std::fs::metadata(&root)
        .map_err(|error| format!("Could not read source root metadata: {error}"))?;
    if !metadata.is_dir() {
        return Err("Source root is not a directory".to_string());
    }

    let limit = if limit == 0 {
        DEFAULT_SOURCE_LIST_LIMIT
    } else {
        limit.min(MAX_SOURCE_LIST_LIMIT)
    };
    let normalized_query = query
        .map(|value| value.trim().to_lowercase())
        .filter(|value| !value.is_empty());
    let collect_limit = source_collection_limit(limit);
    let mut records = Vec::new();
    let mut progress = SourceScanWalkProgress::default();
    collect_source_files(
        &root,
        &root,
        collect_limit,
        normalized_query.as_deref(),
        &mut records,
        &cancellation,
        &mut progress,
    )?;
    progress.report(&cancellation);
    cancellation.ensure_active()?;
    let collected_file_count = records.len();
    records.sort_by(compare_source_records);
    let truncated = collected_file_count > limit;
    records.truncate(limit);
    let mut stats = progress.stats;
    stats.requested_limit = limit;
    stats.returned_files = records.len();
    stats.collection_limit = collect_limit;
    stats.collection_limit_reached = collected_file_count >= collect_limit;
    #[cfg(debug_assertions)]
    crate::debug_log::stderr_log!(
        "mcb tauri source.list root={} count={} truncated={}",
        root.display(),
        records.len(),
        truncated
    );
    Ok(SourceScanResult {
        records,
        limit,
        truncated,
        stats,
    })
}

pub(crate) fn list_source_directory_sync_with_cancellation(
    root: PathBuf,
    directory: PathBuf,
    include_excluded: bool,
    cancellation: SourceScanCancellation,
) -> Result<Vec<SourceDirectoryEntry>, String> {
    cancellation.ensure_active()?;
    let canonical_root = std::fs::canonicalize(&root)
        .map_err(|error| format!("Could not read source root metadata: {error}"))?;
    cancellation.ensure_active()?;
    let canonical_directory = std::fs::canonicalize(&directory)
        .map_err(|error| format!("Could not read source directory metadata: {error}"))?;
    if !canonical_directory.starts_with(&canonical_root) {
        return Err("Source directory is outside the project root".to_string());
    }
    if !canonical_directory.is_dir() {
        return Err("Source path is not a directory".to_string());
    }

    let mut entries = Vec::new();
    for entry in std::fs::read_dir(&directory)
        .map_err(|error| format!("Could not read source directory: {error}"))?
    {
        cancellation.ensure_active()?;
        if let Ok(entry) = entry {
            let Ok(file_type) = entry.file_type() else {
                continue;
            };
            if file_type.is_symlink() {
                continue;
            }
            let name = entry.file_name().to_string_lossy().to_string();
            let is_directory = file_type.is_dir();
            let excluded = is_directory && skip_dir_reason(&name).is_some();
            if (excluded && !include_excluded) || (!is_directory && !file_type.is_file()) {
                continue;
            }
            entries.push(SourceDirectoryEntry {
                path: entry.path().display().to_string(),
                name,
                is_directory,
                excluded,
            });
        }
    }
    cancellation.ensure_active()?;
    entries.sort_by(|left, right| {
        right
            .is_directory
            .cmp(&left.is_directory)
            .then_with(|| left.name.to_lowercase().cmp(&right.name.to_lowercase()))
    });
    Ok(entries)
}

pub(crate) fn collect_source_files(
    root: &Path,
    current: &Path,
    limit: usize,
    query: Option<&str>,
    records: &mut Vec<SourceRecord>,
    cancellation: &SourceScanCancellation,
    progress: &mut SourceScanWalkProgress,
) -> Result<(), String> {
    cancellation.ensure_active()?;
    if records.len() >= limit {
        return Ok(());
    }

    let mut entries = std::fs::read_dir(current)
        .map_err(|error| format!("Could not read source directory: {error}"))?
        .filter_map(Result::ok)
        .collect::<Vec<_>>();
    entries.sort_by(|left, right| compare_source_walk_entries(root, left, right));

    for entry in entries {
        cancellation.ensure_active()?;
        progress.visit_entry(cancellation);

        if records.len() >= limit {
            break;
        }

        let path = entry.path();
        let file_name = entry.file_name().to_string_lossy().to_string();
        let Ok(metadata) = entry.metadata() else {
            progress.skip_unreadable_entry();
            continue;
        };

        if metadata.is_dir() {
            if let Some(reason) = skip_dir_reason(&file_name) {
                progress.skip_directory(root, &path, &file_name, reason);
                continue;
            }
            collect_source_files(root, &path, limit, query, records, cancellation, progress)?;
            continue;
        }

        if !metadata.is_file() {
            progress.skip_unsupported_file();
            continue;
        }

        if !is_source_file(&path) {
            progress.skip_unsupported_file();
            continue;
        }

        let relative_path = normalized_relative_source_path(root, &path);
        if !source_file_matches_query(&relative_path, &file_name, query) {
            continue;
        }

        records.push(SourceRecord {
            path: path.display().to_string(),
            relative_path,
            file_name,
            language: detect_language(&path),
            byte_count: metadata.len(),
        });
        progress.match_file(cancellation);
    }

    Ok(())
}
