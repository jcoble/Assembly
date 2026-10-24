pub(super) mod legacy_import;
pub(super) mod transcript;
use std::collections::BTreeMap;
use std::path::Path;
use std::time::{SystemTime, UNIX_EPOCH};
use mcb_core::session_store::{EventRow, SessionRow, SessionStore};
use serde::{Deserialize, Serialize};
use serde_json::Value;
use uuid::Uuid;
use super::protocol::{
    AgentConversationEvent, AgentConversationPayload, AgentConversationProvider, AgentEventType, AgentRawFrameReference,
    TerminalProjectionPayload,
};
use self::transcript::{
    complete_lines, discover_child, parse_durable_child_line, parse_durable_line, read_range,
    ProjectedRecord,
};

/// Where an import stopped, stored inside the session's `extra_json`.
#[derive(Clone, Debug, Default, Deserialize, Eq, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ImportCursor {
    pub transcript_path: String,
    pub cutoff_offset: u64,
    pub reached_start: bool,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub parent_native_session_id: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub child_session_id: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub parent_owned_id: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub remote_profile_id: Option<String>,
    #[serde(default)]
    pub forward_offset: u64,
    #[serde(default)]
    pub forward_sequence: i64,
    #[serde(default)]
    pub initial_tail_imported: bool,
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub trailing_bytes: Vec<u8>,
    /// The transcript's length when it was named for import; a longer file has turns added since.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub imported_length: Option<u64>,
    /// Records read from the transcript are numbered above this: the head a session read again had.
    #[serde(default)]
    pub numbered_after: i64,
}

pub struct ImportedTail {
    /// Records parsed from the tail, oldest first.
    pub records: Vec<ProjectedRecord>,
    /// Byte offset of the first line included. Pass this back as `end_offset` to read further back.
    pub cutoff_offset: u64,
    /// True when the tail reaches the start of the file and nothing older remains.
    pub reached_start: bool,
    /// Incomplete bytes at the captured end, retained for a later append.
    pub trailing_bytes: Vec<u8>,
}

/// Messages arriving while a session is read in would be lost, so one its own CLI or app is running is refused.
pub const RUNNING_ELSEWHERE: &str = "This session is still running outside Assembly. Stop it there, then resume it here.";

/// Imports a transcript whole: names the session, then reads its newest page.
///
/// Resuming through the front end does these two halves separately, so that the
/// session can be looked at while its conversation is still arriving. Nothing
/// outside the tests wants them welded together, but the tests want to assert on
/// the finished result, and this is the same order the two commands run in.
#[cfg(test)]
#[allow(clippy::too_many_arguments)]
pub fn import_session(
    store: &SessionStore,
    provider: AgentConversationProvider,
    native_session_id: &str,
    path: &Path,
    cwd: &str,
    title: Option<String>,
    max_bytes: u64,
    max_records: usize,
) -> Result<String, String> {
    let (owned_id, _) = begin_import_session(store, provider, native_session_id, path, cwd, title, None, false)?;
    finish_import_session(store, &owned_id, max_bytes, max_records)?;
    Ok(owned_id)
}

/// Writes the session row an import is about to fill, and reads no records.
///
/// A resumed session is two pieces of work with very different costs: naming the
/// row, which is immediate, and reading the transcript into it, which is not.
/// Separating them lets the rail show the session — its name, its agent, its
/// folder — while the conversation is still on its way, rather than showing
/// nothing at all until the last record has landed.
///
/// The cursor starts at the end of the file, which is where reading the newest
/// page begins. An import that never reaches `finish_import_session` therefore
/// leaves a session that is empty but not wrong: asking it for an older page
/// reads the newest one, which is what a first import would have read anyway.
///
/// `title` is the name the past session already had, as the History card shows
/// it. It is written here, with the row, because the database is what the rail
/// reads: a title held only in the front end survived until the next reload and
/// then the row went back to showing a raw owned id. `project_id` is the project the session is filed under when it has none.
#[allow(clippy::too_many_arguments)]
pub fn begin_import_session(store: &SessionStore, provider: AgentConversationProvider, native_session_id: &str,
    path: &Path, cwd: &str, title: Option<String>, project_id: Option<String>, open: bool) -> Result<(String, bool), String> {
    if !open && self::transcript::running_elsewhere(native_session_id) { return Err(RUNNING_ELSEWHERE.into()); }
    let metadata = path.metadata().map_err(|error| format!("Could not inspect transcript: {error}"))?;
    if let Some(mut row) = existing_import_session(store, provider, native_session_id)? {
        if row.project_id.is_none() && project_id.is_some() { row.project_id = project_id; store.upsert_session(&row).map_err(|error| error.to_string())?; }
        // The transcript is the whole conversation. A session never read from it (one
        // started here), or whose file has grown since it was, lacks turns its own events
        // do not hold or would repeat, so it is read again from the file. Its events are
        // numbered on above the old head, which live delivery has already passed.
        let cursor = optional_import_cursor(&row.extra_json)?;
        if cursor.and_then(|cursor| cursor.imported_length).is_some_and(|length| metadata.len() <= length) {
            return Ok((row.owned_id, false));
        }
        if open { return Err("Close this session before importing the turns taken outside Assembly.".into()); }
        // Its saved events go next, so a transcript that cannot be read now leaves them standing.
        std::fs::File::open(path).map_err(|error| format!("Could not read transcript: {error}"))?;
        let head = store.latest_seq(&row.owned_id).map_err(|error| error.to_string())?;
        store.enforce_event_cap(&row.owned_id, 0).map_err(|error| error.to_string())?;
        let cursor = ImportCursor { transcript_path: path.to_string_lossy().into_owned(), cutoff_offset: metadata.len(),
            imported_length: Some(metadata.len()), numbered_after: head, ..ImportCursor::default() };
        row.extra_json = merge_import_cursor(&row.extra_json, &cursor)?;
        store.upsert_session(&row).map_err(|error| error.to_string())?;
        return Ok((row.owned_id, true));
    }
    let owned_id = Uuid::new_v4().to_string();
    // When the transcript was last written is when the session was last active.
    // Reading the clock instead would sort a year-old conversation above today's
    // work for as long as it took the records to arrive, and then move it.
    let last_activity_at_ms = metadata.modified().ok().and_then(|modified| modified.duration_since(UNIX_EPOCH).ok())
        .and_then(|since| i64::try_from(since.as_millis()).ok()).unwrap_or_else(now_millis);
    let cursor = ImportCursor { transcript_path: path.to_string_lossy().into_owned(), cutoff_offset: metadata.len(),
        imported_length: Some(metadata.len()), ..ImportCursor::default() };
    let extra_json = merge_import_cursor(&super::manager::imported_session_extra(provider)?, &cursor)?;
    // Else the folder's project as of now, matched like the back-fill: one added after History scanned this still counts.
    let project_id = match project_id { Some(project_id) => Some(project_id),
        None => crate::project_folders::match_local_folders(store, &[cwd.to_string()])?.into_iter().next().map(|(_, project_id)| project_id) };
    let session = SessionRow {
        owned_id: owned_id.clone(),
        native_session_id: Some(native_session_id.to_string()),
        provider: enum_storage_value(provider)?,
        model: None,
        effort: None,
        cwd: cwd.to_string(),
        worktree: None,
        branch: None,
        title: title
            .map(|title| title.trim().chars().take(200).collect::<String>())
            .filter(|title| !title.is_empty()),
        title_source: None,
        project: None,
        project_id,
        state: "ready".to_string(),
        suspended: false,
        created_at_ms: last_activity_at_ms,
        last_activity_at_ms,
        extra_json,
    };
    store
        .upsert_session(&session)
        .map_err(|error| error.to_string())?;
    Ok((owned_id, false))
}

/// The session already holding this provider conversation. Importing it again reuses the row:
/// a second row for one conversation was filed under two folders once only one learnt its worktree.
pub fn existing_import_session(store: &SessionStore, provider: AgentConversationProvider, native_session_id: &str) -> Result<Option<SessionRow>, String> {
    let stored_provider = enum_storage_value(provider)?;
    store.session_for_native_id(&stored_provider, native_session_id).map_err(|error| error.to_string())
}

