//! `ViewedProject` owns which other project's folder the Files and Git tabs are showing for the selected session, if
//! any. It changes nothing about the session: only what those two tabs (and the Changes tab they open) ask the backend
//! about. The folder travels in their calls' session field as an opaque id (`Viewed::id`), resolved by the backend from
//! the project. Saved with the session's workspace, so it comes back with the session on its own machine.

use gpui_kit::*;
use serde::{Deserialize, Serialize};

use super::selected_session::SelectedSession;
use super::sessions::Sessions;
use crate::core::socket::row_source::ExpectedRowSource;

/// The registry's name for this machine.
pub const LOCAL_MACHINE: &str = "local";

#[derive(Clone, Debug, Deserialize, PartialEq, Serialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct Viewed {
    pub project_id: String,
    /// "local", or the remote machine's name.
    pub machine: String,
    pub folder: String,
    /// The name the bar shows.
    pub title: String,
}

impl Viewed {
    /// What goes in a call's session field.
    pub fn id(&self) -> String {
        format!("view:{}:{}", hex(&self.project_id), hex(&self.folder))
    }
}

fn hex(text: &str) -> String {
    text.bytes().map(|byte| format!("{byte:02x}")).collect()
}

/// Whether a read or write target's session field names a viewed folder rather than a session.
pub fn is_view(session: &str) -> bool {
    session.starts_with("view:")
}

/// Whether a session in `session` is working in `folder`: the same folder, or one inside the other.
pub fn overlaps(session: &str, folder: &str) -> bool {
    let trim = |path: &'_ str| path.trim_end_matches('/').to_string();
    let (session, folder) = (trim(session), trim(folder));
    session == folder
        || session.starts_with(&format!("{folder}/"))
        || folder.starts_with(&format!("{session}/"))
}

#[derive(Default)]
pub struct ViewedProject {
    /// The session it was opened under; another session's selection doesn't see it.
    current: Option<(String, Viewed)>,
}

struct Holder(Entity<ViewedProject>);
impl Global for Holder {}

impl ViewedProject {
    pub fn global(cx: &mut App) -> Entity<Self> {
        if let Some(holder) = cx.try_global::<Holder>() {
            return holder.0.clone();
        }
        let viewed = cx.new(|_| Self::default());
        cx.set_global(Holder(viewed.clone()));
        viewed
    }

    /// What's viewed for the selected session.
    pub fn viewed(cx: &App) -> Option<Viewed> {
        let current = cx.try_global::<Holder>()?.0.read(cx).current.as_ref()?;
        let selected = SelectedSession::try_global(cx)?;
        (selected.read(cx).session_id() == Some(current.0.as_str())).then(|| current.1.clone())
    }

    /// What Files and Git read: the viewed folder (its machine comes from its project), or else the selected session.
    pub fn subject(cx: &App) -> Option<(String, Option<ExpectedRowSource>)> {
        match Self::viewed(cx) {
            Some(viewed) => Some((viewed.id(), None)),
            None => {
                let selected = SelectedSession::try_global(cx)?;
                let selected = selected.read(cx);
                Some((
                    selected.session_id()?.to_string(),
                    selected.source().cloned(),
                ))
            }
        }
    }

    /// Opens `viewed` for `session`, or goes back to the session with `None`.
    pub fn set(session: Option<String>, viewed: Option<Viewed>, cx: &mut App) {
        let current = session.zip(viewed);
        Self::global(cx).update(cx, |this, cx| {
            if this.current != current {
                this.current = current;
                cx.notify();
            }
        });
    }

    /// The one-line reason, naming the session, when a running session works in the viewed folder.
    pub fn read_only_reason(cx: &App) -> Option<String> {
        let viewed = Self::viewed(cx)?;
        let sessions = Sessions::try_global(cx)?;
        let sessions = sessions.read(cx);
        let working = sessions.items().iter().find(|item| {
            matches!(item.state.as_str(), "running" | "waiting")
                && item
                    .machine
                    .as_deref()
                    .filter(|name| !name.is_empty())
                    .unwrap_or(LOCAL_MACHINE)
                    == viewed.machine
                && item
                    .checkout_root
                    .as_deref()
                    .or(item.root.as_deref())
                    .is_some_and(|folder| overlaps(folder, &viewed.folder))
        })?;
        Some(format!(
            "\"{}\" is working in this folder, so it is read-only here.",
            working.title()
        ))
    }
}

#[cfg(test)]
#[path = "viewed_project_tests.rs"]
mod tests;
