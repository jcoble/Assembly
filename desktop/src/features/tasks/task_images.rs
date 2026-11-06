//! Pictures on a task's page: a screenshot pasted anywhere on it, or an image file picked, goes to the end of the page.
//! Pictures go one at a time, in the order they came; a file is read only when its turn comes, so a batch isn't held in
//! memory. The view owns the task sending them. Leaving the page hands the view to the Tasks pane, which keeps it until
//! its pictures are on, and says if any didn't go.

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
const TOO_MANY: &str = "Too many pictures are waiting. Add the rest once these are on.";
/// Pictures waiting their turn, and the pasted ones' bytes held meanwhile.
const MOST_WAITING: usize = 20;
const MOST_WAITING_BYTES: usize = 100 * 1024 * 1024;

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

/// A picture waiting its turn: pasted bytes, or a file read when it's next.
enum Waiting {
    Picture(TaskImage),
    File(PathBuf),
}

/// Pictures waiting for the page, and the first that didn't go since the queue was last empty.
pub(super) struct ImageQueue {
    pub(super) calls: Rc<dyn ImageCalls>,
    waiting: VecDeque<Waiting>,
    sending: Option<Task<()>>,
    picking: Option<Task<()>>,
    pub(super) failure: Option<String>,
    /// How many more didn't go after the first.
    more_failures: usize,
    /// The next read of the page shows a picture just added, so it scrolls there.
    reveal_end: bool,
}

impl Default for ImageQueue {
    fn default() -> Self {
        Self {
            calls: Rc::new(NotionClient),
            waiting: VecDeque::new(),
            sending: None,
            picking: None,
            failure: None,
            more_failures: 0,
            reveal_end: false,
        }
    }
}