/// Assembly's own run of an imported session writes to its transcript too (Claude adds bookkeeping lines on every
/// load and turn), so after each turn and on suspend the saved length follows the file: only growth from outside reads it again.
/// `starting` is Assembly about to run it: growth found then is turns taken elsewhere, so the length is forgotten, which
/// makes the next import read them and stops the following until it has.
pub fn follow_imported_length(store: &SessionStore, owned_id: &str, starting: bool) -> Result<(), String> {
    let Some(mut row) = store.get_session(owned_id).map_err(|error| error.to_string())? else { return Ok(()) };
    let Some(mut cursor) = optional_import_cursor(&row.extra_json)?.filter(|cursor| cursor.imported_length.is_some()) else { return Ok(()) };
    // A session tracked from its start learns where its transcript is once its agent has written it.
    if cursor.transcript_path.is_empty() { cursor.transcript_path = row.native_session_id.as_deref().map(|id| self::transcript::parse_provider(&row.provider).and_then(|provider| self::transcript::discover(provider, id)))
        .transpose()?.flatten().map(|location| location.canonical_path.to_string_lossy().into_owned()).unwrap_or_default(); }
    let Ok(metadata) = std::fs::metadata(&cursor.transcript_path) else { return Ok(()) };
    if starting && cursor.imported_length.is_some_and(|length| metadata.len() <= length) { return Ok(()); }
    row.extra_json = merge_import_cursor(&row.extra_json, &ImportCursor { imported_length: (!starting).then_some(metadata.len()), ..cursor })?;
    store.upsert_session(&row).map_err(|error| error.to_string())
}

/// A session Assembly starts is tracked from then: its transcript's length is saved (nothing until its agent writes the
/// file), so History reads it again only for turns taken elsewhere. Sessions from before this are never guessed at.
pub fn track_new_transcript(store: &SessionStore, owned_id: &str, transcript: Option<&Path>) -> Result<(), String> {
    let Some(mut row) = store.get_session(owned_id).map_err(|error| error.to_string())? else { return Ok(()) };
    if optional_import_cursor(&row.extra_json)?.is_some() { return Ok(()); }
    let cursor = ImportCursor { transcript_path: transcript.map(|path| path.to_string_lossy().into_owned()).unwrap_or_default(), reached_start: true,
        imported_length: Some(transcript.and_then(|path| path.metadata().ok()).map_or(0, |metadata| metadata.len())), ..ImportCursor::default() };
    row.extra_json = merge_import_cursor(&row.extra_json, &cursor)?;
    store.upsert_session(&row).map_err(|error| error.to_string())
}

pub fn ensure_child_import(
    store: &SessionStore,
    parent_owned_id: &str,
    expected_remote_profile_id: Option<&str>,
    child_session_id: &str,
) -> Result<String, String> {
    let owned_id = child_owned_id(parent_owned_id, child_session_id)?;
    if let Some(existing) = store
        .get_session(&owned_id)
        .map_err(|error| error.to_string())?
    {
        if existing.native_session_id.as_deref() != Some(child_session_id) {
            return Err("Child session identity does not match".to_string());
        }
        store
            .upsert_child_session(parent_owned_id, expected_remote_profile_id, &existing)
            .map_err(|error| error.to_string())?;
        return Ok(owned_id);
    }
    let parent = store
        .get_session(parent_owned_id)
        .map_err(|error| error.to_string())?
        .ok_or_else(|| "Child transcript parent was not found".to_string())?;
    let provider = self::transcript::parse_provider(&parent.provider)?;
    let parent_native_session_id = parent
        .native_session_id
        .as_deref()
        .ok_or_else(|| "Child transcript parent has no native session id".to_string())?;
    let location = discover_child(provider, parent_native_session_id, child_session_id)?;
    let cursor = ImportCursor {
        transcript_path: location
            .as_ref()
            .map(|location| location.canonical_path.to_string_lossy().into_owned())
            .unwrap_or_default(),
        cutoff_offset: location.as_ref().map_or(0, |location| location.len),
        reached_start: false,
        parent_native_session_id: Some(parent_native_session_id.to_string()),
        child_session_id: Some(child_session_id.to_string()),
        parent_owned_id: Some(parent_owned_id.to_string()),
        remote_profile_id: expected_remote_profile_id.map(str::to_string),
        forward_offset: location.as_ref().map_or(0, |location| location.len),
        ..ImportCursor::default()
    };
    let extra_json = merge_import_cursor(&super::manager::imported_session_extra(provider)?, &cursor)?;
    let session = SessionRow {
        owned_id: owned_id.clone(),
        native_session_id: Some(child_session_id.to_string()),
        provider: parent.provider,
        model: parent.model,
        effort: parent.effort,
        cwd: parent.cwd,
        worktree: parent.worktree,
        branch: parent.branch,
        title: Some(child_session_id.to_string()),
        title_source: None,
        project: parent.project,
        project_id: None,
        state: "ready".to_string(),
        suspended: false,
        created_at_ms: parent.created_at_ms,
        last_activity_at_ms: parent.last_activity_at_ms,
        extra_json,
    };
    store
        .upsert_child_session(parent_owned_id, expected_remote_profile_id, &session)
        .map_err(|error| error.to_string())?;
    Ok(owned_id)
}

pub fn child_owned_id(parent_owned_id: &str, child_session_id: &str) -> Result<String, String> {
    serde_json::to_string(&("child", parent_owned_id, child_session_id))
        .map_err(|error| format!("Could not encode child history id: {error}"))
}

#[derive(Clone, Copy, Debug, Default, Eq, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct RefreshedChildImport {
    pub added: usize,
    pub read_bytes: u64,
    pub has_more_source: bool,
}

pub fn refresh_child_import(
    store: &SessionStore,
    parent_owned_id: &str,
    expected_remote_profile_id: Option<&str>,
    child_owned_id: &str,
    max_bytes: u64,
    max_records: usize,
) -> Result<RefreshedChildImport, String> {
    if max_bytes == 0 || max_records == 0 {
        return Ok(RefreshedChildImport::default());
    }
    let session = store
        .get_session(child_owned_id)
        .map_err(|error| error.to_string())?
        .ok_or_else(|| "Child import session was not found".to_string())?;
    let cursor = import_cursor(&session.extra_json)?;
    let parent_native_session_id = cursor
        .parent_native_session_id
        .as_deref()
        .ok_or_else(|| "Child import has no parent session id".to_string())?;
    let child_session_id = cursor
        .child_session_id
        .as_deref()
        .ok_or_else(|| "Child import has no child session id".to_string())?;
    let provider = self::transcript::parse_provider(&session.provider)?;
    let location = discover_child(provider, parent_native_session_id, child_session_id)?
        .ok_or_else(|| format!("Child transcript {child_session_id} was not found"))?;
    if !cursor.transcript_path.is_empty()
        && location.canonical_path != Path::new(&cursor.transcript_path)
    {
        return Err("Child transcript path changed".to_string());
    }
    if location.len < cursor.forward_offset {
        return Err("Child transcript became shorter".to_string());
    }
    refresh_child_import_at_location(
        store,
        parent_owned_id,
        expected_remote_profile_id,
        child_owned_id,
        &session,
        cursor,
        provider,
        &location.canonical_path,
        location.len,
        max_bytes,
        max_records,
    )
}

