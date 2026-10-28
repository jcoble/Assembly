//! `TerminalPane` is the bottom dock's Terminal tab: the selected session's terminal, drawn by GPUI in the pane (so
//! menus, the palette and drags cover it as anything else), in the theme's monospace font and colors. It opens the
//! terminal the first time it's drawn with a session selected, never while its dock is closed, and drops it on every
//! session switch, when another tab comes to the front and when the pane is removed (terminal#5). Replaces
//! RightPanel.svelte's terminal slot and WorkspaceTerminal.svelte's view.

use alacritty_terminal::term::TermMode;
use gpui_kit::component::dock::{BasePanel, Panel, PanelEvent, panel_handle, register_panel};
use gpui_kit::component::{ActiveTheme as _, v_flex};
use gpui_kit::prelude::FluentBuilder as _;
use gpui_kit::*;

use super::keys::key_bytes;
use super::live::Live;
use super::palette::Palette;
use super::screen::{Look, Size};
use crate::core::state::selected_session::SelectedSession;

/// The pane name in the saved layout (g0-shape's `TERMINAL`).
const NAME: &str = "Terminal";

pub struct TerminalPane {
    focus: FocusHandle,
    selected: Entity<SelectedSession>,
    /// The terminal shown, while there is one.
    pub(super) live: Option<Live>,
    /// The pane's size in cells, once measured.
    size: Option<Size>,
    /// What a scroll moved short of a whole line, kept for the next (a trackpad moves a few pixels at a time).
    scrolled: Pixels,
    _selected: Subscription,
}

impl TerminalPane {
    /// Registers the pane under the layout's Terminal name, after the placeholders. Tab and Shift-Tab go to the
    /// shell, not to the window's focus moves.
    pub fn register(cx: &mut App) {
        register_panel(cx, NAME, |_, _, cx| panel_handle(cx.new(TerminalPane::new)));
        cx.bind_keys([
            KeyBinding::new("tab", NoAction, Some(NAME)),
            KeyBinding::new("shift-tab", NoAction, Some(NAME)),
        ]);
    }

    fn new(cx: &mut Context<Self>) -> Self {
        let selected = SelectedSession::global(cx);
        let _selected = cx.observe(&selected, |pane, selected, cx| {
            let session = selected.read(cx).session_id().map(String::from);
            if pane.live.as_ref().map(|live| &live.session) != session.as_ref() {
                pane.live = None;
                cx.notify();
            }
        });
        Self {
            focus: cx.focus_handle(),
            selected,
            live: None,
            size: None,
            scrolled: px(0.),
            _selected,
        }
    }

    /// Opens the selected session's terminal once the pane has a size, unless one is shown. The backend refuses a
    /// session on another machine, which this window can't reach yet (terminal#12).
    fn open_selected(&mut self, cx: &mut Context<Self>) {
        let Some(session) = self.selected.read(cx).session_id() else {
            return;
        };
        if let (None, Some(size)) = (&self.live, self.size) {
            self.open(session.to_string(), size, cx);
        }
    }

    fn measured(&mut self, size: Size, cx: &mut Context<Self>) {
        if self.size == Some(size) {
            return;
        }
        self.size = Some(size);
        if self.live.is_some() {
            self.resized(size, cx);
        }
        cx.notify();
    }

    fn key(&mut self, event: &KeyDownEvent, cx: &mut Context<Self>) {
        let Some(live) = &mut self.live else { return };
        let keystroke = &event.keystroke;
        let paste = keystroke.key == "v"
            && (keystroke.modifiers.platform
                || keystroke.modifiers.control && keystroke.modifiers.shift);
        let bytes = if paste {
            let Some(text) = cx.read_from_clipboard().and_then(|item| item.text()) else {
                return;
            };
            if live.screen.mode().contains(TermMode::BRACKETED_PASTE) {
                // Without its escapes, so pasted text can't end the paste early.
                let text = text.replace('\x1b', "");
                format!("\x1b[200~{text}\x1b[201~").into_bytes()
            } else {
                text.into_bytes()
            }
        } else {
            let app_cursor = live.screen.mode().contains(TermMode::APP_CURSOR);
            let Some(bytes) = key_bytes(keystroke, app_cursor) else {
                return;
            };
            bytes
        };
        cx.stop_propagation();
        live.screen.scroll_to_bottom();
        self.write(bytes, cx);
        cx.notify();
    }

