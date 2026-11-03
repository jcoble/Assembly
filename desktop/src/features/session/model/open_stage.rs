//! Request-owned first-page preparation. Subscription and task lifetimes stay in SessionPane.
//! A new Jump invalidates the prior page ticket; only a coherent prepared view can be committed.
use std::collections::HashMap;
use crate::features::session::rows::{row::Row, row_page::RowPage};
use super::session_view::{SessionView, key_of};

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(in crate::features::session) struct PageTicket { generation: u64, pub before: i64 }

pub(in crate::features::session) struct PreparedRows {
    pub view: SessionView,
    pub known_run: Option<String>,
}

pub(in crate::features::session) struct OpenStage {
    session: String,
    generation: u64,
    through: Option<i64>,
    held: Vec<Row>,
    prepared: Option<PreparedRows>,
    page_versions: HashMap<String, i64>,
    tail_count: usize,
    tail_bytes: usize,
    valid: bool,
}

impl OpenStage {
    pub fn new(session: String) -> Self {
        Self { session, generation: 0, through: None, held: Vec::new(), prepared: None,
            page_versions: HashMap::new(), tail_count: 0, tail_bytes: 0, valid: true }
    }

    pub fn jump(&mut self, seq: i64) -> Result<PageTicket, String> {
        let before = seq.checked_add(1).filter(|_| seq >= 0)
            .ok_or("Invalid session row cut")?;
        self.generation = self.generation.checked_add(1).ok_or("Session preparation exhausted")?;
        self.through = Some(seq);
        self.held.clear();
        self.prepared = None;
        self.page_versions.clear();
        self.tail_count = 0;
        self.tail_bytes = 0;
        self.valid = true;
        Ok(PageTicket { generation: self.generation, before })
    }

    pub fn invalidate(&mut self) {
        self.valid = false;
        self.prepared = None;
        self.held.clear();
    }

    pub fn ready(&self) -> bool {
        self.valid && self.prepared.as_ref().is_some_and(|prepared|
            self.through.is_some_and(|through| through >= prepared.view.newest))
    }

    pub fn push(&mut self, row: Row) -> Result<(), String> {
        let result = self.push_inner(row);
        if result.is_err() { self.valid = false; }
        result
    }

    fn push_inner(&mut self, row: Row) -> Result<(), String> {
        if !self.valid || row.session_id != self.session {
            return Err("Session row source changed".into());
        }
        let through = self.through.ok_or("Session rows arrived without their initial cut")?;
        if row.seq <= through { return Ok(()); }
        if row.prev_seq != Some(through) { return Err("Session rows are no longer contiguous".into()); }
        let bytes = row_bytes(&row);
        if self.tail_count == 500 || self.tail_bytes.saturating_add(bytes) > 8 * 1024 * 1024 {
            return Err("Session changed beyond the preparation limit; open it again".into());
        }
        self.tail_count += 1;
        self.tail_bytes += bytes;
        self.through = Some(row.seq);
        if let Some(prepared) = &mut self.prepared {
            apply_newer(&mut prepared.view, &mut self.page_versions, &row);
        } else {
            self.held.push(row);
        }
        Ok(())
    }

    /// Returns false for a cancelled/replaced page, without affecting the current request's rows.
    pub fn install(&mut self, ticket: PageTicket, page: RowPage) -> Result<bool, String> {
        if ticket.generation != self.generation { return Ok(false); }
        if !self.valid { return Err("Session preparation is no longer valid".into()); }
        if page.rows.len() > 100 || page.rows.iter().any(|row| row.session_id != self.session)
            || page.rows.iter().map(row_bytes).sum::<usize>() > 8 * 1024 * 1024 {
            self.valid = false;
            return Err("Session first page has an invalid source or exceeds its limit".into());
        }
        let mut versions = HashMap::<String, i64>::new();
        for row in &page.rows {
            versions.entry(key_of(row)).and_modify(|seq| *seq = (*seq).max(row.seq)).or_insert(row.seq);
        }
        let mut view = SessionView::default();
        view.prepend_page(page.rows, page.has_before);
        for row in self.held.drain(..) { apply_newer(&mut view, &mut versions, &row); }
        self.page_versions = versions;
        self.prepared = Some(PreparedRows { view, known_run: page.active_run_id });
        Ok(true)
    }

    pub fn take(&mut self) -> Result<PreparedRows, String> {
        if !self.ready() { return Err("Session first page is not ready".into()); }
        self.valid = false;
        self.prepared.take().ok_or_else(|| "Session first page is not ready".into())
    }
}

fn apply_newer(view: &mut SessionView, versions: &mut HashMap<String, i64>, row: &Row) {
    let key = key_of(row);
    if versions.get(&key).is_some_and(|seq| *seq >= row.seq) { return; }
    versions.insert(key, row.seq);
    view.apply(row);
}

fn row_bytes(row: &Row) -> usize {
    // Bound the entire retained wire row, including escaped strings, rather than just its text field.
    serde_json::to_vec(&serde_json::json!({
        "sessionId": row.session_id, "seq": row.seq, "runId": row.run_id,
        "itemId": row.item_id, "kind": row.kind, "payload": row.payload,
        "createdAt": row.created_at, "firstSeq": row.first_seq, "prevSeq": row.prev_seq
    })).map_or(usize::MAX, |bytes| bytes.len())
}
