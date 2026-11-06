//! `TerminalPane` owns the bottom dock's Terminal tab: the open session's shell, run by the backend
//! (terminal_link.rs), shown from an `alacritty_terminal` grid (terminal_grid.rs, drawn by terminal_draw.rs).
//!
//! While the tab is shown it opens the session's shell: the running one, or a new one in the session's folder
//! (terminal#1, terminal#9). It follows the shell's output first and then reads what was kept, holding the rows that
//! come meanwhile, so nothing between the two is lost; a row past a gap (a reconnect) reads the kept output again from
//! where it got to (terminal#5). Hiding the tab or picking another session lets go of the view and closes nothing on
//! the backend; the tab leaving the dock closes the shell (g0-host#9). What's typed goes out one write at a time,
//! what's typed meanwhile in the next, in order (terminal#2). The backend routes remote sessions to their machine (terminal#12).
//! What it does as the connection and the shell change, and what it says about them, is in terminal_pane_state.rs.

use crate::core::theme::text::{TextRole, TextRoleExt as _};
use std::mem;
use std::time::Duration;

use gpui_kit::component::dock::{BasePanel, Panel, PanelEvent, panel_handle, register_panel};
use gpui_kit::component::{ActiveTheme as _, Sizable as _, h_flex, v_flex};
use gpui_kit::prelude::FluentBuilder as _;
use gpui_kit::*;

use super::terminal_draw::draw;
use super::terminal_grid::{HoveredLink, TerminalGrid};
use super::terminal_keys::{key_bytes, left_to_input};
use super::terminal_link::TerminalLink;
use super::terminal_palette::palette;
use crate::core::socket::update::Update;
use crate::core::state::appearance::Appearance;
use crate::core::state::preferences::Preferences;
use crate::core::state::selected_session::SelectedSession;
use crate::core::ui::spinner::Spinner;

/// The layout's name for the tab (default_layout.rs).
const TERMINAL: &str = "Terminal";

/// The sizes the backend allows (TerminalHost.cs); the grid keeps to them too, so the two never differ.
const COLS: (usize, usize) = (20, 300);
const ROWS: (usize, usize) = (4, 100);

/// The grid's size before the pane has measured itself: the backend's own default (TerminalHost.cs).
const FIRST_SIZE: (usize, usize) = (96, 28);

/// How long the cursor shows, then hides, while it blinks (ghostty-web's interval, as the old app had).
const BLINK: Duration = Duration::from_millis(530);

#[derive(Debug, PartialEq)]
pub enum Shown {
    /// No session, or the tab is hidden.
    Nothing,
    Opening,
    Open,
    /// The program ended with this code; its output stays (terminal#7).
    Exited(i32),
    /// Why the terminal stopped, or why there's none: the backend's reason, in the error color.
    Said(String),
    /// The connection is down; it comes back by itself, and any output stays.
    Reconnecting,
    /// The shell isn't there any more; its output stays.
    Gone,
}

