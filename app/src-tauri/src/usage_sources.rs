use crate::usage_db::{UsageEvent, UsageSourceCursor};
use serde_json::Value;
use std::collections::HashMap;
use std::fs;
use std::io::{Read, Seek, SeekFrom};
use std::os::unix::fs::MetadataExt;
use std::path::{Path, PathBuf};
use std::time::{SystemTime, UNIX_EPOCH};

#[derive(Debug, Clone)]
pub struct IncrementalJsonl {
    pub lines: Vec<String>,
    pub next_cursor: UsageSourceCursor,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct UsageSourceFile {
    pub provider: String,
    pub provider_instance_id: String,
    pub source_kind: String,
    pub path: PathBuf,
}

#[derive(Debug, Clone, PartialEq)]
pub struct LocalQuotaWindow {
    pub name: String,
    pub semantics: String,
    pub percent_consumed: f64,
    pub reset_at: Option<String>,
}

#[derive(Debug, Clone, PartialEq)]
pub struct LocalQuotaSnapshot {
    pub provider: String,
    pub account: Option<String>,
    pub windows: Vec<LocalQuotaWindow>,
    pub captured_at: u128,
}

#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct UsageSourceContext {
    pub session_id: Option<String>,
    pub cwd: Option<String>,
    pub model: Option<String>,
}

/// Only Claude/Codex have local usage readers; no Gemini source is registered here.
/// Inspected Antigravity ACP 1.2.1 server.py:3004–3065 persists trajectories/cwd metadata;
/// no local usage/quota writer is exposed (4833–4845 only formats quota failures).
pub fn discover_local_usage_sources() -> Vec<UsageSourceFile> {
    let Some(home) = std::env::var_os("HOME").map(PathBuf::from) else {
        return Vec::new();
    };
    let mut files = Vec::new();
    let sources = [
        (home.join(".claude/projects"), "claude", "claude-jsonl"),
        (home.join(".codex/sessions"), "codex", "codex-jsonl"),
        (
            home.join(".codex/archived_sessions"),
            "codex",
            "codex-jsonl",
        ),
    ];
    for (root, provider, source_kind) in sources {
        collect_jsonl_files(&root, 0, &mut files, provider, source_kind);
    }
    files.sort_by(|left, right| left.path.cmp(&right.path));
    files
}

fn collect_jsonl_files(
    root: &Path,
    depth: usize,
    files: &mut Vec<UsageSourceFile>,
    provider: &str,
    source_kind: &str,
) {
    if depth > 8 {
        return;
    }
    let Ok(entries) = fs::read_dir(root) else {
        return;
    };
    for entry in entries.flatten() {
        let path = entry.path();
        let Ok(metadata) = entry.metadata() else {
            continue;
        };
        if metadata.file_type().is_symlink() {
            continue;
        }
        if metadata.is_dir() {
            collect_jsonl_files(&path, depth + 1, files, provider, source_kind);
        } else if metadata.is_file() && path.extension().is_some_and(|value| value == "jsonl") {
            files.push(UsageSourceFile {
                provider: provider.to_string(),
                provider_instance_id: "local".to_string(),
                source_kind: source_kind.to_string(),
                path,
            });
        }
    }
}

