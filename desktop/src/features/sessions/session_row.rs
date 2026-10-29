//! `SessionRow` is one session in the rail, an inbox row of three lines: its title (in the accent with a spinner while
//! it runs, else after its provider's mark) with how long it has run or its age; its state in colour ("offline" first
//! while its machine isn't answering), then its machine and recorded branch; and its newest assistant text, at most
//! two lines. A session another one started is a row like any other, at the same left edge. A click opens the
//! session; the right-click menu moves it between Working and Done or settles it (`sessions/mark`).

use std::rc::Rc;

use gpui_kit::component::menu::{ContextMenuExt as _, PopupMenuItem};
use gpui_kit::component::{ActiveTheme as _, Icon, Sizable as _, h_flex, v_flex};
use gpui_kit::prelude::FluentBuilder as _;
use gpui_kit::*;

use super::history::age::{age, elapsed_text};
use super::presence::Presence;
use super::rail_page::RailEntry;
use super::status::Status;
use crate::core::ui::spinner::RingSpinner;

/// Moves a session to a group: the pane's `mark`.
pub type OnMark = Rc<dyn Fn(String, Status, &mut App)>;
/// Opens the row's session.
pub type OnSession = Rc<dyn Fn(String, &mut Window, &mut App)>;

/// What a row needs from the pane besides its entry: its place, and what its clicks do.
#[derive(Clone)]
pub struct RowContext {
    pub selected: bool,
    /// Its machine didn't answer last time (`Machines::offline`).
    pub offline: bool,
    /// The backend clock's last tick, for its age or running time.
    pub now: Option<i64>,
    pub on_open: OnSession,
    pub on_mark: OnMark,
}

#[derive(IntoElement)]
pub struct SessionRow {
    entry: RailEntry,
    context: RowContext,
}

impl SessionRow {
    pub fn new(entry: RailEntry, context: RowContext) -> Self {
        SessionRow { entry, context }
    }
}

/// The words that start a row's second line: "offline" while its machine isn't answering, ahead of its state. Every
/// row names its state; one that isn't working, waiting, failed or marked Done is "Idle", an unread answer included
/// (the dot before its title says that).
pub fn status_words(presence: Presence, offline: bool) -> Vec<&'static str> {
    let mut words = Vec::new();
    if offline {
        words.push("offline");
    }
    words.push(presence.word().unwrap_or("Idle"));
    words
}

/// The rest of a row's second line: "Workbox Test · lane/rail". A local session is on This Mac.
pub fn details(entry: &RailEntry) -> String {
    let mut parts = vec![entry.machine.clone().unwrap_or_else(|| "This Mac".into())];
    parts.extend(entry.branch.clone().filter(|branch| !branch.is_empty()));
    parts.join(" · ")
}

/// Figures of equal width, so a running time doesn't shift as it counts.
fn tabular() -> FontFeatures {
    FontFeatures(std::sync::Arc::new(vec![("tnum".into(), 1)]))
}

/// The colour of a state's word.
fn tint(presence: Presence, cx: &App) -> Hsla {
    let theme = cx.theme();
    match presence {
        Presence::NeedsYou => theme.warning,
        Presence::Working => theme.info,
        Presence::Failed => theme.danger,
        Presence::Done => theme.success,
        Presence::Unread | Presence::Idle => theme.muted_foreground,
    }
}