#[allow(clippy::too_many_arguments)]
fn refresh_child_import_at_location(
    store: &SessionStore,
    parent_owned_id: &str,
    expected_remote_profile_id: Option<&str>,
    child_owned_id: &str,
    session: &SessionRow,
    mut cursor: ImportCursor,
    provider: AgentConversationProvider,
    path: &Path,
    file_len: u64,
    max_bytes: u64,
    max_records: usize,
) -> Result<RefreshedChildImport, String> {
    if cursor.transcript_path.is_empty() {
        let initialized = ImportCursor {
            transcript_path: path.to_string_lossy().into_owned(),
            cutoff_offset: file_len,
            forward_offset: file_len,
            ..cursor.clone()
        };
        commit_child_page(
            store,
            parent_owned_id,
            expected_remote_profile_id,
            child_owned_id,
            &cursor,
            &initialized,
            &[],
        )?;
        cursor = initialized;
    }
    if file_len < cursor.forward_offset {
        return Err("Child transcript became shorter".to_string());
    }
    let parent_native_session_id = cursor
        .parent_native_session_id
        .as_deref()
        .ok_or_else(|| "Child import has no parent session id".to_string())?;
    let child_session_id = cursor
        .child_session_id
        .as_deref()
        .ok_or_else(|| "Child import has no child session id".to_string())?;
    if !cursor.initial_tail_imported {
        let tail = read_tail_scoped(
            provider,
            parent_native_session_id,
            Some(child_session_id),
            path,
            cursor.forward_offset,
            max_bytes,
            max_records,
        )?;
        let records = child_importable(tail.records);
        let mut rows = Vec::with_capacity(records.len());
        for (index, record) in records.into_iter().enumerate() {
            let sequence = i64::try_from(index + 1)
                .map_err(|_| "Child import sequence exceeded the store limit".to_string())?;
            rows.push(imported_event_row(
                child_owned_id, provider, sequence, record, session.created_at_ms,
            )?);
        }
        let next_cursor = ImportCursor {
            cutoff_offset: tail.cutoff_offset,
            reached_start: tail.reached_start,
            forward_sequence: i64::try_from(rows.len())
                .map_err(|_| "Child import sequence exceeded the store limit".to_string())?,
            initial_tail_imported: true,
            trailing_bytes: tail.trailing_bytes,
            ..cursor.clone()
        };
        commit_child_page(
            store, parent_owned_id, expected_remote_profile_id, child_owned_id,
            &cursor, &next_cursor, &rows,
        )?;
        return Ok(RefreshedChildImport {
            added: rows.len(),
            read_bytes: cursor.forward_offset.saturating_sub(tail.cutoff_offset),
            has_more_source: file_len > cursor.forward_offset,
        });
    }
    let read_len = (file_len - cursor.forward_offset).min(max_bytes);
    let bytes = read_range(path, cursor.forward_offset, read_len)?;
    let trailing_len = u64::try_from(cursor.trailing_bytes.len())
        .map_err(|_| "Child transcript partial line is too large".to_string())?;
    let base_offset = cursor
        .forward_offset
        .checked_sub(trailing_len)
        .ok_or_else(|| "Child import cursor is invalid".to_string())?;
    let mut input = cursor.trailing_bytes.clone();
    input.extend_from_slice(&bytes);
    let (records, stopped_at, trailing) = child_forward_records(
        provider, parent_native_session_id, child_session_id, &input, max_records,
    );
    let next_forward_offset = if let Some(line_start) = stopped_at {
        base_offset
            .checked_add(u64::try_from(line_start).unwrap_or(u64::MAX))
            .ok_or_else(|| "Child import offset exceeded the store limit".to_string())?
    } else {
        cursor
            .forward_offset
            .checked_add(u64::try_from(bytes.len()).unwrap_or(u64::MAX))
            .ok_or_else(|| "Child import offset exceeded the store limit".to_string())?
    };
    let next_trailing = if stopped_at.is_some() {
        Vec::new()
    } else {
        trailing
    };
    let mut rows = Vec::with_capacity(records.len());
    let mut next_sequence = cursor.forward_sequence;
    for record in records {
        next_sequence = next_sequence
            .checked_add(1)
            .ok_or_else(|| "Child import sequence exceeded the store limit".to_string())?;
        rows.push(imported_event_row(
            child_owned_id,
            provider,
            next_sequence,
            record,
            session.created_at_ms,
        )?);
    }
    let next_cursor = ImportCursor {
        forward_offset: next_forward_offset,
        forward_sequence: next_sequence,
        trailing_bytes: next_trailing,
        ..cursor.clone()
    };
    commit_child_page(
        store, parent_owned_id, expected_remote_profile_id, child_owned_id,
        &cursor, &next_cursor, &rows,
    )?;
    Ok(RefreshedChildImport {
        added: rows.len(),
        read_bytes: bytes.len() as u64,
        has_more_source: next_forward_offset < file_len,
    })
}

fn commit_child_page(
    store: &SessionStore,
    parent_owned_id: &str,
    expected_remote_profile_id: Option<&str>,
    child_owned_id: &str,
    cursor: &ImportCursor,
    next_cursor: &ImportCursor,
    rows: &[EventRow],
) -> Result<(), String> {
    let expected = serde_json::to_string(cursor)
        .map_err(|error| format!("Could not encode child import cursor: {error}"))?;
    let next = serde_json::to_string(next_cursor)
        .map_err(|error| format!("Could not encode child import cursor: {error}"))?;
    store.commit_child_import_page(
        parent_owned_id, expected_remote_profile_id, child_owned_id, &expected, &next, rows,
    ).map_err(|error| error.to_string())
}

fn child_forward_records(
    provider: AgentConversationProvider,
    parent_native_session_id: &str,
    child_session_id: &str,
    input: &[u8],
    max_records: usize,
) -> (Vec<ProjectedRecord>, Option<usize>, Vec<u8>) {
    let (lines, trailing) = complete_lines(input, false);
    let input_start = input.as_ptr() as usize;
    let mut records = Vec::new();
    for line in lines {
        let line_start = line.as_ptr() as usize - input_start;
        let projected = child_importable(parse_durable_child_line(
            provider, parent_native_session_id, child_session_id, line,
        ));
        if !records.is_empty() && records.len().saturating_add(projected.len()) > max_records {
            return (records, Some(line_start), Vec::new());
        }
        records.extend(projected);
    }
    (records, None, trailing)
}

fn child_importable(records: Vec<ProjectedRecord>) -> Vec<ProjectedRecord> {
    importable(records)
        .into_iter()
        .filter(|record| record.event_type != AgentEventType::UsageUpdated)
        .collect()
}

/// Reads the newest page of the transcript into a session that already exists,
/// and returns how many records it gained.
///
/// The row's own timestamps are corrected here. They were the file's until now,
/// which is close enough to sort by but is not when the conversation actually
/// started, and the records are the only place that answer lives.
pub fn finish_import_session(
    store: &SessionStore,
    owned_id: &str,
    max_bytes: u64,
    max_records: usize,
) -> Result<usize, String> {
    // Reopening a session that already holds its transcript reads nothing. The
    // page this would fetch is the one already stored, and writing it again
    // would put every message in twice.
    if store.has_display_events(owned_id).map_err(|error| error.to_string())? { return Ok(0); }
    let mut session = store.get_session(owned_id).map_err(|error| error.to_string())?.ok_or_else(|| "Imported session was not found".to_string())?;
    let cursor = import_cursor(&session.extra_json)?;
    let provider = self::transcript::parse_provider(&session.provider)?;
    let native_session_id = session.native_session_id.clone().ok_or_else(|| "Imported session has no native session id".to_string())?;
    let tail = read_tail(provider, &native_session_id, Path::new(&cursor.transcript_path), cursor.cutoff_offset, max_bytes, max_records)?;
    // What this conversation actually ran on. Asking the agent is no use: an
    // adapter reports what it OFFERS, and Claude's answers "default" — true of a
    // session about to start, and wrong for one being picked up, which ran on
    // something specific and should carry on with it. The transcript wrote the
    // model and the effort against every message, and the newest of those is
    // what the conversation was using when it stopped.
    //
    // Read before `importable`, which keeps only records that are the
    // conversation itself. The configuration is the one record that is not — it
    // describes the session rather than appearing in it — so it is dropped there.
    let ran_with = tail
        .records
        .iter()
        .rev()
        .find(|record| record.event_type == AgentEventType::SessionConfigUpdated)
        .and_then(|record| record.payload.get("config"))
        .cloned();
    let ran_with_model = transcript_config_value(&ran_with, "model");
    let ran_with_effort = transcript_config_value(&ran_with, "effort");
    let records = importable(tail.records);
    let record_count = records.len();
    let created_at_ms = records
        .first()
        .and_then(record_timestamp)
        .unwrap_or(session.created_at_ms);
    let last_activity_at_ms = records
        .last()
        .and_then(record_timestamp)
        .unwrap_or(created_at_ms);
    for (index, record) in records.into_iter().enumerate() {
        let sequence = i64::try_from(index + 1).ok().and_then(|index| index.checked_add(cursor.numbered_after))
            .ok_or_else(|| "Imported event sequence exceeded the store limit".to_string())?;
        append_imported_record(store, owned_id, provider, sequence, record, created_at_ms)?;
    }
    session.created_at_ms = created_at_ms;
    session.last_activity_at_ms = last_activity_at_ms;
    if ran_with_model.is_some() {
        session.model.clone_from(&ran_with_model);
    }
    if ran_with_effort.is_some() {
        session.effort.clone_from(&ran_with_effort);
    }
    session.extra_json =
        merge_transcript_config(&session.extra_json, &ran_with_model, &ran_with_effort)?;
    session.extra_json = merge_import_cursor(
        &session.extra_json,
        &ImportCursor {
            transcript_path: cursor.transcript_path,
            cutoff_offset: tail.cutoff_offset,
            reached_start: tail.reached_start,
            ..cursor
        },
    )?;
    store
        .upsert_session(&session)
        .map_err(|error| error.to_string())?;
    Ok(record_count)
}

/// What one reach further back into a transcript found.
#[derive(Clone, Copy, Debug, Default, serde::Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ExtendedImport {
    /// How many events the reach added.
    pub added: usize,
    /// Whether the beginning of the transcript has now been reached.
    pub reached_start: bool,
}

/// How many windows one reach will read before giving the answer it has.
///
/// A window of a transcript can hold nothing a reader would want — a long run
/// of internal records, or of a tool talking to itself. Stopping at the first
/// such window and calling it the beginning is what made scrolling up look
/// broken: there was more behind it, and nothing asked again. So a reach keeps
/// going until it finds something or runs out of transcript, and this bounds
/// how much of the file one reach is allowed to walk in the meantime.
const EXTEND_WINDOW_LIMIT: usize = 8;