/// Read only the bytes after the durable cursor. A truncated or replaced file
/// starts from zero and is allowed to deduplicate through `source_event_id`.
pub fn read_incremental_jsonl(
    path: &Path,
    cursor: Option<&UsageSourceCursor>,
    provider: &str,
    provider_instance_id: &str,
    source_kind: &str,
    source_key: &str,
) -> Result<IncrementalJsonl, String> {
    let metadata =
        fs::metadata(path).map_err(|error| format!("Usage source is unavailable: {error}"))?;
    let file_identity = file_identity(path, &metadata)?;
    let cursor_offset = cursor
        .filter(|cursor| cursor.file_identity == file_identity && cursor.offset <= metadata.len())
        .map(|cursor| cursor.offset as usize)
        .unwrap_or(0);
    let mut file = fs::File::open(path)
        .map_err(|error| format!("Usage source could not be opened: {error}"))?;
    file.seek(SeekFrom::Start(cursor_offset as u64))
        .map_err(|error| format!("Usage source cursor could not seek: {error}"))?;
    let mut tail = Vec::new();
    file.read_to_end(&mut tail)
        .map_err(|error| format!("Usage source tail could not be read: {error}"))?;
    let complete_len = tail
        .iter()
        .rposition(|byte| *byte == b'\n')
        .map(|index| cursor_offset.saturating_add(index + 1))
        .unwrap_or(cursor_offset);
    let complete_tail_len = complete_len.saturating_sub(cursor_offset);
    let lines = String::from_utf8_lossy(&tail[..complete_tail_len])
        .lines()
        .filter(|line| !line.trim().is_empty())
        .map(ToString::to_string)
        .collect::<Vec<_>>();
    let next_cursor = UsageSourceCursor {
        provider: provider.to_string(),
        provider_instance_id: provider_instance_id.to_string(),
        source_kind: source_kind.to_string(),
        source_key: opaque_source_key(source_key),
        file_identity,
        offset: complete_len as u64,
        size: metadata.len(),
        modified_at_micros: metadata_modified_micros(&metadata),
        last_event_id: None,
    };
    Ok(IncrementalJsonl { lines, next_cursor })
}

pub fn parse_usage_events_for_source_with_context(
    lines: &[String],
    provider: &str,
    provider_instance_id: &str,
    source_kind: &str,
    source_key: &str,
    context: &UsageSourceContext,
) -> Vec<UsageEvent> {
    let mut session_id = context.session_id.clone();
    let mut cwd = context.cwd.clone();
    let mut model = context.model.clone();
    let mut positions = HashMap::new();
    let mut events = Vec::new();

    for (index, line) in lines.iter().enumerate() {
        let Ok(value) = serde_json::from_str::<Value>(line) else {
            continue;
        };
        let event = if provider.eq_ignore_ascii_case("codex") {
            if value.get("type").and_then(Value::as_str) == Some("session_meta") {
                session_id = value
                    .pointer("/payload/id")
                    .and_then(Value::as_str)
                    .map(ToString::to_string)
                    .or(session_id);
                cwd = value
                    .pointer("/payload/cwd")
                    .and_then(Value::as_str)
                    .map(ToString::to_string)
                    .or(cwd);
            }
            if value.get("type").and_then(Value::as_str) == Some("turn_context") {
                model = value
                    .pointer("/payload/model")
                    .and_then(Value::as_str)
                    .filter(|value| !value.trim().is_empty())
                    .map(ToString::to_string)
                    .or(model);
            }
            parse_codex_usage_event(
                &value,
                index,
                provider,
                provider_instance_id,
                source_kind,
                source_key,
                session_id.as_deref(),
                cwd.as_deref(),
                model.as_deref(),
            )
        } else {
            parse_claude_usage_event(
                &value,
                index,
                provider,
                provider_instance_id,
                source_kind,
                source_key,
            )
        };
        let Some(event) = event else {
            continue;
        };
        // Claude repeats a message's usage on each of its lines and the last
        // carries the final count, so a repeated id replaces the earlier event.
        let position = *positions
            .entry(event.source_event_id.clone())
            .or_insert(events.len());
        if position < events.len() {
            events[position] = event;
        } else {
            events.push(event);
        }
    }
    events
}

