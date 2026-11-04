//! What each Board tab lists, chosen from the shared engine rows. These pick and order; they hold nothing and fetch nothing.
use std::collections::HashMap;

use crate::core::engine::engine_items::{NeedItem, PrItem, SessionItem};
use crate::core::engine::workflow_items::{RunItem, StepItem};
use crate::core::engine::{EngineFeed, EngineItem, EngineKey, SessionKey};

/// One read of the shared rows, taken when they change rather than on each frame.
#[derive(Default)]
pub struct BoardData {
    pub needs: HashMap<EngineKey, NeedItem>,
    pub sessions: HashMap<EngineKey, SessionItem>,
    pub runs: HashMap<EngineKey, RunItem>,
    pub steps: Vec<StepItem>,
    pub prs: Vec<PrItem>,
}

impl BoardData {
    pub fn read(feed: &EngineFeed) -> Self {
        Self::of(
            &feed.topic_items("needs_me"),
            &feed.topic_items("sessions"),
            &feed.topic_items("runs"),
            &feed.topic_items("prs"),
        )
    }

    pub fn of(needs: &[EngineItem], sessions: &[EngineItem], runs: &[EngineItem], prs: &[EngineItem]) -> Self {
        let decode = |kind: &str| runs.iter().filter(move |item| item.key.kind == kind).map(|item| item.value.clone()).collect::<Vec<_>>();
        Self {
            needs: needs.iter().filter_map(NeedItem::of).map(|need| (need.key.clone(), need)).collect(),
            sessions: sessions.iter().filter_map(SessionItem::of).map(|session| (session.key.clone(), session)).collect(),
            runs: decode("run").into_iter().filter_map(|value| RunItem::decode(value).ok()).map(|run| (run.key(), run)).collect(),
            steps: decode("step").into_iter().filter_map(|value| StepItem::decode(value).ok()).collect(),
            prs: prs.iter().filter_map(PrItem::of).collect(),
        }
    }

    pub fn session(&self, key: &SessionKey) -> Option<&SessionItem> {
        self.sessions.get(&EngineKey { machine: key.machine.clone(), kind: "session".into(), id: key.id.clone() })
    }

    /// The session's run, in the session's own machine.
    pub fn run_of(&self, session: &SessionItem) -> Option<&RunItem> {
        let id = session.run_id.as_ref()?;
        self.runs.get(&EngineKey { machine: session.key.machine.clone(), kind: "run".into(), id: id.clone() })
    }

    /// A run's steps in the order the engine stores them. The step list's order comes from `workflow/run`.
    pub fn steps_of(&self, run: &RunItem) -> Vec<&StepItem> {
        self.steps.iter().filter(|step| step.machine == run.machine && step.run_id == run.id).collect()
    }

    /// PRs of the session's run, or that it owns itself.
    pub fn prs_for(&self, session: &SessionItem) -> Vec<&PrItem> {
        self.prs
            .iter()
            .filter(|pr| {
                pr.key.machine == session.key.machine
                    && (pr.owner_session_id.as_ref() == Some(&session.key.id)
                        || (pr.run_id.is_some() && pr.run_id == session.run_id))
            })
            .collect()
    }
}

fn is_owner_question(need: &NeedItem) -> bool {
    need.addressee_session_id.is_none()
}

fn settled_ms(need: &NeedItem) -> i64 {
    need.answered_ms.or(need.updated_ms).unwrap_or(0)
}

fn sorted_by<T>(items: impl Iterator<Item = T>, order: impl Fn(&T) -> (i64, EngineKey), key: impl Fn(&T) -> EngineKey) -> Vec<EngineKey> {
    let mut items = items.collect::<Vec<_>>();
    items.sort_by_key(|item| order(item));
    items.iter().map(key).collect()
}

/// Open questions addressed to the owner, oldest first. Ones addressed to a parent stay on that session's tab.
pub fn open_owner_questions(data: &BoardData) -> Vec<EngineKey> {
    sorted_by(
        data.needs.values().filter(|need| need.state == "open" && is_owner_question(need)),
        |need| (need.created_ms.unwrap_or(0), need.key.clone()),
        |need| need.key.clone(),
    )
}

/// Questions that closed since midnight, newest first.
pub fn answered_today(data: &BoardData, midnight_ms: i64) -> Vec<EngineKey> {
    sorted_by(
        data.needs.values().filter(|need| need.state != "open" && is_owner_question(need) && settled_ms(need) >= midnight_ms),
        |need| (-settled_ms(need), need.key.clone()),
        |need| need.key.clone(),
    )
}

pub fn badge_count(data: &BoardData) -> usize {
    data.needs.values().filter(|need| need.state == "open" && is_owner_question(need)).count()
}

