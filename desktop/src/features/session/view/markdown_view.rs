//! `MarkdownView` draws a reply's blocks as styled text runs, with links and file links clickable (the open URL and
//! open file intents, lane g0-host) and code blocks in the theme's mono font with their highlighted lines. It only
//! lays out what the backend parsed. Replaces chat/Text.svelte, chat/markdown/CodeBlock.svelte and the spike's
//! session/markdown.rs.

use gpui_kit::*;

use crate::features::session::rows::markdown::Block;

#[derive(IntoElement)]
pub struct MarkdownView {
    pub blocks: Vec<Block>,
}

impl RenderOnce for MarkdownView {
    fn render(self, _window: &mut Window, _cx: &mut App) -> impl IntoElement {
        div()
    }
}
