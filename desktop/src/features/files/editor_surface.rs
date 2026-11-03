//! Adapter for the pinned GPUI Kit source editor; no second editing engine.
use gpui_kit::component::input::EditorState;
use gpui_kit::*;

#[cfg(test)]
#[path = "editor_surface_tests.rs"]
mod tests;

pub struct EditorSurface {
    pub state: Entity<EditorState>,
}

impl EditorSurface {
    pub fn new(path: &str, text: &str, window: &mut Window, cx: &mut App) -> Self {
        let language = language(path);
        let state = cx.new(|cx| {
            EditorState::new(window, cx)
                .language(language)
                .default_value(text.to_owned())
        });
        Self { state }
    }

    pub fn jump(&mut self, line: u32, column: u32, cx: &mut App) {
        self.state.update(cx, |state, cx| {
            let at = position(state.value().as_ref(), line, column);
            state.set_selected_range(at..at, cx);
        });
    }
}

pub fn position(text: &str, line: u32, column: u32) -> usize {
    let mut start = 0;
    for _ in 1..line.max(1) {
        let Some(end) = text[start..].find('\n') else {
            return text.len();
        };
        start += end + 1;
    }
    let end = text[start..].find('\n').map_or(text.len(), |at| start + at);
    let body = text[start..end]
        .strip_suffix('\r')
        .unwrap_or(&text[start..end]);
    start
        + body
            .char_indices()
            .nth(column.max(1) as usize - 1)
            .map_or(body.len(), |(at, _)| at)
}

fn language(path: &str) -> &'static str {
    match path.rsplit('.').next().unwrap_or_default() {
        "rs" => "rust",
        "cs" => "c_sharp",
        "ts" | "tsx" => "typescript",
        "js" | "jsx" => "javascript",
        "py" => "python",
        "json" => "json",
        "md" => "markdown",
        "html" | "htm" => "html",
        "css" => "css",
        "sh" => "bash",
        "yaml" | "yml" => "yaml",
        "toml" => "toml",
        "dart" => "dart",
        "xml" => "xml",
        _ => "plaintext",
    }
}