fn parse_claude_usage_event(
    value: &Value,
    index: usize,
    provider: &str,
    provider_instance_id: &str,
    source_kind: &str,
    source_key: &str,
) -> Option<UsageEvent> {
    let usage = value.pointer("/message/usage")?.as_object()?;
    let input_tokens = value_u64(usage, "input_tokens");
    let output_tokens = value_u64(usage, "output_tokens");
    let cache_read_tokens = value_u64(usage, "cache_read_input_tokens");
    let cache_write_tokens = value_u64(usage, "cache_creation_input_tokens");
    if input_tokens == 0 && output_tokens == 0 && cache_read_tokens == 0 && cache_write_tokens == 0
    {
        return None;
    }
    let session_id = value
        .get("sessionId")
        .and_then(Value::as_str)
        .unwrap_or("local-session");
    let source_event_id = value
        .pointer("/message/id")
        .and_then(Value::as_str)
        .or_else(|| value.get("uuid").and_then(Value::as_str))
        .map(ToString::to_string)
        .unwrap_or_else(|| stable_line_id(source_kind, source_key, index, value));
    Some(UsageEvent {
        provider: provider.to_string(),
        provider_instance_id: provider_instance_id.to_string(),
        owned_id: Some(session_id.to_string()),
        workflow_id: None,
        turn_id: value
            .get("parentUuid")
            .and_then(Value::as_str)
            .map(ToString::to_string),
        project_id: value.get("cwd").and_then(Value::as_str).and_then(path_leaf),
        workspace_id: value
            .get("cwd")
            .and_then(Value::as_str)
            .map(ToString::to_string),
        occurred_at_micros: value
            .get("timestamp")
            .and_then(parse_timestamp_micros)
            .unwrap_or_else(now_micros),
        input_tokens,
        output_tokens,
        cache_read_tokens,
        cache_write_tokens,
        reasoning_tokens: 0,
        model: value
            .pointer("/message/model")
            .and_then(Value::as_str)
            .filter(|model| !model.trim().is_empty())
            .unwrap_or("unknown")
            .to_string(),
        estimated_cost_micros: None,
        estimate_rate_version: None,
        source_kind: source_kind.to_string(),
        source_event_id,
        source_key: opaque_source_key(source_key),
    })
}

fn parse_codex_usage_event(
    value: &Value,
    index: usize,
    provider: &str,
    provider_instance_id: &str,
    source_kind: &str,
    source_key: &str,
    session_id: Option<&str>,
    cwd: Option<&str>,
    model: Option<&str>,
) -> Option<UsageEvent> {
    if value.get("type").and_then(Value::as_str) != Some("event_msg")
        || value.pointer("/payload/type").and_then(Value::as_str) != Some("token_count")
    {
        return None;
    }
    let usage = value
        .pointer("/payload/info/last_token_usage")?
        .as_object()?;
    let output_tokens = value_u64(usage, "output_tokens");
    let cache_read_tokens = value_u64(usage, "cached_input_tokens");
    let cache_write_tokens = value_u64(usage, "cache_write_input_tokens");
    let input_tokens = value_u64(usage, "input_tokens").saturating_sub(cache_read_tokens + cache_write_tokens); // Codex counts cached input inside input_tokens; rows store uncached input.
    let reasoning_tokens = value_u64(usage, "reasoning_output_tokens");
    if input_tokens == 0 && output_tokens == 0 && cache_read_tokens == 0 && reasoning_tokens == 0 {
        return None;
    }
    let source_event_id = stable_line_id(source_kind, source_key, index, value);
    Some(UsageEvent {
        provider: provider.to_string(),
        provider_instance_id: provider_instance_id.to_string(),
        owned_id: Some(session_id.unwrap_or("local-session").to_string()),
        workflow_id: None,
        turn_id: Some(format!("token-count-{index}")),
        project_id: cwd.and_then(path_leaf),
        workspace_id: cwd.map(ToString::to_string),
        occurred_at_micros: value
            .get("timestamp")
            .and_then(parse_timestamp_micros)
            .unwrap_or_else(now_micros),
        input_tokens,
        output_tokens,
        cache_read_tokens,
        cache_write_tokens,
        reasoning_tokens,
        model: model.unwrap_or("codex").to_string(),
        estimated_cost_micros: None,
        estimate_rate_version: None,
        source_kind: source_kind.to_string(),
        source_event_id,
        source_key: opaque_source_key(source_key),
    })
}

fn value_u64(values: &serde_json::Map<String, Value>, key: &str) -> u64 {
    values.get(key).and_then(Value::as_u64).unwrap_or_default()
}

