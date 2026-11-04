//! Save/Discard/Cancel is sequential and owned by the workspace transition that would release these drafts.
use crate::core::state::workspace_panes::WorkspaceKey;
use crate::core::ui::center_panes::CenterPanes;
use crate::features::files::document_store::DocumentStore;
use futures_channel::oneshot;
use gpui_kit::*;

pub fn run(key: WorkspaceKey, window: &mut Window, cx: &mut App) -> Task<bool> {
    let keys = cx
        .try_global::<DocumentStore>()
        .map(|docs| docs.dirty_keys(&key.machine, &key.checkout_root))
        .unwrap_or_default();
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
                    "This workspace could not be saved. Keep the draft before leaving?",
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
        cx.update(|cx| {
            cx.try_global::<DocumentStore>()
                .is_none_or(|docs| docs.dirty_keys(&key.machine, &key.checkout_root).is_empty())
        })
    })
}

#[cfg(test)]
#[path = "drafts_guard_tests.rs"]
mod tests;
