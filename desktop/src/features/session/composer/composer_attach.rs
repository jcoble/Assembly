//! Attaching images to the draft: picked with the Attach button, dropped on the composer or pasted. Each png, jpeg, gif
//! or webp up to 20 MB shows as a chip and starts uploading at once (upload.rs); anything else is turned away with why,
//! above the box. Other dropped files go into the draft at the caret as their paths, which mean something only to an
//! agent on this computer. Send waits until each image has uploaded (composer_view.rs). Removing a chip drops its
//! upload, and one that failed says why on its chip.

use std::path::PathBuf;
use std::sync::Arc;

use gpui_kit::*;

use super::Composer;
use crate::features::session::composer::attachment::{Attachment, AttachmentState};
use crate::features::session::composer::upload::{UploadSource, size_of, upload_image};

impl Composer {
    /// Asks for images with the system's file picker.
    pub(super) fn pick_images(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        let picked = cx.prompt_for_paths(PathPromptOptions {
            files: true,
            directories: false,
            multiple: true,
            prompt: Some("Attach".into()),
        });
        self._tasks.retain(|task| !task.is_ready());
        self._tasks
            .push(cx.spawn_in(window, async move |composer, cx| {
                let Ok(Ok(Some(paths))) = picked.await else {
                    return;
                };
                composer
                    .update(cx, |composer, cx| composer.attach_paths(&paths, cx))
                    .ok();
            }));
    }

    /// Attaches each file, by its name's type and its size on disk.
    pub(super) fn attach_paths(&mut self, paths: &[PathBuf], cx: &mut Context<Self>) {
        for path in paths {
            let name = path
                .file_name()
                .unwrap_or(path.as_os_str())
                .to_string_lossy()
                .into_owned();
            let mime = Attachment::mime_of(&name).unwrap_or_default();
            let size = size_of(path).unwrap_or_default();
            self.attach(name, mime, size, UploadSource::Path(path.clone()), cx);
        }
    }

    /// Images attach; anything else goes into the draft at the caret.
    pub(super) fn dropped(
        &mut self,
        paths: &[PathBuf],
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        let (images, others): (Vec<PathBuf>, Vec<PathBuf>) = paths
            .iter()
            .cloned()
            .partition(|path| Attachment::mime_of(&path.to_string_lossy()).is_some());
        self.attach_paths(&images, cx);
        if others.is_empty() {
            return;
        }
        let others: Vec<String> = (others.iter())
            .map(|path| path.to_string_lossy().into_owned())
            .collect();
        let (text, caret) = {
            let draft = self.draft.read(cx);
            (draft.value().to_string(), draft.cursor())
        };
        let (text, caret) = Attachment::insert_at(&text, caret, &Attachment::paths_text(&others));
        self.draft.update(cx, |draft, cx| {
            draft.set_value(text, window, cx);
            draft.set_selected_range(caret..caret, cx);
            draft.focus(window, cx);
        });
        cx.notify();
    }

    /// A paste with an image attaches it and pastes no text; `false` leaves any other paste to the draft.
    pub(super) fn pasted(&mut self, item: &ClipboardItem, cx: &mut Context<Self>) -> bool {
        let mut taken = false;
        for entry in item.entries() {
            let ClipboardEntry::Image(image) = entry else {
                continue;
            };
            let (mime, extension) = match image.format {
                ImageFormat::Png => ("image/png", ".png"),
                ImageFormat::Jpeg => ("image/jpeg", ".jpg"),
                ImageFormat::Gif => ("image/gif", ".gif"),
                ImageFormat::Webp => ("image/webp", ".webp"),
                _ => ("", ""),
            };
            let bytes = UploadSource::Bytes(Arc::from(image.bytes.as_slice()));
            let name = format!("Pasted image{extension}");
            self.attach(name, mime, image.bytes.len() as u64, bytes, cx);
            taken = true;
        }
        taken
    }

    pub(super) fn remove_attachment(&mut self, id: u64, cx: &mut Context<Self>) {
        self.attachments.retain(|attachment| attachment.id != id);
        self.uploads.remove(&id);
        cx.notify();
    }

    /// Shows its chip and starts its upload, or says why it can't attach.
    fn attach(
        &mut self,
        name: String,
        mime: &'static str,
        size: u64,
        source: UploadSource,
        cx: &mut Context<Self>,
    ) {
        let Some(session) = self.session.clone() else {
            return;
        };
        if let Some(why) = Attachment::rejection(mime, size) {
            self.error = Some(format!("{name} is {why}.").into());
            cx.notify();
            return;
        }
        self.attached += 1;
        let id = self.attached;
        self.attachments.push(Attachment {
            id,
            name: name.into(),
            size,
            state: AttachmentState::Uploading,
        });
        let upload = upload_image(self.client.clone(), session, source, mime, cx);
        self.uploads.retain(|_, task| !task.is_ready());
        self.uploads.insert(
            id,
            cx.spawn(async move |composer, cx| {
                let saved = upload.await;
                composer
                    .update(cx, |composer, cx| {
                        let attachment = composer
                            .attachments
                            .iter_mut()
                            .find(|attachment| attachment.id == id);
                        if let Some(attachment) = attachment {
                            attachment.state = match saved {
                                Ok(path) => AttachmentState::Saved { path },
                                Err(error) => AttachmentState::Failed(error.reason().into()),
                            };
                            cx.notify();
                        }
                    })
                    .ok();
            }),
        );
        cx.notify();
    }
}

#[cfg(test)]
#[path = "composer_attach_tests.rs"]
mod tests;
