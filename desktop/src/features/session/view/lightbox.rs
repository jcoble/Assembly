//! `Lightbox` opens an attached image full size in a kit dialog, fitted to the window less 48 px; a click or Esc
//! closes it. The image's bytes come from `attachments/read` before it opens. Replaces FullImage.svelte.

use std::sync::Arc;

use gpui_kit::*;

pub struct Lightbox;

impl Lightbox {
    pub fn open(_image: Arc<Image>, _name: SharedString, _window: &mut Window, _cx: &mut App) {
        todo!()
    }
}
