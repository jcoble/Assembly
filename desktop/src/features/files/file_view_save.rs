//! FileView's one sequential bulk save. Lost commit replies are reconciled, never replayed.
use super::FileView;
use crate::core::socket::{client::SocketClient, outcome::Outcome, pending_call::PendingCall};
use crate::features::files::{
    document_store::DocumentStore,
    file_save::{FileSaveController, SaveState},
};
use base64::Engine as _;
use gpui_kit::*;
use serde::Deserialize;
use serde_json::{Value, json};

#[derive(Deserialize)]
pub(super) struct Begun {
    upload: String,
}
#[derive(Deserialize)]
struct Reply {
    state: String,
    revision: Option<String>,
    error: Option<Fault>,
}
#[derive(Deserialize)]
struct Fault {
    message: String,
}

impl FileView {
    pub(super) fn save_document(&mut self, cx: &mut Context<Self>) {
        self.save_document_at(None, cx);
    }

    pub(super) fn save_document_at(&mut self, revision: Option<String>, cx: &mut Context<Self>) {
        if self.saving.is_some() {
            return;
        }
        if revision.is_none()
            && self
                .save
                .as_ref()
                .is_some_and(|save| save.state == SaveState::Unknown)
        {
            self.reconcile_save(cx);
            return;
        }
        let (Some(key), Some(socket)) = (&self.document, SocketClient::try_bulk(cx)) else {
            return;
        };
        let Ok(mut snapshot) = cx.global::<DocumentStore>().snapshot(key) else {
            return;
        };
        if let Some(revision) = revision {
            snapshot.base_revision = revision;
        }
        let args = json!({ "session": self.session, "path": self.path,
            "expectedRevision": snapshot.base_revision, "size": snapshot.text.len() });
        let bytes = snapshot.text.clone().into_bytes();
        self.save = Some(FileSaveController::begin(snapshot));
        let begin = socket
            .read(cx)
            .call::<Begun>("files", "beginSave", args, None);
        self.saving = Some(cx.spawn(async move |view, cx| {
            let upload = match upload_draft(&view, cx, &socket, begin, &bytes).await {
                Ok(upload) => upload,
                Err(message) => {
                    let _ = view.update(cx, |view, cx| {
                        view.drop_upload(cx);
                        view.save_failed(message, false, cx);
                    });
                    return;
                }
            };
            let Ok(call) = view.update(cx, |_, cx| {
                socket.read(cx).call::<Reply>(
                    "files",
                    "commitSave",
                    json!({ "upload": upload }),
                    None,
                )
            }) else {
                return;
            };
            let result = call.await;
            let _ = view.update(cx, |view, cx| {
                view.upload = None;
                view.saving = None;
                match result {
                    Outcome::Ok(reply) if reply.state == "saved" && reply.revision.is_some() => {
                        if let Some(save) = &mut view.save {
                            view.last_save = save.finish_confirmed(
                                reply.revision.expect("confirmed revision"),
                                cx.global_mut::<DocumentStore>(),
                            );
                        }
                        view.editor_error = None;
                    }
                    Outcome::Ok(reply) => {
                        if let Some(save) = &mut view.save {
                            save.state = if reply.state == "conflict" {
                                SaveState::Conflict
                            } else {
                                SaveState::Failed
                            };
                            save.captured.text.clear();
                            save.captured.text.shrink_to_fit();
                        }
                        view.editor_error = Some(reply.error.map_or_else(
                            || "The file was not saved; the draft is preserved.".into(),
                            |error| error.message,
                        ));
                    }
                    Outcome::Failed(error) => view.save_failed(error.message, true, cx),
                    Outcome::Aborted => view.save_failed(
                        "The save result is unknown. Check the file before saving again.".into(),
                        true,
                        cx,
                    ),
                }
                cx.notify();
            });
        }));
        cx.notify();
    }