pub struct TerminalPane {
    focus: FocusHandle,
    /// The kit shows this tab.
    active: bool,
    /// The session whose shell this is.
    session: Option<String>,
    grid: TerminalGrid,
    shown: Shown,
    /// The columns and rows the pane has room for, once it has measured itself.
    size: (usize, usize),
    measured: bool,
    /// Where the grid starts in the window, the room it has and each cell's size, as last drawn: what the mouse
    /// points at.
    origin: Point<Pixels>,
    bounds: Bounds<Pixels>,
    cell: Size<Pixels>,
    /// A left-button drag is selecting.
    selecting: bool,
    /// Where that drag's mouse last was, in the window.
    dragged_to: Point<Pixels>,
    /// The wheel's movement not yet a whole line.
    wheeled: Pixels,
    link: Option<TerminalLink>,
    /// Rows that came while the kept output was being read; `None` while following.
    held: Option<Vec<Update>>,
    /// Typed while a write was on its way.
    typed: Vec<u8>,
    writing: bool,
    /// The shell just opened: the pane takes the focus on its next draw.
    focus_wanted: bool,
    /// The open under way is a retry, from the timer or a push, not from showing the tab or picking a session.
    retried: bool,
    /// The blinking cursor's half of the blink; it stays shown while it doesn't blink.
    cursor_on: bool,
    /// The pane has the focus, one of the blink's conditions.
    has_focus: bool,
    /// Its window is the active one, the blink's third condition: GPUI keeps the pane's focus while another window is.
    window_active: bool,
    /// Follows `window_active`; its notify draws the steady cursor, which a blur inside a draw can't.
    _activation: Subscription,
    _selected: Subscription,
    /// Draws again when the terminal's font or the theme changes.
    _preferences: Option<Subscription>,
    /// Tell a program that asks when the pane takes and loses the focus.
    _focused: [Subscription; 2],
    /// Opens the shell, then follows its rows; dropping it unsubscribes.
    _follow: Option<Task<()>>,
    _replay: Option<Task<()>>,
    _write: Option<Task<()>>,
    /// The resize on its way, one at a time; dropping it cancels the call.
    _resize: Option<Task<()>>,
    /// The size to send when that one is answered.
    resize_wanted: Option<(usize, usize)>,
    /// The connection came back: the next draw's size goes to the program.
    size_unsent: bool,
    /// While the open waits for the connection: the machines list and the socket, which can each say to try again.
    reopen: Vec<Subscription>,
    /// While no push can come: the wait before the next try to open.
    _retry: Option<Task<()>>,
    /// The tries to open on that timer so far, for its back-off.
    retries: usize,
    /// Blinks the cursor (see `blink_as_needed`); dropping it stops the timer.
    _blink: Option<Task<()>>,
    /// Scrolls during a drag near the edge or past it; dropping it stops the timer.
    _autoscroll: Option<Task<()>>,
    /// The scrollbar's opacity, from 0 to 1.
    scrollbar_opacity: f32,
    /// The scrollbar's thumb is held: where the press was, from the grid's top, and the offset then.
    dragging_bar: Option<(Pixels, usize)>,
    /// A press on the scrollbar, thumb or track, until its release, which it takes.
    bar_pressed: bool,
    /// Fades the scrollbar in, waits and fades it out; it ends once hidden.
    _scrollbar: Option<Task<()>>,
    /// The link under the mouse.
    hovered: Option<HoveredLink>,
    /// The cell the mouse was last looked at over.
    hovered_cell: Option<(usize, usize)>,
    /// A Cmd or Ctrl-click opened a link: its release goes nowhere.
    link_pressed: bool,
    /// The composition in progress, kept but not shown or sent (terminal_pane_ime.rs).
    marked: Option<String>,
}

impl TerminalPane {
    /// Registers the tab under the layout's Terminal name, after the placeholders, since the last registration wins.
    pub fn register(cx: &mut App) {
        register_panel(cx, TERMINAL, |_, window, cx| {
            panel_handle(cx.new(|cx| TerminalPane::new(window, cx)))
        });
    }

    pub fn new(window: &mut Window, cx: &mut Context<Self>) -> Self {
        let focus = cx.focus_handle();
        let focused = [
            cx.on_focus(&focus, window, |pane, _, cx| pane.focused(true, cx)),
            cx.on_blur(&focus, window, |pane, _, cx| pane.focused(false, cx)),
        ];
        let activation = cx.observe_window_activation(window, |pane, window, cx| {
            pane.window_active = window.is_window_active();
            pane.blink_as_needed(cx);
        });
        Self {
            focus,
            active: false,
            session: None,
            grid: TerminalGrid::new(FIRST_SIZE.0, FIRST_SIZE.1),
            shown: Shown::Nothing,
            size: FIRST_SIZE,
            measured: false,
            origin: Point::default(),
            bounds: Bounds::default(),
            cell: Size::default(),
            selecting: false,
            dragged_to: Point::default(),
            wheeled: px(0.),
            link: None,
            held: None,
            typed: Vec::new(),
            writing: false,
            focus_wanted: false,
            retried: false,
            cursor_on: true,
            has_focus: false,
            window_active: window.is_window_active(),
            _activation: activation,
            _focused: focused,
            _preferences: Preferences::try_global(cx).map(|preferences| {
                cx.observe(&preferences, |pane, _, cx| pane.blink_as_needed(cx))
            }),
            _selected: cx.observe(&SelectedSession::global(cx), |pane, _, cx| pane.refresh(cx)),
            _follow: None,
            _replay: None,
            _write: None,
            _resize: None,
            resize_wanted: None,
            size_unsent: false,
            reopen: Vec::new(),
            _retry: None,
            retries: 0,
            _blink: None,
            _autoscroll: None,
            scrollbar_opacity: 0.,
            dragging_bar: None,
            bar_pressed: false,
            _scrollbar: None,
            hovered: None,
            hovered_cell: None,
            link_pressed: false,
            marked: None,
        }
    }