fn path_leaf(path: &str) -> Option<String> {
    Path::new(path)
        .file_name()
        .map(|value| value.to_string_lossy().to_string())
}

fn stable_line_id(source_kind: &str, source_key: &str, index: usize, value: &Value) -> String {
    let mut hash = 0xcbf29ce484222325_u64;
    for byte in format!("{source_kind}:{source_key}:{index}:{value}").as_bytes() {
        hash ^= u64::from(*byte);
        hash = hash.wrapping_mul(0x100000001b3);
    }
    format!("{source_kind}-{hash:016x}")
}

/// Read only the small session header needed to keep incremental tails
/// attributed to the same session/workspace after the cursor moves past the
/// initial metadata line.
pub fn read_source_context(path: &Path, provider: &str) -> UsageSourceContext {
    if !provider.eq_ignore_ascii_case("codex") {
        return UsageSourceContext::default();
    }
    let Ok(file) = fs::File::open(path) else {
        return UsageSourceContext::default();
    };
    let mut text = String::new();
    if file.take(64 * 1024).read_to_string(&mut text).is_err() {
        return UsageSourceContext::default();
    }
    let mut context = UsageSourceContext::default();
    for line in text.lines() {
        let Ok(value) = serde_json::from_str::<Value>(line) else {
            continue;
        };
        match value.get("type").and_then(Value::as_str) {
            Some("session_meta") => {
                context.session_id = value
                    .pointer("/payload/id")
                    .and_then(Value::as_str)
                    .map(ToString::to_string)
                    .or(context.session_id);
                context.cwd = value
                    .pointer("/payload/cwd")
                    .and_then(Value::as_str)
                    .map(ToString::to_string)
                    .or(context.cwd);
            }
            Some("turn_context") => {
                context.model = value
                    .pointer("/payload/model")
                    .and_then(Value::as_str)
                    .filter(|value| !value.trim().is_empty())
                    .map(ToString::to_string)
                    .or(context.model);
            }
            _ => {}
        }
        if context.session_id.is_some() && context.cwd.is_some() && context.model.is_some() {
            break;
        }
    }
    context
}

fn parse_timestamp_micros(value: &Value) -> Option<i64> {
    if let Some(number) = value.as_i64() {
        return Some(if number < 10_000_000_000 {
            number.saturating_mul(1_000_000)
        } else if number < 10_000_000_000_000 {
            number.saturating_mul(1_000)
        } else {
            number
        });
    }
    let text = value.as_str()?;
    parse_rfc3339_micros(text)
}

fn parse_rfc3339_micros(value: &str) -> Option<i64> {
    let (date, time) = value.split_once('T').or_else(|| value.split_once(' '))?;
    let mut date_parts = date.split('-');
    let year = date_parts.next()?.parse::<i64>().ok()?;
    let month = date_parts.next()?.parse::<i64>().ok()?;
    let day = date_parts.next()?.parse::<i64>().ok()?;
    let timezone_start = time.find(['Z', '+', '-']).unwrap_or(time.len());
    let clock = &time[..timezone_start];
    let mut clock_parts = clock.split(':');
    let hour = clock_parts.next()?.parse::<i64>().ok()?;
    let minute = clock_parts.next()?.parse::<i64>().ok()?;
    let second_fraction = clock_parts.next()?;
    let (second_text, fraction_text) = second_fraction
        .split_once('.')
        .unwrap_or((second_fraction, ""));
    let second = second_text.parse::<i64>().ok()?;
    let fraction = if fraction_text.is_empty() {
        0
    } else {
        let digits = fraction_text.chars().take(6).collect::<String>();
        digits
            .parse::<i64>()
            .ok()?
            .saturating_mul(10_i64.pow(6_u32.saturating_sub(digits.len() as u32)))
    };
    let offset_minutes = if time[timezone_start..].starts_with('Z') || timezone_start == time.len()
    {
        0
    } else {
        let offset = &time[timezone_start..];
        let sign = if offset.starts_with('-') { -1 } else { 1 };
        let offset = offset.trim_start_matches(['+', '-']);
        let mut parts = offset.split(':');
        sign * (parts.next()?.parse::<i64>().ok()?.saturating_mul(60)
            + parts.next().unwrap_or("0").parse::<i64>().ok()?)
    };
    Some(
        (days_from_civil(year, month, day)
            .saturating_mul(86_400)
            .saturating_add(hour.saturating_mul(3_600))
            .saturating_add(minute.saturating_mul(60))
            .saturating_add(second)
            .saturating_sub(offset_minutes.saturating_mul(60)))
        .saturating_mul(1_000_000)
        .saturating_add(fraction),
    )
}

