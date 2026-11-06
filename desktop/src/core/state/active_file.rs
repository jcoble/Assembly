//! `ActiveFile` is the file on screen in the editor: the shown file tab's session, path and source. A file tab sets it
//! when it's shown, clears it when it's hidden or closed, and moves it when it's renamed, so the Files tab's history
//! card follows the editor without reading the dock.

use crate::core::socket::row_source::ExpectedRowSource;
use gpui_kit::*;

#[derive(Clone, Debug, PartialEq)]
pub struct OnScreen {
    pub session: String,
    pub path: String,
    pub source: Option<ExpectedRowSource>,
}

#[derive(Default)]
pub struct ActiveFile {
    file: Option<OnScreen>,
}

struct Holder(Entity<ActiveFile>);
impl Global for Holder {}

impl ActiveFile {
    pub fn global(cx: &mut App) -> Entity<Self> {
        if let Some(holder) = cx.try_global::<Holder>() {
            return holder.0.clone();
        }
        let active = cx.new(|_| Self::default());
        cx.set_global(Holder(active.clone()));
        active
    }

    pub fn file(&self) -> Option<&OnScreen> {
        self.file.as_ref()
    }

    /// `path` in `session` is the file on screen.
    pub fn set(session: &str, path: &str, source: Option<ExpectedRowSource>, cx: &mut App) {
        let file = OnScreen {
            session: session.to_string(),
            path: path.to_string(),
            source,
        };
        Self::global(cx).update(cx, |this, cx| {
            if this.file.as_ref() != Some(&file) {
                this.file = Some(file);
                cx.notify();
            }
        });
    }

    /// `path` in `session` left the screen. A file that has already taken its place stays.
    pub fn clear(session: &str, path: &str, cx: &mut App) {
        let Some(active) = cx.try_global::<Holder>().map(|holder| holder.0.clone()) else {
            return;
        };
        active.update(cx, |this, cx| {
            if this
                .file
                .as_ref()
                .is_some_and(|file| file.session == session && file.path == path)
            {
                this.file = None;
                cx.notify();
            }
        });
    }
}
