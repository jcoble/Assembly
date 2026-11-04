//! The existing Composer can own an unsent start draft without any session authority.
//! The form alone owns create/byClient/checkout lifecycle; these are local input facts.
use super::super::upload::UploadSource;
use super::{Attachment, AttachmentState, Composer, ComposerEvent};
use gpui_kit::*;
use std::sync::Arc;
#[derive(Clone)]
pub struct StagedAttachment {
    name: SharedString,
    mime: &'static str,
    image: Arc<Image>,
}
impl StagedAttachment {
    pub fn name(&self) -> &SharedString {
        &self.name
    }
    pub fn mime(&self) -> &'static str {
        self.mime
    }
    pub fn image(&self) -> &Arc<Image> {
        &self.image
    }
    pub fn size(&self) -> u64 {
        self.image.bytes.len() as u64
    }
    pub(super) fn of(
        name: SharedString,
        mime: &'static str,
        image: Arc<Image>,
    ) -> Result<Self, String> {
        if image.bytes.is_empty() {
            return Err("The selected image is empty.".into());
        }
        if let Some(reason) = Attachment::rejection(mime, image.bytes.len() as u64) {
            return Err(format!("{name} is {reason}."));
        }
        Ok(Self { name, mime, image })
    }
}
pub(super) struct StartDraft {
    pub(super) ready: bool,
    pub(super) busy: bool,
}
/// A new session's own controls in the shared box. The form owns what they do; the composer only places them.
#[derive(Clone)]
pub struct StartSlots {
    /// Who the message goes to, for the draft's hint.
    pub provider: std::rc::Rc<dyn Fn(&App) -> Option<SharedString>>,
    /// Ahead of the settings: the agent picker.
    pub footer: std::rc::Rc<dyn Fn(&mut Window, &mut App) -> AnyElement>,
    /// The settings pills; the argument says the composer is narrow enough to fold them.
    pub settings: std::rc::Rc<dyn Fn(bool, &mut Window, &mut App) -> AnyElement>,
    /// Directly below the box: the machine and checkout.
    pub sub_bar: std::rc::Rc<dyn Fn(&mut Window, &mut App) -> AnyElement>,
}
impl Composer {
    pub fn new_start_draft(
        client: std::rc::Rc<dyn crate::features::session::client::session_client::SessionClient>,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) -> Self {
        let mut composer = Self::new(client, window, cx);
        composer.start_draft = Some(StartDraft {
            ready: false,
            busy: false,
        });
        composer
    }
    /// Presentation of the form's actual create/checkout state; performs no backend work.
    pub fn set_start_state(
        &mut self,
        ready: bool,
        busy: bool,
        error: Option<String>,
        cx: &mut Context<Self>,
    ) {
        let Some(start) = self.start_draft.as_mut() else {
            return;
        };
        start.ready = ready;
        start.busy = busy;
        self.error = error.map(Into::into);
        cx.notify();
    }
    pub fn set_start_slots(&mut self, slots: StartSlots, cx: &mut Context<Self>) {
        self.start_slots = Some(slots);
        cx.notify();
    }
    pub fn focus_input(&self, window: &mut Window, cx: &mut Context<Self>) {
        self.draft.read(cx).focus_handle(cx).focus(window, cx);
    }
    pub(super) fn start_busy(&self) -> bool {
        self.start_draft.as_ref().is_some_and(|start| start.busy)
    }
    pub(super) fn start_ready(&self) -> bool {
        self.start_draft
            .as_ref()
            .is_some_and(|start| start.ready && !start.busy)
            && self
                .attachments
                .iter()
                .all(|attachment| matches!(attachment.state, AttachmentState::Staged { .. }))
    }
    pub(super) fn send_start(&mut self, cx: &mut Context<Self>) {
        if !self.start_ready() {
            return;
        }
        let text = self.draft.read(cx).value().trim().to_string();
        let images = self
            .attachments
            .iter()
            .filter_map(|attachment| match &attachment.state {
                AttachmentState::Staged { image, mime } => Some(StagedAttachment {
                    name: attachment.name.clone(),
                    mime,
                    image: image.clone(),
                }),
                _ => None,
            })
            .collect::<Vec<_>>();
        if text.is_empty() && images.is_empty() {
            return;
        }
        self.start_draft.as_mut().expect("start draft").busy = true;
        cx.emit(ComposerEvent::StartRequested { text, images });
        cx.notify();
    }
    pub(super) fn stage_image(
        &mut self,
        name: SharedString,
        mime: &'static str,
        image: Arc<Image>,
        cx: &mut Context<Self>,
    ) {
        if self.start_draft.is_none() || self.start_busy() {
            return;
        }
        match StagedAttachment::of(name, mime, image) {
            Ok(image) => {
                let size = image.size();
                self.attached += 1;
                self.attachments.push(Attachment {
                    id: self.attached,
                    name: image.name,
                    size,
                    state: AttachmentState::Staged {
                        image: image.image,
                        mime: image.mime,
                    },
                    preview: None,
                });
            }
            Err(error) => self.error = Some(error.into()),
        }
        cx.notify();
    }
    pub(super) fn stage_source(
        &mut self,
        name: String,
        mime: &'static str,
        size: u64,
        source: UploadSource,
        cx: &mut Context<Self>,
    ) {
        if self.start_busy() {
            return;
        }
        if let Some(reason) = Attachment::rejection(mime, size) {
            self.error = Some(format!("{name} is {reason}.").into());
            cx.notify();
            return;
        }
        self.attached += 1;
        let id = self.attached;
        let name: SharedString = name.into();
        self.attachments.push(Attachment {
            id,
            name: name.clone(),
            size,
            state: AttachmentState::Uploading,
            preview: None,
        });
        let read = cx.background_spawn(async move {
            use std::io::Read as _;
            let bytes = match source {
                UploadSource::Path(path) => {
                    let mut bytes = Vec::new();
                    std::fs::File::open(path)
                        .and_then(|file| {
                            file.take(super::super::attachment::MAX_BYTES + 1)
                                .read_to_end(&mut bytes)
                        })
                        .map_err(|error| error.to_string())?;
                    bytes
                }
                UploadSource::Bytes(bytes) => bytes.to_vec(),
                UploadSource::Image(image) => return StagedAttachment::of(name, mime, image),
            };
            let format = match mime {
                "image/png" => ImageFormat::Png,
                "image/jpeg" => ImageFormat::Jpeg,
                "image/gif" => ImageFormat::Gif,
                "image/webp" => ImageFormat::Webp,
                _ => return Err("Not a supported image.".into()),
            };
            StagedAttachment::of(name, mime, Arc::new(Image::from_bytes(format, bytes)))
        });
        self.uploads.insert(
            id,
            cx.spawn(async move |composer, cx| {
                let image = read.await;
                let _ = composer.update(cx, |composer, cx| {
                    if composer.start_draft.is_none() {
                        return;
                    }
                    if let Some(attachment) = composer
                        .attachments
                        .iter_mut()
                        .find(|attachment| attachment.id == id)
                    {
                        match image {
                            Ok(image) => {
                                attachment.size = image.size();
                                attachment.state = AttachmentState::Staged {
                                    image: image.image,
                                    mime: image.mime,
                                };
                            }
                            Err(error) => attachment.state = AttachmentState::Failed(error.into()),
                        }
                    }
                    composer.uploads.remove(&id);
                    cx.notify();
                });
            }),
        );
        cx.notify();
    }
}
