//! Opens an attached image full size in a kit dialog, fitted to the window less 48 px; a click or Esc
//! closes it. The image's bytes come from `attachments/read` before it opens. Replaces FullImage.svelte.

use std::sync::Arc;

use gpui_kit::component::WindowExt as _;
use gpui_kit::*;

pub fn open_lightbox(image: Arc<Image>, name: SharedString, window: &mut Window, cx: &mut App) {
    let room = window.viewport_size() - size(px(48.), px(48.));
    window.open_dialog(cx, move |dialog, _, _| {
        dialog
            .close_button(false)
            .centered()
            .max_w(room.width)
            .child(
                div()
                    .id(name.clone())
                    .flex()
                    .justify_center()
                    .on_click(|_, window, cx| window.close_dialog(cx))
                    .child(
                        img(image.clone())
                            .max_w(room.width)
                            .max_h(room.height)
                            .object_fit(ObjectFit::Contain),
                    ),
            )
    });
}