fn days_from_civil(mut year: i64, month: i64, day: i64) -> i64 {
    year -= i64::from(month <= 2);
    let era = if year >= 0 { year } else { year - 399 } / 400;
    let year_of_era = year - era * 400;
    let month_prime = month + if month > 2 { -3 } else { 9 };
    let day_of_year = (153 * month_prime + 2) / 5 + day - 1;
    let day_of_era = year_of_era * 365 + year_of_era / 4 - year_of_era / 100 + day_of_year;
    era * 146_097 + day_of_era - 719_468
}

/// Only Codex publishes event_msg.token_count.rate_limits in these local JSONL stores
/// (live_quota_record_is_cleanly_available_or_unavailable below); Claude uses usage_remote.
/// Missing rate_limits returns None, so callers show unavailable instead of fabricated zero.
pub fn read_latest_local_quota(provider: &str) -> Option<LocalQuotaSnapshot> {
    if !provider.eq_ignore_ascii_case("codex") {
        return None;
    }
    let mut sources = discover_local_usage_sources()
        .into_iter()
        .filter(|source| source.provider == "codex")
        .collect::<Vec<_>>();
    sources.sort_by_key(|source| {
        fs::metadata(&source.path)
            .and_then(|metadata| metadata.modified())
            .ok()
    });
    let mut latest = None::<i64>;
    let mut latest_windows = std::collections::BTreeMap::<String, (i64, Value)>::new();
    let mut account = None::<String>;
    for source in sources.into_iter().rev() {
        let Ok(text) = read_tail_text(&source.path, 256 * 1024) else {
            continue;
        };
        for line in text.lines() {
            let Ok(value) = serde_json::from_str::<Value>(line) else {
                continue;
            };
            if value.pointer("/payload/type").and_then(Value::as_str) != Some("token_count") {
                continue;
            }
            let Some(rate_limits) = value.pointer("/payload/rate_limits") else {
                continue;
            };
            if rate_limits.is_null() {
                continue;
            }
            let captured = value
                .get("timestamp")
                .and_then(parse_timestamp_micros)
                .unwrap_or_default();
            if latest.map_or(true, |current| captured > current) {
                latest = Some(captured);
            }
            if let Some(plan_type) = rate_limits.get("plan_type").and_then(Value::as_str) {
                account = Some(plan_type.to_string());
            }
            for key in ["primary", "secondary"] {
                if let Some(window) = rate_limits.get(key) {
                    if latest_windows
                        .get(key)
                        .map_or(true, |(current, _)| captured > *current)
                    {
                        latest_windows.insert(key.to_string(), (captured, window.clone()));
                    }
                }
            }
        }
    }
    let captured = latest?;
    let rate_limits = Value::Object(
        latest_windows
            .into_iter()
            .map(|(key, (_, window))| (key, window))
            .collect(),
    );
    let windows = parse_quota_windows(&rate_limits);
    (!windows.is_empty()).then_some(LocalQuotaSnapshot {
        provider: provider.to_string(),
        account,
        windows,
        captured_at: (captured.max(0) as u128) / 1_000,
    })
}

fn parse_quota_windows(rate_limits: &Value) -> Vec<LocalQuotaWindow> {
    ["primary", "secondary"]
        .into_iter()
        .filter_map(|key| rate_limits.get(key))
        .filter_map(parse_quota_window)
        .collect()
}

