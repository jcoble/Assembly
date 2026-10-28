//! `RailPage` is one `sessions/rail` answer (dotnet/.../Sessions/SessionsFeature.cs, `Rail`): each tab's total, the
//! providers to filter by, a page of parents in the backend's order and the expanded parent's helpers. `Rail` is the
//! sessions column's read: the tab and filters it asks for, what it holds, and its one read at a time. A change while
//! a read is out reads again once it answers; a new tab or filter reads at once, and the older read's answer is
//! dropped. The window never counts, sorts or groups: it draws what the backend sent.

use serde::{Deserialize, Serialize};
use serde_json::{Value, json};

use crate::core::socket::outcome::Outcome;

/// A first read's parents and an older page's; a read from the top asks for at least what's held, up to the backend's
/// most (`SessionsFeature.ListLimit`).
pub const PAGE: usize = 50;
const MOST: usize = 200;

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub enum Tab {
    #[default]
    All,
    Running,
    NeedsYou,
}

impl Tab {
    pub const ALL: [Tab; 3] = [Tab::All, Tab::Running, Tab::NeedsYou];

    fn wire(self) -> &'static str {
        match self {
            Tab::All => "all",
            Tab::Running => "running",
            Tab::NeedsYou => "needs",
        }
    }

    /// "All 12": the tab's name and its total.
    pub fn label(self, totals: &RailTotals) -> String {
        match self {
            Tab::All => format!("All {}", totals.all),
            Tab::Running => format!("Running {}", totals.running),
            Tab::NeedsYou => format!("Needs you {}", totals.needs_you),
        }
    }
}

#[derive(Clone, Debug, Default, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct RailTotals {
    pub all: usize,
    pub running: usize,
    pub needs_you: usize,
}

/// Where the next parent page starts (`SessionsFeature.RailCursor`).
#[derive(Clone, Debug, Deserialize, Serialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct RailCursor {
    group: String,
    repo_url: String,
    updated_at: i64,
    session_id: String,
}

/// Where the expanded parent's next helper page starts (`SessionsFeature.RailChildCursor`).
#[derive(Clone, Debug, Deserialize, Serialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct RailChildCursor {
    updated_at: i64,
    session_id: String,
}

/// One parent or helper (`SessionsFeature.RailEntry`). A parent's `running` and `needs_you` hold for its helpers'
/// work and asks too; `group_start` marks its project's first parent in the tab.
#[derive(Clone, Debug, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct RailEntry {
    pub session_id: String,
    pub agent: String,
    pub repo_url: String,
    pub group: String,
    pub group_start: bool,
    pub title: Option<String>,
    pub machine: Option<String>,
    pub updated_at: i64,
    pub completed_at: Option<i64>,
    pub running: bool,
    pub needs_you: bool,
    pub failed: bool,
    pub unread: bool,
    pub helpers: usize,
    pub ask_session: Option<String>,
    pub ask_seq: Option<i64>,
    /// The newest branch a turn's stats recorded: what it was then, not the checkout now.
    pub branch: Option<String>,
    pub branch_at: Option<i64>,
}

#[derive(Clone, Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct RailPage {
    pub totals: RailTotals,
    pub agents: Vec<String>,
    pub sessions: Vec<RailEntry>,
    pub next: Option<RailCursor>,
    pub helpers: Vec<RailEntry>,
    pub helpers_next: Option<RailChildCursor>,
}

/// Which read is out: from the top (a change, a new tab, filter or expanded parent), the next parent page, or the
/// expanded parent's next helper page (with the parents from the top again, as the backend reads both at once).
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
enum Read {
    Top,
    More,
    Helpers,
}

/// A read to send: its number, which its answer is matched by, and the call's args.
pub type Request = (u64, Value);

