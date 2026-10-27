//! `Attachment` is an image attached to the draft: its name, type and size for the chip, and where its upload is.
//! G0 makes no thumbnails; a sent message's lines may still name one, from the old app. A sent message carries each as an "Attached image: <path> (<name>) thumbnail: <path>" line after the
//! text, which the history reads back. Only png, jpeg, gif and webp up to 20 MB attach. Replaces attachments.ts.

use gpui_kit::*;

pub struct Attachment {
    pub name: SharedString,
    pub mime: &'static str,
    pub size: u64,
    pub state: AttachmentState,
}

pub enum AttachmentState {
    Uploading,
    /// Saved where the session's agent runs, at this path.
    Saved {
        path: String,
    },
    Failed(SharedString),
}

impl Attachment {
    /// Why a file can't attach: "not a supported image" or "larger than 20 MB"; `None` when it can.
    pub fn rejection(_mime: &str, _size: u64) -> Option<&'static str> {
        todo!()
    }

    /// The draft with one reference line per saved attachment after it.
    pub fn with_lines(_text: &str, _attachments: &[Attachment]) -> String {
        todo!()
    }

    /// "512 B", "2.0 KB", "3.5 MB", for the chip.
    pub fn size_text(_size: u64) -> String {
        todo!()
    }

    /// Dropped files and folders as draft text: their paths, separated by spaces, a path with a space in quotes.
    pub fn paths_text(_paths: &[String]) -> String {
        todo!()
    }

    /// `text` put in `draft` at the byte `caret`, with a space either side unless the draft has one there; the new
    /// draft and caret.
    pub fn insert_at(_draft: &str, _caret: usize, _text: &str) -> (String, usize) {
        todo!()
    }

    /// A sent message's text and the attachments its trailing lines name, as (path, name, thumbnail).
    pub fn split_lines(_text: &str) -> (String, Vec<(String, String, Option<String>)>) {
        todo!()
    }
}
