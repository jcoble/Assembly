//! `FolderTouched` says a session's tool call that can change its folder (an edit, a move, a command) just finished,
//! so Git and Files read that folder again mid-turn. The session pane raises it from the rows it already gets pushed;
//! nothing polls or watches the disk. The Git panel raises it after its own write that changed the folder, which it
//! already shows, so only Files reads again then.

use gpui_kit::*;

#[derive(Default)]
pub struct FolderTouched {
    session: Option<String>,
    count: u64,
    by_git_panel: bool,
}

struct Holder(Entity<FolderTouched>);
impl Global for Holder {}

impl FolderTouched {
    pub fn global(cx: &mut App) -> Entity<Self> {
        if let Some(holder) = cx.try_global::<Holder>() {
            return holder.0.clone();
        }
        let touched = cx.new(|_| Self::default());
        cx.set_global(Holder(touched.clone()));
        touched
    }

    /// A tool call in `session` that can change its folder finished.
    pub fn touch(session: &str, cx: &mut App) {
        Self::global(cx).update(cx, |this, cx| {
            this.session = Some(session.to_string());
            this.count += 1;
            this.by_git_panel = false;
            cx.notify();
        });
    }

    /// The Git panel's own write changed the folder `session` reads (a switch, stash, pull or discard).
    pub fn git_panel_wrote(session: &str, cx: &mut App) {
        Self::global(cx).update(cx, |this, cx| {
            this.session = Some(session.to_string());
            this.count += 1;
            this.by_git_panel = true;
            cx.notify();
        });
    }

    /// Whether the last touch was the Git panel's own write, which the panel shows without reading again.
    pub fn by_git_panel(&self) -> bool {
        self.by_git_panel
    }

    /// The session the last touch was for.
    pub fn session(&self) -> Option<&str> {
        self.session.as_deref()
    }

    /// How many touches there have been, so an observer can tell a new one from a notify for another reason.
    pub fn count(&self) -> u64 {
        self.count
    }
}

/// Whether a finished tool call of this ACP kind can have changed the folder. Reads, searches, thinking and fetches
/// can't; anything else (edit, delete, move, execute, other or no kind) might.
pub fn can_change_folder(kind: Option<&str>) -> bool {
    !matches!(kind, Some("read" | "search" | "think" | "fetch"))
}

#[cfg(test)]
#[path = "folder_touched_tests.rs"]
mod tests;
