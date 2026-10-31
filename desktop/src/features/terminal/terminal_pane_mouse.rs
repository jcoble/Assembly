//! The mouse on the Terminal tab: a drag selects (Copy is in `key_down`), and the wheel scrolls back through the
//! history, or goes to a full-screen program as arrow keys, as xterm sends them.

use gpui_kit::*;

use super::{Shown, TerminalPane};
use crate::features::terminal::terminal_keys::key_bytes;

impl TerminalPane {
    pub(super) fn mouse_down(
        &mut self,
        event: &MouseDownEvent,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        window.focus(&self.focus, cx);
        let Some((column, line, right)) = self.cell_at(event.position) else {
            return;
        };
        self.grid.select_start(column, line, right);
        self.selecting = true;
        cx.notify();
    }

    pub(super) fn mouse_move(
        &mut self,
        event: &MouseMoveEvent,
        _: &mut Window,
        cx: &mut Context<Self>,
    ) {
        if !self.selecting || event.pressed_button != Some(MouseButton::Left) {
            return;
        }
        if let Some((column, line, right)) = self.cell_at(event.position) {
            self.grid.select_to(column, line, right);
            cx.notify();
        }
    }

    pub(super) fn mouse_up(&mut self, _: &MouseUpEvent, _: &mut Window, _: &mut Context<Self>) {
        self.selecting = false;
    }

    /// Whole lines only; a trackpad's smaller steps add up until they make one.
    pub(super) fn wheel(&mut self, event: &ScrollEvent, _: &mut Window, cx: &mut Context<Self>) {
        if self.cell.height <= px(0.) {
            return;
        }
        self.wheeled += event.delta.pixel_delta(self.cell.height).y;
        let lines = (self.wheeled / self.cell.height).trunc();
        if lines == 0. {
            return;
        }
        self.wheeled -= self.cell.height * lines;
        cx.stop_propagation();
        let lines = lines as i32;
        if !self.grid.wheel_sends_arrows() {
            self.grid.scroll(lines);
            cx.notify();
            return;
        }
        if self.shown != Shown::Open {
            return;
        }
        let arrow = Keystroke::parse(if lines > 0 { "up" } else { "down" });
        let arrow = (arrow.ok()).and_then(|arrow| key_bytes(&arrow, self.grid.app_cursor()));
        for _ in 0..lines.unsigned_abs() {
            self.typed.extend(arrow.iter().flatten());
        }
        self.write(cx);
    }

    /// The screen cell under a window position and whether it's on the cell's right half; none before the grid is
    /// drawn.
    fn cell_at(&self, position: Point<Pixels>) -> Option<(usize, usize, bool)> {
        if self.cell.width <= px(0.) || self.cell.height <= px(0.) {
            return None;
        }
        let x = ((position.x - self.origin.x) / self.cell.width).max(0.);
        let y = ((position.y - self.origin.y) / self.cell.height).max(0.);
        let column = (x as usize).min(self.size.0 - 1);
        let line = (y as usize).min(self.size.1 - 1);
        Some((column, line, x.fract() >= 0.5))
    }
}