    fn save_failed(&mut self, message: String, unknown: bool, cx: &mut Context<Self>) {
        self.saving = None;
        if let Some(save) = &mut self.save {
            if unknown {
                save.unknown();
            } else {
                save.state = SaveState::Failed;
                save.captured.text.clear();
                save.captured.text.shrink_to_fit();
            }
        }
        self.editor_error = Some(message);
        cx.notify();
    }

    pub(super) fn drop_upload(&mut self, cx: &mut Context<Self>) {
        if let (Some(upload), Some(socket)) = (self.upload.take(), SocketClient::try_bulk(cx)) {
            let call = socket.read(cx).call::<Value>(
                "files",
                "dropSave",
                json!({ "upload": upload }),
                None,
            );
            call.ignore_reply();
        }
    }

    fn reconcile_save(&mut self, cx: &mut Context<Self>) {
        let Some(socket) = SocketClient::try_bulk(cx) else {
            return;
        };
        let session = self.session.clone();
        let path = self.path.clone();
        self.saving = Some(cx.spawn(async move |view, cx| {
            let mut content = String::new();
            let mut offset = 0;
            let mut revision: Option<String> = None;
            loop {
                let args = json!({ "session": session, "path": path, "offset": offset, "revision": revision });
                let Ok(call) = view.update(cx, |_, cx| socket.read(cx).call::<crate::features::files::entry::FileChunk>("files", "read", args, None)) else { return; };
                let chunk = match call.await {
                    Outcome::Ok(chunk) if !chunk.binary && revision.as_ref().is_none_or(|r| r == &chunk.revision) => chunk,
                    _ => { let _ = view.update(cx, |view, cx| view.save_failed("The save is still unknown; the file could not be checked.".into(), true, cx)); return; }
                };
                content.push_str(&chunk.content);
                revision = Some(chunk.revision);
                if let Some(next) = chunk.next_offset { offset = next; } else { break; }
            }
            let _ = view.update(cx, |view, cx| {
                view.saving = None;
                if let Some(save) = &mut view.save {
                    view.last_save = save.reconcile(&content, revision.expect("complete read"), cx.global_mut::<DocumentStore>());
                    view.editor_error = if view.last_save.is_some() { None } else {
                        Some("The disk differs from the sent draft. The save remains unknown; compare or reload before another write.".into())
                    };
                }
                cx.notify();
            });
        }));
    }
}

/// Save and Compare use the same sequential bounded transfer and the view owns its returned upload.
pub(super) async fn upload_draft(
    view: &WeakEntity<FileView>,
    cx: &mut AsyncApp,
    socket: &Entity<SocketClient>,
    begin: PendingCall<Begun>,
    bytes: &[u8],
) -> Result<String, String> {
    let upload = match begin.await {
        Outcome::Ok(begun) => begun.upload,
        Outcome::Failed(error) => return Err(error.message),
        Outcome::Aborted => return Err("The upload could not be started.".into()),
    };
    if view
        .update(cx, |view, _| view.upload = Some(upload.clone()))
        .is_err()
    {
        let _ = cx.update(|cx| {
            socket
                .read(cx)
                .call::<Value>("files", "dropSave", json!({ "upload": upload }), None)
                .ignore_reply()
        });
        return Err("The file view was closed.".into());
    }
    for (part, chunk) in bytes.chunks(512 * 1024).enumerate() {
        let args = json!({ "upload": upload, "offset": part * 512 * 1024,
            "data": base64::engine::general_purpose::STANDARD.encode(chunk) });
        let call = view
            .update(cx, |_, cx| {
                socket
                    .read(cx)
                    .call::<Value>("files", "saveChunk", args, None)
            })
            .map_err(|_| "The file view was closed.".to_string())?;
        match call.await {
            Outcome::Ok(_) => {}
            Outcome::Failed(error) => return Err(error.message),
            Outcome::Aborted => return Err("The upload was interrupted.".into()),
        }
    }
    Ok(upload)
}
