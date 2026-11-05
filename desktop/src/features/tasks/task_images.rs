//! Pictures on a task's page: a screenshot pasted anywhere on it, or an image file picked, goes to the end of the page.
//! Pictures go one at a time, in the order they came. The view owns the task sending them, so leaving the page cancels
//! the call in flight and the backend drops the upload with it.

use std::collections::VecDeque;
use std::path::{Path, PathBuf};
use std::rc::Rc;
use std::sync::Arc;

use base64::Engine as _;
use base64::engine::general_purpose::STANDARD;
use gpui_kit::*;

use crate::inline_field::SaveFailure;

use super::super::notion_client::NotionClient;
use super::super::task_edit::{AfterEdit, edit_outcome};
use super::super::task_export::*;
use super::super::tasks_state::error_text;
use super::{TaskDetailEvent, TaskDetailView};

/// NotionImageUpload's limit.
const MAX_BYTES: usize = 20 * 1024 * 1024;
const WRONG_KIND: &str = "Notion takes PNG, JPEG, GIF or WebP pictures.";
const TOO_LARGE: &str = "That picture is over Notion's 20 MB limit.";

#[derive(Clone, Debug, PartialEq, Eq)]
pub(super) struct TaskImage {
    mime: &'static str,
    bytes: Arc<[u8]>,
}

/// The backend's upload calls.
pub(super) trait ImageCalls {
    fn begin(
        &self,
        source: NotionTaskRef,
        mime: String,
        size: i64,
        cx: &mut App,
    ) -> Task<Result<NotionImageLease, NotionError>>;
    fn chunk(
        &self,
        upload_id: String,
        offset: i64,
        data: String,
        cx: &mut App,
    ) -> Task<Result<NotionEmpty, NotionError>>;
    fn commit(
        &self,
        upload_id: String,
        cx: &mut App,
    ) -> Task<Result<NotionEditReceipt, NotionError>>;
    fn abandon(&self, upload_id: String, cx: &mut App) -> Task<Result<NotionEmpty, NotionError>>;
}

impl ImageCalls for NotionClient {
    fn begin(
        &self,
        source: NotionTaskRef,
        mime: String,
        size: i64,
        cx: &mut App,
    ) -> Task<Result<NotionImageLease, NotionError>> {
        self.image_begin(source, mime, size, cx)
    }
    fn chunk(
        &self,
        upload_id: String,
        offset: i64,
        data: String,
        cx: &mut App,
    ) -> Task<Result<NotionEmpty, NotionError>> {
        self.image_chunk(upload_id, offset, data, cx)
    }
    fn commit(
        &self,
        upload_id: String,
        cx: &mut App,
    ) -> Task<Result<NotionEditReceipt, NotionError>> {
        self.image_commit(upload_id, cx)
    }
    fn abandon(&self, upload_id: String, cx: &mut App) -> Task<Result<NotionEmpty, NotionError>> {
        self.image_drop(upload_id, cx)
    }
}

/// Pictures waiting for the page, and what became of the last one.
pub(super) struct ImageQueue {
    pub(super) calls: Rc<dyn ImageCalls>,
    waiting: VecDeque<TaskImage>,
    sending: Option<Task<()>>,
    reading: Vec<Task<()>>,
    pub(super) failure: Option<String>,
    /// The next read of the page shows a picture just added, so it scrolls there.
    reveal_end: bool,
}

impl Default for ImageQueue {
    fn default() -> Self {
        Self {
            calls: Rc::new(NotionClient),
            waiting: VecDeque::new(),
            sending: None,
            reading: Vec::new(),
            failure: None,
            reveal_end: false,
        }
    }
}

impl ImageQueue {
    pub(super) fn sending(&self) -> bool {
        self.sending.is_some()
    }
    /// Whether the page just read should scroll to the picture at its end.
    pub(super) fn take_reveal(&mut self) -> bool {
        std::mem::take(&mut self.reveal_end)
    }
}

fn mime_of(format: ImageFormat) -> Option<&'static str> {
    match format {
        ImageFormat::Png => Some("image/png"),
        ImageFormat::Jpeg => Some("image/jpeg"),
        ImageFormat::Gif => Some("image/gif"),
        ImageFormat::Webp => Some("image/webp"),
        _ => None,
    }
}

fn mime_of_path(path: &Path) -> Option<&'static str> {
    match path.extension()?.to_str()?.to_ascii_lowercase().as_str() {
        "png" => Some("image/png"),
        "jpg" | "jpeg" => Some("image/jpeg"),
        "gif" => Some("image/gif"),
        "webp" => Some("image/webp"),
        _ => None,
    }
}

fn accepted(mime: Option<&'static str>, bytes: Arc<[u8]>) -> Result<TaskImage, String> {
    let mime = mime.ok_or(WRONG_KIND)?;
    if bytes.is_empty() {
        return Err(WRONG_KIND.into());
    }
    if bytes.len() > MAX_BYTES {
        return Err(TOO_LARGE.into());
    }
    Ok(TaskImage { mime, bytes })
}

/// How far a picture got.
enum Sent {
    /// Nothing reached the page.
    NotSent(NotionError),
    /// The commit's answer, which may leave the outcome unknown.
    Committed(Result<NotionEditReceipt, NotionError>),
}

