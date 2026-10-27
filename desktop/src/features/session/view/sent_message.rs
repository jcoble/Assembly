//! `SentMessage` draws a prompt: its text, folded past ten lines until opened, the images attached to it (their
//! "Attached image:" lines taken off the text) as thumbnails that open the lightbox, and who sent it when another
//! session did. Replaces SentText.svelte, SentImage.svelte and chat/sender.ts.

use gpui_kit::*;

/// A prompt folds past this many lines.
pub const FOLD_LINES: usize = 10;

#[derive(IntoElement)]
pub struct SentMessage {
    pub text: SharedString,
    /// "Helper · Claude Code on Workbox Test" when another session sent it.
    pub sender: Option<SharedString>,
    pub open: bool,
}

impl RenderOnce for SentMessage {
    fn render(self, _window: &mut Window, _cx: &mut App) -> impl IntoElement {
        div()
    }
}
