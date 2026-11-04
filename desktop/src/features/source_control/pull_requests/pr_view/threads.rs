//! The Threads section of a pull request's side column: the session the owning Engine recorded for this pull request,
//! directly or through its run. Only that Engine's rows are read, and only while the page is showing.
use super::super::pr_owner::PrOwner;
use super::summary::quiet;
use crate::core::engine::{
    EngineChanged, EngineFeed, EngineItem, EngineKey, EngineScope, EngineWatch, SessionKey,
};
use crate::core::intents::session_open::{SessionOpenRequest, SessionOpenTarget};
use gpui_kit::assets::IconName;
use gpui_kit::component::{ActiveTheme as _, Icon, h_flex};
use gpui_kit::*;
use std::collections::{HashMap, HashSet};

const TOPIC: &str = "prs";

#[derive(Debug, PartialEq)]
pub(super) enum Recorded {
    Session(String),
    Run(String),
}
/// Who the Engine recorded for this pull request. It holds one row per repository and number.
pub(super) fn recorded_on(rows: &[EngineItem], repository: &str, number: u64) -> Option<Recorded> {
    let row = rows.iter().find(|row| {
        row.key.kind == "pr"
            && row.value["number"].as_u64() == Some(number)
            && row.value["repo"]
                .as_str()
                .is_some_and(|repo| repo.eq_ignore_ascii_case(repository))
    })?;
    let named = |field: &str| {
        row.value[field]
            .as_str()
            .filter(|id| !id.is_empty())
            .map(str::to_owned)
    };
    named("ownerSessionId")
        .map(Recorded::Session)
        .or_else(|| named("runId").map(Recorded::Run))
}
#[derive(Clone, Debug, PartialEq)]
pub(super) enum ThreadsBody {
    /// The rows are on their way; the section shows only its heading.
    Waiting,
    Unavailable,
    Empty,
    One {
        key: SessionKey,
        title: String,
    },
}
pub(super) struct PrThreads {
    machine: Option<String>,
    number: u64,
    repository: Option<String>,
    feed: Entity<EngineFeed>,
    active: bool,
    _watch: Option<EngineWatch>,
    refused: bool,
    reads: HashMap<EngineKey, Task<()>>,
    /// Rows that couldn't be read; the next push of this Engine's pull requests tries them again.
    unreadable: HashSet<EngineKey>,
    shown: ThreadsBody,
    _changes: Subscription,
}
fn scope_of(machine: &str) -> EngineScope {
    EngineScope {
        machine: machine.to_owned(),
        topic: TOPIC.to_owned(),
        key: None,
    }
}
impl PrThreads {
    pub(super) fn new(
        owner: &PrOwner,
        repository: Option<String>,
        number: u64,
        cx: &mut Context<Self>,
    ) -> Self {
        let machine = match owner {
            PrOwner::Session { source, .. } => source.as_ref().map(|source| source.machine.clone()),
            PrOwner::Project { source, .. } => Some(source.machine.clone()),
        };
        let feed = EngineFeed::global(cx);
        let changes = cx.subscribe(
            &feed,
            |threads: &mut Self, _, change: &EngineChanged, cx| threads.heard(change, cx),
        );
        Self {
            machine,
            number,
            repository,
            feed,
            active: false,
            _watch: None,
            refused: false,
            reads: HashMap::new(),
            unreadable: HashSet::new(),
            shown: ThreadsBody::Waiting,
            _changes: changes,
        }
    }
    pub(super) fn shown(&self) -> &ThreadsBody {
        &self.shown
    }
    pub(super) fn set_repository(&mut self, repository: String, cx: &mut Context<Self>) {
        self.repository = Some(repository);
        if self.active {
            self.recompute(cx);
        }
    }
    pub(super) fn set_active(&mut self, active: bool, cx: &mut Context<Self>) {
        if self.active == active {
            return;
        }
        self.active = active;
        if !active {
            self._watch = None;
            self.refused = false;
            self.reads.clear();
            self.unreadable.clear();
            return;
        }
        if let Some(machine) = self.machine.clone() {
            match self
                .feed
                .update(cx, |feed, cx| feed.watch(scope_of(&machine), cx))
            {
                Ok(watch) => self._watch = Some(watch),
                Err(_) => self.refused = true,
            }
        }
        self.recompute(cx);
    }
    fn heard(&mut self, change: &EngineChanged, cx: &mut Context<Self>) {
        let Some(machine) = self.machine.as_deref().filter(|_| self.active) else {
            return;
        };
        let scoped = |topic: &str| {
            change
                .scopes
                .iter()
                .any(|scope| scope.machine == machine && scope.topic == topic)
        };
        let pushed = scoped(TOPIC);
        let relevant = pushed
            || scoped("sessions")
            || change.keys.iter().any(|key| {
                key.machine == machine && matches!(key.kind.as_str(), "pr" | "run" | "session")
            })
            || change.sessions.iter().any(|key| key.machine == machine);
        if pushed {
            self.unreadable.clear();
        }
        if relevant {
            self.recompute(cx);
        }
    }
    fn recompute(&mut self, cx: &mut Context<Self>) {
        let (body, missing) = self.reading(cx);
        if let Some(key) = missing {
            self.fetch(key, cx);
        }
        if body != self.shown {
            self.shown = body;
            cx.notify();
        }
    }
    /// What to show now, and the row that would complete it.
    fn reading(&self, cx: &App) -> (ThreadsBody, Option<EngineKey>) {
        let Some(machine) = self.machine.as_ref().filter(|_| !self.refused) else {
            return (ThreadsBody::Unavailable, None);
        };
        let Some(repository) = self.repository.as_deref() else {
            return (ThreadsBody::Waiting, None);
        };
        let feed = self.feed.read(cx);
        let scope = scope_of(machine);
        if !feed.loaded(&scope) {
            let body = if feed.error().is_some() {
                ThreadsBody::Unavailable
            } else {
                ThreadsBody::Waiting
            };
            return (body, None);
        }
        let session = match recorded_on(&feed.items(&scope), repository, self.number) {
            None => return (ThreadsBody::Empty, None),
            Some(Recorded::Session(id)) => id,
            Some(Recorded::Run(id)) => {
                let key = EngineKey {
                    machine: machine.clone(),
                    kind: "run".into(),
                    id,
                };
                let Some(run) = feed.item(&key) else {
                    return if self.unreadable.contains(&key) {
                        (ThreadsBody::Unavailable, None)
                    } else {
                        (ThreadsBody::Waiting, Some(key))
                    };
                };
                match run.value["ownerSessionId"]
                    .as_str()
                    .filter(|id| !id.is_empty())
                {
                    Some(id) => id.to_owned(),
                    None => return (ThreadsBody::Unavailable, None),
                }
            }
        };
        let key = SessionKey {
            machine: machine.clone(),
            id: session,
        };
        let title = feed
            .session(&key)
            .and_then(|session| session.title)
            .map(|title| title.trim().to_owned())
            .filter(|title| !title.is_empty())
            .unwrap_or_else(|| "Untitled session".to_owned());
        let row = EngineKey {
            machine: machine.clone(),
            kind: "session".into(),
            id: key.id.clone(),
        };
        let missing = (feed.item(&row).is_none() && !self.unreadable.contains(&row)).then_some(row);
        (ThreadsBody::One { key, title }, missing)
    }
    fn fetch(&mut self, key: EngineKey, cx: &mut Context<Self>) {
        if self.reads.contains_key(&key) {
            return;
        }
        let read = self
            .feed
            .update(cx, |feed, cx| feed.ensure_item(key.clone(), cx));
        let asked = key.clone();
        let task = cx.spawn(async move |threads, cx| {
            let answer = read.await;
            _ = threads.update(cx, |threads, cx| {
                threads.reads.remove(&asked);
                if !matches!(answer, Ok(Some(_))) {
                    threads.unreadable.insert(asked);
                }
                threads.recompute(cx);
            });
        });
        self.reads.insert(key, task);
    }
}
/// What goes under the section's heading: the session to jump to, or why there isn't one.
pub(super) fn threads_body(shown: &ThreadsBody, cx: &App) -> Option<AnyElement> {
    match shown {
        ThreadsBody::Waiting => None,
        ThreadsBody::Unavailable => Some(quiet("Threads unavailable", cx).into_any_element()),
        ThreadsBody::Empty => Some(quiet("No threads", cx).into_any_element()),
        ThreadsBody::One { key, title } => {
            let key = key.clone();
            let hover = cx.theme().secondary_hover;
            Some(
                h_flex()
                    .id("pr-thread")
                    .debug_selector(|| "pr-thread".into())
                    .gap_2()
                    .w_full()
                    .min_w_0()
                    .py_1()
                    .rounded(px(8.))
                    .cursor_pointer()
                    .hover(move |row| row.bg(hover))
                    .child(Icon::new(IconName::MessageSquare).size(px(15.)))
                    .child(div().flex_1().min_w_0().truncate().child(title.clone()))
                    .on_click(move |_, window, cx| {
                        window.dispatch_action(
                            Box::new(SessionOpenRequest {
                                key: key.clone(),
                                request_id: uuid::Uuid::new_v4().to_string(),
                                target: SessionOpenTarget::Engine,
                            }),
                            cx,
                        )
                    })
                    .into_any_element(),
            )
        }
    }
}
#[cfg(test)]
#[path = "threads_tests.rs"]
mod tests;