async fn send(
    calls: Rc<dyn ImageCalls>,
    source: NotionTaskRef,
    image: TaskImage,
    cx: &mut AsyncApp,
) -> Sent {
    let size = image.bytes.len() as i64;
    let begin = cx.update(|cx| calls.begin(source, image.mime.into(), size, cx));
    let lease = match begin.await {
        Ok(lease) => lease,
        Err(error) => return Sent::NotSent(error),
    };
    let mut offset = 0;
    for chunk in image.bytes.chunks(lease.chunk_bytes.max(1) as usize) {
        let call = cx.update(|cx| {
            let data = STANDARD.encode(chunk);
            calls.chunk(lease.upload_id.clone(), offset, data, cx)
        });
        if let Err(error) = call.await {
            let abandon = cx.update(|cx| calls.abandon(lease.upload_id.clone(), cx));
            let _ = abandon.await;
            return Sent::NotSent(error);
        }
        offset += chunk.len() as i64;
    }
    let commit = cx.update(|cx| calls.commit(lease.upload_id.clone(), cx));
    Sent::Committed(commit.await)
}

impl TaskDetailView {
    /// A paste with a picture in it adds the picture and pastes nothing else; `false` leaves it to whatever has focus.
    pub(super) fn paste_item(
        &mut self,
        item: &ClipboardItem,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) -> bool {
        let mut taken = false;
        for entry in item.entries() {
            match entry {
                ClipboardEntry::Image(image) => {
                    taken = true;
                    let bytes = Arc::from(image.bytes.as_slice());
                    self.take_image(accepted(mime_of(image.format), bytes), window, cx);
                }
                ClipboardEntry::ExternalPaths(paths) => {
                    let pictures: Vec<PathBuf> = paths
                        .paths()
                        .iter()
                        .filter(|path| mime_of_path(path).is_some())
                        .cloned()
                        .collect();
                    if !pictures.is_empty() {
                        taken = true;
                        self.read_files(pictures, window, cx);
                    }
                }
                _ => {}
            }
        }
        taken
    }

    pub(super) fn pick_images(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        let picked = cx.prompt_for_paths(PathPromptOptions {
            files: true,
            directories: false,
            multiple: true,
            prompt: Some("Add".into()),
        });
        self.images.reading.retain(|task| !task.is_ready());
        let task = cx.spawn_in(window, async move |view, cx| {
            let Ok(Ok(Some(paths))) = picked.await else {
                return;
            };
            let _ = view.update_in(cx, |view, window, cx| {
                view.read_files(paths, window, cx);
            });
        });
        self.images.reading.push(task);
    }

    /// Files are read off the main thread; each joins the queue in the order given.
    fn read_files(&mut self, paths: Vec<PathBuf>, window: &mut Window, cx: &mut Context<Self>) {
        let read = cx.background_spawn(async move {
            paths
                .into_iter()
                .map(|path| {
                    let mime = mime_of_path(&path);
                    match std::fs::metadata(&path) {
                        Ok(file) if file.len() > MAX_BYTES as u64 => Err(TOO_LARGE.to_owned()),
                        Ok(_) => std::fs::read(&path)
                            .map_err(|_| format!("Assembly couldn't read {}.", path.display()))
                            .and_then(|bytes| accepted(mime, Arc::from(bytes))),
                        Err(_) => Err(format!("Assembly couldn't read {}.", path.display())),
                    }
                })
                .collect::<Vec<_>>()
        });
        self.images.reading.retain(|task| !task.is_ready());
        let task = cx.spawn_in(window, async move |view, cx| {
            let pictures = read.await;
            let _ = view.update_in(cx, |view, window, cx| {
                for picture in pictures {
                    view.take_image(picture, window, cx);
                }
            });
        });
        self.images.reading.push(task);
    }

    fn take_image(
        &mut self,
        image: Result<TaskImage, String>,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        match image {
            Ok(image) => {
                self.images.failure = None;
                self.images.waiting.push_back(image);
                self.send_next(window, cx);
            }
            Err(reason) => self.images.failure = Some(reason),
        }
        cx.notify();
    }

    fn send_next(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        if self.images.sending() {
            return;
        }
        let Some(image) = self.images.waiting.pop_front() else {
            return;
        };
        let (calls, source) = (self.images.calls.clone(), self.source.clone());
        // Held before the task can answer: an answer that's already there still finds it sending.
        self.images.sending = Some(Task::ready(()));
        let task = cx.spawn_in(window, async move |view, cx| {
            let sent = send(calls, source, image, cx).await;
            let _ = view.update_in(cx, |view, window, cx| {
                view.image_finished(sent, window, cx);
            });
        });
        self.images.sending = Some(task);
    }

    fn image_finished(&mut self, sent: Sent, window: &mut Window, cx: &mut Context<Self>) {
        self.images.sending = None;
        let (landed, after, failure) = match sent {
            Sent::NotSent(error) => (false, AfterEdit::Nothing, Some(error_text(&error.code))),
            Sent::Committed(result) => {
                let (saved, after) = edit_outcome(result.as_ref());
                match saved {
                    Ok(()) => (true, after, None),
                    Err(SaveFailure::Refused(reason) | SaveFailure::Uncertain(reason)) => {
                        (false, after, Some(reason))
                    }
                }
            }
        };
        if failure.is_some() {
            self.images.failure = failure;
        }
        self.follow_up(after, cx);
        if landed {
            self.images.reveal_end = true;
            cx.emit(TaskDetailEvent::Reread);
        }
        self.send_next(window, cx);
        cx.notify();
    }
}

#[cfg(test)]
#[path = "task_images_tests.rs"]
mod tests;
