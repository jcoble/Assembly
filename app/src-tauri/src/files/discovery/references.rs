use std::collections::HashMap;
use std::path::{Path, PathBuf};
use std::time::{Duration, Instant};
use crate::files::lsp;
// The counting pass behind the margin reference counts lives in mcb-core, not
// here, so that it is compiled optimized even when this crate is not — see the
// module's own note and the `[profile.dev.package."*"]` block in Cargo.toml.
use mcb_core::reference_counts::{
    count_reference_lines_across_files, is_source_token_boundary,
    normalized_reference_count_symbols, ReferenceCountFile, ReferenceCountPlan,
    MAX_REFERENCE_SCAN_BYTES,
};
use crate::files::discovery::{
    DEFAULT_REFERENCE_COUNT_DEADLINE_MS, DEFAULT_SOURCE_LIST_LIMIT, DEFAULT_SOURCE_REFERENCE_LIMIT, MAX_REFERENCE_COUNT_DEADLINE_MS, MAX_SOURCE_REFERENCE_LIMIT, SourceScanCancellation, SourceScanWalkProgress,
};
use crate::files::discovery::paths::{
    source_collection_limit,
};
use crate::files::discovery::symbols::{
    compact_source_line_excerpt,
};
use crate::files::discovery::walk::{
    collect_source_files,
};
use crate::files::io::{
    MAX_PREVIEW_BYTES,
};
use crate::files::types::{
    SourceRecord, SourceReferenceCountResult, SourceReferenceTarget,
};

