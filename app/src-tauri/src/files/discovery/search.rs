use std::path::{Path, PathBuf};
use crate::files::discovery::{
    DEFAULT_SOURCE_SEARCH_LIMIT, MAX_SOURCE_SEARCH_LIMIT, SourceScanCancellation,
};
use crate::files::discovery::paths::{
    clamp_source_search_page_size, compare_source_walk_entries, normalized_relative_source_path, skip_dir_reason,
};
use crate::files::discovery::symbols::{
    compact_source_line_excerpt,
};
use crate::files::io::{
    MAX_PREVIEW_BYTES,
};
use crate::files::types::{
    SourceRecord, SourceSearchMatch, SourceTreeSearchMatch, SourceTreeSearchPage,
};

pub(crate) fn search_source_files_sync(
    records: Vec<SourceRecord>,
    query: String,
    limit: Option<usize>,
) -> Result<Vec<SourceSearchMatch>, String> {
    let normalized_query = query.trim().to_lowercase();
    if normalized_query.is_empty() {
        return Ok(Vec::new());
    }

    let capped_limit = limit
        .unwrap_or(DEFAULT_SOURCE_SEARCH_LIMIT)
        .min(MAX_SOURCE_SEARCH_LIMIT);
    if capped_limit == 0 {
        return Ok(Vec::new());
    }

    let mut matches = Vec::new();
    for record in records {
        if matches.len() >= capped_limit {
            break;
        }

        let path = PathBuf::from(&record.path);
        let Ok(metadata) = std::fs::metadata(&path) else {
            continue;
        };
        if !metadata.is_file() || metadata.len() > MAX_PREVIEW_BYTES {
            continue;
        }

        let Ok(bytes) = std::fs::read(&path) else {
            continue;
        };
        let content = String::from_utf8_lossy(&bytes);
        append_source_search_matches(
            &record,
            &content,
            &normalized_query,
            capped_limit,
            &mut matches,
        );
    }

    Ok(matches)
}

pub(crate) fn search_source_tree_sync(
    root: PathBuf,
    query: String,
    page_size: Option<usize>,
    cursor: Option<usize>,
    include_excluded: bool,
    cancellation: SourceScanCancellation,
) -> Result<SourceTreeSearchPage, String> {
    cancellation.ensure_active()?;
    let metadata = std::fs::metadata(&root)
        .map_err(|error| format!("Could not read source root metadata: {error}"))?;
    if !metadata.is_dir() {
        return Err("Source root is not a directory".to_string());
    }

    let normalized_query = query.trim().to_lowercase();
    let page_size = clamp_source_search_page_size(page_size);
    let offset = cursor.unwrap_or(0);
    if normalized_query.is_empty() {
        return Ok(SourceTreeSearchPage {
            matches: Vec::new(),
            next_cursor: None,
            complete: true,
        });
    }

    let mut matches = Vec::with_capacity(page_size.saturating_add(1));
    let mut matched_count = 0;
    collect_source_tree_search_matches(
        &root,
        &root,
        &normalized_query,
        page_size,
        offset,
        include_excluded,
        &mut matched_count,
        &mut matches,
        &cancellation,
    )?;

    cancellation.ensure_active()?;
    let complete = matches.len() <= page_size;
    let next_cursor = if complete {
        None
    } else {
        matches.truncate(page_size);
        Some(offset.saturating_add(page_size))
    };
    Ok(SourceTreeSearchPage {
        matches,
        next_cursor,
        complete,
    })
}

fn collect_source_tree_search_matches(
    root: &Path,
    current: &Path,
    query: &str,
    page_size: usize,
    offset: usize,
    include_excluded: bool,
    matched_count: &mut usize,
    matches: &mut Vec<SourceTreeSearchMatch>,
    cancellation: &SourceScanCancellation,
) -> Result<bool, String> {
    cancellation.ensure_active()?;
    let mut entries = std::fs::read_dir(current)
        .map_err(|error| format!("Could not read source directory: {error}"))?
        .filter_map(Result::ok)
        .collect::<Vec<_>>();
    entries.sort_by(|left, right| compare_source_walk_entries(root, left, right));

    for entry in entries {
        cancellation.ensure_active()?;
        let file_type = entry
            .file_type()
            .map_err(|error| format!("Could not read source entry metadata: {error}"))?;
        if file_type.is_symlink() || (!file_type.is_dir() && !file_type.is_file()) {
            continue;
        }

        let path = entry.path();
        let name = entry.file_name().to_string_lossy().to_string();
        let is_directory = file_type.is_dir();
        let excluded = is_directory && skip_dir_reason(&name).is_some();
        if is_directory && excluded && !include_excluded {
            continue;
        }

        let relative_path = normalized_relative_source_path(root, &path);
        if relative_path.to_lowercase().contains(query) {
            if *matched_count >= offset {
                matches.push(SourceTreeSearchMatch {
                    path: path.display().to_string(),
                    relative_path,
                    name,
                    is_directory,
                    excluded,
                });
            }
            *matched_count = (*matched_count).saturating_add(1);
            if matches.len() > page_size {
                return Ok(true);
            }
        }

        if is_directory
            && collect_source_tree_search_matches(
                root,
                &path,
                query,
                page_size,
                offset,
                include_excluded,
                matched_count,
                matches,
                cancellation,
            )?
        {
            return Ok(true);
        }
    }

    Ok(false)
}

fn append_source_search_matches(
    record: &SourceRecord,
    content: &str,
    normalized_query: &str,
    limit: usize,
    matches: &mut Vec<SourceSearchMatch>,
) {
    for (line_index, line) in content.lines().enumerate() {
        let Some(column_index) = line.to_lowercase().find(normalized_query) else {
            continue;
        };

        matches.push(SourceSearchMatch {
            path: record.path.clone(),
            relative_path: record.relative_path.clone(),
            file_name: record.file_name.clone(),
            language: record.language.clone(),
            byte_count: record.byte_count,
            line: line_index + 1,
            column: column_index + 1,
            excerpt: compact_source_line_excerpt(line),
        });

        if matches.len() >= limit {
            break;
        }
    }
}
