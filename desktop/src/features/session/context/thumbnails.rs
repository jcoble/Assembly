//! `Thumbnails` makes the Attachments card's small pictures of sent images: one queue per pane reads each full image
//! through `attachments/read`, one at a time, newest first, and scales it to a small picture on a background thread.
//! The full bytes drop as soon as it's scaled. It runs only while the tab shows: the pane clears it when the tab hides
//! or another session is picked, which cancels the queue and releases every picture, GPUI's copy in each window's
//! sprite atlas included. The pictures are GPUI `RenderImage`s made here, so GPUI's asset cache never holds them.
//! Feature 26's stored thumbnails replace the reads here and nothing else.

use std::collections::{HashMap, HashSet};
use std::rc::Rc;
use std::sync::Arc;

use gpui_kit::*;

use super::context_pane::ContextPane;
use crate::core::socket::row_source::ExpectedRowSource;
use crate::features::session::client::session_client::{CallError, SessionClient};
use crate::features::session::view::lightbox::read_image;

/// The longest edge of a thumbnail, in pixels.
const EDGE: u32 = 120;

pub struct Thumbnails {
    client: Rc<dyn SessionClient>,
    made: HashMap<String, Arc<RenderImage>>,
    /// Paths that failed or can't be read, so the queue doesn't try them again.
    skipped: HashSet<String>,
    /// The sent images' paths, newest first.
    wanted: Vec<String>,
    /// The session and source the wanted images are read from.
    scope: Option<(String, Option<ExpectedRowSource>)>,
    /// The queue is reading; it stops when nothing wanted is left.
    reading: bool,
    _queue: Option<Task<()>>,
}

/// One image the queue reads next.
struct Job {
    client: Rc<dyn SessionClient>,
    session: String,
    source: Option<ExpectedRowSource>,
    path: String,
}

impl Thumbnails {
    pub fn new(client: Rc<dyn SessionClient>) -> Self {
        Self {
            client,
            made: HashMap::new(),
            skipped: HashSet::new(),
            wanted: Vec::new(),
            scope: None,
            reading: false,
            _queue: None,
        }
    }

    pub fn get(&self, path: &str) -> Option<Arc<RenderImage>> {
        self.made.get(path).cloned()
    }

    /// Sets what's wanted, newest first, and starts one reader if none is going. A thumbnail whose path is no longer
    /// wanted is released; one still wanted is kept.
    pub fn want(
        &mut self,
        session: String,
        source: Option<ExpectedRowSource>,
        paths: Vec<String>,
        cx: &mut Context<ContextPane>,
    ) {
        let gone: Vec<String> = (self.made.keys())
            .filter(|path| !paths.contains(path))
            .cloned()
            .collect();
        for path in gone {
            if let Some(image) = self.made.remove(&path) {
                cx.drop_image(image, None);
            }
        }
        self.wanted = paths;
        self.scope = Some((session, source));
        if !self.reading {
            self.reading = true;
            self._queue = Some(cx.spawn(async move |pane, cx| run(pane, cx).await));
        }
    }

    /// Cancels the queue and releases every thumbnail, including GPUI's copy in each window's sprite atlas. `window` is
    /// the window being updated, if any, since GPUI holds that one apart from the others while it runs.
    pub fn clear(&mut self, mut window: Option<&mut Window>, cx: &mut App) {
        self._queue = None;
        self.reading = false;
        (self.wanted, self.scope) = (Vec::new(), None);
        self.skipped.clear();
        for (_, image) in self.made.drain() {
            cx.drop_image(image, window.as_deref_mut());
        }
    }

    /// The first wanted image with neither a thumbnail nor a skip, or `None` when the queue is done, which stops it.
    fn next(&mut self) -> Option<Job> {
        let path = self
            .wanted
            .iter()
            .find(|path| !self.made.contains_key(*path) && !self.skipped.contains(*path))
            .cloned();
        let (Some(path), Some((session, source))) = (path, self.scope.clone()) else {
            self.reading = false;
            return None;
        };
        Some(Job {
            client: self.client.clone(),
            session,
            source,
            path,
        })
    }

    /// Keeps a thumbnail, or skips its path for good after a failure. A changed source answers false, which stops the
    /// queue.
    fn take(&mut self, path: String, made: Result<RenderImage, CallError>) -> bool {
        match made {
            Ok(image) => {
                self.made.insert(path, Arc::new(image));
            }
            Err(CallError::Refused { code, .. }) if code == "source_changed" => {
                self.reading = false;
                return false;
            }
            Err(_) => {
                self.skipped.insert(path);
            }
        }
        true
    }
}

/// The queue: reads and scales one image at a time until nothing wanted is left, the pane drops it, or the source
/// changes.
async fn run(pane: WeakEntity<ContextPane>, cx: &mut AsyncApp) {
    loop {
        let job = pane.update(cx, |pane, _| {
            pane.thumbnails.as_mut().and_then(Thumbnails::next)
        });
        let Ok(Some(job)) = job else {
            return;
        };
        let file = (job.path.rsplit(['/', '\\']).next())
            .unwrap_or_default()
            .to_string();
        let made = match read_image(job.client, job.session, file, job.source, cx).await {
            Ok(image) => (cx.background_spawn(async move { scale(&image) }).await)
                .map_err(|error| CallError::Failed(error.to_string())),
            Err(error) => Err(error),
        };
        let going = pane.update(cx, |pane, cx| {
            let Some(thumbnails) = pane.thumbnails.as_mut() else {
                return false;
            };
            let going = thumbnails.take(job.path, made);
            match going {
                true => cx.notify(),
                false => pane.thumbnail_source_changed(cx),
            }
            going
        });
        if !matches!(going, Ok(true)) {
            return;
        }
    }
}

/// Decodes an image and scales it so its longest edge is at most 120 px, as a picture GPUI draws directly.
pub(super) fn scale(image: &Image) -> image::ImageResult<RenderImage> {
    let mut small = image::load_from_memory(image.bytes())?
        .thumbnail(EDGE, EDGE)
        .into_rgba8();
    // GPUI draws BGRA.
    for pixel in small.pixels_mut() {
        pixel.0.swap(0, 2);
    }
    Ok(RenderImage::new(vec![image::Frame::new(small)]))
}

#[cfg(test)]
#[path = "thumbnails_tests.rs"]
mod tests;