    /// Opens the selected session's shell when the tab is shown and it isn't open yet; lets go of the last one's view
    /// otherwise.
    fn refresh(&mut self, cx: &mut Context<Self>) {
        let wanted = self
            .active
            .then(|| {
                SelectedSession::global(cx)
                    .read(cx)
                    .session_id()
                    .map(String::from)
            })
            .flatten();
        if wanted.is_some() && wanted == self.session {
            return;
        }
        (self._follow, self._replay, self._write) = (None, None, None);
        (self.link, self.held, self.writing) = (None, None, false);
        (self._resize, self.resize_wanted, self.size_unsent) = (None, None, false);
        (self._retry, self.retries, self.retried) = (None, 0, false);
        (self._autoscroll, self.selecting) = (None, false);
        (self._scrollbar, self.scrollbar_opacity) = (None, 0.);
        (self.dragging_bar, self.bar_pressed) = (None, false);
        (self.hovered, self.hovered_cell, self.link_pressed) = (None, None, false);
        (self.marked, self.focus_wanted) = (None, false);
        self.reopen.clear();
        self.typed.clear();
        self.grid = TerminalGrid::new(self.size.0, self.size.1);
        self.session = wanted.clone();
        self.set_shown(Shown::Nothing, cx);
        let Some(_) = wanted else {
            return;
        };
        self.set_shown(Shown::Opening, cx);
        self.open(cx);
    }

    /// Opens the session's shell once the pane knows its size, so the shell starts at the size it's shown at.
    fn open(&mut self, cx: &mut Context<Self>) {
        let Some(session) = self.session.clone() else {
            return;
        };
        if !self.measured || self.shown != Shown::Opening || self._follow.is_some() {
            return;
        }
        let size = self.size;
        self._follow = Some(cx.spawn(async move |pane, cx| {
            let opened = TerminalLink::open(&session, size, cx).await;
            let subscription = pane.update(cx, |pane, cx| {
                let followed = opened.and_then(|(link, started)| {
                    let subscription = link.follow(cx)?;
                    pane.link = Some(link);
                    // A reused shell may have another size, and the pane may have measured itself meanwhile.
                    if started != pane.size {
                        pane.send_size(cx);
                    }
                    // The subscription state starts catch-up, so its arrival cannot cause a second initial read.
                    Ok(subscription)
                });
                followed
                    .map_err(|failure| pane.call_failed(failure, cx))
                    .ok()
            });
            let Ok(Some(mut subscription)) = subscription else {
                return;
            };
            while let Ok(update) = subscription.updates.recv().await {
                if pane.update(cx, |pane, cx| pane.output(update, cx)).is_err() {
                    return;
                }
            }
        }));
    }

    /// The room the pane has changed: the grid and the program take the new size (terminal#4).
    pub fn resized(&mut self, (cols, rows): (usize, usize), cx: &mut Context<Self>) {
        let size = (cols.clamp(COLS.0, COLS.1), rows.clamp(ROWS.0, ROWS.1));
        if !self.measured {
            self.measured = true;
            self.size = size;
            self.grid.resize(size.0, size.1);
            self.open(cx);
            return;
        }
        if size != self.size {
            self.size = size;
            self.grid.resize(size.0, size.1);
            self.unhover();
            self.size_unsent = true;
            cx.notify();
        }
        if mem::take(&mut self.size_unsent) {
            self.send_size(cx);
        }
    }

    fn key_down(&mut self, event: &KeyDownEvent, _: &mut Window, cx: &mut Context<Self>) {
        let keystroke = &event.keystroke;
        let modifiers = keystroke.modifiers;
        // Cmd on the Mac, Control-Shift elsewhere, as terminals take Copy and Paste.
        let shortcut = |key: &str| {
            keystroke.key == key && (modifiers.platform || (modifiers.control && modifiers.shift))
        };
        if shortcut("c") {
            if let Some(text) = self.grid.selection_text() {
                cx.write_to_clipboard(ClipboardItem::new_string(text));
                cx.stop_propagation();
            }
            return;
        }
        // A typed character goes on to the input handler (terminal_pane_ime.rs), which writes it once.
        if !self.typing() || left_to_input(keystroke) {
            return;
        }
        let bytes = match shortcut("v") {
            true => self.clipboard_bytes(cx),
            false => key_bytes(keystroke, self.grid.app_cursor()),
        };
        let Some(bytes) = bytes else {
            return;
        };
        cx.stop_propagation();
        self.send_typed(bytes, cx);
    }