    fn scrolled(&mut self, event: &ScrollEvent, line: Pixels, cx: &mut Context<Self>) {
        let Some(live) = &mut self.live else { return };
        self.scrolled += event.delta.pixel_delta(line).y;
        let lines = (self.scrolled / line).trunc();
        if lines != 0. {
            self.scrolled -= line * lines;
            live.screen.scroll(lines as i32);
            cx.notify();
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
        NAME
    }

    fn closable(&self, _cx: &App) -> bool {
        false
    }

    /// Another tab came to the front: the view goes, the backend keeps the terminal.
    fn set_active(&mut self, active: bool, _window: &mut Window, cx: &mut Context<Self>) {
        if !active {
            self.live = None;
            cx.notify();
        }
    }

    fn on_removed(&mut self, _window: &mut Window, _cx: &mut Context<Self>) {
        self.live = None;
    }
}

impl Panel for TerminalPane {
    fn title(&mut self, _window: &mut Window, _cx: &mut Context<Self>) -> impl IntoElement {
        NAME
    }
}

impl Render for TerminalPane {
    fn render(&mut self, window: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        self.open_selected(cx);
        let theme = cx.theme();
        let palette = Palette::from_theme(theme);
        let (family, font_size) = (theme.mono_font_family.clone(), theme.mono_font_size);
        let (muted, chip) = (theme.muted_foreground, theme.muted);
        let line = (font_size * 1.35).round();
        let base = font(family.clone());
        let cell = window
            .text_system()
            .advance(window.text_system().resolve_font(&base), font_size, 'm')
            .map(|size| size.width)
            .unwrap_or(font_size * 0.6);
        let focused = self.focus.is_focused(window);
        let entity = cx.entity();
        let notice = |text: String| div().p_2().text_color(muted).child(text);
        let body = match &self.live {
            None if self.selected.read(cx).session_id().is_none() => {
                notice("Pick a session to open its terminal.".into()).into_any_element()
            }
            Some(Live {
                error: Some(error), ..
            }) => notice(error.clone()).into_any_element(),
            Some(live) => {
                let exited = live.output.exit();
                let lines = live.screen.lines(&palette, focused && exited.is_none());
                v_flex()
                    .children(lines.into_iter().map(|text| {
                        let runs = text
                            .runs
                            .iter()
                            .map(|(len, look)| run(*len, look, &base))
                            .collect();
                        div()
                            .h(line)
                            .whitespace_nowrap()
                            .child(StyledText::new(text.text).with_runs(runs))
                    }))
                    // Over the last lines, since a full screen leaves no room below them.
                    .when_some(exited, |this, code| {
                        this.child(
                            div()
                                .absolute()
                                .bottom_1()
                                .right_2()
                                .px_2()
                                .rounded_md()
                                .bg(chip)
                                .text_color(muted)
                                .child(format!("Terminal exited (code {code})")),
                        )
                    })
                    .into_any_element()
            }
            None => div().into_any_element(),
        };
        div()
            .id("terminal")
            .track_focus(&self.focus)
            .key_context(NAME)
            .size_full()
            .overflow_hidden()
            .px_1()
            .bg(palette.background)
            .text_color(palette.foreground)
            .font_family(family)
            .text_size(font_size)
            .line_height(line)
            .on_key_down(cx.listener(|pane, event, _, cx| pane.key(event, cx)))
            .on_scroll(cx.listener(move |pane, event, _, cx| pane.scrolled(event, line, cx)))
            .child(
                div().relative().size_full().child(body).child(
                    // Measures the pane in cells as it lays out.
                    canvas(
                        move |bounds, _, cx| {
                            let size = Size {
                                cols: ((bounds.size.width / cell).floor() as u16).clamp(20, 300),
                                rows: ((bounds.size.height / line).floor() as u16).clamp(4, 100),
                            };
                            let entity = entity.clone();
                            cx.defer(move |cx| {
                                entity.update(cx, |pane, cx| pane.measured(size, cx))
                            });
                        },
                        |_, _, _, _| {},
                    )
                    .absolute()
                    .size_full(),
                ),
            )
    }
}

/// One run of cells in the theme's monospace font, bold or italic as the cells are.
fn run(len: usize, look: &Look, base: &Font) -> TextRun {
    let mut font = base.clone();
    if look.bold {
        font.weight = FontWeight::BOLD;
    }
    if look.italic {
        font.style = FontStyle::Italic;
    }
    TextRun {
        len,
        font,
        color: look.foreground,
        background_color: look.background,
        underline: look.underline.then(|| UnderlineStyle {
            thickness: px(1.),
            color: None,
            wavy: false,
        }),
        strikethrough: look.strike.then(|| StrikethroughStyle {
            thickness: px(1.),
            color: None,
        }),
    }
}
