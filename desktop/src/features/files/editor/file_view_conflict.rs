//! Explicit draft-loss confirmation and revision-safe reload; the old draft survives a failed or stale disk read.
use super::FileView;
use super::saving::{Begun, upload_draft};
use crate::core::socket::{client::SocketClient, outcome::Outcome};
use crate::features::files::document_store::DocumentStore;
use crate::features::files::file_comparison::{ComparedFile, FileComparison};
use gpui_kit::*;
use serde_json::json;

impl FileView {
    pub(super) fn compare_document(&mut self, cx: &mut Context<Self>) {
        // Compare stages the draft beside the file, so a locked file doesn't.
        if self.saving.is_some() || self.locked {
            return;
        }
        let (Some(snapshot), Some(socket)) =
            (self.document_snapshot(cx), SocketClient::try_bulk(cx))
        else {
            return;
        };
        self.comparison = None;
        let begin = socket.read(cx).call::<Begun>(
            "files",
            "beginSave",
            self.file_args(json!({
            "expectedRevision": snapshot.base_revision, "size": snapshot.text.len() })),
            None,
        );
        self.saving = Some(cx.spawn(async move |view, cx| {
            let upload =
                match upload_draft(&view, cx, &socket, begin, snapshot.text.as_bytes()).await {
                    Ok(upload) => upload,
                    Err(message) => {
                        let _ = view.update(cx, |view, cx| {
                            view.saving = None;
                            view.drop_upload(cx);
                            view.editor_error = Some(message);
                            cx.notify();
                        });
                        return;
                    }
                };
            let Ok(call) = view.update(cx, |_, cx| {
                socket.read(cx).call::<ComparedFile>(
                    "files",
                    "compare",
                    json!({ "upload": upload }),
                    None,
                )
            }) else {
                return;
            };
            let answer = call.await;
            let _ = view.update(cx, |view, cx| {
                view.saving = None;
                view.drop_upload(cx);
                match answer {
                    Outcome::Ok(read) => {
                        view.comparison =
                            Some(cx.new(|cx| FileComparison::new(snapshot.version, read, cx)));
                        view.editor_error = None;
                    }
                    Outcome::Failed(error) => view.editor_error = Some(error.message),
                    Outcome::Aborted => {
                        view.editor_error =
                            Some("Comparison was interrupted; your draft is preserved.".into())
                    }
                }
                cx.notify();
            });
        }));
        cx.notify();
    }

    pub(super) fn request_overwrite(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        if self.saving.is_some() || self.prompting.is_some() {
            return;
        }
        let (Some(snapshot), Some(comparison)) = (self.document_snapshot(cx), &self.comparison)
        else {
            return;
        };
        let revision = match comparison
            .read(cx)
            .comparison
            .overwrite_revision(snapshot.version)
        {
            Ok(revision) => revision.to_string(),
            Err(message) => {
                self.editor_error = Some(message.into());
                cx.notify();
                return;
            }
        };
        let answer = window.prompt(PromptLevel::Warning, "Overwrite the file on disk?",
            Some("Save the compared draft over the observed disk version. Another disk change will still cause a conflict."), &["Overwrite", "Cancel"], cx);
        self.prompting = Some(cx.spawn(async move |view, cx| {
            let answer = answer.await;
            let _ = view.update(cx, |view, cx| {
                view.prompting = None;
                if matches!(answer, Ok(0)) {
                    if view
                        .document_snapshot(cx)
                        .is_some_and(|current| current.version == snapshot.version)
                    {
                        view.save_document_at(Some(revision), cx);
                    } else {
                        view.editor_error =
                            Some("The draft changed during confirmation. Compare it again.".into());
                        cx.notify();
                    }
                }
            });
        }));
    }

    pub(super) fn request_reload(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        if self.saving.is_some() || self.prompting.is_some() {
            return;
        }
        let Some(key) = &self.document else {
            return;
        };
        let Ok(snapshot) = cx.global::<DocumentStore>().snapshot(key) else {
            return;
        };
        let dirty = cx.global::<DocumentStore>().is_dirty(key);
        if !dirty {
            self.begin_reload(snapshot.version, cx);
            return;
        }
        let answer = window.prompt(
            PromptLevel::Warning,
            "Reload this file from disk?",
            Some(&format!("Discard the unsaved draft for {}?", self.path)),
            &["Reload", "Cancel"],
            cx,
        );
        self.prompting = Some(cx.spawn(async move |view, cx| {
            let answer = answer.await;
            let _ = view.update(cx, |view, cx| {
                view.prompting = None;
                if matches!(answer, Ok(0)) {
                    view.begin_reload(snapshot.version, cx);
                }
            });
        }));
    }

    fn begin_reload(&mut self, version: u64, cx: &mut Context<Self>) {
        self.stash_editor_view(cx);
        self.loading = None;
        self.release_read(cx);
        self.editor_events = None;
        self.editor = None;
        self.reload_version = Some(version);
        self.editor_error = None;
        self.text.start();
        self.load_owner(cx);
        cx.notify();
    }
}
