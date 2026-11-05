//! Composed text on the Terminal tab (spec 8): dead keys and input methods. A key the pane writes itself stops there
//! (`key_down`); the rest reach this input handler, which writes the finished text once. A composition in progress is
//! kept in `marked` and not shown, as the old renderer did, and nothing of it is sent (spec reading 5).

use std::ops::Range;

use gpui_kit::*;

use super::TerminalPane;

impl TerminalPane {
    /// Registers the pane's input handler on each paint while it has the focus, so the window's input methods reach it.
    pub(super) fn input_handler(&self, cx: &mut Context<Self>) -> impl IntoElement {
        let (focus, pane) = (self.focus.clone(), cx.entity());
        canvas(
            |_, _, _| (),
            move |bounds, _, window, cx| {
                window.handle_input(&focus, ElementInputHandler::new(bounds, pane), cx);
            },
        )
        .absolute()
        .size_full()
    }

    /// The composition's length in UTF-16 units, the input handler's measure.
    fn marked_len(&self) -> usize {
        self.marked
            .as_deref()
            .map_or(0, |marked| marked.encode_utf16().count())
    }
}

impl EntityInputHandler for TerminalPane {
    fn text_for_range(
        &mut self,
        range: Range<usize>,
        adjusted_range: &mut Option<Range<usize>>,
        _window: &mut Window,
        _cx: &mut Context<Self>,
    ) -> Option<String> {
        let units: Vec<u16> = self.marked.as_deref()?.encode_utf16().collect();
        let end = range.end.min(units.len());
        let start = range.start.min(end);
        *adjusted_range = Some(start..end);
        String::from_utf16(&units[start..end]).ok()
    }

    /// The insertion point sits at the end of the composition, or at the start when there's none.
    fn selected_text_range(
        &mut self,
        _ignore_disabled_input: bool,
        _window: &mut Window,
        _cx: &mut Context<Self>,
    ) -> Option<UTF16Selection> {
        let end = self.marked_len();
        Some(UTF16Selection {
            range: end..end,
            reversed: false,
        })
    }

    fn marked_text_range(
        &self,
        _window: &mut Window,
        _cx: &mut Context<Self>,
    ) -> Option<Range<usize>> {
        self.marked.as_ref().map(|_| 0..self.marked_len())
    }

    fn unmark_text(&mut self, _window: &mut Window, _cx: &mut Context<Self>) {
        self.marked = None;
    }

    /// Finished text: it goes to the shell once, while typing is on.
    fn replace_text_in_range(
        &mut self,
        _range: Option<Range<usize>>,
        text: &str,
        _window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        self.marked = None;
        if self.typing() && !text.is_empty() {
            self.send_typed(text.as_bytes().to_vec(), cx);
        }
    }

    /// A composition in progress: kept, not sent. Empty text ends it.
    fn replace_and_mark_text_in_range(
        &mut self,
        _range: Option<Range<usize>>,
        new_text: &str,
        _new_selected_range: Option<Range<usize>>,
        _window: &mut Window,
        _cx: &mut Context<Self>,
    ) {
        self.marked = (self.typing() && !new_text.is_empty()).then(|| new_text.to_string());
    }

    /// The cursor's cell, so an input method's candidate window sits there.
    fn bounds_for_range(
        &mut self,
        _range_utf16: Range<usize>,
        _element_bounds: Bounds<Pixels>,
        _window: &mut Window,
        _cx: &mut Context<Self>,
    ) -> Option<Bounds<Pixels>> {
        let (column, line) = self.grid.cursor()?;
        let at = point(
            self.cell.width * column as f32,
            self.cell.height * line as f32,
        );
        Some(Bounds::new(self.origin + at, self.cell))
    }

    fn character_index_for_point(
        &mut self,
        _point: Point<Pixels>,
        _window: &mut Window,
        _cx: &mut Context<Self>,
    ) -> Option<usize> {
        None
    }
}