fn parse_quota_window(window: &Value) -> Option<LocalQuotaWindow> {
    let window = window.as_object()?;
    let percent_consumed = window.get("used_percent").and_then(Value::as_f64)?;
    let window_minutes = window.get("window_minutes").and_then(Value::as_u64);
    let reset_at = window
        .get("resets_at")
        .and_then(Value::as_i64)
        .map(|value| value.to_string());
    Some(LocalQuotaWindow {
        name: quota_window_label(window_minutes),
        semantics: window_minutes.map_or_else(
            || "Provider-defined quota window".to_string(),
            |minutes| format!("Provider-defined {minutes}-minute quota window"),
        ),
        percent_consumed: percent_consumed.clamp(0.0, 100.0),
        reset_at,
    })
}

fn quota_window_label(window_minutes: Option<u64>) -> String {
    let Some(minutes) = window_minutes.filter(|minutes| *minutes > 0) else {
        return "Provider window".to_string();
    };
    if (6 * 24 * 60..=8 * 24 * 60).contains(&minutes) {
        return "Weekly".to_string();
    }
    if minutes < 60 {
        return format!("{minutes}-minute");
    }
    if minutes % 60 == 0 {
        return format!("{}-hour", minutes / 60);
    }
    let hours = minutes as f64 / 60.0;
    format!("{hours:.1}-hour")
}

fn read_tail_text(path: &Path, max_bytes: u64) -> Result<String, String> {
    let metadata = fs::metadata(path)
        .map_err(|error| format!("Usage source metadata is unavailable: {error}"))?;
    let start = metadata.len().saturating_sub(max_bytes);
    let mut file = fs::File::open(path)
        .map_err(|error| format!("Usage source could not be opened: {error}"))?;
    file.seek(SeekFrom::Start(start))
        .map_err(|error| format!("Usage source tail could not seek: {error}"))?;
    let mut bytes = Vec::new();
    file.read_to_end(&mut bytes)
        .map_err(|error| format!("Usage source tail could not be read: {error}"))?;
    if start > 0 {
        if let Some(index) = bytes.iter().position(|byte| *byte == b'\n') {
            bytes.drain(..=index);
        }
    }
    Ok(String::from_utf8_lossy(&bytes).into_owned())
}

pub fn opaque_source_key(source_key: &str) -> String {
    if source_key.len() == 23
        && source_key.starts_with("source-")
        && source_key[7..]
            .chars()
            .all(|character| character.is_ascii_hexdigit())
    {
        return source_key.to_string();
    }
    // FNV-1a is small, deterministic, and does not retain any portion of a
    // path or provider payload in the database cursor key.
    let mut hash = 0xcbf29ce484222325_u64;
    for byte in source_key.as_bytes() {
        hash ^= u64::from(*byte);
        hash = hash.wrapping_mul(0x100000001b3);
    }
    format!("source-{hash:016x}")
}

fn file_identity(_path: &Path, metadata: &fs::Metadata) -> Result<String, String> {
    let mut hash = 0xcbf29ce484222325_u64;
    for byte in metadata
        .dev()
        .to_le_bytes()
        .iter()
        .chain(metadata.ino().to_le_bytes().iter())
    {
        hash ^= u64::from(*byte);
        hash = hash.wrapping_mul(0x100000001b3);
    }
    Ok(format!("file-{hash:016x}"))
}

fn metadata_modified_micros(metadata: &fs::Metadata) -> i64 {
    metadata
        .modified()
        .ok()
        .and_then(|value| value.duration_since(UNIX_EPOCH).ok())
        .map(|value| value.as_micros().min(i64::MAX as u128) as i64)
        .unwrap_or_else(|| now_micros())
}

