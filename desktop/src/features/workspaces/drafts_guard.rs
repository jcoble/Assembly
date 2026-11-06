//! Save/Discard/Cancel is sequential and owned by the transition that would release these drafts: a workspace's own
//! drafts when it's left, and the drafts in outside tabs no workspace holds, which close unsaved when a session opens
//! or the window closes.
use crate::core::state::workspace_panes::{WorkspaceKey, WorkspacePanes};
use crate::core::ui::center_panes::CenterPanes;
use crate::features::files::document_store::{DocumentKey, DocumentStore};
use futures_channel::oneshot;
use gpui_kit::*;
use std::cell::{Cell, RefCell};
use std::rc::Rc;

/// The drafts a guard asks about.
#[derive(Clone)]
pub enum Drafts {
    /// A workspace's checkout drafts.
    Checkout(WorkspaceKey),
    /// The drafts in outside tabs opened with no workspace: nothing saves them, so they'd go with their tabs.
    Unowned,
}

impl Drafts {
    /// What the prompt asks: a workspace that couldn't be saved, or a file nothing will save.
    fn question(&self) -> &'static str {
        match self {
            Self::Checkout(_) => {
                "This workspace could not be saved. Keep the draft before leaving?"
            }
            Self::Unowned => "This file has unsaved changes. Save them before leaving?",
        }
    }

    /// The dirty documents it covers, now.
    pub fn keys(&self, cx: &App) -> Vec<DocumentKey> {
        let Some(docs) = cx.try_global::<DocumentStore>() else {
            return Vec::new();
        };
        match self {
            Self::Checkout(key) => docs.dirty_keys(&key.machine, &key.checkout_root),
            Self::Unowned if WorkspacePanes::key(cx).is_some() => Vec::new(),
            Self::Unowned => {
                let mut keys: Vec<_> = CenterPanes::file_views(cx)
                    .into_iter()
                    .filter_map(|view| {
                        let view = view.read(cx);
                        view.is_outside()
                            .then(|| view.document_key().cloned())
                            .flatten()
                    })
                    .filter(|document| docs.is_dirty(document))
                    .collect();
                keys.sort_by_key(DocumentKey::identity);
                keys.dedup();
                keys
            }
        }
    }
}

/// Closing the window, which quits the app, first asks about the drafts nothing saves, with the same prompt; the window
/// closes once each is saved or discarded, and stays on Cancel. The window's close handler holds the one asking task.
pub fn guard_close(window: &mut Window, cx: &mut App) {
    let asking = Rc::new(Cell::new(false));
    let held: Rc<RefCell<Option<Task<()>>>> = Rc::default();
    window.on_window_should_close(cx, move |window, cx| {
        if Drafts::Unowned.keys(cx).is_empty() {
            return true;
        }
        if asking.replace(true) {
            return false;
        }
        let guard = run(Drafts::Unowned, window, cx);
        let (handle, asking) = (window.window_handle(), asking.clone());
        // Only a finished task is replaced here, since a new one starts only once the last has answered.
        *held.borrow_mut() = Some(cx.spawn(async move |cx| {
            let safe = guard.await;
            asking.set(false);
            if safe {
                let _ = handle.update(cx, |_, window, _| window.remove_window());
            }
        }));
        false
    });
}

pub fn run(drafts: Drafts, window: &mut Window, cx: &mut App) -> Task<bool> {
    let (keys, question) = (drafts.keys(cx), drafts.question());
    let handle = window.window_handle();
    cx.spawn(async move |cx| {
        for document in keys {
            let version = cx.update(|cx| {
                cx.try_global::<DocumentStore>()
                    .and_then(|docs| docs.version(&document))
            });
            let Some(version) = version else {
                continue;
            };
            let prompt = handle.update(cx, |_, window, cx| {
                window.prompt(
                    PromptLevel::Warning,
                    question,
                    Some(&document.path),
                    &["Save to file", "Discard", "Cancel"],
                    cx,
                )
            });
            let Ok(prompt) = prompt else {
                return false;
            };
            match prompt.await {
                Ok(0) => {
                    let (answer, saved) = oneshot::channel();
                    let started = cx.update(|cx| {
                        let view = CenterPanes::file_views(cx)
                            .into_iter()
                            .find(|view| view.read(cx).document_key() == Some(&document));
                        if let Some(view) = view {
                            view.update(cx, |view, cx| view.save_for_guard(answer, cx));
                            true
                        } else {
                            false
                        }
                    });
                    if !started || !saved.await.unwrap_or(false) {
                        return false;
                    }
                }
                Ok(1) => {
                    let discarded = cx.update(|cx| {
                        let view = CenterPanes::file_views(cx)
                            .into_iter()
                            .find(|view| view.read(cx).document_key() == Some(&document));
                        if let Some(view) = view {
                            view.update(cx, |view, cx| view.discard_for_guard(version, cx))
                        } else {
                            cx.global_mut::<DocumentStore>()
                                .discard(&document, version)
                                .is_ok()
                        }
                    });
                    if !discarded {
                        return false;
                    }
                }
                _ => return false,
            }
        }
        cx.update(|cx| drafts.keys(cx).is_empty())
    })
}

#[cfg(test)]
#[path = "drafts_guard_tests.rs"]
mod tests;
