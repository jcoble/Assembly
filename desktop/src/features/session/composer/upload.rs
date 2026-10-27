//! Sends one image over the socket: `attachments/begin` with its type and size, then `attachments/chunk` a
//! megabyte at a time off the main thread (base64 in the call's JSON, since the backend reads `data` as bytes), then
//! `attachments/commit`, which answers its path where the agent runs. It reads the file's bytes (it doesn't draw).
//! There's no retry and no wait: a failed upload shows on its chip, and a failure after begin drops what it began.
//! An upload whose task is dropped part way is left to the backend, which drops it when the socket closes.

use std::path::{Path, PathBuf};
use std::rc::Rc;
use std::sync::Arc;

use base64::Engine as _;
use base64::engine::general_purpose::STANDARD;
use gpui_kit::*;
use serde_json::json;

use crate::features::session::client::session_client::{CallError, SessionClient};

/// The most bytes one chunk carries (AttachmentsFeature.ChunkBytes).
pub const CHUNK_BYTES: usize = 1024 * 1024;

/// A picked or dropped file, or a pasted image's bytes.
pub enum UploadSource {
    Path(PathBuf),
    Bytes(Arc<[u8]>),
}

/// A file's size on disk, to turn away one too large before it uploads; `None` when it can't be read.
pub fn size_of(path: &Path) -> Option<u64> {
    std::fs::metadata(path).ok().map(|metadata| metadata.len())
}

/// Uploads one image to the session and answers the path it was saved at.
pub fn upload_image(
    client: Rc<dyn SessionClient>,
    session: String,
    source: UploadSource,
    mime: &'static str,
    cx: &mut App,
) -> Task<Result<String, CallError>> {
    let read = cx.background_spawn(async move {
        match source {
            UploadSource::Path(path) => std::fs::read(&path)
                .map(Arc::from)
                .map_err(|error| CallError::Failed(format!("Couldn't read the image: {error}"))),
            UploadSource::Bytes(bytes) => Ok(bytes),
        }
    });
    cx.spawn(async move |cx| {
        let bytes = read.await?;
        let begin = json!({ "session": session, "mime": mime, "size": bytes.len() });
        let begun = cx
            .update(|cx| client.call("attachments", "begin", begin, None, cx))
            .await?;
        let upload = begun["upload"]
            .as_str()
            .ok_or_else(|| CallError::Failed("The upload didn't start.".into()))?;
        let saved = send(&*client, upload, bytes, cx).await;
        if saved.is_err() {
            let drop = json!({ "upload": upload });
            cx.update(|cx| client.call("attachments", "drop", drop, None, cx))
                .await
                .ok();
        }
        saved
    })
}

/// The chunks, then the commit, which answers the path.
async fn send(
    client: &dyn SessionClient,
    upload: &str,
    bytes: Arc<[u8]>,
    cx: &mut AsyncApp,
) -> Result<String, CallError> {
    for offset in (0..bytes.len()).step_by(CHUNK_BYTES) {
        let chunk = bytes.clone();
        let data = cx
            .background_spawn(async move {
                STANDARD.encode(&chunk[offset..(offset + CHUNK_BYTES).min(chunk.len())])
            })
            .await;
        let chunk = json!({ "upload": upload, "offset": offset, "data": data });
        cx.update(|cx| client.call("attachments", "chunk", chunk, None, cx))
            .await?;
    }
    let commit = json!({ "upload": upload });
    let committed = cx
        .update(|cx| client.call("attachments", "commit", commit, None, cx))
        .await?;
    committed["path"]
        .as_str()
        .map(String::from)
        .ok_or_else(|| CallError::Failed("The upload saved no path.".into()))
}

#[cfg(test)]
#[path = "upload_tests.rs"]
mod tests;
