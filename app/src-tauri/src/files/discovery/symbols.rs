use std::path::PathBuf;
use std::time::{Duration, Instant};
use crate::files::discovery::{
    DEFAULT_REFERENCE_COUNT_DEADLINE_MS, DEFAULT_SOURCE_DEFINITION_LIMIT, DEFAULT_SOURCE_LIST_LIMIT, MAX_SOURCE_DEFINITION_LIMIT, SourceScanCancellation, SourceScanWalkProgress,
};
use crate::files::discovery::paths::{
    source_collection_limit,
};
use crate::files::discovery::walk::{
    collect_source_files,
};
use crate::files::io::{
    MAX_PREVIEW_BYTES,
};
use crate::files::types::{
    SourceDefinitionTarget, SourceRecord,
};

pub(crate) fn find_source_definitions_sync(
    records: Vec<SourceRecord>,
    symbol_name: String,
    limit: Option<usize>,
) -> Result<Vec<SourceDefinitionTarget>, String> {
    let normalized_symbol_name = symbol_name.trim().to_lowercase();
    if normalized_symbol_name.is_empty() {
        return Ok(Vec::new());
    }

    let capped_limit = limit
        .unwrap_or(DEFAULT_SOURCE_DEFINITION_LIMIT)
        .min(MAX_SOURCE_DEFINITION_LIMIT);
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
        append_source_definition_targets(
            &record,
            &content,
            &normalized_symbol_name,
            capped_limit,
            &mut targets,
        );
    }

    Ok(targets)
}

pub(crate) fn find_source_definitions_in_root_sync(
    root: PathBuf,
    symbol_name: String,
    limit: Option<usize>,
) -> Result<Vec<SourceDefinitionTarget>, String> {
    let normalized_symbol_name = symbol_name.trim().to_lowercase();
    if normalized_symbol_name.is_empty() {
        return Ok(Vec::new());
    }
    let capped_limit = limit
        .unwrap_or(DEFAULT_SOURCE_DEFINITION_LIMIT)
        .min(MAX_SOURCE_DEFINITION_LIMIT);
    if capped_limit == 0 {
        return Ok(Vec::new());
    }

    let mut records = Vec::new();
    let mut progress = SourceScanWalkProgress::default();
    let deadline = Instant::now() + Duration::from_millis(DEFAULT_REFERENCE_COUNT_DEADLINE_MS);
    let _ = collect_source_files(
        &root,
        &root,
        source_collection_limit(DEFAULT_SOURCE_LIST_LIMIT),
        None,
        &mut records,
        &SourceScanCancellation::until(deadline),
        &mut progress,
    );

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
        append_source_definition_targets(
            &record,
            &String::from_utf8_lossy(&bytes),
            &normalized_symbol_name,
            capped_limit,
            &mut targets,
        );
    }
    Ok(targets)
}

fn append_source_definition_targets(
    record: &SourceRecord,
    content: &str,
    normalized_symbol_name: &str,
    limit: usize,
    targets: &mut Vec<SourceDefinitionTarget>,
) {
    for (line_index, line) in content.lines().enumerate() {
        let Some(symbol) = parse_source_symbol_line(&record.language, line) else {
            continue;
        };
        if symbol.name.to_lowercase() != normalized_symbol_name {
            continue;
        }

        targets.push(SourceDefinitionTarget {
            path: record.path.clone(),
            relative_path: record.relative_path.clone(),
            file_name: record.file_name.clone(),
            language: record.language.clone(),
            byte_count: record.byte_count,
            symbol_name: symbol.name,
            kind: symbol.kind,
            line: line_index + 1,
            column: symbol.column,
            detail: line.trim().to_string(),
        });

        if targets.len() >= limit {
            break;
        }
    }
}

#[derive(Debug, PartialEq, Eq)]
struct ParsedSourceSymbol {
    name: String,
    kind: String,
    column: usize,
}

fn parse_source_symbol_line(language: &str, line: &str) -> Option<ParsedSourceSymbol> {
    match language {
        "csharp" => parse_csharp_symbol_line(line),
        "typescript" | "tsx" | "javascript" | "jsx" => parse_typescript_symbol_line(line),
        _ => None,
    }
}

fn parse_csharp_symbol_line(line: &str) -> Option<ParsedSourceSymbol> {
    let trimmed = line.trim();
    if trimmed.is_empty() || trimmed.starts_with("//") {
        return None;
    }

    if let Some(namespace_name) = trimmed.strip_prefix("namespace ") {
        let name = trim_symbol_name(namespace_name, &[';', '{']);
        return source_symbol_from_name(line, name, "namespace");
    }

    let tokens = source_line_tokens(trimmed);
    for (index, token) in tokens.iter().enumerate() {
        if !matches!(*token, "class" | "interface" | "record" | "enum" | "struct") {
            continue;
        }

        let name = tokens.get(index + 1)?;
        return source_symbol_from_name(line, name, token);
    }

    let before_params = trimmed.split_once('(')?.0.trim_end();
    let method_name = before_params.split_whitespace().last()?;
    if method_name.is_empty() || !starts_with_identifier(method_name) {
        return None;
    }

    let first_token = tokens.first().copied().unwrap_or_default();
    if !matches!(
        first_token,
        "public"
            | "private"
            | "protected"
            | "internal"
            | "static"
            | "async"
            | "virtual"
            | "override"
    ) {
        return None;
    }

    source_symbol_from_name(line, method_name, "method")
}

fn parse_typescript_symbol_line(line: &str) -> Option<ParsedSourceSymbol> {
    let trimmed = line.trim();
    if trimmed.is_empty() || trimmed.starts_with("//") {
        return None;
    }

    let tokens = source_line_tokens(trimmed);
    let mut index = 0;
    while matches!(
        tokens.get(index).copied(),
        Some("export" | "default" | "abstract" | "async" | "declare")
    ) {
        index += 1;
    }

    match tokens.get(index).copied()? {
        "class" | "interface" | "type" | "enum" => {
            source_symbol_from_name(line, tokens.get(index + 1)?, tokens[index])
        }
        "function" => source_symbol_from_name(line, tokens.get(index + 1)?, "function"),
        "const" | "let" | "var" => {
            source_symbol_from_name(line, tokens.get(index + 1)?, "constant")
        }
        _ => None,
    }
}

fn source_line_tokens(line: &str) -> Vec<&str> {
    line.split_whitespace()
        .map(|token| trim_symbol_name(token, &[';', '{', '(', ')', ':', ',', '=', '<']))
        .filter(|token| !token.is_empty())
        .collect()
}

fn trim_symbol_name<'a>(value: &'a str, separators: &[char]) -> &'a str {
    value
        .trim()
        .trim_matches(|character: char| separators.contains(&character))
        .split(|character: char| separators.contains(&character))
        .next()
        .unwrap_or("")
}

fn source_symbol_from_name(line: &str, name: &str, kind: &str) -> Option<ParsedSourceSymbol> {
    if name.is_empty() || !starts_with_identifier(name) {
        return None;
    }

    Some(ParsedSourceSymbol {
        name: name.to_string(),
        kind: kind.to_string(),
        column: line.find(name).map(|index| index + 1).unwrap_or(1),
    })
}

fn starts_with_identifier(value: &str) -> bool {
    value
        .chars()
        .next()
        .is_some_and(|character| character == '_' || character.is_ascii_alphabetic())
}

pub(super) fn compact_source_line_excerpt(line: &str) -> String {
    let trimmed = line.trim();
    if trimmed.chars().count() <= 180 {
        return trimmed.to_string();
    }

    format!("{}...", trimmed.chars().take(177).collect::<String>())
}
