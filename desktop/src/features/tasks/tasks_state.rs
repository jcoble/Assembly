//! What the Tasks tab knows, apart from drawing it: the query, the loaded rows, which read is current, and the task
//! that's open. Each read carries a token, so an answer to an older query or source is dropped when it arrives.

use serde_json::Value;

use super::task_export::*;

pub const PAGE_SIZE: i32 = 25;
/// How many rows past the visible ones the list draws, and how close to the end it asks for more.
pub const OVERSCAN: usize = 6;
/// The most one read can ask for.
const MAX_WINDOW: i32 = 100;

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct ReadToken(u64);
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct DetailToken(u64);

/// A task's page, read whole.
#[derive(Clone, Debug, PartialEq)]
pub struct TaskDetail {
    pub source: NotionTaskRef,
    pub revision: String,
    pub properties: Value,
    pub blocks: Vec<NotionBlock>,
    pub comments: Vec<Value>,
    pub comments_unavailable: bool,
}

/// What an answered read did to the list.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum PageOutcome {
    /// An older read's answer; nothing changed.
    Stale,
    /// The rows were replaced.
    Replaced,
    /// `count` rows were added from `from`.
    Appended {
        from: usize,
        count: usize,
    },
    /// A picked filter no longer exists in the snapshot, so it stopped filtering. Read again.
    FiltersDropped,
    Failed,
}

#[derive(Default)]
pub struct TasksState {
    pub query: NotionQuery,
    pub rows: Vec<NotionTaskRow>,
    pub has_more: bool,
    pub statuses: Vec<String>,
    pub priorities: Vec<String>,
    pub projects: Vec<NotionProjectOption>,
    pub truncated: bool,
    pub fetched_ms: i64,
    pub loading: bool,
    pub error: Option<String>,
    pub settings: Option<NotionSettingsView>,
    /// The person asked to see the connection card.
    pub setup_requested: bool,
    token_failed: bool,
    read: u64,
    /// The current read, and whether it adds to the rows.
    pending: Option<(ReadToken, bool)>,
    detail_read: u64,
    selected: Option<NotionTaskRef>,
    detail: Option<TaskDetail>,
    pub detail_error: Option<String>,
    /// The project the selected session's folder maps to; a project the person picks replaces it until it changes.
    followed_root: Option<String>,
}

impl TasksState {
    fn begin(&mut self, append: bool) -> ReadToken {
        self.read += 1;
        let token = ReadToken(self.read);
        self.pending = Some((token, append));
        self.loading = true;
        token
    }

    /// The query's first page, for a new query.
    pub fn first_page(&mut self) -> (ReadToken, NotionQuery) {
        let token = self.begin(false);
        let mut query = self.query.clone();
        query.offset = 0;
        query.limit = PAGE_SIZE;
        (token, query)
    }

    /// The rows already loaded, read again, so a pushed change doesn't lose the person's place.
    pub fn same_window(&mut self) -> (ReadToken, NotionQuery) {
        let token = self.begin(false);
        let mut query = self.query.clone();
        query.offset = 0;
        query.limit = (self.rows.len() as i32).clamp(PAGE_SIZE, MAX_WINDOW);
        (token, query)
    }

    /// The next page, unless a read is running or there's no more.
    pub fn next_page(&mut self) -> Option<(ReadToken, NotionQuery)> {
        if self.loading || !self.has_more {
            return None;
        }
        let token = self.begin(true);
        let mut query = self.query.clone();
        query.offset = self.rows.len() as i32;
        query.limit = PAGE_SIZE;
        Some((token, query))
    }

    pub fn page_result(
        &mut self,
        token: ReadToken,
        result: Result<NotionTaskPage, NotionError>,
    ) -> PageOutcome {
        let Some((current, append)) = self.pending.filter(|(current, _)| *current == token) else {
            return PageOutcome::Stale;
        };
        debug_assert_eq!(current, token);
        self.pending = None;
        self.loading = false;
        match result {
            Err(error) => {
                self.note_error(&error);
                PageOutcome::Failed
            }
            Ok(page) => {
                self.token_failed = false;
                self.error = None;
                self.apply(page, append)
            }
        }
    }

