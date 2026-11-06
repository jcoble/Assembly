//! The pictures on a task's page, shown where they sit. Rows ask for theirs as they come on screen, so a long page reads
//! only what's looked at, and a few at a time. The view owns each read, so leaving the page cancels them, and hands
//! every picture back to the GPU when it goes. Notion's addresses change with every read of the page, so a picture is
//! kept by its block and only read again when it's really a different one. Past a memory budget, pictures off screen
//! are let go, to be read again if they come back.

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
/// What a page's pictures may hold, decoded.
const BUDGET: usize = 256 * 1024 * 1024;
/// A picture that can't be read gets one more go, at the address the next read of the page gives it.
const MOST_TRIES: u8 = 2;

type Reader = Arc<dyn Fn(&str, &dyn Fn() -> bool) -> Option<Arc<RenderImage>> + Send + Sync>;

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
    tries: u8,
    /// The last frame its row was drawn in.
    drawn: u64,
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
    frame: u64,
    pub(super) budget: usize,
}

impl Default for PagePictures {
    fn default() -> Self {
        Self {
            read: Arc::new(read_page_picture),
            slots: HashMap::new(),
            frame: 0,
            budget: BUDGET,
        }
    }
}

fn bytes_of(picture: &RenderImage) -> usize {
    let size = picture.size(0);
    size.width.0.max(0) as usize * size.height.0.max(0) as usize * 4
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

    /// Each draw of the page is a new frame, so a picture knows when its row was last on screen.
    pub(super) fn next_frame(&mut self) {
        self.frame += 1;
    }

    /// Whether this row's address still has to be read: a failed one gets another go at a newly signed address.
    fn to_read(&self, block: &str, url: &str) -> bool {
        match self.slots.get(block) {
            None => true,
            Some(slot) => {
                matches!(slot.picture, Picture::Unreadable)
                    && slot.url != url
                    && slot.tries < MOST_TRIES
            }
        }
    }

    fn seen(&mut self, block: &str) {
        if let Some(slot) = self.slots.get_mut(block) {
            slot.drawn = self.frame;
        }
    }

    /// Over budget, lets go of the pictures drawn longest ago, never one on screen now.
    fn evict(&mut self, cx: &mut App) {
        let mut held: usize = self
            .slots
            .values()
            .filter_map(|slot| match &slot.picture {
                Picture::Shown(picture) => Some(bytes_of(picture)),
                _ => None,
            })
            .sum();
        while held > self.budget {
            let Some(block) = self
                .slots
                .iter()
                .filter(|(_, slot)| {
                    matches!(slot.picture, Picture::Shown(_)) && slot.drawn < self.frame
                })
                .min_by_key(|(_, slot)| slot.drawn)
                .map(|(block, _)| block.clone())
            else {
                return;
            };
            if let Some(Slot {
                picture: Picture::Shown(picture),
                ..
            }) = self.slots.remove(&block)
            {
                held -= bytes_of(&picture);
                cx.drop_image(picture, None);
            }
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
        self.pictures.seen(&row.id);
        if !self.pictures.to_read(&row.id, url) || self.pictures.reading() >= MOST_AT_ONCE {
            return;
        }
        let tries = self
            .pictures
            .slots
            .get(&row.id)
            .map_or(0, |slot| slot.tries)
            + 1;
        let wanted = Arc::new(());
        let still_wanted = Arc::downgrade(&wanted);
        let read = self.pictures.read.clone();
        let asked = url.to_owned();
        let made =
            cx.background_spawn(async move { read(&asked, &|| still_wanted.strong_count() > 0) });
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
                tries,
                drawn: self.pictures.frame,
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
        self.pictures.evict(cx);
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