pub(crate) fn find_source_references_sync(
    records: Vec<SourceRecord>,
    symbol_name: String,
    limit: Option<usize>,
) -> Result<Vec<SourceReferenceTarget>, String> {
    let normalized_symbol_name = symbol_name.trim().to_lowercase();
    if normalized_symbol_name.is_empty() {
        return Ok(Vec::new());
    }

    let capped_limit = limit
        .unwrap_or(DEFAULT_SOURCE_REFERENCE_LIMIT)
        .min(MAX_SOURCE_REFERENCE_LIMIT);
    if capped_limit == 0 {
        return Ok(Vec::new());
    }

    let mut targets = Vec::new();
    for record in records {
        if targets.len() >= capped_limit {
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
        append_source_reference_targets(
            &record,
            &content,
            &normalized_symbol_name,
            capped_limit,
            &mut targets,
        );
    }

    Ok(targets)
}

pub(crate) fn find_source_references_in_root_sync(
    root: PathBuf,
    symbol_name: String,
    limit: Option<usize>,
) -> Result<Vec<SourceReferenceTarget>, String> {
    let capped_limit = limit
        .unwrap_or(DEFAULT_SOURCE_REFERENCE_LIMIT)
        .min(MAX_SOURCE_REFERENCE_LIMIT);
    if symbol_name.trim().is_empty() || capped_limit == 0 {
        return Ok(Vec::new());
    }

    let deadline = Instant::now() + Duration::from_millis(DEFAULT_REFERENCE_COUNT_DEADLINE_MS);
    let mut records = Vec::new();
    let mut progress = SourceScanWalkProgress::default();
    let _ = collect_source_files(
        &root,
        &root,
        source_collection_limit(DEFAULT_SOURCE_LIST_LIMIT),
        None,
        &mut records,
        &SourceScanCancellation::until(deadline),
        &mut progress,
    );

    let normalized_symbol_name = symbol_name.trim().to_lowercase();
    let mut targets = Vec::new();
    for record in records {
        if targets.len() >= capped_limit || Instant::now() >= deadline {
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
        append_source_reference_targets(
            &record,
            &String::from_utf8_lossy(&bytes),
            &normalized_symbol_name,
            capped_limit,
            &mut targets,
        );
    }

    Ok(targets)
}

fn append_source_reference_targets(
    record: &SourceRecord,
    content: &str,
    normalized_symbol_name: &str,
    limit: usize,
    targets: &mut Vec<SourceReferenceTarget>,
) {
    for (line_index, line) in content.lines().enumerate() {
        let Some(column_index) = find_source_reference_column(line, normalized_symbol_name) else {
            continue;
        };

        let symbol_name = line
            .get(column_index..column_index + normalized_symbol_name.len())
            .unwrap_or(normalized_symbol_name)
            .to_string();
        targets.push(SourceReferenceTarget {
            path: record.path.clone(),
            relative_path: record.relative_path.clone(),
            file_name: record.file_name.clone(),
            language: record.language.clone(),
            byte_count: record.byte_count,
            symbol_name,
            line: line_index + 1,
            column: column_index + 1,
            excerpt: compact_source_line_excerpt(line),
        });

        if targets.len() >= limit {
            break;
        }
    }
}

fn find_source_reference_column(line: &str, normalized_symbol_name: &str) -> Option<usize> {
    let normalized_line = line.to_lowercase();
    let mut search_start = 0;

    while search_start < normalized_line.len() {
        let relative_index = normalized_line[search_start..].find(normalized_symbol_name)?;
        let index = search_start + relative_index;
        let end_index = index + normalized_symbol_name.len();
        if is_source_token_boundary(line, index, end_index) {
            return Some(index);
        }
        search_start = end_index;
    }

    None
}

pub(crate) fn count_source_references_sync(
    root: PathBuf,
    symbol_names: Vec<String>,
    deadline_ms: Option<u64>,
) -> Result<SourceReferenceCountResult, String> {
    let started = Instant::now();
    let metadata = std::fs::metadata(&root)
        .map_err(|error| format!("Could not read source root metadata: {error}"))?;
    if !metadata.is_dir() {
        return Err("Source root is not a directory".to_string());
    }

    let names = normalized_reference_count_symbols(symbol_names);
    if names.is_empty() {
        return Ok(SourceReferenceCountResult {
            counts: HashMap::new(),
            approximate: false,
            scanned_files: 0,
            elapsed_ms: elapsed_millis(started),
        });
    }

    let budget = Duration::from_millis(
        deadline_ms
            .unwrap_or(DEFAULT_REFERENCE_COUNT_DEADLINE_MS)
            .clamp(1, MAX_REFERENCE_COUNT_DEADLINE_MS),
    );
    let deadline = started + budget;

    // Walking the project here rather than accepting a file list from the
    // caller is the point: the list is thousands of entries and used to be
    // serialized across the bridge once per symbol.
    let collection_limit = source_collection_limit(DEFAULT_SOURCE_LIST_LIMIT);
    let mut records = Vec::new();
    let mut walk_progress = SourceScanWalkProgress::default();
    let walk = collect_source_files(
        &root,
        &root,
        collection_limit,
        None,
        &mut records,
        &SourceScanCancellation::until(deadline),
        &mut walk_progress,
    );
    let mut approximate = walk.is_err() || records.len() >= collection_limit;

    // The counting pass takes only a path and a size per file; the rest of a
    // record (language, display names) means nothing to it.
    let files = records
        .iter()
        .map(|record| ReferenceCountFile {
            path: Path::new(&record.path),
            byte_count: record.byte_count,
        })
        .collect::<Vec<_>>();

    let plan = ReferenceCountPlan::new(&names);
    // Not `MAX_PREVIEW_BYTES`: that ceiling decides how much text is worth putting on
    // screen, which has nothing to do with how much text this pass can walk through.
    // Reusing it meant one 660KB source file made every count in the project a floor.
    let pass =
        count_reference_lines_across_files(&files, &plan, deadline, MAX_REFERENCE_SCAN_BYTES);
    log_reference_count_timing(&pass, names.len(), started);
    // Both mean the same thing to the reader: not every file was counted, so
    // the totals are floors and the margin must say "at least", never an exact
    // number nobody actually took.
    approximate = approximate || pass.ran_out_of_time || pass.skipped_files;

    let counts = names
        .iter()
        .cloned()
        .zip(pass.counts)
        .collect::<HashMap<String, u32>>();

    Ok(SourceReferenceCountResult {
        counts,
        approximate,
        scanned_files: pass.scanned_files,
        elapsed_ms: elapsed_millis(started),
    })
}

fn elapsed_millis(started: Instant) -> u64 {
    u64::try_from(started.elapsed().as_millis()).unwrap_or(u64::MAX)
}

/// Say, on the terminal the app was started from, how long one pass of counting
/// mentions across the project took — but only when the reader asked for timings
/// by starting the app with `MCB_TIMING=1`. Silence otherwise.
fn log_reference_count_timing(
    pass: &mcb_core::reference_counts::ReferenceCountPass,
    symbol_count: usize,
    started: Instant,
) {
    if !lsp::timing_enabled() {
        return;
    }

    let outcome = if pass.ran_out_of_time {
        "it ran out of time, so the totals are floors"
    } else if pass.skipped_files {
        "some files were too big or could not be read, so the totals are floors"
    } else {
        "every file on the list was read, so the totals are exact"
    };
    crate::debug_log::stderr_log!(
        "Timing: counting {symbol_count} names across {} files took {} ms; {outcome}.",
        pass.scanned_files,
        elapsed_millis(started)
    );
}