    fn apply(&mut self, page: NotionTaskPage, append: bool) -> PageOutcome {
        if !append && self.drop_gone_filters(&page) {
            self.statuses = page.statuses;
            self.priorities = page.priorities;
            self.projects = page.projects;
            return PageOutcome::FiltersDropped;
        }
        self.has_more = page.has_more;
        self.statuses = page.statuses;
        self.priorities = page.priorities;
        self.projects = page.projects;
        self.truncated = page.truncated;
        self.fetched_ms = page.fetched_ms;
        if append {
            let from = self.rows.len();
            let count = page.rows.len();
            self.rows.extend(page.rows);
            PageOutcome::Appended { from, count }
        } else {
            self.rows = page.rows;
            PageOutcome::Replaced
        }
    }

    /// A value the snapshot no longer offers has no pill to show or clear it. An empty snapshot offers nothing yet, so
    /// it leaves the saved view alone.
    fn drop_gone_filters(&mut self, page: &NotionTaskPage) -> bool {
        if page.statuses.is_empty() && page.priorities.is_empty() {
            return false;
        }
        let before = (
            self.query.statuses.len(),
            self.query.priorities.len(),
            self.query.project_id.is_some(),
        );
        self.query
            .statuses
            .retain(|value| page.statuses.contains(value));
        self.query
            .priorities
            .retain(|value| page.priorities.contains(value));
        let known = |id: &String| page.projects.iter().any(|project| project.id == *id);
        self.query.project_id = self.query.project_id.take().filter(known);
        before
            != (
                self.query.statuses.len(),
                self.query.priorities.len(),
                self.query.project_id.is_some(),
            )
    }

    pub fn note_error(&mut self, error: &NotionError) {
        if is_token_failure(&error.code) {
            self.token_failed = true;
        }
        self.error = Some(error_text(&error.code));
    }

    /// Connected to a source, as far as the app can tell.
    pub fn connected(&self) -> bool {
        self.settings
            .as_ref()
            .is_some_and(|settings| settings.has_token && !settings.data_source_id.is_empty())
    }

    /// The connection is known and unusable: never made, or Notion turned the token away.
    pub fn needs_connecting(&self) -> bool {
        self.token_failed || (self.settings.is_some() && !self.connected())
    }

    pub fn show_setup(&self) -> bool {
        self.setup_requested || self.needs_connecting()
    }

    /// Returns whether the source changed. A new source drops everything read from the old one.
    pub fn set_settings(&mut self, settings: NotionSettingsView) -> bool {
        let changed = self.settings.as_ref().is_none_or(|known| {
            known.generation != settings.generation
                || known.data_source_id != settings.data_source_id
        });
        if changed {
            self.rows.clear();
            self.has_more = false;
            self.statuses.clear();
            self.priorities.clear();
            self.projects.clear();
            self.truncated = false;
            self.token_failed = false;
            self.error = None;
            self.pending = None;
            self.loading = false;
            self.back();
        }
        self.settings = Some(settings);
        changed
    }

    pub fn source(&self) -> Option<String> {
        self.connected()
            .then(|| {
                self.settings
                    .as_ref()
                    .map(|view| view.data_source_id.clone())
            })
            .flatten()
    }

    // The open task.

    pub fn select(&mut self, source: NotionTaskRef) -> DetailToken {
        self.selected = Some(source);
        self.detail = None;
        self.detail_error = None;
        self.next_detail_read()
    }

    /// The open task again, keeping what's on screen until the answer comes.
    pub fn reread(&mut self) -> Option<(DetailToken, NotionTaskRef)> {
        let source = self.selected.clone()?;
        Some((self.next_detail_read(), source))
    }

    fn next_detail_read(&mut self) -> DetailToken {
        self.detail_read += 1;
        DetailToken(self.detail_read)
    }

    /// Whether this answer is the current one; an older read's is dropped.
    pub fn detail_result(&mut self, token: DetailToken, detail: TaskDetail) -> bool {
        if token.0 != self.detail_read || self.selected.as_ref() != Some(&detail.source) {
            return false;
        }
        self.detail = Some(detail);
        self.detail_error = None;
        true
    }

    pub fn detail_failed(&mut self, token: DetailToken, error: &NotionError) -> bool {
        if token.0 != self.detail_read || self.selected.is_none() {
            return false;
        }
        if is_token_failure(&error.code) {
            self.token_failed = true;
        }
        self.detail_error = Some(error_text(&error.code));
        true
    }

    pub fn back(&mut self) {
        self.selected = None;
        self.detail = None;
        self.detail_error = None;
        self.detail_read += 1;
    }

    pub fn selected(&self) -> Option<&NotionTaskRef> {
        self.selected.as_ref()
    }

    pub fn detail(&self) -> Option<&TaskDetail> {
        self.detail.as_ref()
    }

