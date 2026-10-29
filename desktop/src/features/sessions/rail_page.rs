//! `RailPage` is one `sessions/rail` answer: the filtered needs total, providers and ordered header/session entries.
//! `Rail` owns the filters and view options it asks for, what it holds, and its one read at a time. A change while
//! a read is out reads again once it answers; a new filter or view reads at once and drops the older answer.
//! It holds a window of entries, at most `KEEP` once
//! the reader has gone past them. A read again names the row in view and how many held rows sit above it, so the
//! backend's page keeps it wherever it moved; scrolling past `KEEP` lets the first rows go, and going back reads them
//! again. The window never counts, sorts or groups: it draws what the backend sent.

use serde::{Deserialize, Serialize};
use serde_json::{Value, json};

use crate::core::socket::outcome::Outcome;
use crate::features::sessions::view_options::View;

/// A first read's entries and an older page's; a read from the top asks for at least what's held, up to the backend's
/// most (`SessionsFeature.ListLimit`).
pub const PAGE: usize = 50;
const MOST: usize = 200;
const KEEP: usize = MOST - PAGE;

#[derive(Clone, Debug, Default, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct RailTotals {
    pub needs: usize,
}

/// Where the next parent page starts (`SessionsFeature.RailCursor`).
#[derive(Clone, Debug, Deserialize, Serialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct RailCursor {
    section: usize,
    group: String,
    repo_url: String,
    header: usize,
    ordering: String,
}

/// One session's own state and SQL order.
#[derive(Clone, Debug, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct RailEntry {
    pub session_id: String,
    pub kind: String,
    pub section: String,
    pub agent: String,
    pub repo_url: String,
    pub group: String,
    pub count: usize,
    pub machines: Vec<String>,
    pub title: Option<String>,
    pub machine: Option<String>,
    pub updated_at: i64,
    pub completed_at: Option<i64>,
    pub running: bool,
    pub needs_you: bool,
    pub failed: bool,
    pub unread: bool,
    pub ordering: String,
    pub ask_session: Option<String>,
    pub ask_seq: Option<i64>,
    /// The newest branch a turn's stats recorded: what it was then, not the checkout now.
    pub branch: Option<String>,
    pub branch_at: Option<i64>,
    /// The provider's glyph: a bundled icon's path, picked by the backend.
    pub icon: String,
    pub section_index: usize,
    pub header: usize,
    /// A parent's newest assistant text, cut to 160 characters by the backend.
    pub preview: Option<String>,
}

#[derive(Clone, Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct RailPage {
    pub totals: RailTotals,
    pub agents: Vec<String>,
    pub sessions: Vec<RailEntry>,
    pub next: Option<RailCursor>,
    /// How many sessions come before this page.
    #[serde(default)]
    pub start: usize,
}

/// A refresh, the next page, or the page before the held rows.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
enum Read {
    Top,
    More,
    Earlier,
}

/// A read to send: its number, which its answer is matched by, and the call's args.
pub type Request = (u64, Value);

#[derive(Default)]
pub struct Rail {
    pub providers: Vec<String>,
    /// `local` and `remote`.
    pub locations: Vec<String>,
    pub statuses: Vec<String>,
    pub view_options: View,
    pub folded: Vec<String>,
    pub filter: String,
    pub totals: RailTotals,
    pub agents: Vec<String>,
    pub sessions: Vec<RailEntry>,
    next: Option<RailCursor>,
    /// How many sessions precede the held rows.
    pub ahead: usize,
    /// The session at the top of the view, which a read again keeps.
    view: Option<String>,
    /// Why the last read failed, until one answers.
    pub error: Option<String>,
    /// A first read has answered.
    pub loaded: bool,
    reading: Option<(u64, Read)>,
    reads: u64,
    /// A change came while a read was out.
    again: bool,
}

impl Rail {
    /// A change came: read now, or once the read that's out answers.
    pub fn changed(&mut self) -> Option<Request> {
        if self.reading.is_some() {
            self.again = true;
            return None;
        }
        Some(self.start(Read::Top))
    }

    /// The providers, locations and lifecycle statuses picked, in one read.
    pub fn set_picks(
        &mut self,
        providers: Vec<String>,
        locations: Vec<String>,
        statuses: Vec<String>,
    ) -> Request {
        (self.providers, self.locations, self.statuses) = (providers, locations, statuses);
        self.requery()
    }

    pub fn set_view(&mut self, view: View) -> Request {
        self.view_options = view;
        self.requery()
    }

    pub fn restore(
        &mut self,
        providers: Vec<String>,
        locations: Vec<String>,
        statuses: Vec<String>,
        view: View,
    ) -> Request {
        (
            self.providers,
            self.locations,
            self.statuses,
            self.view_options,
        ) = (providers, locations, statuses, view);
        self.requery()
    }

    pub fn toggle_fold(&mut self, key: String) -> Request {
        if let Some(at) = self.folded.iter().position(|folded| folded == &key) {
            self.folded.remove(at);
        } else {
            self.folded.push(key);
        }
        self.start(Read::Top)
    }

    /// Nothing when the text is what's filtered already.
    pub fn set_filter(&mut self, text: &str) -> Option<Request> {
        if self.filter == text.trim() {
            return None;
        }
        self.filter = text.trim().to_owned();
        Some(self.requery())
    }

