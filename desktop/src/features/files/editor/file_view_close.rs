//! Explicit close keeps the tab until the chosen save is confirmed; cancel and stale discard preserve the draft.
use super::FileView;
use crate::core::ui::center_panes::CenterPanes;
use crate::features::files::document_store::DocumentStore;
use gpui_kit::*;

impl FileView {
    pub(super) fn request_close(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        if self.saving.is_some() || self.prompting.is_some() {
            return;
        }
        let Some(snapshot) = self.document_snapshot(cx) else {
            self.remove_file(window.window_handle(), cx);
            return;
        };
        if !cx.global::<DocumentStore>().is_dirty(&snapshot.key) {
            self.remove_file(window.window_handle(), cx);
            return;
        }
        let handle = window.window_handle();
        let answer = window.prompt(
            PromptLevel::Warning,
            "Save changes before closing?",
            Some(&self.path),
            &["Save", "Discard", "Cancel"],
            cx,
        );
        self.prompting = Some(cx.spawn(async move |view, cx| {
            let answer = answer.await;
            let _ = view.update(cx, |view, cx| {
                view.prompting = None;
                match answer {
                    Ok(0) if view.locked => {
                        view.editor_error = Some(super::lock::SAVE_LOCKED.into());
                    }
                    Ok(0) => {
                        view.closing_window = Some(handle);
                        view.save_document(cx);
                        if view.saving.is_none() { view.closing_window = None; }
                    }
                    Ok(1) => match cx.global_mut::<DocumentStore>().discard(&snapshot.key, snapshot.version) {
                        Ok(()) => view.remove_file(handle, cx),
                        Err(_) => { view.editor_error = Some("The draft changed during confirmation. Close it again to review the current version.".into()); }
                    },
                    _ => {},
                }
                cx.notify();
            });
        }));
    }

    pub(super) fn finish_close_after_save(&mut self, cx: &mut Context<Self>) {
        let Some(window) = self.closing_window.take() else {
            return;
        };
        if self
            .document
            .as_ref()
            .is_some_and(|key| cx.global::<DocumentStore>().is_dirty(key))
        {
            self.editor_error = Some("Newer edits remain unsaved. The file stays open.".into());
            return;
        }
        self.remove_file(window, cx);
    }

    fn remove_file(&self, window: AnyWindowHandle, cx: &mut Context<Self>) {
        let view = cx.entity();
        cx.defer(move |cx| {
            let _ = window.update(cx, |_, window, cx| {
                if let Some(dock) = CenterPanes::dock(cx) {
                    dock.update(cx, |dock, cx| dock.remove_panel(view, window, cx));
                }
            });
        });
    }
}
