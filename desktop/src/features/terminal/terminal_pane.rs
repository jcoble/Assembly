//! `TerminalPane` owns the bottom dock's Terminal tab: the open session's shell, run by the backend
//! (terminal_link.rs), shown from an `alacritty_terminal` grid (terminal_grid.rs, drawn by terminal_draw.rs).
//!
//! While the tab is shown it opens the session's shell: the running one, or a new one in the session's folder
//! (terminal#1, terminal#9). It follows the shell's output first and then reads what was kept, holding the rows that
//! come meanwhile, so nothing between the two is lost; a row past a gap (a reconnect) reads the kept output again from
//! where it got to (terminal#5). Hiding the tab or picking another session lets go of the view and closes nothing on
//! the backend; the tab leaving the dock closes the shell (g0-host#9). What's typed goes out one write at a time,
//! what's typed meanwhile in the next, in order (terminal#2). The backend routes remote sessions to their machine (terminal#12).

use std::mem;
use std::time::Duration;

use gpui_kit::component::dock::{BasePanel, Panel, PanelEvent, panel_handle, register_panel};
use gpui_kit::component::{ActiveTheme as _, v_flex};
use gpui_kit::*;

use super::terminal_draw::draw;
use super::terminal_grid::TerminalGrid;
use super::terminal_keys::{key_bytes, pasted};
use super::terminal_link::{Output, TerminalLink};
use super::terminal_palette::palette;
use crate::core::socket::update::Update;
use crate::core::state::appearance::Appearance;
use crate::core::state::preferences::Preferences;
use crate::core::state::selected_session::SelectedSession;

/// The layout's name for the tab (default_layout.rs).
const TERMINAL: &str = "Terminal";

/// The sizes the backend allows (TerminalHost.cs); the grid keeps to them too, so the two never differ.
const COLS: (usize, usize) = (20, 300);
const ROWS: (usize, usize) = (4, 100);

/// The grid's size before the pane has measured itself: the backend's own default (TerminalHost.cs).
const FIRST_SIZE: (usize, usize) = (96, 28);

/// How long the cursor shows, then hides, while it blinks (ghostty-web's interval, as the old app had).
const BLINK: Duration = Duration::from_millis(600);