fn now_micros() -> i64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|value| value.as_micros().min(i64::MAX as u128) as i64)
        .unwrap_or_default()
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::io::Write;
    mod claude;

    #[test]
    fn usage_source_cursor_resets_after_truncation_without_replaying_same_suffix() {
        let path =
            std::env::temp_dir().join(format!("mcb-usage-source-{}.jsonl", std::process::id()));
        let mut file = fs::File::create(&path).unwrap();
        writeln!(file, "{{\"provider\":\"p\"}}").unwrap();
        let first = read_incremental_jsonl(&path, None, "p", "i", "jsonl", "source").unwrap();
        assert_eq!(first.lines.len(), 1);
        fs::write(&path, b"{\"provider\":\"p\"}\n").unwrap();
        let second = read_incremental_jsonl(
            &path,
            Some(&UsageSourceCursor {
                offset: 999,
                ..first.next_cursor.clone()
            }),
            "p",
            "i",
            "jsonl",
            "source",
        )
        .unwrap();
        assert_eq!(second.lines.len(), 1);
        assert_eq!(first.next_cursor.source_key, opaque_source_key("source"));
        assert!(!first.next_cursor.source_key.contains("/"));
        let _ = fs::remove_file(path);
    }

    #[test]
    fn codex_token_count_records_become_non_zero_events() {
        let lines = vec![
            r#"{"type":"session_meta","payload":{"id":"session-2","cwd":"/tmp/project/workspace"}}"#.to_string(),
            r#"{"type":"turn_context","payload":{"model":"gpt-5.6-sol"}}"#.to_string(),
            r#"{"type":"event_msg","timestamp":"2026-08-08T12:34:56Z","payload":{"type":"token_count","info":{"last_token_usage":{"input_tokens":20,"cached_input_tokens":5,"output_tokens":9,"reasoning_output_tokens":4}}}}"#.to_string(),
        ];
        let events = parse_usage_events_for_source_with_context(
            &lines,
            "codex",
            "local",
            "codex-jsonl",
            "/tmp/rollout.jsonl",
            &UsageSourceContext::default(),
        );
        assert_eq!(events.len(), 1);
        assert_eq!(events[0].owned_id.as_deref(), Some("session-2"));
        assert_eq!(events[0].input_tokens, 15);
        assert_eq!(events[0].cache_read_tokens, 5);
        assert_eq!(events[0].output_tokens, 9);
        assert_eq!(events[0].reasoning_tokens, 4);
        assert_eq!(events[0].model, "gpt-5.6-sol");
    }

    #[test]
    #[ignore = "reads provider quota records from the machine"]
    fn live_quota_record_is_cleanly_available_or_unavailable() {
        match read_latest_local_quota("codex") {
            Some(snapshot) => crate::debug_log::stderr_log!(
                "live quota provider={} account={:?} windows={:?}",
                snapshot.provider,
                snapshot.account,
                snapshot.windows
            ),
            None => crate::debug_log::stderr_log!("live quota unavailable"),
        }
    }

    #[test]
    fn weekly_only_primary_report_produces_one_weekly_window() {
        let windows = parse_quota_windows(&serde_json::json!({
            "primary": {
                "used_percent": 42.0,
                "window_minutes": 10_080,
                "resets_at": 1_786_160_562_i64
            },
            "secondary": null
        }));

        assert_eq!(windows.len(), 1);
        assert_eq!(windows[0].name, "Weekly");
        assert_eq!(windows[0].percent_consumed, 42.0);
        assert_eq!(windows[0].reset_at.as_deref(), Some("1786160562"));
    }

    #[test]
    fn five_hour_and_weekly_report_produces_two_duration_labeled_windows() {
        let windows = parse_quota_windows(&serde_json::json!({
            "primary": {
                "used_percent": 12.0,
                "window_minutes": 300,
                "resets_at": 1_783_741_721_i64
            },
            "secondary": {
                "used_percent": 17.0,
                "window_minutes": 10_080,
                "resets_at": 1_784_310_518_i64
            }
        }));

        assert_eq!(windows.len(), 2);
        assert_eq!(windows[0].name, "5-hour");
        assert_eq!(windows[1].name, "Weekly");
    }
}