    /// Typed or pasted bytes go to the shell, and the view returns to the bottom.
    fn send_typed(&mut self, bytes: Vec<u8>, cx: &mut Context<Self>) {
        if self.grid.scroll_to_bottom() {
            self.show_scrollbar(cx);
        }
        self.cursor_on = true;
        // A composition the platform finished without unmarking it (an xkb compose) is over.
        self.marked = None;
        self.typed.extend(bytes);
        self.write(cx);
    }

    /// The pane took or lost the focus, which a program that asked for focus reports hears as `ESC [I` or `ESC [O`.
    fn focused(&mut self, focused: bool, cx: &mut Context<Self>) {
        self.has_focus = focused;
        self.blink_as_needed(cx);
        if self.shown != Shown::Open || !self.grid.focus_reporting() {
            return;
        }
        self.typed
            .extend_from_slice(if focused { b"\x1b[I" } else { b"\x1b[O" });
        self.write(cx);
    }

    /// Blinks the cursor only while the shell is open, the pane has the focus in the active window and the Cursor
    /// blink preference is on; otherwise no timer runs and the cursor shows steady. A blink already running carries on.
    pub(super) fn blink_as_needed(&mut self, cx: &mut Context<Self>) {
        let wanted = self.has_focus
            && self.window_active
            && self.shown == Shown::Open
            && Preferences::read(cx, Appearance::terminal_cursor_blink);
        if !wanted {
            (self._blink, self.cursor_on) = (None, true);
        } else if self._blink.is_none() {
            self.cursor_on = true;
            self._blink = Some(cx.spawn(async move |pane, cx| {
                loop {
                    cx.background_executor().timer(BLINK).await;
                    let blinked = pane.update(cx, |pane, cx| {
                        pane.cursor_on = !pane.cursor_on;
                        cx.notify();
                    });
                    if blinked.is_err() {
                        return;
                    }
                }
            }));
        }
        cx.notify();
    }

    /// Sends what's been typed, unless a write is on its way: then it goes when that one's answered.
    fn write(&mut self, cx: &mut Context<Self>) {
        let Some(link) = &self.link else {
            return;
        };
        if self.writing || self.typed.is_empty() {
            return;
        }
        let write = link.write(&mem::take(&mut self.typed), cx);
        self.writing = true;
        self._write = Some(cx.spawn(async move |pane, cx| {
            let written = write.await;
            pane.update(cx, |pane, cx| {
                pane.writing = false;
                if let Err(failure) = written {
                    pane.write_failed(failure, cx);
                    return;
                }
                pane.write(cx);
            })
            .ok();
        }));
    }

    #[cfg(test)]
    fn writing(&self) -> bool {
        self.writing
    }

    #[cfg(test)]
    fn catching_up(&self) -> bool {
        self.held.is_some()
    }
}

impl EventEmitter<PanelEvent> for TerminalPane {}

impl Focusable for TerminalPane {
    fn focus_handle(&self, _: &App) -> FocusHandle {
        self.focus.clone()
    }
}

impl BasePanel for TerminalPane {
    fn panel_name(&self) -> &'static str {
        TERMINAL
    }

    /// A default layout pane: a permanent tab, with no close button.
    fn closable(&self, _: &App) -> bool {
        false
    }

    fn set_active(&mut self, active: bool, _: &mut Window, cx: &mut Context<Self>) {
        self.active = active;
        self.refresh(cx);
    }

    /// The tab left the dock for good: its shell ends (terminal#8).
    fn on_removed(&mut self, _: &mut Window, cx: &mut Context<Self>) {
        if let Some(link) = &self.link {
            link.close(cx);
        }
        self.active = false;
        self.refresh(cx);
    }
}

impl Panel for TerminalPane {
    fn title(&mut self, _: &mut Window, _: &mut Context<Self>) -> impl IntoElement {
        TERMINAL
    }
}