pub fn extend_session(
    store: &SessionStore,
    owned_id: &str,
    max_bytes: u64,
    max_records: usize,
) -> Result<ExtendedImport, String> {
    let session = store
        .get_session(owned_id)
        .map_err(|error| error.to_string())?
        .ok_or_else(|| "Imported session was not found".to_string())?;
    // A session started here rather than picked up from a transcript has no
    // cursor, and nothing older than its first event exists. Scrolling to the
    // top of one asks this question and deserves an answer, not an error.
    let Some(cursor) = optional_import_cursor(&session.extra_json)? else { return Ok(ExtendedImport { added: 0, reached_start: true }) };
    if cursor.reached_start { return Ok(ExtendedImport { added: 0, reached_start: true }); }
    // Named but its newest page not read yet: that read numbers the session, so older pages wait for it.
    if cursor.imported_length == Some(cursor.cutoff_offset) { return Ok(ExtendedImport { added: 0, reached_start: false }); }
    let provider = self::transcript::parse_provider(&session.provider)?;
    let native_session_id = session.native_session_id.clone().ok_or_else(|| "Imported session has no native session id".to_string())?;
    let transcript_session_id = cursor.parent_native_session_id.as_deref().unwrap_or(&native_session_id);

    // Keep reading back until there is something to show or the file runs out.
    // An empty window is not the beginning of a conversation, only a stretch of
    // it that holds nothing a reader wants.
    let mut offset = cursor.cutoff_offset;
    let mut reached_start = false;
    let mut records = Vec::new();
    for _ in 0..EXTEND_WINDOW_LIMIT {
        let tail = read_tail_scoped(
            provider,
            transcript_session_id,
            cursor.child_session_id.as_deref(),
            Path::new(&cursor.transcript_path),
            offset,
            max_bytes,
            max_records,
        )?;
        offset = tail.cutoff_offset;
        reached_start = tail.reached_start;
        records = if cursor.parent_owned_id.is_some() {
            child_importable(tail.records)
        } else {
            importable(tail.records)
        };
        if !records.is_empty() || reached_start {
            break;
        }
    }
    let record_count = records.len();
    let lowest_sequence = store
        .list_events(owned_id, i64::MIN, 1)
        .map_err(|error| error.to_string())?
        .first()
        .map_or(0, |event| event.seq);
    let first_sequence = lowest_sequence
        .checked_sub(
            i64::try_from(record_count)
                .map_err(|_| "Imported event count exceeded the store limit".to_string())?,
        )
        .ok_or_else(|| "Imported event sequence exceeded the store limit".to_string())?;
    let mut rows = Vec::with_capacity(record_count);
    for (index, record) in records.into_iter().enumerate() {
        let sequence = first_sequence
            .checked_add(
                i64::try_from(index)
                    .map_err(|_| "Imported event sequence exceeded the store limit".to_string())?,
            )
            .ok_or_else(|| "Imported event sequence exceeded the store limit".to_string())?;
        let row = imported_event_row(
            owned_id,
            enum_from_storage(&session.provider)?,
            sequence,
            record,
            session.created_at_ms,
        )?;
        if cursor.parent_owned_id.is_some() {
            rows.push(row);
        } else {
            store.append_event(&row).map_err(|error| error.to_string())?;
        }
    }
    let next_cursor = ImportCursor {
        transcript_path: cursor.transcript_path.clone(),
        cutoff_offset: offset,
        reached_start,
        ..cursor.clone()
    };
    if let Some(parent_owned_id) = cursor.parent_owned_id.as_deref() {
        commit_child_page(
            store,
            parent_owned_id,
            cursor.remote_profile_id.as_deref(),
            owned_id,
            &cursor,
            &next_cursor,
            &rows,
        )?;
        return Ok(ExtendedImport {
            added: record_count,
            reached_start,
        });
    }
    // Onto the row as it is now: a running session may have saved itself while this page was read.
    let mut latest = store.get_session(owned_id).map_err(|error| error.to_string())?.ok_or("Imported session was not found")?;
    latest.extra_json = merge_import_cursor(&latest.extra_json, &next_cursor)?;
    store.upsert_session(&latest).map_err(|error| error.to_string())?;
    Ok(ExtendedImport {
        added: record_count,
        reached_start,
    })
}

/// The records an import stores: the ones that are conversation in their own
/// right, and nothing else.
///
/// A transcript line only becomes a row when it has an event of the app's own to
/// become. A past session's settings and a sub-agent whose parent tool call the
/// file never recorded have no such event, and they are read past rather than
/// stored wrapped — the wrapped form belongs to the terminal route, which is not
/// in use, and one shape in the database is worth more than those two records.
///
/// The settings are the smaller loss than it looks: the adapter is asked for the
/// real ones when the session opens, and its answer is the current one.
/// The reader that rebuilds a conversation refuses any event that does not
/// follow the last one it saw, so the rows an import writes have to be numbered
/// without a break in them. Two things here would otherwise put breaks in.
///
/// The first is a token count. Only the newest is worth keeping and the store
/// drops the older ones as each new one lands — but a row deleted after it was
/// numbered leaves a hole where it used to be, and the reader stops at the first
/// hole. So all but the last are dropped here instead, before anything is
/// numbered, and no row is written that will not survive.
///
/// The second is the numbering itself. It starts at one, because the reader
/// begins a conversation expecting the number before the first event it is
/// given, and there is no number before zero to expect.
fn importable(records: Vec<ProjectedRecord>) -> Vec<ProjectedRecord> {
    let newest_usage = records
        .iter()
        .rposition(|record| record.event_type == AgentEventType::UsageUpdated);
    records
        .into_iter()
        .enumerate()
        .filter(|(index, record)| {
            record.native.is_some()
                && (record.event_type != AgentEventType::UsageUpdated
                    || Some(*index) == newest_usage)
        })
        .map(|(_, record)| record)
        .collect()
}

/// Writes one projected transcript record as its own row.
///
/// The records reaching here are already the conversation rather than the raw
/// file: the provider projectors keep messages, the session's configuration and
/// its token count, and drop everything else, so a two megabyte page arrives as
/// a few dozen records rather than a few hundred lines. Each one is a thing the
/// transcript shows, which is why each one is a row — the same shape a live
/// message is stored in, so a resumed conversation and the turns taken after it
/// are not two different kinds of thing.
///
/// A stored event is read back as a whole conversation event, so that is what an
/// import has to write. Storing only the projection inside it left rows that
/// could be written and never read: the transcript came back as a decode
/// failure, and the session it belonged to could not be opened at all.
///
/// The row's own sequence is what orders the transcript, and reading older pages
/// walks it backwards past zero — an event's `sequence` cannot go there, so
/// pages read before the first import all carry zero. That costs nothing today:
/// the transcript of an imported session is read as a whole from the store, in
/// row order, and the one place a sequence is compared against the last one seen
/// is live streaming, which an import is not.
fn append_imported_record(
    store: &SessionStore,
    owned_id: &str,
    provider: AgentConversationProvider,
    sequence: i64,
    record: ProjectedRecord,
    fallback_timestamp: i64,
) -> Result<(), String> {
    let row = imported_event_row(
        owned_id,
        provider,
        sequence,
        record,
        fallback_timestamp,
    )?;
    store.append_event(&row).map_err(|error| error.to_string())
}

fn imported_event_row(
    owned_id: &str,
    provider: AgentConversationProvider,
    sequence: i64,
    mut record: ProjectedRecord,
    fallback_timestamp: i64,
) -> Result<EventRow, String> {
    let created_at_ms = record_timestamp(&record).unwrap_or(fallback_timestamp);
    let kind = enum_storage_value(record.event_type)?;
    // A message out of a past transcript is stored as the message it is, not as
    // a description of a line in a file. That is what makes a resumed
    // conversation and the turns taken after it the same kind of thing: one
    // shape in the database, one shape on the way out, and no reader that has to
    // know which half of the conversation it is looking at.
    //
    // `importable` has already dropped anything without a shape of its own, so
    // there is nothing here to fall back to.
    let mut payload = record.native.take().ok_or_else(|| {
        "An imported record reached the store with no event of its own".to_string()
    })?;
    if let AgentConversationPayload::AssistantMessage { text, blocks, .. } = &mut payload {
        if blocks.is_none() && !std::env::args().any(|argument| argument == "--assembly-server") {
            *blocks = Some(super::safe_markdown::parse_safe_markdown(text));
        }
    }
    let event = AgentConversationEvent {
        runtime_state: None,
        owned_id: owned_id.to_string(),
        provider,
        generation: 0,
        sequence,
        timestamp_ms: u128::try_from(created_at_ms.max(0)).unwrap_or_default(),
        turn_id: None,
        payload,
    };
    let payload_json = serde_json::to_string(&event)
        .map_err(|error| format!("Could not encode imported transcript event: {error}"))?;
    Ok(EventRow {
        owned_id: owned_id.to_string(),
        seq: sequence,
        turn_id: None,
        kind,
        payload_json,
        created_at_ms,
    })
}

