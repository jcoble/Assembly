//! The pictures on a task's page, shown where they sit. Rows ask for theirs as they come on screen, so a long page reads
//! only what's looked at, and a few at a time. The view owns each read, so leaving the page cancels them, and hands
//! every picture back to the GPU when it goes. Notion's addresses change with every read of the page, so a picture is
//! kept by its block and only read again when it's really a different one.

use std::collections::HashMap;
use std::sync::Arc;

use gpui_kit::*;

use crate::core::socket::picture_client::read_page_picture;

use super::super::task_body::BodyRow;
use super::TaskDetailView;

/// Each read holds a background thread for as long as it takes.
const MOST_AT_ONCE: usize = 3;
/// The old page's limit.
const TALLEST: f32 = 480.;

type Reader = Arc<dyn Fn(&str, &dyn Fn() -> bool) -> Option<Image> + Send + Sync>;

enum Picture {
    Reading {
        _task: Task<()>,
        /// The read sees this go, and stops before its next request.
        _wanted: Arc<()>,
    },
    Shown(Arc<RenderImage>),
    Unreadable,
}

struct Slot {
    url: String,
    picture: Picture,
}

/// What a row draws.
pub(super) enum PictureState {
    Coming,
    Shown(Arc<RenderImage>),
    Unreadable,
}

pub(in super::super) struct PagePictures {
    pub(super) read: Reader,
    slots: HashMap<String, Slot>,
}

impl Default for PagePictures {
    fn default() -> Self {
        Self {
            read: Arc::new(read_page_picture),
            slots: HashMap::new(),
        }
    }
}

/// A Notion file's address carries a signature that's new with every read; what comes before it says which file.
fn place(url: &str) -> &str {
    match url.split_once('?') {
        Some((place, query)) if query.contains("X-Amz-Signature=") => place,
        _ => url,
    }
}

/// The largest a picture draws: its own size, down to the old page's height. Layout shrinks it further to fit across.
pub(super) fn largest(natural: Size<DevicePixels>) -> Size<Pixels> {
    let (width, height) = (natural.width.0 as f32, natural.height.0 as f32);
    if width <= 0. || height <= 0. {
        return size(px(0.), px(0.));
    }
    let scale = (TALLEST / height).min(1.);
    size(px(width * scale), px(height * scale))
}

impl PagePictures {
    pub(super) fn state(&self, block: &str) -> PictureState {
        match self.slots.get(block).map(|slot| &slot.picture) {
            None | Some(Picture::Reading { .. }) => PictureState::Coming,
            Some(Picture::Shown(picture)) => PictureState::Shown(picture.clone()),
            Some(Picture::Unreadable) => PictureState::Unreadable,
        }
    }

    /// Whether this row's address still has to be read: a failed one gets another go at a newly signed address.
    fn to_read(&self, block: &str, url: &str) -> bool {
        match self.slots.get(block) {
            None => true,
            Some(slot) => matches!(slot.picture, Picture::Unreadable) && slot.url != url,
        }
    }

    fn reading(&self) -> usize {
        self.slots
            .values()
            .filter(|slot| matches!(slot.picture, Picture::Reading { .. }))
            .count()
    }

    /// Lets go of a picture whose block left the page or now holds a different one.
    pub(super) fn keep_only(&mut self, rows: &[BodyRow], window: &mut Window, cx: &mut App) {
        let urls: HashMap<&str, &str> = rows
            .iter()
            .filter_map(|row| {
                let url = row.image.as_ref()?.url.as_deref()?;
                Some((row.id.as_str(), url))
            })
            .collect();
        let gone: Vec<String> = self
            .slots
            .iter()
            .filter(|(block, slot)| {
                urls.get(block.as_str())
                    .is_none_or(|url| place(url) != place(&slot.url))
            })
            .map(|(block, _)| block.clone())
            .collect();
        for block in gone {
            if let Some(Slot {
                picture: Picture::Shown(picture),
                ..
            }) = self.slots.remove(&block)
            {
                cx.drop_image(picture, Some(&mut *window));
            }
        }
    }

    pub(super) fn release(&mut self, cx: &mut App) {
        for (_, slot) in self.slots.drain() {
            if let Picture::Shown(picture) = slot.picture {
                cx.drop_image(picture, None);
            }
        }
    }
}

impl TaskDetailView {
    /// Called as a row is drawn. At the limit it asks nothing: each read's end redraws the rows on screen, which ask again.
    pub(super) fn want_picture(&mut self, row: &BodyRow, cx: &mut Context<Self>) {
        let Some(url) = row.image.as_ref().and_then(|image| image.url.as_deref()) else {
            return;
        };
        if !self.pictures.to_read(&row.id, url) || self.pictures.reading() >= MOST_AT_ONCE {
            return;
        }
        let wanted = Arc::new(());
        let still_wanted = Arc::downgrade(&wanted);
        let read = self.pictures.read.clone();
        let svg = cx.svg_renderer();
        let asked = url.to_owned();
        let made = cx.background_spawn(async move {
            let image = read(&asked, &|| still_wanted.strong_count() > 0)?;
            image.to_image_data(svg).ok()
        });
        let block = row.id.clone();
        let task = cx.spawn(async move |view, cx| {
            let picture = made.await;
            let _ = view.update(cx, |view, cx| view.picture_read(&block, picture, cx));
        });
        self.pictures.slots.insert(
            row.id.clone(),
            Slot {
                url: url.to_owned(),
                picture: Picture::Reading {
                    _task: task,
                    _wanted: wanted,
                },
            },
        );
    }

    fn picture_read(
        &mut self,
        block: &str,
        picture: Option<Arc<RenderImage>>,
        cx: &mut Context<Self>,
    ) {
        let Some(slot) = self.pictures.slots.get_mut(block) else {
            return;
        };
        slot.picture = picture.map_or(Picture::Unreadable, Picture::Shown);
        // The row's height changed. The first item is the page's head, so a row sits one past its place.
        if let Some(place) = self.rows.iter().position(|row| row.id == block) {
            self.list.remeasure_items(place + 1..place + 2);
        }
        cx.notify();
    }
}

#[cfg(test)]
#[path = "task_pictures_tests.rs"]
mod tests;