impl Render for TerminalPane {
    fn render(&mut self, window: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        // After the replay, so a program that asked for focus reports hears it has the focus.
        // A retried open takes the focus only when nothing else has it, so keys typed elsewhere stay there.
        let free = !self.retried || window.focused(cx).is_none();
        if self.held.is_none() && mem::take(&mut self.focus_wanted) && free {
            let focus = self.focus.clone();
            window.defer(cx, move |window, cx| window.focus(&focus, cx));
        }
        let muted = cx.theme().muted_foreground;
        let opening = self.shown == Shown::Opening;
        let spinning = opening && !self.retried;
        let color = if self.error_note() {
            cx.theme().danger
        } else {
            muted
        };
        let pane = v_flex()
            .id("terminal-pane")
            .track_focus(&self.focus)
            .on_key_down(cx.listener(Self::key_down))
            .on_mouse_down(MouseButton::Left, cx.listener(Self::mouse_down))
            .on_mouse_down(MouseButton::Middle, cx.listener(Self::mouse_down))
            .on_mouse_down(MouseButton::Right, cx.listener(Self::mouse_down))
            .on_mouse_move(cx.listener(Self::mouse_move))
            .on_mouse_up(MouseButton::Left, cx.listener(Self::mouse_up))
            .on_mouse_up(MouseButton::Middle, cx.listener(Self::mouse_up))
            .on_mouse_up(MouseButton::Right, cx.listener(Self::mouse_up))
            .on_mouse_down_out(cx.listener(Self::pressed_outside))
            .on_scroll(cx.listener(Self::wheel))
            .on_hover(cx.listener(Self::hovered_pane))
            .when(self.hovered.is_some(), |pane| pane.cursor_pointer())
            .size_full()
            .text_role(TextRole::Body, cx)
            .child(self.input_handler(cx))
            .when(self.selecting, |pane| pane.child(self.drag_listeners(cx)))
            .when(self.bar_pressed, |pane| pane.child(self.bar_listeners(cx)));
        let note = |text: SharedString| {
            h_flex()
                .gap_1()
                .p_2()
                .text_color(color)
                .when(spinning, |note| {
                    note.child(Spinner::new().xsmall().color(muted))
                })
                .child(text)
        };
        let pane = match (&self.shown, self.status()) {
            (Shown::Nothing, _) => pane.child(note("Pick a session to open its terminal.".into())),
            // Stopped with no shell: the note fills the pane.
            (Shown::Said(_) | Shown::Gone, Some(status)) if self.link.is_none() => {
                pane.child(note(status.into()))
            }
            (_, status) => {
                let this = cx.entity().downgrade();
                let fitted = move |(size, bounds, cell): (_, Bounds<Pixels>, _), cx: &mut App| {
                    this.update(cx, |pane, cx| {
                        (pane.origin, pane.bounds, pane.cell) = (bounds.origin, bounds, cell);
                        pane.resized(size, cx);
                    })
                    .ok();
                };
                let cursor = (self.shown == Shown::Open && self.cursor_on)
                    .then(|| self.grid.cursor())
                    .flatten();
                let grid = draw(
                    self.cells(),
                    (self.size.1, cursor),
                    Preferences::terminal_text(cx),
                    palette(Preferences::read(cx, Appearance::theme)),
                    fitted,
                    window,
                    cx,
                );
                // Until there's a shell, the note lies over the grid, so the grid measures the room the shell will have.
                let over = opening || self.link.is_none();
                let grid = div().relative().size_full().child(grid);
                let grid = grid.children(self.scrollbar(cx));
                let grid = div().relative().flex_1().min_h_0().p_1().child(grid);
                match status {
                    Some(status) if over => {
                        pane.child(grid.child(note(status.into()).absolute().top_0().left_0()))
                    }
                    Some(status) => pane.child(grid).child(note(status.into())),
                    None => pane.child(grid),
                }
            }
        };
        self.with_menu(pane, cx)
    }
}

#[path = "terminal_pane_mouse.rs"]
mod mouse;

#[path = "terminal_pane_state.rs"]
mod state;

#[path = "terminal_pane_select.rs"]
mod select;

#[path = "terminal_pane_ime.rs"]
mod ime;

#[path = "terminal_pane_scrollbar.rs"]
mod scrollbar;

#[cfg(test)]
#[path = "terminal_pane_tests.rs"]
mod tests;