pub fn badge_label(count: usize) -> Option<String> {
    match count {
        0 => None,
        1..=99 => Some(count.to_string()),
        _ => Some("99+".into()),
    }
}

/// A run is over only by its recorded outcome. Closing it is how it's shown, never a state.
pub fn is_terminal(run: &RunItem) -> bool {
    matches!(run.state.as_str(), "succeeded" | "failed" | "cancelled")
}

fn has_unfinished_run(data: &BoardData, session: &SessionItem) -> bool {
    data.run_of(session).is_some_and(|run| !is_terminal(run))
}

fn is_top_level(session: &SessionItem) -> bool {
    session.parent_id.is_none()
}

/// Top-level sessions that are starting, working, or own an unfinished run. Idle ones with nothing open stay on the rail.
pub fn live_top_level(data: &BoardData) -> Vec<EngineKey> {
    sorted_by(
        data.sessions.values().filter(|session| {
            is_top_level(session)
                && !matches!(session.state.as_str(), "ended" | "foreign" | "handing_off")
                && (matches!(session.state.as_str(), "starting" | "running") || has_unfinished_run(data, session))
        }),
        |session| (session.created_ms.unwrap_or(0), session.key.clone()),
        |session| session.key.clone(),
    )
}

/// Top-level sessions that ended since midnight, newest first.
pub fn done_today(data: &BoardData, midnight_ms: i64) -> Vec<EngineKey> {
    let ended = |session: &SessionItem| session.updated_ms.unwrap_or(0);
    sorted_by(
        data.sessions.values().filter(|session| is_top_level(session) && session.state == "ended" && ended(session) >= midnight_ms),
        |session| (-ended(session), session.key.clone()),
        |session| session.key.clone(),
    )
}

pub fn live_runs(data: &BoardData) -> Vec<EngineKey> {
    sorted_by(
        data.runs.values().filter(|run| !is_terminal(run)),
        |run| (run.started_ms.unwrap_or(0), run.key()),
        |run| run.key(),
    )
}

/// Runs that finished since midnight, newest first.
pub fn done_runs_today(data: &BoardData, midnight_ms: i64) -> Vec<EngineKey> {
    let ended = |run: &RunItem| run.ended_ms.unwrap_or(0);
    sorted_by(
        data.runs.values().filter(|run| is_terminal(run) && ended(run) >= midnight_ms),
        |run| (-ended(run), run.key()),
        |run| run.key(),
    )
}

/// The session's and its children's questions: open ones oldest first, then today's closed ones newest first.
pub fn session_questions(data: &BoardData, session: &SessionKey, midnight_ms: i64) -> Vec<EngineKey> {
    let asked_here = |need: &NeedItem| {
        need.session == *session
            || need.addressee().as_ref() == Some(session)
            || data.session(&need.session).is_some_and(|asker| {
                asker.parent_id.as_ref() == Some(&session.id) && asker.parent_machine.as_ref() == Some(&session.machine)
            })
    };
    let mut keys = sorted_by(
        data.needs.values().filter(|need| need.state == "open" && asked_here(need)),
        |need| (need.created_ms.unwrap_or(0), need.key.clone()),
        |need| need.key.clone(),
    );
    keys.extend(sorted_by(
        data.needs.values().filter(|need| need.state != "open" && asked_here(need) && settled_ms(need) >= midnight_ms),
        |need| (-settled_ms(need), need.key.clone()),
        |need| need.key.clone(),
    ));
    keys
}

/// For the header's "3 running · 1 waiting · 5 done today".
pub fn summary_counts(data: &BoardData, midnight_ms: i64) -> (usize, usize, usize) {
    let live = data.runs.values().filter(|run| !is_terminal(run));
    let waiting = live.clone().filter(|run| run.state == "waiting" || run.paused).count();
    (live.count() - waiting, waiting, done_runs_today(data, midnight_ms).len())
}

/// Live keys grouped by machine, this machine first and the rest by id, so a remote never reads as local.
pub fn by_machine(keys: Vec<EngineKey>, local: Option<&str>) -> Vec<(String, Vec<EngineKey>)> {
    let mut groups: Vec<(String, Vec<EngineKey>)> = Vec::new();
    for key in keys {
        match groups.iter_mut().find(|(machine, _)| *machine == key.machine) {
            Some((_, group)) => group.push(key),
            None => groups.push((key.machine.clone(), vec![key])),
        }
    }
    groups.sort_by_key(|(machine, _)| (Some(machine.as_str()) != local, machine.clone()));
    groups
}

#[cfg(test)]
#[path = "board_queries_tests.rs"]
mod tests;
