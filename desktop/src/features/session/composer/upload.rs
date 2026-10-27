//! `Upload` sends one image over the socket: `attachments/begin` with its type and size, `attachments/chunk` a
//! megabyte at a time off the main thread, then `attachments/commit`, which answers its path where the agent runs.
//! It reads the file's bytes (it doesn't draw). Dropping it before commit sends `attachments/drop`. There's no retry
//! and no wait: a failed upload shows on its chip. The thumbnail uploads the same way.

use std::path::PathBuf;

use gpui_kit::*;

use crate::features::session::client::session_client::{CallError, SessionClient};

/// The most bytes one chunk carries (AttachmentsFeature.ChunkBytes).
pub const CHUNK_BYTES: usize = 1024 * 1024;

pub struct Upload {
    /// The backend's upload id, once begin answers.
    id: Option<String>,
}

impl Upload {
    /// Uploads the file at `path` to the session; answers the saved path.
    pub fn start(
        _client: std::rc::Rc<dyn SessionClient>,
        _session: String,
        _path: PathBuf,
        _mime: &'static str,
        _cx: &mut App,
    ) -> Task<Result<String, CallError>> {
        todo!()
    }
}