impl RenderOnce for SessionRow {
    fn render(self, _window: &mut Window, cx: &mut App) -> impl IntoElement {
        let theme = cx.theme();
        let entry = self.entry;
        let title = match entry.title.as_deref().map(str::trim) {
            Some(title) if !title.is_empty() => title.to_string(),
            _ => entry.group.clone(),
        };
        let RowContext {
            selected,
            offline,
            now,
            on_open,
            on_mark,
        } = self.context;
        let presence = Presence::of(&entry);
        let (running, accent, faint) = (
            entry.running,
            theme.caret,
            theme.muted_foreground.opacity(0.7),
        );
        let id = entry.session_id.clone();
        // Hover is quieter than the selected row's fill.
        let hover = theme.secondary.opacity(0.6);
        let mark = match running {
            true => RingSpinner::new(theme.info).into_any_element(),
            false => Icon::empty()
                .path(entry.icon.clone())
                .xsmall()
                .text_color(theme.muted_foreground)
                .into_any_element(),
        };
        let time = now.map(|now| match running {
            true => div()
                .text_color(theme.info)
                .child(elapsed_text(now, entry.updated_at)),
            false => div().text_color(faint).child(age(now, entry.updated_at)),
        });
        let unread = (presence == Presence::Unread)
            .then(|| div().flex_none().size(px(6.)).rounded_full().bg(accent));
        let first = h_flex()
            .gap(px(7.))
            .children(unread)
            .child(mark)
            .child(
                div()
                    .flex_1()
                    .min_w_0()
                    .truncate()
                    .text_size(px(13.))
                    .line_height(px(18.))
                    .font_weight(FontWeight::SEMIBOLD)
                    .when(running, |text| text.text_color(accent))
                    .child(title),
            )
            .children(time.map(|time| {
                time.flex_none()
                    .text_size(px(11.5))
                    .font_features(tabular())
            }));
        let dot = |color: Hsla| div().flex_none().size(px(6.)).rounded_full().bg(color);
        let second = h_flex()
            .mt(px(3.))
            .gap(px(6.))
            .text_size(px(11.5))
            .line_height(px(16.))
            .text_color(faint)
            .children(status_words(presence, offline).into_iter().map(|word| {
                match word {
                    "Needs you" => h_flex()
                        .flex_none()
                        .h(px(18.))
                        .px(px(7.))
                        .gap_1()
                        .rounded_full()
                        .bg(theme.warning.opacity(0.12))
                        .text_color(theme.warning)
                        .font_weight(FontWeight::SEMIBOLD)
                        .child(dot(theme.warning))
                        .child(word),
                    "offline" => h_flex().flex_none().child(word),
                    _ => h_flex()
                        .flex_none()
                        .gap(px(5.))
                        .font_weight(FontWeight::MEDIUM)
                        .text_color(tint(presence, cx))
                        .child(dot(tint(presence, cx)))
                        .child(word),
                }
            }))
            .child(
                div()
                    .min_w_0()
                    .truncate()
                    .child(format!("· {}", details(&entry))),
            );
        let preview = entry.preview.clone().map(|preview| {
            div()
                .mt_1()
                .text_size(px(12.))
                .line_height(px(17.))
                .text_color(theme.muted_foreground)
                .line_clamp(2)
                .child(preview)
        });
        let opened = id.clone();
        let selector = format!("session-{id}");
        let status = Status::of(entry.completed_at);
        v_flex()
            .id(SharedString::from(format!("session:{id}")))
            .debug_selector(move || selector.clone())
            .relative()
            .w_full()
            .pt(px(9.))
            .pb(px(10.))
            .pl(px(16.))
            .pr(px(14.))
            .border_t_1()
            .border_color(theme.table_row_border)
            .cursor_pointer()
            .when(selected, |row| {
                row.bg(theme.secondary).child(
                    div()
                        .absolute()
                        .left_0()
                        .top_0()
                        .bottom_0()
                        .w(px(2.))
                        .bg(accent),
                )
            })
            .when(!selected, |row| row.hover(|row| row.bg(hover)))
            .on_click(move |_, window, cx| on_open(opened.clone(), window, cx))
            .child(first)
            .child(second)
            .children(preview)
            .context_menu(move |mut menu, _, _| {
                for (label, to) in status.moves() {
                    let (id, on_mark) = (id.clone(), on_mark.clone());
                    menu = menu.item(
                        PopupMenuItem::new(label)
                            .on_click(move |_, _, cx| on_mark(id.clone(), to, cx)),
                    );
                }
                menu
            })
    }
}

#[cfg(test)]
#[path = "session_row_tests.rs"]
mod tests;