/// Wraps a record as a terminal projection.
///
/// This is the terminal route, which nothing calls today. It is kept whole
/// rather than deleted because the terminal way of reading a session is coming
/// back, and this is the piece that turns a transcript record into one of its
/// events. Importing does not go through it: an imported message is stored as a
/// message.
#[allow(dead_code)]
fn projection_payload(
    native_session_id: &str,
    record: ProjectedRecord,
) -> TerminalProjectionPayload {
    TerminalProjectionPayload {
        event_type: record.event_type,
        provider_instance_id: format!("imported-transcript:{native_session_id}"),
        timestamp_ms: u64::try_from(record.timestamp_ms)
            .ok()
            .filter(|value| *value != 0),
        native_session_id: native_session_id.to_string(),
        item_id: record.item_id.clone(),
        payload: record.payload,
        provider_metadata: BTreeMap::from([
            ("source".into(), Value::String("imported-transcript".into())),
            ("historical".into(), Value::Bool(true)),
        ]),
        raw_frame_reference: AgentRawFrameReference {
            id: record.key,
            redacted: true,
        },
    }
}

/// The import cursor when the session has one, and `None` when it never did.
pub(super) fn optional_import_cursor(extra_json: &str) -> Result<Option<ImportCursor>, String> {
    let extra: Value = serde_json::from_str(extra_json)
        .map_err(|error| format!("Could not decode stored session metadata: {error}"))?;
    let Some(cursor) = extra.get("import").cloned() else {
        return Ok(None);
    };
    serde_json::from_value(cursor)
        .map(Some)
        .map_err(|error| format!("Could not decode import cursor: {error}"))
}

fn import_cursor(extra_json: &str) -> Result<ImportCursor, String> {
    let extra: Value = serde_json::from_str(extra_json)
        .map_err(|error| format!("Could not decode stored session metadata: {error}"))?;
    serde_json::from_value(
        extra
            .get("import")
            .cloned()
            .ok_or_else(|| "Session has no import cursor".to_string())?,
    )
    .map_err(|error| format!("Could not decode import cursor: {error}"))
}

/// One field out of the configuration a transcript recorded, when it said anything.
fn transcript_config_value(config: &Option<Value>, field: &str) -> Option<String> {
    let value = config.as_ref()?.get(field)?.as_str()?.trim();
    (!value.is_empty()).then(|| value.to_string())
}

/// Put what the transcript ran on into the session's own configuration.
///
/// Only what the transcript actually recorded: a missing model leaves whatever
/// is already stored rather than blanking it, because "the file did not say" and
/// "it was nothing" are different answers. The lists of what is on OFFER are not
/// touched — a transcript knows what was used, not what could have been, and the
/// adapter fills those in when it is next asked.
fn merge_transcript_config(
    extra_json: &str,
    model: &Option<String>,
    effort: &Option<String>,
) -> Result<String, String> {
    if model.is_none() && effort.is_none() {
        return Ok(extra_json.to_string());
    }
    let mut extra: Value = serde_json::from_str(extra_json)
        .map_err(|error| format!("Could not decode stored session metadata: {error}"))?;
    let object = extra
        .as_object_mut()
        .ok_or_else(|| "Stored session metadata was not a JSON object".to_string())?;
    let config = object
        .entry("config")
        .or_insert_with(|| Value::Object(serde_json::Map::new()));
    let config = config
        .as_object_mut()
        .ok_or_else(|| "Stored session configuration was not a JSON object".to_string())?;
    if let Some(model) = model {
        config.insert("model".to_string(), Value::String(model.clone()));
    }
    if let Some(effort) = effort {
        config.insert("reasoningEffort".to_string(), Value::String(effort.clone()));
    }
    serde_json::to_string(&extra)
        .map_err(|error| format!("Could not encode stored session metadata: {error}"))
}

fn merge_import_cursor(extra_json: &str, cursor: &ImportCursor) -> Result<String, String> {
    let mut extra: Value = serde_json::from_str(extra_json)
        .map_err(|error| format!("Could not decode stored session metadata: {error}"))?;
    let object = extra
        .as_object_mut()
        .ok_or_else(|| "Stored session metadata was not a JSON object".to_string())?;
    object.insert(
        "import".to_string(),
        serde_json::to_value(cursor)
            .map_err(|error| format!("Could not encode import cursor: {error}"))?,
    );
    serde_json::to_string(&extra)
        .map_err(|error| format!("Could not encode stored session metadata: {error}"))
}

fn enum_from_storage<T: for<'de> Deserialize<'de>>(value: &str) -> Result<T, String> {
    serde_json::from_value(Value::String(value.to_string()))
        .map_err(|error| format!("Could not decode stored enum value: {error}"))
}

fn enum_storage_value<T: Serialize>(value: T) -> Result<String, String> {
    serde_json::to_value(value)
        .map_err(|error| format!("Could not encode stored enum value: {error}"))?
        .as_str()
        .map(str::to_string)
        .ok_or_else(|| "Stored enum value was not a string".to_string())
}

fn record_timestamp(record: &ProjectedRecord) -> Option<i64> {
    (record.timestamp_ms != 0).then(|| i64::try_from(record.timestamp_ms).unwrap_or(i64::MAX))
}

fn now_millis() -> i64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|duration| i64::try_from(duration.as_millis()).unwrap_or(i64::MAX))
        .unwrap_or(0)
}

#[cfg(test)]
mod tests {
    use std::fs;
    use std::path::{Path, PathBuf};

    use mcb_core::session_store::SessionStore;
    use serde_json::{json, Value};
    use uuid::Uuid;

    use super::*;

