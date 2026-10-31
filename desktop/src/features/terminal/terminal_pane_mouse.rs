//! The mouse on the Terminal tab: a drag selects (Copy is in `key_down`), and the wheel scrolls back through the
//! history, or reports the mouse in the tracking/encoding modes requested by the program.

use alacritty_terminal::term::TermMode;
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
        if let Some(button) = button_code(event.button)
            && self.report_mouse(button, event.position, event.modifiers, false, cx)
        {
            return;
        }
        if event.button != MouseButton::Left {
            return;
        }
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
        let mode = self.grid.mouse_mode();
        if mode.intersects(TermMode::MOUSE_MODE) {
            if mode.contains(TermMode::MOUSE_MOTION)
                || (mode.contains(TermMode::MOUSE_DRAG) && event.pressed_button.is_some())
            {
                let button = event.pressed_button.and_then(button_code).unwrap_or(3) + 32;
                self.report_mouse(button, event.position, event.modifiers, false, cx);
            }
            return;
        }
        if !self.selecting || event.pressed_button != Some(MouseButton::Left) {
            return;
        }
        if let Some((column, line, right)) = self.cell_at(event.position) {
            self.grid.select_to(column, line, right);
            cx.notify();
        }
    }

    pub(super) fn mouse_up(
        &mut self,
        event: &MouseUpEvent,
        _: &mut Window,
        cx: &mut Context<Self>,
    ) {
        self.selecting = false;
        if let Some(button) = button_code(event.button) {
            self.report_mouse(button, event.position, event.modifiers, true, cx);
        }
    }

    /// Tracking modes consume mouse input; otherwise selection and scrollback keep their usual behavior.
    fn report_mouse(
        &mut self,
        button: u8,
        position: Point<Pixels>,
        modifiers: Modifiers,
        released: bool,
        cx: &mut Context<Self>,
    ) -> bool {
        let mode = self.grid.mouse_mode();
        if !mode.intersects(TermMode::MOUSE_MODE) {
            return false;
        }
        cx.stop_propagation();
        if self.shown != Shown::Open {
            return true;
        }
        let Some((column, line, _)) = self.cell_at(position) else {
            return true;
        };
        let mods = u8::from(modifiers.shift) * 4
            + u8::from(modifiers.alt) * 8
            + u8::from(modifiers.control) * 16;
        if mode.contains(TermMode::SGR_MOUSE) {
            self.typed.extend(
                format!(
                    "\x1b[<{};{};{}{}",
                    button + mods,
                    column + 1,
                    line + 1,
                    if released { 'm' } else { 'M' }
                )
                .bytes(),
            );
        } else {
            let limit = if mode.contains(TermMode::UTF8_MOUSE) {
                2015
            } else {
                223
            };
            // Legacy encoding has no representation beyond this range; never wrap to another cell.
            if column >= limit || line >= limit {
                return true;
            }
            let button = if released { 3 } else { button };
            self.typed.extend_from_slice(b"\x1b[M");
            self.typed.push(32 + button + mods);
            for value in [column + 33, line + 33] {
                if mode.contains(TermMode::UTF8_MOUSE) {
                    self.typed
                        .extend(char::from_u32(value as u32).unwrap().to_string().bytes());
                } else {
                    self.typed.push(value as u8);
                }
            }
        }
        self.write(cx);
        true
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
        if self.grid.mouse_mode().intersects(TermMode::MOUSE_MODE) {
            for _ in 0..lines.unsigned_abs() {
                self.report_mouse(
                    if lines > 0 { 64 } else { 65 },
                    event.position,
                    event.modifiers,
                    false,
                    cx,
                );
            }
            return;
        }
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
        // Past the last column is its right half, so a drag there takes that column.
        let right = x >= self.size.0 as f32 || x.fract() >= 0.5;
        Some((column, line, right))
    }
}

fn button_code(button: MouseButton) -> Option<u8> {
    match button {
        MouseButton::Left => Some(0),
        MouseButton::Middle => Some(1),
        MouseButton::Right => Some(2),
        _ => None,
    }
}
