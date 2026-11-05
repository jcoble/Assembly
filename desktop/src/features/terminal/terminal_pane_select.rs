//! Selecting on the Terminal tab (spec 6): a drag copies when the button comes up, a click outside clears it, and a drag
//! within `EDGE` of the top or bottom, or past it, scrolls while it lasts. Right-click opens Copy and Paste.

use std::time::Duration;

use alacritty_terminal::term::TermMode;
use gpui_kit::component::menu::{ContextMenuExt as _, PopupMenuItem};
use gpui_kit::*;

use super::TerminalPane;
use crate::features::terminal::terminal_keys::pasted;

/// How near the top or bottom edge a drag scrolls, as the old renderer's.
pub const EDGE: Pixels = px(30.);
pub const AUTOSCROLL_EVERY: Duration = Duration::from_millis(50);
pub const AUTOSCROLL_LINES: i32 = 3;

impl TerminalPane {
    /// The drag ended: the selection goes to the clipboard. Either listener may come first; the second does nothing.
    pub(super) fn end_drag(&mut self, cx: &mut Context<Self>) {
        self._autoscroll = None;
        if !std::mem::take(&mut self.selecting) {
            return;
        }
        if let Some(text) = self.grid.selection_text() {
            cx.write_to_clipboard(ClipboardItem::new_string(text));
        }
    }

    /// The mouse moved during a drag, anywhere in the window: the selection follows it, and near an edge or past it,
    /// the view scrolls.
    fn drag_moved(&mut self, position: Point<Pixels>, cx: &mut Context<Self>) {
        if !self.selecting {
            return;
        }
        self.dragged_to = position;
        self.extend_selection();
        cx.notify();
        if self.scroll_direction() == 0 {
            self._autoscroll = None;
            return;
        }
        if self._autoscroll.is_some() {
            return;
        }
        self._autoscroll = Some(cx.spawn(async move |pane, cx| {
            loop {
                cx.background_executor().timer(AUTOSCROLL_EVERY).await;
                let scrolled = pane.update(cx, |pane, cx| {
                    pane.grid.scroll(pane.scroll_direction() * AUTOSCROLL_LINES);
                    pane.extend_selection();
                    cx.notify();
                });
                if scrolled.is_err() {
                    return;
                }
            }
        }));
    }

    fn extend_selection(&mut self) {
        if let Some((column, line, right)) = self.cell_at(self.dragged_to) {
            self.grid.select_to(column, line, right);
        }
    }

    /// Back into the history (1) near the top, forward (-1) near the bottom, else 0.
    fn scroll_direction(&self) -> i32 {
        let top = self.origin.y;
        let bottom = top + self.cell.height * self.size.1 as f32;
        match self.dragged_to.y {
            y if y < top + EDGE => 1,
            y if y > bottom - EDGE => -1,
            _ => 0,
        }
    }

    /// While a drag selects, listens to the whole window, since the pane's own listeners stop at its edge.
    pub(super) fn drag_listeners(&self, cx: &mut Context<Self>) -> impl IntoElement {
        let this = cx.entity().downgrade();
        canvas(
            |_, _, _| (),
            move |_, _, window, _| {
                let moved = this.clone();
                window.on_mouse_event(move |event: &MouseMoveEvent, phase, _, cx| {
                    if phase.bubble() && event.pressed_button == Some(MouseButton::Left) {
                        let position = event.position;
                        moved
                            .update(cx, |pane, cx| pane.drag_moved(position, cx))
                            .ok();
                    }
                });
                let released = this.clone();
                window.on_mouse_event(move |event: &MouseUpEvent, phase, _, cx| {
                    if phase.bubble() && event.button == MouseButton::Left {
                        released.update(cx, |pane, cx| pane.end_drag(cx)).ok();
                    }
                });
            },
        )
        .absolute()
        .size_0()
    }

    /// A press anywhere outside the pane clears the selection.
    pub(super) fn pressed_outside(
        &mut self,
        _: &MouseDownEvent,
        _: &mut Window,
        cx: &mut Context<Self>,
    ) {
        if self.grid.clear_selection() {
            cx.notify();
        }
    }

    /// Copy and Paste on right-click, unless a program holds the mouse: then the press goes to it.
    pub(super) fn with_menu(&self, pane: Stateful<Div>, cx: &mut Context<Self>) -> AnyElement {
        if self.grid.mouse_mode().intersects(TermMode::MOUSE_MODE) {
            return pane.into_any_element();
        }
        let this = cx.entity().downgrade();
        pane.context_menu(move |menu, _, cx| {
            let Some((selected, typing)) = this
                .read_with(cx, |pane, _| (pane.grid.selection_text(), pane.typing()))
                .ok()
            else {
                return menu;
            };
            // Taken now: the press on the item is outside the pane, and clears the selection before the click.
            let copy = PopupMenuItem::new("Copy").disabled(selected.is_none());
            let copy = copy.on_click(move |_, _, cx| {
                if let Some(text) = &selected {
                    cx.write_to_clipboard(ClipboardItem::new_string(text.clone()));
                }
            });
            let pane = this.clone();
            let paste = PopupMenuItem::new("Paste").disabled(!typing);
            let paste = paste.on_click(move |_, _, cx| {
                pane.update(cx, |pane, cx| pane.paste(cx)).ok();
            });
            menu.item(copy).item(paste)
        })
        .into_any_element()
    }

    /// Sends the clipboard's text to the shell while typing is on.
    pub(super) fn paste(&mut self, cx: &mut Context<Self>) {
        if !self.typing() {
            return;
        }
        if let Some(bytes) = self.clipboard_bytes(cx) {
            self.send_typed(bytes, cx);
        }
    }

    /// The clipboard's text as the program takes a paste.
    pub(super) fn clipboard_bytes(&self, cx: &App) -> Option<Vec<u8>> {
        (cx.read_from_clipboard().and_then(|item| item.text()))
            .map(|text| pasted(&text, self.grid.bracketed_paste()))
    }
}
