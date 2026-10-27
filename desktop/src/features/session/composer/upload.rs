//! Sends one image over the socket: `attachments/begin` with its type and size, then `attachments/chunk` a
//! megabyte at a time off the main thread (base64 in the call's JSON, since the backend reads `data` as bytes), then
//! `attachments/commit`, which answers its path where the agent runs. It reads the file's bytes (it doesn't draw).
//! There's no retry and no wait: a failed upload shows on its chip.

use std::path::PathBuf;
use std::rc::Rc;
use std::sync::Arc;

use gpui_kit::*;

use crate::features::session::client::session_client::{CallError, SessionClient};

/// The most bytes one chunk carries (AttachmentsFeature.ChunkBytes).
pub const CHUNK_BYTES: usize = 1024 * 1024;

/// A picked or dropped file, or a pasted image's bytes.
pub enum UploadSource {
    Path(PathBuf),
    Bytes(Arc<[u8]>),
}

/// Uploads one image to the session and answers the path it was saved at. Dropping the task before it answers
/// stops it and sends `attachments/drop` for what it opened.
pub fn upload_image(
    _client: Rc<dyn SessionClient>,
    _session: String,
    _source: UploadSource,
    _mime: &'static str,
    _cx: &mut App,
) -> Task<Result<String, CallError>> {
    todo!()
}
