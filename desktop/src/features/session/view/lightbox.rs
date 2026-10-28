//! Opens an attached image full size in a kit dialog, fitted to the window less 48 px; a click or Esc
//! closes it. The image's bytes come from `attachments/read` before it opens. Replaces FullImage.svelte.

use std::rc::Rc;
use std::sync::Arc;

use base64::Engine as _;
use base64::engine::general_purpose::STANDARD;
use gpui_kit::component::WindowExt as _;
use gpui_kit::*;
use serde_json::json;

use crate::features::session::client::session_client::{CallError, SessionClient};

/// Reads a session's attached image piece by piece (`attachments/read`), by its file name in the store. Chat's chips
/// and the Context tab both open images this way.
pub async fn read_image(
    client: Rc<dyn SessionClient>,
    session: String,
    file: String,
    cx: &mut AsyncWindowContext,
) -> Result<Arc<Image>, CallError> {
    let mut bytes: Vec<u8> = Vec::new();
    let mime = loop {
        let args = json!({ "session": session, "name": file, "offset": bytes.len() });
        let call = cx
            .update(|_, cx| client.call("attachments", "read", args, None, cx))
            .map_err(|_| CallError::Aborted)?;
        let piece = call.await?;
        let data = piece["data"].as_str().unwrap_or_default().to_string();
        let decoded = cx
            .background_spawn(async move { STANDARD.decode(data) })
            .await
            .map_err(|error| CallError::Failed(error.to_string()))?;
        bytes.extend(decoded.iter());
        let size = piece["size"].as_u64().unwrap_or_default() as usize;
        if decoded.is_empty() || bytes.len() >= size {
            break piece["mime"].as_str().unwrap_or_default().to_string();
        }
    };
    let format = ImageFormat::from_mime_type(&mime)
        .ok_or_else(|| CallError::Failed(format!("it isn't an image ({mime})")))?;
    Ok(Arc::new(Image::from_bytes(format, bytes)))
}

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
