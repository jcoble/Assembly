//! `ViewedProject` owns which other project's folder the Files and Git tabs are showing for the selected session, if
//! any. It changes nothing about the session: only what those two tabs (and the Changes tab they open) ask the backend
//! about. The folder travels in their calls' session field as an opaque id (`Viewed::id`), resolved by the backend from
//! the project. Saved with the session's workspace, so it comes back with the session on its own machine.

use gpui_kit::*;
use serde::{Deserialize, Serialize};
use serde_json::json;

use super::selected_session::SelectedSession;
use super::sessions::Sessions;
use crate::core::socket::client::SocketClient;
use crate::core::socket::outcome::Outcome;
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

#[derive(Default)]
pub struct ViewedProject {
    /// The session it was opened under; another session's selection doesn't see it.
    current: Option<(String, Viewed)>,
    /// The title of the session the backend says works in the viewed folder. Only the backend can say: the folder and
    /// the session's may be the same one through a symlink or a differently cased path, on either machine.
    in_use: Option<String>,
    /// The active sessions when the backend was last asked.
    asked_for: Vec<(String, String)>,
    _asking: Option<Task<()>>,
    /// Held only while a folder is viewed, so nothing runs otherwise.
    _sessions: Option<Subscription>,
}

#[derive(Deserialize)]
struct FolderInUse {
    session: Option<String>,
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
                this.in_use = None;
                this.follow_sessions(cx);
                cx.notify();
            }
        });
    }

    fn follow_sessions(&mut self, cx: &mut Context<Self>) {
        self._asking = None;
        self._sessions = None;
        if self.current.is_none() {
            return;
        }
        if let Some(sessions) = Sessions::try_global(cx) {
            self._sessions = Some(cx.observe(&sessions, |this, _, cx| {
                if this.asked_for != Self::active_sessions(cx) {
                    this.ask(cx);
                }
            }));
        }
        self.ask(cx);
    }

    /// Who is working anywhere, which is all that can change the backend's answer.
    fn active_sessions(cx: &App) -> Vec<(String, String)> {
        let Some(sessions) = Sessions::try_global(cx) else {
            return Vec::new();
        };
        let mut active: Vec<_> = sessions
            .read(cx)
            .items()
            .iter()
            .filter(|item| matches!(item.state.as_str(), "running" | "waiting"))
            .map(|item| (item.session_id.clone(), item.state.clone()))
            .collect();
        active.sort();
        active
    }

    fn ask(&mut self, cx: &mut Context<Self>) {
        self.asked_for = Self::active_sessions(cx);
        let (Some((_, viewed)), Some(socket)) = (&self.current, SocketClient::try_global(cx))
        else {
            return;
        };
        // Keyed, so a newer question cancels this one.
        let call = socket.read(cx).call::<FolderInUse>(
            "git",
            "inUse",
            json!({ "session": viewed.id() }),
            Some("git/inUse"),
        );
        self._asking = Some(cx.spawn(async move |this, cx| {
            let answer = call.await;
            _ = this.update(cx, |this, cx| {
                // Not knowing draws as writable; the backend still refuses a write it must.
                let in_use = match answer {
                    Outcome::Ok(answer) => answer.session,
                    Outcome::Failed(_) | Outcome::Aborted => None,
                };
                if this.in_use != in_use {
                    this.in_use = in_use;
                    cx.notify();
                }
            });
        }));
    }

    /// The one-line reason, naming the session, when the backend says a session works in the viewed folder.
    pub fn read_only_reason(cx: &App) -> Option<String> {
        Self::viewed(cx)?;
        let title = cx.try_global::<Holder>()?.0.read(cx).in_use.clone()?;
        let who = if title.trim().is_empty() {
            "Another session".to_string()
        } else {
            format!("\"{title}\"")
        };
        Some(format!(
            "{who} is working in this folder, so it is read-only here."
        ))
    }
}

#[cfg(test)]
#[path = "viewed_project_tests.rs"]
mod tests;