    /// The next parent page, when there is one and nothing's out.
    pub fn more(&mut self) -> Option<Request> {
        (self.reading.is_none() && self.next.is_some()).then(|| self.start(Read::More))
    }

    /// The parents before the held ones, when the reader scrolled past some and nothing's out.
    pub fn earlier(&mut self) -> Option<Request> {
        (self.reading.is_none() && self.ahead > 0).then(|| self.start(Read::Earlier))
    }

    /// The session at the top of the view.
    pub fn look_at(&mut self, session_id: Option<String>) {
        self.view = session_id;
    }

    /// Takes read `id`'s answer, and gives the read to send next: from the top again if a change came meanwhile. An
    /// answer to a read a newer one replaced changes nothing.
    pub fn finish(&mut self, id: u64, outcome: Outcome<RailPage>) -> Option<Request> {
        let read = match self.reading {
            Some((reading, read)) if reading == id => read,
            _ => return None,
        };
        self.reading = None;
        match outcome {
            Outcome::Ok(page) => self.take(read, page),
            Outcome::Failed(failure) => {
                self.error = Some(format!("Couldn't load the sessions: {}", failure.message))
            }
            Outcome::Aborted => {}
        }
        std::mem::take(&mut self.again).then(|| self.start(Read::Top))
    }

    fn take(&mut self, read: Read, page: RailPage) {
        self.error = None;
        self.loaded = true;
        self.totals = page.totals;
        self.agents = page.agents;
        if read == Read::More {
            self.sessions.extend(page.sessions);
            self.next = page.next;
            self.ahead += trim(&mut self.sessions);
            return;
        }
        (self.sessions, self.next, self.ahead) = (page.sessions, page.next, page.start);
        if read == Read::Earlier && self.sessions.len() > KEEP {
            self.sessions.truncate(KEEP);
            self.next = Some(cursor(&self.sessions[KEEP - 1], self.view_options));
        }
    }

    /// New filters or view options: what's held was for the old ones.
    fn requery(&mut self) -> Request {
        self.sessions.clear();
        (self.next, self.view) = (None, None);
        self.ahead = 0;
        self.start(Read::Top)
    }

    fn start(&mut self, read: Read) -> Request {
        self.reads += 1;
        self.reading = Some((self.reads, read));
        self.again = false;
        (self.reads, self.args(read))
    }

    fn args(&self, read: Read) -> Value {
        let more = read == Read::More;
        let parent = keep(
            &self.sessions,
            self.view.as_ref(),
            self.ahead,
            read == Read::Earlier,
        );
        let (anchor, above) = parent.filter(|_| !more).unzip();
        json!({
            "providers": self.providers,
            "locations": self.locations,
            "statuses": self.statuses,
            "groupBy": self.view_options.group_by,
            "sort": self.view_options.sort_wire(),
            "dir": self.view_options.dir_wire(),
            "folded": self.folded,
            "filter": (!self.filter.is_empty()).then_some(&self.filter),
            // Once the row in view has left the current view, the page starts after where it was.
            "after": if more { self.next.clone() } else { anchor.map(|entry| cursor(entry, self.view_options)) },
            "limit": if more { PAGE } else { (self.sessions.len() + PAGE).min(MOST) },
            "anchor": anchor.map(|entry| &entry.session_id),
            "above": above.unwrap_or(0),

        })
    }
}

/// The held row a read keeps and how many held rows to keep above it: the one in view, else the first; for the rows
/// before the held ones, the first with a page above it. None reads from the start, for a window there already.
fn keep<'a>(
    held: &'a [RailEntry],
    view: Option<&String>,
    ahead: usize,
    earlier: bool,
) -> Option<(&'a RailEntry, usize)> {
    let at = (held.iter())
        .position(|entry| Some(&entry.session_id) == view)
        .filter(|_| !earlier);
    match at {
        _ if earlier => held.first().map(|entry| (entry, PAGE)),
        Some(at) if at > 0 || ahead > 0 => Some((&held[at], at)),
        None if ahead > 0 => held.first().map(|entry| (entry, 0)),
        _ => None,
    }
}

/// Lets the first rows go past `KEEP`, and says how many went.
fn trim(held: &mut Vec<RailEntry>) -> usize {
    let gone = held.len().saturating_sub(KEEP);
    held.drain(..gone);
    gone
}

/// Where an entry sits in the backend's order, to read after it.
fn cursor(entry: &RailEntry, view: View) -> RailCursor {
    RailCursor {
        section: usize::from(view.group_by.status) * entry.section_index,
        group: if view.group_by.project {
            entry.group.clone()
        } else {
            String::new()
        },
        repo_url: if view.group_by.project {
            entry.repo_url.clone()
        } else {
            String::new()
        },
        header: entry.header,
        ordering: entry.ordering.clone(),
    }
}

/// A parent with nothing going on, for the tests to change.
#[cfg(test)]
pub fn test_entry(id: &str, repo_url: &str) -> RailEntry {
    serde_json::from_value(json!({ "sessionId": id, "kind": "session", "section": "working", "agent": "claude",
        "repoUrl": repo_url, "group": repo_url, "count": 0, "machines": [],
        "title": null, "machine": null, "updatedAt": 1, "completedAt": null, "running": false,
        "needsYou": false, "failed": false, "unread": false, "ordering": id, "askSession": null, "askSeq": null,
        "branch": null, "branchAt": null, "icon": "icons/bot.svg", "sectionIndex": 0, "header": 2 }))
    .unwrap()
}
