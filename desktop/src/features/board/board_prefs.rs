//! View preferences, kept per device under `board.*` in the backend's preferences: which tab, and what's folded or open.
//! They're never sent to the engine.
use std::collections::VecDeque;

use gpui_kit::App;
use serde_json::{Value, json};

use crate::core::state::preferences::Preferences;

const CAP: usize = 500;

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub enum BoardTab {
    #[default]
    Needs,
    Workflows,
    Sessions,
}

impl BoardTab {
    pub const ALL: [Self; 3] = [Self::Needs, Self::Workflows, Self::Sessions];

    fn name(self) -> &'static str {
        match self {
            Self::Needs => "needs",
            Self::Workflows => "workflows",
            Self::Sessions => "sessions",
        }
    }

    fn named(name: &str) -> Option<Self> {
        Self::ALL.into_iter().find(|tab| tab.name() == name)
    }
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum SessionTab {
    Questions,
    Workflow,
    SubAgents,
    Prs,
}

impl SessionTab {
    pub const ALL: [Self; 4] = [Self::Questions, Self::Workflow, Self::SubAgents, Self::Prs];

    fn name(self) -> &'static str {
        match self {
            Self::Questions => "questions",
            Self::Workflow => "workflow",
            Self::SubAgents => "sub_agents",
            Self::Prs => "prs",
        }
    }

    fn named(name: &str) -> Option<Self> {
        Self::ALL.into_iter().find(|tab| tab.name() == name)
    }
}

/// An insertion-ordered set of ids. Past its cap, the oldest goes.
#[derive(Clone, Debug, Default, PartialEq, Eq)]
pub struct BoundedSet(VecDeque<String>);

impl BoundedSet {
    pub fn contains(&self, id: &str) -> bool {
        self.0.iter().any(|held| held == id)
    }

    pub fn insert(&mut self, id: String) {
        if self.contains(&id) {
            return;
        }
        if self.0.len() == CAP {
            self.0.pop_front();
        }
        self.0.push_back(id);
    }

    pub fn remove(&mut self, id: &str) {
        self.0.retain(|held| held != id);
    }

    fn of(value: Option<Value>) -> Self {
        let mut set = Self::default();
        for id in value
            .iter()
            .flat_map(|value| value.as_array())
            .flatten()
            .filter_map(Value::as_str)
        {
            set.insert(id.to_owned());
        }
        set
    }

    fn to_json(&self) -> Value {
        json!(self.0)
    }
}

fn remember(tabs: &mut Vec<(String, SessionTab)>, id: String, tab: SessionTab) {
    tabs.retain(|(held, _)| *held != id);
    if tabs.len() == CAP {
        tabs.remove(0);
    }
    tabs.push((id, tab));
}

fn saved_tabs(value: &Value) -> Vec<(String, SessionTab)> {
    let mut tabs = Vec::new();
    for pair in value.as_array().into_iter().flatten() {
        if let (Some(id), Some(tab)) = (
            pair[0].as_str(),
            pair[1].as_str().and_then(SessionTab::named),
        ) {
            remember(&mut tabs, id.to_owned(), tab);
        }
    }
    tabs
}

#[derive(Clone, Debug, Default, PartialEq)]
pub struct BoardPrefs {
    pub tab: BoardTab,
    /// The sections opened that fold by default.
    pub unfolded: BoundedSet,
    /// The rows left open.
    pub open: BoundedSet,
    /// Each session's chosen tab, oldest first.
    pub session_tabs: Vec<(String, SessionTab)>,
}

impl BoardPrefs {
    pub fn read(cx: &App) -> Self {
        Self {
            tab: Preferences::value("board.current_tab", cx)
                .and_then(|tab| tab.as_str().and_then(BoardTab::named))
                .unwrap_or_default(),
            unfolded: BoundedSet::of(Preferences::value("board.folds", cx)),
            open: BoundedSet::of(Preferences::value("board.open", cx)),
            session_tabs: Preferences::value("board.tabs", cx)
                .as_ref()
                .map(saved_tabs)
                .unwrap_or_default(),
        }
    }

    pub fn save_tab(&self, cx: &mut App) {
        Preferences::save("board.current_tab", json!(self.tab.name()), cx);
    }

    pub fn save_folds(&self, cx: &mut App) {
        Preferences::save("board.folds", self.unfolded.to_json(), cx);
    }

    pub fn save_open(&self, cx: &mut App) {
        Preferences::save("board.open", self.open.to_json(), cx);
    }

    pub fn session_tab(&self, id: &str) -> Option<SessionTab> {
        self.session_tabs
            .iter()
            .find(|(held, _)| held == id)
            .map(|(_, tab)| *tab)
    }

    pub fn remember_session_tab(&mut self, id: String, tab: SessionTab, cx: &mut App) {
        remember(&mut self.session_tabs, id, tab);
        let tabs = self
            .session_tabs
            .iter()
            .map(|(id, tab)| json!([id, tab.name()]))
            .collect();
        Preferences::save("board.tabs", Value::Array(tabs), cx);
    }
}

#[cfg(test)]
#[path = "board_prefs_tests.rs"]
mod tests;
