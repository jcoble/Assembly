//! The lock on an outside file (spec decision 2): it opens read-only and marked as outside the project; Edit anyway
//! unlocks it for the life of its tab, after which it saves with the usual revision check. A session file is never
//! locked.
use super::FileView;
use crate::activity::Tone;
use crate::notice_strip::NoticeStrip;
use gpui_kit::*;

#[cfg(test)]
#[path = "file_view_lock_tests.rs"]
mod tests;

/// What the close prompt's Save says on a locked file, which keeps its tab open.
pub(super) const SAVE_LOCKED: &str = "Press Edit anyway to save this file.";

impl FileView {
    /// The banner over a locked file's editor.
    pub(super) fn banner_words(&self) -> Option<&'static str> {
        self.locked
            .then_some("Outside this session's project, so it opened read-only.")
    }

    /// The header's marker for an outside file; it stays after Edit anyway (spec 2.4).
    pub(super) fn status_words(&self) -> Option<&'static str> {
        self.outside.is_some().then_some("outside project")
    }

    /// History reads a session's checkout, so an outside file has none.
    pub(super) fn has_history(&self) -> bool {
        self.outside.is_none()
    }

    /// The person chose to change this file: it stays unlocked until its tab closes.
    pub(crate) fn edit_anyway(&mut self, cx: &mut Context<Self>) {
        self.locked = false;
        cx.notify();
    }

    /// The banner with its Edit anyway button, while locked.
    pub(super) fn lock_banner(&self, cx: &mut Context<Self>) -> Option<NoticeStrip> {
        let words = self.banner_words()?;
        Some(
            NoticeStrip::new("file-outside-notice", words, Tone::Warning).action(
                "Edit anyway",
                cx.listener(|view, _, _, cx| view.edit_anyway(cx)),
            ),
        )
    }
}