impl ImageQueue {
    pub(super) fn sending(&self) -> bool {
        self.sending.is_some()
    }
    /// Pictures still to reach the page.
    pub(super) fn pending(&self) -> bool {
        self.sending() || !self.waiting.is_empty()
    }
    /// What didn't go, the first reason and how many more.
    pub(super) fn failure_text(&self) -> Option<String> {
        let first = self.failure.as_ref()?;
        Some(match self.more_failures {
            0 => first.clone(),
            1 => format!("{first} One more picture didn't go on either."),
            more => format!("{first} {more} more pictures didn't go on either."),
        })
    }
    fn failed(&mut self, reason: String) {
        if self.failure.is_none() {
            self.failure = Some(reason);
        } else {
            self.more_failures += 1;
        }
    }
    /// A new batch starts with nothing failed; one added to a running batch keeps what's failed so far.
    fn start_batch(&mut self) {
        if !self.pending() {
            self.failure = None;
            self.more_failures = 0;
        }
    }
    fn join(&mut self, picture: Waiting) -> Result<(), &'static str> {
        let held: usize = self
            .waiting
            .iter()
            .map(|waiting| match waiting {
                Waiting::Picture(image) => image.bytes.len(),
                Waiting::File(_) => 0,
            })
            .sum();
        let adding = match &picture {
            Waiting::Picture(image) => image.bytes.len(),
            Waiting::File(_) => 0,
        };
        if self.waiting.len() >= MOST_WAITING || held + adding > MOST_WAITING_BYTES {
            return Err(TOO_MANY);
        }
        self.waiting.push_back(picture);
        Ok(())
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

/// Reads a picked or pasted file, off the main thread.
fn read_file(path: &Path) -> Result<TaskImage, String> {
    let unreadable = || format!("Assembly couldn't read {}.", path.display());
    let mime = mime_of_path(path);
    match std::fs::metadata(path) {
        Ok(file) if file.len() > MAX_BYTES as u64 => Err(TOO_LARGE.to_owned()),
        Ok(_) => std::fs::read(path)
            .map_err(|_| unreadable())
            .and_then(|bytes| accepted(mime, Arc::from(bytes))),
        Err(_) => Err(unreadable()),
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
    /// Its file couldn't be read, or isn't a picture Notion takes.
    Unread(String),
    /// Nothing reached the page.
    Failed(NotionError),
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
        Err(error) => return Sent::Failed(error),
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
            return Sent::Failed(error);
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
                    if !taken {
                        self.images.start_batch();
                    }
                    taken = true;
                    let bytes = Arc::from(image.bytes.as_slice());
                    match accepted(mime_of(image.format), bytes) {
                        Ok(image) => self.take(Waiting::Picture(image)),
                        Err(reason) => self.images.failed(reason),
                    }
                }
                ClipboardEntry::ExternalPaths(paths) => {
                    for path in paths
                        .paths()
                        .iter()
                        .filter(|path| mime_of_path(path).is_some())
                    {
                        if !taken {
                            self.images.start_batch();
                        }
                        taken = true;
                        self.take(Waiting::File(path.clone()));
                    }
                }
                _ => {}
            }
        }
        if taken {
            self.send_next(window, cx);
            cx.notify();
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
        self.images.picking = Some(cx.spawn_in(window, async move |view, cx| {
            let Ok(Ok(Some(paths))) = picked.await else {
                return;
            };
            let _ = view.update_in(cx, |view, window, cx| {
                view.images.start_batch();
                for path in paths {
                    view.take(Waiting::File(path));
                }
                view.send_next(window, cx);
                cx.notify();
            });
        }));
    }

    fn take(&mut self, picture: Waiting) {
        if let Err(reason) = self.images.join(picture) {
            self.images.failed(reason.to_owned());
        }
    }

    /// Whether pictures are still on their way to this page.
    pub fn pictures_pending(&self) -> bool {
        self.images.pending()
    }

    /// What didn't go on the page, once its pictures have settled.
    pub fn pictures_failure(&self) -> Option<String> {
        self.images.failure_text()
    }

    fn send_next(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        if self.images.sending() {
            return;
        }
        let Some(next) = self.images.waiting.pop_front() else {
            cx.emit(TaskDetailEvent::PicturesSettled);
            return;
        };
        let (calls, source) = (self.images.calls.clone(), self.source.clone());
        // Held before the task can answer: an answer that's already there still finds it sending.
        self.images.sending = Some(Task::ready(()));
        let task = cx.spawn_in(window, async move |view, cx| {
            let image = match next {
                Waiting::Picture(image) => Ok(image),
                Waiting::File(path) => cx.background_spawn(async move { read_file(&path) }).await,
            };
            let sent = match image {
                Ok(image) => send(calls, source, image, cx).await,
                Err(reason) => Sent::Unread(reason),
            };
            let _ = view.update_in(cx, |view, window, cx| {
                view.image_finished(sent, window, cx);
            });
        });
        self.images.sending = Some(task);
    }

    fn image_finished(&mut self, sent: Sent, window: &mut Window, cx: &mut Context<Self>) {
        self.images.sending = None;
        let (landed, after, failure) = match sent {
            Sent::Unread(reason) => (false, AfterEdit::Nothing, Some(reason)),
            Sent::Failed(error) => (false, AfterEdit::Nothing, Some(error_text(&error.code))),
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
        if let Some(reason) = failure {
            self.images.failed(reason);
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

/// A picture file for a test, removed when dropped. Here because the tests' file draws, and drawing files don't touch disk.
#[cfg(test)]
struct TestFile(PathBuf);

#[cfg(test)]
impl TestFile {
    fn new(bytes: &[u8]) -> Self {
        let path =
            std::env::temp_dir().join(format!("assembly-task-picture-{}.png", std::process::id()));
        std::fs::write(&path, bytes).expect("written");
        Self(path)
    }
}

#[cfg(test)]
impl Drop for TestFile {
    fn drop(&mut self) {
        let _ = std::fs::remove_file(&self.0);
    }
}

#[cfg(test)]
#[path = "task_images_tests.rs"]
mod tests;