    #[test]
    fn desktop_markdown_import_prepares_only_at_desktop_save() {
        let server = std::env::args().any(|arg| arg == "--assembly-server");
        for (provider, line) in [
            (AgentConversationProvider::Codex,
                r#"{"type":"response_item","payload":{"type":"message","id":"a","role":"assistant","content":[{"type":"output_text","text":"**hello**"}]}}"#),
            (AgentConversationProvider::Claude,
                r#"{"type":"assistant","uuid":"a","sessionId":"native","message":{"content":[{"type":"text","text":"**hello**"}]}}"#),
        ] {
            let records = super::transcript::parse_durable_line(provider, "native", line.as_bytes());
            let record = records.into_iter().find(|record| matches!(record.native,
                Some(AgentConversationPayload::AssistantMessage { .. }))).unwrap();
            assert!(matches!(record.native,
                Some(AgentConversationPayload::AssistantMessage { blocks: None, .. })));
            let row = imported_event_row("owned", provider, 1, record, 1000).unwrap();
            let event: AgentConversationEvent = serde_json::from_str(&row.payload_json).unwrap();
            assert!(matches!(event.payload,
                AgentConversationPayload::AssistantMessage { blocks, .. } if blocks.is_some() == !server));
        }
        if !server {
            let output = std::process::Command::new(std::env::current_exe().unwrap())
                .args(["--exact",
                    "agent_conversation::history::tests::desktop_markdown_import_prepares_only_at_desktop_save",
                    "--skip", "--assembly-server", "--nocapture"])
                .output().unwrap();
            assert!(output.status.success(), "{}\n{}",
                String::from_utf8_lossy(&output.stdout), String::from_utf8_lossy(&output.stderr));
        }
    }

    struct TranscriptFixture {
        path: PathBuf,
        contents: String,
    }

    impl TranscriptFixture {
        fn new(record_count: usize) -> Self {
            Self::from_lines((1..=record_count).map(message_line).collect())
        }

        fn from_lines(lines: Vec<String>) -> Self {
            let path = std::env::temp_dir().join(format!("mcb-tail-{}.jsonl", Uuid::new_v4()));
            let contents = lines.join("");
            fs::write(&path, &contents).expect("transcript fixture should be written");
            Self { path, contents }
        }

        fn line_len(&self) -> u64 {
            self.contents
                .find('\n')
                .map(|index| index as u64 + 1)
                .expect("fixture should contain a complete line")
        }

        fn len(&self) -> u64 {
            self.contents.len() as u64
        }
    }

    impl Drop for TranscriptFixture {
        fn drop(&mut self) {
            let _ = fs::remove_file(&self.path);
        }
    }

    fn message_line(index: usize) -> String {
        format!(
            "{{\"type\":\"response_item\",\"payload\":{{\"type\":\"message\",\"id\":\"item-{index}\",\"role\":\"user\",\"content\":[{{\"type\":\"input_text\",\"text\":\"message\"}}]}}}}\n"
        )
    }

    /// A line a reader keeps nothing from. Real transcripts are full of them:
    /// bookkeeping, internal state, one side of a tool talking to itself.
    fn skipped_line(index: usize) -> String {
        format!(
            "{{\"type\":\"response_item\",\"payload\":{{\"type\":\"message\",\"id\":\"skip-{index}\",\"role\":\"system\",\"content\":[{{\"type\":\"input_text\",\"text\":\"message\"}}]}}}}\n"
        )
    }

    fn item_ids(tail: &ImportedTail) -> Vec<&str> {
        tail.records
            .iter()
            .filter_map(|record| record.item_id.as_deref())
            .collect()
    }

    fn fixture_provider() -> AgentConversationProvider {
        super::transcript::parse_provider(&["co", "dex"].concat())
            .expect("fixture provider should be supported")
    }

    fn read_fixture(
        fixture: &TranscriptFixture,
        end_offset: u64,
        max_bytes: u64,
        max_records: usize,
    ) -> ImportedTail {
        read_tail(
            fixture_provider(),
            "session-1",
            Path::new(&fixture.path),
            end_offset,
            max_bytes,
            max_records,
        )
        .expect("tail should be read")
    }

    fn stored_item_ids(store: &SessionStore, owned_id: &str) -> Vec<String> {
        store
            .list_events(owned_id, i64::MIN, 100)
            .expect("events should be listed")
            .into_iter()
            .map(|event| {
                // A stored row holds a whole conversation event, and the
                // projection an import writes sits inside its payload.
                serde_json::from_str::<Value>(&event.payload_json)
                    .expect("event payload should be JSON")
                    .get("payload")
                    .and_then(|payload| payload.get("itemId"))
                    .and_then(Value::as_str)
                    .expect("event should have an item id")
                    .to_string()
            })
            .collect()
    }

    fn stored_cursor(store: &SessionStore, owned_id: &str) -> ImportCursor {
        let session = store
            .get_session(owned_id)
            .expect("session should be read")
            .expect("session should exist");
        serde_json::from_str::<Value>(&session.extra_json)
            .expect("session extra should be JSON")
            .get("import")
            .cloned()
            .map(serde_json::from_value)
            .expect("session should have an import cursor")
            .expect("import cursor should be valid")
    }

    #[test]
    fn import_writes_the_tail_and_records_where_it_stopped() {
        let fixture = TranscriptFixture::new(6);
        let store = SessionStore::open_in_memory().expect("store should open");

        let owned_id = import_session(
            &store,
            fixture_provider(),
            "session-1",
            &fixture.path,
            "/tmp/project",
            None,
            fixture.line_len() * 4,
            usize::MAX,
        )
        .expect("session should import");

        let session = store
            .get_session(&owned_id)
            .expect("session should be read")
            .expect("session should exist");
        assert_eq!(session.native_session_id.as_deref(), Some("session-1"));
        assert_eq!(session.cwd, "/tmp/project");
        assert_eq!(
            stored_item_ids(&store, &owned_id),
            ["item-4", "item-5", "item-6"]
        );
        let cursor = stored_cursor(&store, &owned_id);
        assert!(cursor.cutoff_offset > 0);
        assert!(!cursor.reached_start);
    }

    #[test]
    fn imported_events_read_back_as_conversation_events() {
        // What an import writes has to survive being read the way every other
        // stored event is read. It did not: the rows held only the projection,
        // so listing a resumed session's transcript failed to decode and the
        // session could not be opened at all.
        let fixture = TranscriptFixture::new(3);
        let store = SessionStore::open_in_memory().expect("store should open");

        let owned_id = import_session(
            &store,
            fixture_provider(),
            "session-1",
            &fixture.path,
            "/tmp/project",
            None,
            u64::MAX,
            usize::MAX,
        )
        .expect("session should import");

        let rows = store
            .list_events(&owned_id, i64::MIN, 100)
            .expect("events should be listed");
        assert_eq!(rows.len(), 3);
        for row in rows {
            let event: AgentConversationEvent = serde_json::from_str(&row.payload_json)
                .expect("stored event should decode as a conversation event");
            assert_eq!(event.owned_id, owned_id);
            assert_eq!(event.provider, fixture_provider());
            // An imported message is stored as a message, the same as a live one.
            assert!(matches!(
                event.payload,
                AgentConversationPayload::UserMessage { .. }
                    | AgentConversationPayload::AssistantMessage { .. }
            ));
        }
    }

    #[test]
    fn child_tail_and_forward_append_keep_order_across_partial_reopen() {
        let fixture = TranscriptFixture::new(6);
        let initial = read_tail_scoped(
            fixture_provider(), "parent", Some("child"), &fixture.path,
            fixture.len(), fixture.line_len() * 4, usize::MAX,
        ).unwrap();
        let older = read_tail_scoped(
            fixture_provider(), "parent", Some("child"), &fixture.path,
            initial.cutoff_offset, fixture.len(), usize::MAX,
        ).unwrap();
        let line = message_line(7);
        let split = line.len() / 2;
        let (records, stopped, trailing) = child_forward_records(
            fixture_provider(), "parent", "child", &line.as_bytes()[..split], usize::MAX,
        );
        assert!(records.is_empty());
        assert!(stopped.is_none());
        let cursor = ImportCursor { trailing_bytes: trailing, ..ImportCursor::default() };
        let reopened: ImportCursor = serde_json::from_str(
            &serde_json::to_string(&cursor).unwrap(),
        ).unwrap();
        let mut completed = reopened.trailing_bytes;
        completed.extend_from_slice(&line.as_bytes()[split..]);
        let (records, stopped, trailing) = child_forward_records(
            fixture_provider(), "parent", "child", &completed, usize::MAX,
        );
        let ids = item_ids(&older).into_iter()
            .chain(item_ids(&initial))
            .chain(records.iter().filter_map(|record| record.item_id.as_deref()))
            .collect::<Vec<_>>();
        assert_eq!(ids, ["item-1", "item-2", "item-3", "item-4", "item-5", "item-6", "item-7"]);
        assert!(stopped.is_none());
        assert!(trailing.is_empty());
    }

    #[test]
    fn existing_child_import_reopens_without_its_source_transcript() {
        let fixture = TranscriptFixture::new(1);
        let store = SessionStore::open_in_memory().unwrap();
        let parent_owned_id = begin_import_session(
            &store, fixture_provider(), "parent", &fixture.path,
            "/tmp/project", None, None, false,
        ).unwrap().0;
        let child_owned_id = serde_json::to_string(&("child", &parent_owned_id, "child-1")).unwrap();
        let mut child = store.get_session(&parent_owned_id).unwrap().unwrap();
        child.owned_id = child_owned_id.clone();
        child.native_session_id = Some("child-1".into());
        store.upsert_child_session(&parent_owned_id, None, &child).unwrap();
        fs::remove_file(&fixture.path).unwrap();

        assert_eq!(ensure_child_import(
            &store, &parent_owned_id, None, "child-1",
        ).unwrap(), child_owned_id);
    }

    #[test]
    fn child_selected_before_its_file_exists_gets_a_durable_row_then_imports() {
        let fixture = TranscriptFixture::new(2);
        let store = SessionStore::open_in_memory().unwrap();
        let parent_owned_id = begin_import_session(
            &store,
            fixture_provider(),
            "parent",
            &fixture.path,
            "/tmp/project",
            None, None,
            false,
        )
        .unwrap().0;

        let child_owned_id = ensure_child_import(
            &store,
            &parent_owned_id,
            None,
            "child-created-later",
        )
        .unwrap();
        let child = store.get_session(&child_owned_id).unwrap().unwrap();
        let placeholder = import_cursor(&child.extra_json).unwrap();
        assert!(placeholder.transcript_path.is_empty());
        assert!(store
            .list_items_before(&child_owned_id, i64::MAX, 1024, None)
            .unwrap()
            .events
            .is_empty());

        let imported = refresh_child_import_at_location(
            &store,
            &parent_owned_id,
            None,
            &child_owned_id,
            &child,
            placeholder,
            fixture_provider(),
            &fixture.path,
            fixture.len(),
            fixture.len(),
            usize::MAX,
        )
        .unwrap();
        assert_eq!(imported.added, 2);
        assert_eq!(stored_item_ids(&store, &child_owned_id), ["item-1", "item-2"]);
    }

    #[test]
    fn child_import_refreshes_tail_and_append_across_store_reopen() {
        let fixture = TranscriptFixture::new(6);
        let db_path = std::env::temp_dir().join(format!("mcb-child-store-{}.sqlite", Uuid::new_v4()));
        let store = SessionStore::open(&db_path).unwrap();
        let parent_owned_id = begin_import_session(
            &store, fixture_provider(), "parent", &fixture.path, "/tmp/project", None, None, false,
        ).unwrap().0;
        let child_owned_id = serde_json::to_string(&("child", &parent_owned_id, "child")).unwrap();
        let mut child = store.get_session(&parent_owned_id).unwrap().unwrap();
        child.owned_id = child_owned_id.clone();
        child.native_session_id = Some("child".into());
        let cursor = ImportCursor {
            transcript_path: fixture.path.to_string_lossy().into_owned(),
            cutoff_offset: fixture.len(),
            reached_start: false,
            parent_native_session_id: Some("parent".into()),
            child_session_id: Some("child".into()),
            parent_owned_id: Some(parent_owned_id.clone()),
            forward_offset: fixture.len(),
            ..ImportCursor::default()
        };
        child.extra_json = merge_import_cursor(&child.extra_json, &cursor).unwrap();
        store.upsert_child_session(&parent_owned_id, None, &child).unwrap();
        let initial = refresh_child_import_at_location(
            &store, &parent_owned_id, None, &child_owned_id, &child, cursor,
            fixture_provider(), &fixture.path, fixture.len(), fixture.line_len() * 4, usize::MAX,
        ).unwrap();
        assert_eq!(initial.added, 3);
        assert!(initial.read_bytes <= fixture.line_len() * 4);
        assert!(!initial.has_more_source);

        let append = message_line(7);
        let split = append.len() / 2;
        fs::write(&fixture.path, format!("{}{}", fixture.contents, &append[..split])).unwrap();
        let session = store.get_session(&child_owned_id).unwrap().unwrap();
        let cursor = import_cursor(&session.extra_json).unwrap();
        let partial = refresh_child_import_at_location(
            &store, &parent_owned_id, None, &child_owned_id, &session, cursor,
            fixture_provider(), &fixture.path, fixture.len() + split as u64,
            fixture.line_len() * 2, usize::MAX,
        ).unwrap();
        assert_eq!(partial.added, 0);
        assert!(!partial.has_more_source);
        drop(store);

        fs::write(&fixture.path, format!("{}{}", fixture.contents, append)).unwrap();
        let store = SessionStore::open(&db_path).unwrap();
        let session = store.get_session(&child_owned_id).unwrap().unwrap();
        let cursor = import_cursor(&session.extra_json).unwrap();
        let completed = refresh_child_import_at_location(
            &store, &parent_owned_id, None, &child_owned_id, &session, cursor,
            fixture_provider(), &fixture.path, fixture.len() + append.len() as u64,
            fixture.line_len() * 2, usize::MAX,
        ).unwrap();
        assert_eq!(completed.added, 1);
        assert!(!completed.has_more_source);
        let append_more = format!("{}{}{}", fixture.contents, append, message_line(8));
        let append_more = format!("{}{}", append_more, message_line(9));
        fs::write(&fixture.path, &append_more).unwrap();
        let session = store.get_session(&child_owned_id).unwrap().unwrap();
        let cursor = import_cursor(&session.extra_json).unwrap();
        let bounded = refresh_child_import_at_location(
            &store, &parent_owned_id, None, &child_owned_id, &session, cursor,
            fixture_provider(), &fixture.path, append_more.len() as u64,
            fixture.line_len() * 4, 1,
        ).unwrap();
        assert_eq!(bounded.added, 1);
        assert!(bounded.has_more_source);
        let session = store.get_session(&child_owned_id).unwrap().unwrap();
        let cursor = import_cursor(&session.extra_json).unwrap();
        let final_page = refresh_child_import_at_location(
            &store, &parent_owned_id, None, &child_owned_id, &session, cursor,
            fixture_provider(), &fixture.path, append_more.len() as u64,
            fixture.line_len() * 4, 1,
        ).unwrap();
        assert_eq!(final_page.added, 1);
        assert!(!final_page.has_more_source);
        extend_session(&store, &child_owned_id, fixture.len(), usize::MAX).unwrap();
        assert_eq!(stored_item_ids(&store, &child_owned_id),
            ["item-1", "item-2", "item-3", "item-4", "item-5", "item-6", "item-7", "item-8", "item-9"]);
        fs::remove_file(&fixture.path).unwrap();
        assert_eq!(ensure_child_import(
            &store, &parent_owned_id, None, "child",
        ).unwrap(), child_owned_id);
        assert_eq!(stored_item_ids(&store, &child_owned_id),
            ["item-1", "item-2", "item-3", "item-4", "item-5", "item-6", "item-7", "item-8", "item-9"]);
        drop(store);
        fs::remove_file(&db_path).ok();
    }

    #[test]
    fn import_of_a_short_transcript_reports_it_reached_the_start() {
        let fixture = TranscriptFixture::new(2);
        let store = SessionStore::open_in_memory().expect("store should open");

        let owned_id = import_session(
            &store,
            fixture_provider(),
            "session-1",
            &fixture.path,
            "/tmp/project",
            None,
            fixture.len() + 1,
            usize::MAX,
        )
        .expect("session should import");

        let cursor = stored_cursor(&store, &owned_id);
        assert_eq!(cursor.cutoff_offset, 0);
        assert!(cursor.reached_start);
    }

    #[test]
    fn extending_prepends_older_records_before_the_existing_ones() {
        let fixture = TranscriptFixture::new(6);
        let store = SessionStore::open_in_memory().expect("store should open");
        let owned_id = import_session(
            &store,
            fixture_provider(),
            "session-1",
            &fixture.path,
            "/tmp/project",
            None,
            fixture.line_len() * 4,
            usize::MAX,
        )
        .expect("session should import");
        let mut session = store.get_session(&owned_id).unwrap().unwrap();
        let mut cursor = import_cursor(&session.extra_json).unwrap();
        cursor.forward_offset = fixture.line_len();
        cursor.forward_sequence = 7;
        cursor.trailing_bytes = vec![b'{'];
        session.extra_json = merge_import_cursor(&session.extra_json, &cursor).unwrap();
        store.upsert_session(&session).unwrap();

        let added = extend_session(&store, &owned_id, fixture.line_len() * 4, usize::MAX)
            .expect("session should extend");

        assert_eq!(added.added, 3);
        assert!(added.reached_start);
        assert_eq!(
            stored_item_ids(&store, &owned_id),
            ["item-1", "item-2", "item-3", "item-4", "item-5", "item-6"]
        );
        let cursor = stored_cursor(&store, &owned_id);
        assert_eq!((cursor.forward_offset, cursor.forward_sequence, cursor.trailing_bytes),
            (fixture.line_len(), 7, vec![b'{']));
    }

    /// Scrolling to the top of any conversation asks for older history. Most
    /// sessions were started here rather than picked up from a transcript, so
    /// they have no import cursor and there is simply nothing older on disk.
    /// That is an answer, not a failure.
    #[test]
    fn extending_a_session_that_was_never_imported_adds_nothing() {
        let store = SessionStore::open_in_memory().expect("store should open");
        store
            .upsert_session(&mcb_core::session_store::SessionRow {
                owned_id: "owned-native".to_owned(),
                native_session_id: None,
                provider: "codex".to_owned(),
                model: None,
                effort: None,
                cwd: "/tmp/project".to_owned(),
                worktree: None,
                branch: None,
                title: None,
                title_source: None,
                project: None,
                project_id: None,
                state: "idle".to_owned(),
                suspended: false,
                created_at_ms: 1,
                last_activity_at_ms: 1,
                extra_json: "{}".to_owned(),
            })
            .expect("session should be written");

        let added = extend_session(&store, "owned-native", 1024, usize::MAX)
            .expect("a session with no transcript behind it reports nothing older");
        assert_eq!(added.added, 0);
        assert!(added.reached_start);
    }

    /// Scrolling up used to stop for good in the middle of a conversation. A
    /// window of transcript can hold nothing a reader wants, and reporting that
    /// as "nothing older" made the last thing on screen look like the first
    /// thing said. Reaching back keeps going until it finds something.
    #[test]
    fn extending_reads_past_a_window_holding_nothing_worth_showing() {
        let mut lines = vec![message_line(1), message_line(2)];
        lines.extend((1..=6).map(skipped_line));
        lines.push(message_line(3));
        let fixture = TranscriptFixture::from_lines(lines);
        let store = SessionStore::open_in_memory().expect("store should open");
        let owned_id = import_session(
            &store,
            fixture_provider(),
            "session-1",
            &fixture.path,
            "/tmp/project",
            None,
            fixture.line_len() * 2,
            usize::MAX,
        )
        .expect("session should import");
        assert_eq!(stored_item_ids(&store, &owned_id), ["item-3"]);

        let added = extend_session(&store, &owned_id, fixture.line_len() * 2, usize::MAX)
            .expect("session should extend");

        let stored = stored_item_ids(&store, &owned_id);
        assert!(added.added > 0, "one reach should cross the empty windows");
        assert!(stored.contains(&"item-2".to_owned()), "{stored:?}");
    }

    #[test]
    fn extending_a_session_that_reached_the_start_adds_nothing() {
        let fixture = TranscriptFixture::new(2);
        let store = SessionStore::open_in_memory().expect("store should open");
        let owned_id = import_session(
            &store,
            fixture_provider(),
            "session-1",
            &fixture.path,
            "/tmp/project",
            None,
            fixture.len() + 1,
            usize::MAX,
        )
        .expect("session should import");
        let events_before = store
            .list_events(&owned_id, i64::MIN, 100)
            .expect("events should be listed");
        let cursor_before = stored_cursor(&store, &owned_id);

        let added = extend_session(&store, &owned_id, fixture.len(), usize::MAX)
            .expect("extension should be checked");

        assert_eq!(added.added, 0);
        assert!(added.reached_start);
        assert_eq!(
            store
                .list_events(&owned_id, i64::MIN, 100)
                .expect("events should be listed"),
            events_before
        );
        assert_eq!(stored_cursor(&store, &owned_id), cursor_before);
    }

    #[test]
    fn import_preserves_other_extra_json_content() {
        let fixture = TranscriptFixture::new(4);
        let store = SessionStore::open_in_memory().expect("store should open");
        let owned_id = import_session(
            &store,
            fixture_provider(),
            "session-1",
            &fixture.path,
            "/tmp/project",
            None,
            fixture.line_len() * 2,
            usize::MAX,
        )
        .expect("session should import");
        let mut session = store
            .get_session(&owned_id)
            .expect("session should be read")
            .expect("session should exist");
        let mut extra = serde_json::from_str::<Value>(&session.extra_json)
            .expect("session extra should be JSON");
        extra["other"] = json!({ "kept": true });
        session.extra_json = serde_json::to_string(&extra).expect("session extra should encode");
        store
            .upsert_session(&session)
            .expect("session should be updated");

        extend_session(&store, &owned_id, fixture.line_len() * 2, usize::MAX)
            .expect("session should extend");

        let session = store
            .get_session(&owned_id)
            .expect("session should be read")
            .expect("session should exist");
        let extra = serde_json::from_str::<Value>(&session.extra_json)
            .expect("session extra should be JSON");
        assert_eq!(extra["other"], json!({ "kept": true }));
        assert!(extra.get("import").is_some());
    }

    /// A conversation picked up from a transcript carries on with what it was
    /// using. The adapter cannot supply this — Claude's reports "default",
    /// meaning "whatever this machine is set to" — so the composer showed
    /// "default" against a session that had plainly been running on something
    /// else.
    #[test]
    fn an_imported_session_takes_the_model_and_effort_its_transcript_ran_on() {
        let path = std::env::temp_dir().join(format!("mcb-claude-config-{}.jsonl", Uuid::new_v4()));
        fs::write(
            &path,
            concat!(
                r#"{"type":"user","uuid":"u1","sessionId":"s1","isSidechain":false,"message":{"content":"Hello"}}"#,
                "\n",
                r#"{"type":"assistant","uuid":"a1","sessionId":"s1","isSidechain":false,"effort":"medium","message":{"model":"claude-fable-5","content":[{"type":"text","text":"Answer"}]}}"#,
                "\n",
            ),
        )
        .expect("transcript fixture should be written");
        let store = SessionStore::open_in_memory().expect("store should open");
        let provider = super::transcript::parse_provider("claude")
            .expect("claude should be a supported provider");

        let owned_id = import_session(
            &store,
            provider,
            "s1",
            &path,
            "/tmp/project",
            None,
            u64::MAX,
            usize::MAX,
        )
        .expect("session should import");

        let session = store
            .get_session(&owned_id)
            .expect("session should be read")
            .expect("session should exist");
        assert_eq!(session.model.as_deref(), Some("claude-fable-5"));
        assert_eq!(session.effort.as_deref(), Some("medium"));

        let extra = serde_json::from_str::<Value>(&session.extra_json)
            .expect("session extra should be JSON");
        assert_eq!(extra["config"]["model"], json!("claude-fable-5"));
        assert_eq!(extra["config"]["reasoningEffort"], json!("medium"));
        // The transcript says what was used, never what was on offer; those
        // lists stay for the adapter to fill in when it is next asked.
        assert_eq!(extra["config"]["availableModels"], json!([]));

        fs::remove_file(&path).ok();
    }

    #[test]
    fn tail_stops_at_the_byte_budget() {
        let fixture = TranscriptFixture::new(5);

        let tail = read_fixture(&fixture, fixture.len(), fixture.line_len() * 3, usize::MAX);

        assert!(!tail.records.is_empty());
        assert!(tail.cutoff_offset > 0);
        assert!(!tail.reached_start);
    }

    #[test]
    fn tail_reaching_the_start_reports_it() {
        let fixture = TranscriptFixture::new(2);

        let tail = read_fixture(&fixture, fixture.len(), fixture.len() + 1, usize::MAX);

        assert_eq!(item_ids(&tail), ["item-1", "item-2"]);
        assert_eq!(tail.cutoff_offset, 0);
        assert!(tail.reached_start);
    }

    #[test]
    fn a_window_with_no_usable_line_still_moves_the_cutoff_back() {
        let path = std::env::temp_dir().join(format!("mcb-tail-noise-{}.jsonl", Uuid::new_v4()));
        let contents = "not json\nalso not json\n";
        fs::write(&path, contents).expect("noise fixture should be written");
        let len = contents.len() as u64;

        let tail = read_tail(
            AgentConversationProvider::Codex,
            "session",
            &path,
            len,
            len,
            usize::MAX,
        )
        .expect("reading the noise fixture should succeed");

        assert!(tail.records.is_empty());
        assert_eq!(tail.cutoff_offset, 0);
        assert!(tail.reached_start);
        let _ = fs::remove_file(&path);
    }

    #[test]
    fn tail_honours_the_record_cap() {
        let fixture = TranscriptFixture::new(4);

        let tail = read_fixture(&fixture, fixture.len(), fixture.len(), 2);

        assert_eq!(item_ids(&tail), ["item-3", "item-4"]);
        assert!(tail.records.len() <= 2);
        assert_eq!(tail.cutoff_offset, fixture.line_len() * 2);
        assert!(!tail.reached_start);
    }

    #[test]
    fn reading_again_from_the_cutoff_walks_backwards_without_gaps() {
        let fixture = TranscriptFixture::new(6);
        let max_bytes = fixture.line_len() * 4;

        let newer = read_fixture(&fixture, fixture.len(), max_bytes, usize::MAX);
        let older = read_fixture(&fixture, newer.cutoff_offset, max_bytes, usize::MAX);
        let ids = item_ids(&older)
            .into_iter()
            .chain(item_ids(&newer))
            .collect::<Vec<_>>();

        assert_eq!(
            ids,
            ["item-1", "item-2", "item-3", "item-4", "item-5", "item-6"]
        );
        assert!(older.reached_start);
    }
}

pub fn read_tail(
    provider: AgentConversationProvider,
    native_session_id: &str,
    path: &Path,
    end_offset: u64,
    max_bytes: u64,
    max_records: usize,
) -> Result<ImportedTail, String> {
    read_tail_scoped(
        provider,
        native_session_id,
        None,
        path,
        end_offset,
        max_bytes,
        max_records,
    )
}

fn read_tail_scoped(
    provider: AgentConversationProvider,
    native_session_id: &str,
    child_session_id: Option<&str>,
    path: &Path,
    end_offset: u64,
    max_bytes: u64,
    max_records: usize,
) -> Result<ImportedTail, String> {
    let start = end_offset.saturating_sub(max_bytes);
    let read_len = end_offset - start;
    if read_len == 0 {
        return Ok(ImportedTail {
            records: Vec::new(),
            cutoff_offset: end_offset,
            reached_start: end_offset == 0,
            trailing_bytes: Vec::new(),
        });
    }

    let bytes = read_range(path, start, read_len)?;
    let (lines, trailing_bytes) = complete_lines(&bytes, start > 0);
    let bytes_start = bytes.as_ptr() as usize;
    let mut parsed_lines = lines
        .into_iter()
        .filter_map(|line| {
            let records = child_session_id.map_or_else(
                || parse_durable_line(provider, native_session_id, line),
                |child_id| {
                    parse_durable_child_line(provider, native_session_id, child_id, line)
                },
            );
            if records.is_empty() {
                return None;
            }
            let line_offset = start + (line.as_ptr() as usize - bytes_start) as u64;
            Some((line_offset, records))
        })
        .collect::<Vec<_>>();

    let mut record_count = parsed_lines
        .iter()
        .map(|(_, records)| records.len())
        .sum::<usize>();
    let mut first_surviving_line = 0;
    while record_count > max_records && first_surviving_line < parsed_lines.len() {
        record_count -= parsed_lines[first_surviving_line].1.len();
        first_surviving_line += 1;
    }
    if first_surviving_line > 0 {
        parsed_lines.drain(..first_surviving_line);
    }

    // A window with no usable line still consumed everything back to `start`, so the cutoff moves
    // there. Reporting `end_offset` instead would hand back the same window forever.
    let cutoff_offset = parsed_lines.first().map_or(start, |(offset, _)| *offset);
    let records = parsed_lines
        .into_iter()
        .flat_map(|(_, records)| records)
        .collect();

    Ok(ImportedTail {
        records,
        cutoff_offset,
        reached_start: cutoff_offset == 0,
        trailing_bytes,
    })
}
