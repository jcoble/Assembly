//! How the pane's root stands, and the one place a root failure is recorded.
use super::FilesPane;
use crate::core::socket::call_failure::CallFailure;
use crate::core::socket::error_code::ErrorCode;
use crate::core::state::sessions::Sessions;
use crate::features::files::panel_message::{
    FOLDER_NOT_MADE, PanelInput, PanelMessage, RootState, panel_message,
};
use gpui_kit::*;

/// A failure the backend didn't send: the words are this client's own.
pub(super) fn plain_failure(message: &str) -> CallFailure {
    CallFailure {
        code: ErrorCode::Failed,
        message: message.into(),
    }
}

impl FilesPane {
    /// What the panel says instead of rows, if anything.
    pub(in crate::features::files) fn message(&self) -> Option<PanelMessage> {
        panel_message(PanelInput {
            subject: self.session.is_some(),
            root: self.root_state(),
        })
    }

    fn root_state(&self) -> RootState<'_> {
        if let Some(failure) = &self.root_failure {
            return RootState::Failed(failure);
        }
        if self.tree.root_loaded() {
            return RootState::Listed {
                has_rows: self.tree.row_count() > 0,
            };
        }
        if self.loads.contains_key("") {
            return RootState::Listing;
        }
        if self.started {
            RootState::NotLoaded
        } else {
            RootState::Waiting
        }
    }

    /// The root can't be listed. The rows it had no longer stand, so the message takes their place.
    pub(super) fn fail_root(&mut self, failure: CallFailure, cx: &mut Context<Self>) {
        let tree = self.walked_tree();
        tree.start();
        tree.loaded("", Err(failure.message.clone()));
        self.root_failure = Some(failure);
        self.follow_first_message(cx);
    }

    /// A folder's first page failed, or a later page of any folder, which keeps the rows it has.
    pub(super) fn fail_folder(&mut self, dir: &str, failure: CallFailure, cx: &mut Context<Self>) {
        if dir.is_empty() && !self.walked_tree().root_loaded() {
            self.fail_root(failure, cx);
        } else {
            self.walked_tree().loaded(dir, Err(failure.message));
        }
    }

    pub(super) fn clear_root_failure(&mut self) {
        self.root_failure = None;
        self.first_message = None;
    }

    /// A folder made by the first message comes with no tool call to say so. The session's own row changing does, so
    /// the pane listens to the list only while it's waiting for that folder.
    fn follow_first_message(&mut self, cx: &mut Context<Self>) {
        self.first_message = None;
        let waiting = self.root_failure.as_ref().is_some_and(|failure| {
            matches!(&failure.code, ErrorCode::Refused { code } if code == FOLDER_NOT_MADE)
        });
        let (true, Some(sessions)) = (waiting, Sessions::try_global(cx)) else {
            return;
        };
        let mut seen = self.session_updated_at(&sessions, cx);
        self.first_message = Some(cx.observe(&sessions, move |pane, sessions, cx| {
            let now = pane.session_updated_at(&sessions, cx);
            if now != seen {
                seen = now;
                pane.reload_kept(cx);
            }
        }));
    }

    fn session_updated_at(&self, sessions: &Entity<Sessions>, cx: &App) -> Option<i64> {
        let session = self.session.as_deref()?;
        sessions
            .read(cx)
            .items()
            .iter()
            .find(|item| item.session_id == session)
            .map(|item| item.updated_at)
    }

    #[cfg(test)]
    pub(in crate::features::files) fn owner_for_test(
        &self,
    ) -> crate::features::files::entry::FileOwnership {
        self.owner.clone().expect("an owner")
    }

    /// Whether a pane is showing this session's listed folder, so the backend has already said it has one.
    pub(crate) fn lists(session: &str, cx: &App) -> bool {
        Self::workspace_views(cx).iter().any(|pane| {
            let pane = pane.read(cx);
            pane.session.as_deref() == Some(session) && pane.tree.root_loaded()
        })
    }
}