#[derive(Debug, PartialEq)]
pub enum Shown {
    /// No session, or the tab is hidden.
    Nothing,
    Opening,
    Open,
    /// The program ended with this code; its output stays (terminal#7).
    Exited(i32),
    /// Why there's no terminal: the backend refused.
    Said(String),
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
    /// Where the grid starts in the window and each cell's size, as last drawn: what the mouse points at.
    origin: Point<Pixels>,
    cell: Size<Pixels>,
    /// A left-button drag is selecting.
    selecting: bool,
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
    /// The blinking cursor's half of the blink; it stays shown while the pane hasn't the focus.
    cursor_on: bool,
    _selected: Subscription,
    /// Draws again when the terminal's font or the theme changes.
    _preferences: Option<Subscription>,
    /// Tell a program that asks when the pane takes and loses the focus.
    _focused: [Subscription; 2],
    /// Opens the shell, then follows its rows; dropping it unsubscribes.
    _follow: Option<Task<()>>,
    _replay: Option<Task<()>>,
    _write: Option<Task<()>>,
    /// Blinks the cursor while the pane has the focus; dropping it stops the timer.
    _blink: Option<Task<()>>,
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
        Self {
            focus,
            active: false,
            session: None,
            grid: TerminalGrid::new(FIRST_SIZE.0, FIRST_SIZE.1),
            shown: Shown::Nothing,
            size: FIRST_SIZE,
            measured: false,
            origin: Point::default(),
            cell: Size::default(),
            selecting: false,
            wheeled: px(0.),
            link: None,
            held: None,
            typed: Vec::new(),
            writing: false,
            focus_wanted: false,
            cursor_on: true,
            _focused: focused,
            _preferences: Preferences::try_global(cx)
                .map(|preferences| cx.observe(&preferences, |_, _, cx| cx.notify())),
            _selected: cx.observe(&SelectedSession::global(cx), |pane, _, cx| pane.refresh(cx)),
            _follow: None,
            _replay: None,
            _write: None,
            _blink: None,
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
        self.typed.clear();
        self.grid = TerminalGrid::new(self.size.0, self.size.1);
        self.session = wanted.clone();
        self.shown = Shown::Nothing;
        cx.notify();
        let Some(_) = wanted else {
            return;
        };
        self.shown = Shown::Opening;
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
            let subscription = pane.update(cx, |pane, cx| match opened {
                Ok((link, started, before)) => {
                    pane.grid.feed(&before);
                    let subscription = match link.follow(cx) {
                        Ok(subscription) => subscription,
                        Err(why) => {
                            pane.shown = Shown::Said(why);
                            cx.notify();
                            return None;
                        }
                    };
                    // A reused shell may have another size, and the pane may have measured itself meanwhile.
                    if started != pane.size {
                        link.resize(pane.size, cx);
                    }
                    pane.link = Some(link);
                    // The subscription state starts catch-up, so its arrival cannot cause a second initial read.
                    Some(subscription)
                }
                Err(why) => {
                    pane.shown = Shown::Said(why);
                    cx.notify();
                    None
                }
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

    /// Reads the kept output from where the grid got to, then hands on the rows held meanwhile.
    fn catch_up(&mut self, cx: &mut Context<Self>) {
        let Some(mut link) = self.link.clone() else {
            return;
        };
        self.held = Some(Vec::new());
        self._replay = Some(cx.spawn(async move |pane, cx| {
            let kept = link.replay(cx).await;
            pane.update(cx, |pane, cx| {
                match kept {
                    Ok(bytes) => {
                        pane.grid.feed(&bytes);
                        pane.link = Some(link);
                    }
                    Err(why) => eprintln!("terminal: its output didn't read: {why}"),
                }
                for update in pane.held.take().unwrap_or_default() {
                    pane.output(update, cx);
                }
                cx.notify();
            })
            .ok();
        }));
    }

    /// One update from the shell's rows: its bytes to the grid, or its exit. A state is a new subscription after a
    /// reconnect, so output may have come meanwhile: the kept output is read again. A row past a gap waits for that
    /// read and is handed on after it.
    fn output(&mut self, update: Update, cx: &mut Context<Self>) {
        if let Some(held) = &mut self.held {
            // The read on its way covers what a state stands for.
            if !matches!(update, Update::State(_)) {
                held.push(update);
            }
            return;
        }
        let Some(link) = &mut self.link else {
            return;
        };
        if matches!(update, Update::State(_)) {
            if self.shown == Shown::Opening {
                self.shown = Shown::Open;
                self.focus_wanted = true;
            }
            self.catch_up(cx);
            return;
        }
        match link.step(&update) {
            Ok(output) => {
                match output {
                    Some(Output::Bytes(bytes)) => self.grid.feed(&bytes),
                    Some(Output::Exit(code)) => self.shown = Shown::Exited(code),
                    None => {}
                }
                cx.notify();
            }
            Err(()) => {
                self.catch_up(cx);
                self.held.get_or_insert_default().push(update);
            }
        }
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
        if size == self.size {
            return;
        }
        self.size = size;
        self.grid.resize(size.0, size.1);
        if let Some(link) = &self.link {
            link.resize(size, cx);
        }
        cx.notify();
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
        if self.shown != Shown::Open {
            return;
        }
        let bytes = match shortcut("v") {
            true => (cx.read_from_clipboard().and_then(|item| item.text()))
                .map(|text| pasted(&text, self.grid.bracketed_paste())),
            false => key_bytes(keystroke, self.grid.app_cursor()),
        };
        let Some(bytes) = bytes else {
            return;
        };
        cx.stop_propagation();
        self.grid.scroll_to_bottom();
        self.cursor_on = true;
        self.typed.extend(bytes);
        self.write(cx);
    }

    /// The pane took or lost the focus, which a program that asked for focus reports hears as `ESC [I` or `ESC [O`.
    fn focused(&mut self, focused: bool, cx: &mut Context<Self>) {
        self.blink(focused, cx);
        if self.shown != Shown::Open || !self.grid.focus_reporting() {
            return;
        }
        self.typed
            .extend_from_slice(if focused { b"\x1b[I" } else { b"\x1b[O" });
        self.write(cx);
    }

    /// Blinks the cursor while the pane has the focus, and shows it steady otherwise. It draws again only while the
    /// shell is open, where the cursor shows.
    fn blink(&mut self, focused: bool, cx: &mut Context<Self>) {
        self.cursor_on = true;
        self._blink = focused.then(|| {
            cx.spawn(async move |pane, cx| {
                loop {
                    cx.background_executor().timer(BLINK).await;
                    let blinked = pane.update(cx, |pane, cx| {
                        pane.cursor_on = !pane.cursor_on;
                        if pane.shown == Shown::Open {
                            cx.notify();
                        }
                    });
                    if blinked.is_err() {
                        return;
                    }
                }
            })
        });
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
                if let Err(why) = written {
                    // As the old app: the keys went nowhere, so the terminal stops and says why.
                    pane.shown = Shown::Said(format!("The terminal didn't take the keys: {why}"));
                    (pane._follow, pane._replay) = (None, None);
                    pane.typed.clear();
                    cx.notify();
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

    /// The line under the grid, or in its place.
    fn status(&self) -> Option<String> {
        match &self.shown {
            Shown::Opening => Some("Opening terminal…".into()),
            Shown::Exited(code) => Some(format!("exited with {code}")),
            Shown::Said(why) => Some(why.clone()),
            _ => None,
        }
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
        if self.held.is_none() && mem::take(&mut self.focus_wanted) {
            let focus = self.focus.clone();
            window.defer(cx, move |window, cx| window.focus(&focus, cx));
        }
        let muted = cx.theme().muted_foreground;
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
            .on_scroll(cx.listener(Self::wheel))
            .size_full()
            .text_size(px(13.));
        let note = |text: SharedString| div().p_2().text_color(muted).child(text);
        match &self.shown {
            Shown::Nothing => pane.child(note("Pick a session to open its terminal.".into())),
            Shown::Said(why) => pane.child(note(why.clone().into())),
            _ => {
                let this = cx.entity().downgrade();
                let fitted = move |(size, origin, cell), cx: &mut App| {
                    this.update(cx, |pane, cx| {
                        (pane.origin, pane.cell) = (origin, cell);
                        pane.resized(size, cx);
                    })
                    .ok();
                };
                let cursor = (self.shown == Shown::Open && self.cursor_on)
                    .then(|| self.grid.cursor())
                    .flatten();
                let grid = draw(
                    self.grid.cells(),
                    (self.size.1, cursor),
                    Preferences::terminal_text(cx),
                    palette(Preferences::read(cx, Appearance::theme)),
                    fitted,
                    window,
                    cx,
                );
                // While opening, the note lies over the grid, so the grid measures the room the shell will have.
                let opening = self.shown == Shown::Opening;
                let grid = div().relative().flex_1().min_h_0().p_1().child(grid);
                match self.status() {
                    Some(status) if opening => {
                        pane.child(grid.child(note(status.into()).absolute().top_0().left_0()))
                    }
                    Some(status) => pane.child(grid).child(note(status.into())),
                    None => pane.child(grid),
                }
            }
        }
    }
}

#[path = "terminal_pane_mouse.rs"]
mod mouse;

#[cfg(test)]
#[path = "terminal_pane_tests.rs"]
mod tests;