#[derive(Default)]
pub struct Rail {
    pub tab: Tab,
    pub providers: Vec<String>,
    /// `local` and `remote`.
    pub locations: Vec<String>,
    pub filter: String,
    /// The one parent whose helpers show under it.
    pub expanded: Option<String>,
    pub totals: RailTotals,
    pub agents: Vec<String>,
    pub sessions: Vec<RailEntry>,
    pub helpers: Vec<RailEntry>,
    next: Option<RailCursor>,
    helpers_next: Option<RailChildCursor>,
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
    /// A change came: read from the top now, or once the read that's out answers. Every change counts, a helper's
    /// too: which tab its parent is in is the backend's to say.
    pub fn changed(&mut self) -> Option<Request> {
        if self.reading.is_some() {
            self.again = true;
            return None;
        }
        Some(self.start(Read::Top))
    }

    pub fn set_tab(&mut self, tab: Tab) -> Request {
        self.tab = tab;
        self.requery()
    }

    /// Picks the provider or location, or drops it when it's picked.
    pub fn toggle_provider(&mut self, agent: &str) -> Request {
        toggle(&mut self.providers, agent);
        self.requery()
    }

    pub fn toggle_location(&mut self, location: &str) -> Request {
        toggle(&mut self.locations, location);
        self.requery()
    }

    /// Nothing when the text is what's filtered already.
    pub fn set_filter(&mut self, text: &str) -> Option<Request> {
        if self.filter == text.trim() {
            return None;
        }
        self.filter = text.trim().to_owned();
        Some(self.requery())
    }

    /// Shows a parent's helpers under it, or hides them; one parent's at a time.
    pub fn toggle_expanded(&mut self, session_id: &str) -> Request {
        self.expanded =
            (self.expanded.as_deref() != Some(session_id)).then(|| session_id.to_owned());
        self.helpers.clear();
        self.helpers_next = None;
        self.start(Read::Top)
    }

    /// The next parent page, when there is one and nothing's out.
    pub fn more(&mut self) -> Option<Request> {
        (self.reading.is_none() && self.next.is_some()).then(|| self.start(Read::More))
    }

    pub fn has_more_helpers(&self) -> bool {
        self.helpers_next.is_some()
    }

    pub fn more_helpers(&mut self) -> Option<Request> {
        (self.reading.is_none() && self.has_more_helpers()).then(|| self.start(Read::Helpers))
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
            let fresh: Vec<RailEntry> = (page.sessions.into_iter())
                .filter(|entry| {
                    !self
                        .sessions
                        .iter()
                        .any(|held| held.session_id == entry.session_id)
                })
                .collect();
            self.sessions.extend(fresh);
            self.next = page.next;
            return;
        }
        self.sessions = page.sessions;
        self.next = page.next;
        if read == Read::Helpers {
            self.helpers.extend(page.helpers);
        } else {
            self.helpers = page.helpers;
        }
        self.helpers_next = page.helpers_next;
    }

    /// A new tab or filter: what's held was for the old one.
    fn requery(&mut self) -> Request {
        self.sessions.clear();
        self.helpers.clear();
        (self.next, self.helpers_next) = (None, None);
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
        let held = self.sessions.len().max(self.helpers.len());
        json!({
            "tab": self.tab.wire(),
            "providers": self.providers,
            "locations": self.locations,
            "filter": (!self.filter.is_empty()).then_some(&self.filter),
            "after": if more { self.next.as_ref() } else { None },
            "limit": if more { PAGE } else { held.clamp(PAGE, MOST) },
            "expanded": if more { None } else { self.expanded.as_ref() },
            "helpersAfter": if read == Read::Helpers { self.helpers_next.as_ref() } else { None },
        })
    }
}

/// A parent with nothing going on, for the tests to change.
#[cfg(test)]
pub fn test_entry(id: &str, repo_url: &str) -> RailEntry {
    serde_json::from_value(json!({ "sessionId": id, "agent": "claude", "repoUrl": repo_url, "group": repo_url,
        "groupStart": false, "title": null, "machine": null, "updatedAt": 1, "completedAt": null, "running": false,
        "needsYou": false, "failed": false, "unread": false, "helpers": 0, "askSession": null, "askSeq": null,
        "branch": null, "branchAt": null }))
    .unwrap()
}

fn toggle(values: &mut Vec<String>, value: &str) {
    match values.iter().position(|held| held == value) {
        Some(at) => _ = values.remove(at),
        None => values.push(value.to_owned()),
    }
}