    pub fn row(&self, page_id: &str) -> Option<&NotionTaskRow> {
        self.rows.iter().find(|row| row.page_id == page_id)
    }

    // The list follows the selected session's project.

    /// Called with the selected session's folder. When the folder changes to one that maps to a project, that project
    /// is shown. Returns whether the query changed.
    pub fn follow_root(&mut self, root: Option<&str>) -> bool {
        if self.followed_root.as_deref() == root {
            return false;
        }
        self.followed_root = root.map(str::to_owned);
        let matched = root.and_then(|root| self.session_project(root));
        match matched {
            Some(id) if self.query.project_id.as_deref() != Some(id.as_str()) => {
                self.query.project_id = Some(id);
                true
            }
            _ => false,
        }
    }

    fn session_project(&self, root: &str) -> Option<String> {
        let titles: Vec<&str> = self
            .projects
            .iter()
            .map(|project| project.title.as_str())
            .collect();
        let at = match_project(root, &titles)?;
        Some(self.projects[at].id.clone())
    }

    /// The projects arrived after the session did, so look again.
    pub fn refollow(&mut self) -> bool {
        let root = self.followed_root.take();
        self.follow_root(root.as_deref())
    }

    /// The session has a folder, the snapshot has projects, and none is its own.
    pub fn no_session_project(&self) -> bool {
        self.query.project_id.is_none()
            && !self.projects.is_empty()
            && self
                .followed_root
                .as_deref()
                .is_some_and(|root| self.session_project(root).is_none())
    }
}

fn letters(value: &str) -> String {
    value
        .chars()
        .filter(char::is_ascii_alphabetic)
        .map(|c| c.to_ascii_lowercase())
        .collect()
}

/// The project a session's folder belongs to. A linked worktree (`.../worktrees/<repo>/<task>`) belongs to its repo. An
/// exact letters-only match wins (`mac-command-bar`, `MacCommandBar`); otherwise the project whose name starts with the
/// folder's first word (`rental-management`, `Rental Command`).
pub fn match_project(root: &str, projects: &[&str]) -> Option<usize> {
    let parts: Vec<&str> = root.split('/').filter(|part| !part.is_empty()).collect();
    let worktrees = parts.iter().rposition(|part| *part == "worktrees");
    let folder = match worktrees {
        Some(at) if parts.len() > at + 2 => parts[at + 1],
        _ => parts.last().copied()?,
    };
    let whole = letters(folder);
    if whole.is_empty() {
        return None;
    }
    if let Some(at) = projects
        .iter()
        .position(|project| letters(project) == whole)
    {
        return Some(at);
    }
    let first = letters(first_word(folder));
    if first.is_empty() {
        return None;
    }
    projects
        .iter()
        .position(|project| letters(project).starts_with(&first))
}

/// Up to the first `-`, `_`, or lower-to-upper step.
fn first_word(folder: &str) -> &str {
    let chars: Vec<(usize, char)> = folder.char_indices().collect();
    for pair in chars.windows(2) {
        let ((_, this), (at, next)) = (pair[0], pair[1]);
        if next == '-' || next == '_' || (this.is_ascii_lowercase() && next.is_ascii_uppercase()) {
            return &folder[..at];
        }
    }
    folder
}

pub fn is_token_failure(code: &str) -> bool {
    matches!(code, "invalid_token" | "missing_token" | "notion_http_401")
}

/// A backend failure, in words.
pub fn error_text(code: &str) -> String {
    match code {
        "invalid_token" | "notion_http_401" => {
            "Notion turned down the saved sign-in. Connect again to keep working.".into()
        }
        "missing_token" | "notion_unconfigured" => "Notion isn't connected yet.".into(),
        "ambiguous_data_source" => {
            "This workspace has more than one task database, or none Assembly can read. Enter the one to use.".into()
        }
        "notion_changed" => "This task changed in Notion. It's been read again, so try once more.".into(),
        "notion_transport" | "disconnected" => "Assembly couldn't reach Notion.".into(),
        "notion_http_429" => "Notion asked Assembly to slow down. Try again shortly.".into(),
        "notion_http_403" | "notion_http_404" => {
            "Notion says this connection can't read that page.".into()
        }
        "notion_source_mismatch" => "That page isn't in the connected task database.".into(),
        "notion_too_large" => "That's too large to read or save here.".into(),
        "notion_connection_changed" => "The Notion connection changed. Try again.".into(),
        "cancelled" => "The call was cancelled.".into(),
        other => format!("Notion reported a problem: {other}"),
    }
}
