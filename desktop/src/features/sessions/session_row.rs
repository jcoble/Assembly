//! `SessionRow` is one session in the rail, always three lines on the kit's list item: its title (in the accent with a
//! spinner while it runs, else after its provider's mark) with how long it has run or its age; its state in colour
//! ("offline" first while its machine isn't answering), then its machine and recorded branch; and its newest assistant
//! text, one line, or its project before it has any. A session another one started is a row like any other, at the same left edge. A click opens the
//! session; the right-click menu moves it between Working and Done or settles it (`sessions/mark`).

use crate::core::theme::text::{TextRole, TextRoleExt as _};
use std::rc::Rc;

use gpui_kit::component::menu::{ContextMenuExt as _, PopupMenuItem};
use gpui_kit::component::list::ListItem;
use gpui_kit::component::label::Label;
use gpui_kit::component::tag::Tag;
use gpui_kit::component::{ActiveTheme as _, Icon, Sizable as _, h_flex, v_flex};
use gpui_kit::*;

use super::history::age::{age, elapsed_text};
use super::presence::Presence;
use super::rail_page::RailEntry;
use super::status::Status;
use crate::core::ui::spinner::RingSpinner;
use crate::core::state::preferences::Preferences;

/// Above and below a row's three lines.
const ROW_PADDING: Pixels = px(7.);

/// Moves a session to a group: the pane's `mark`.
pub type OnMark = Rc<dyn Fn(String, Status, &mut App)>;
/// Opens the row's session.
pub type OnSession = Rc<dyn Fn(String, Option<String>, &mut Window, &mut App)>;

/// What a row needs from the pane besides its entry: its place, and what its clicks do.
#[derive(Clone)]
pub struct RowContext {
    pub selected: bool,
    pub needs_you: bool,
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
        let (_, title_height) = Preferences::read(cx, |appearance| appearance.ui_text(TextRole::Title));
        let theme = cx.theme();
        let (muted, accent, radius) = (theme.muted_foreground, theme.caret, theme.radius);
        let info = theme.info;
        let entry = self.entry;
        let title = match entry.title.as_deref().map(str::trim) {
            Some(title) if !title.is_empty() => title.to_string(),
            _ => entry.group.clone(),
        };
        let RowContext {
            selected,
            needs_you,
            offline,
            now,
            on_open,
            on_mark,
        } = self.context;
        let presence = Presence::of(&entry, needs_you);
        let running = entry.running;
        let id = entry.session_id.clone();
        let route = entry.machine.clone();
        let mark = match running {
            true => RingSpinner::new(info).into_any_element(),
            false => Icon::empty()
                .path(entry.icon.clone())
                .small()
                .text_color(muted)
                .into_any_element(),
        };
        let time = now.map(|now| match running {
            true => (elapsed_text(now, entry.updated_at), info),
            false => (age(now, entry.updated_at), muted),
        });
        // In the left gutter, level with the title, so it doesn't move the title.
        let unread = (presence == Presence::Unread).then(|| {
            div()
                .absolute()
                .left(px(3.))
                .top(ROW_PADDING + title_height * 0.5 - px(3.))
                .size(px(6.))
                .rounded_full()
                .bg(accent)
        });
        let title_color = match (running, offline) {
            (true, _) => accent,
            (false, true) => muted,
            (false, false) => cx.theme().foreground,
        };
        let first = h_flex()
            .gap_2()
            .child(mark)
            .child(
                Label::new(title)
                    .flex_1()
                    .min_w_0()
                    .truncate()
                    .text_role(TextRole::Title, cx)
                    .font_weight(FontWeight::MEDIUM)
                    .text_color(title_color),
            )
            .children(time.map(|(time, color)| {
                Label::new(time)
                    .flex_none()
                    .text_role(TextRole::Meta, cx)
                    .font_features(tabular())
                    .text_color(color)
            }));
        let second = h_flex()
            .gap_1p5()
            .children(status_words(presence, offline).into_iter().map(|word| {
                match word {
                    "Needs you" => Tag::warning()
                        .small()
                        .rounded_full()
                        .flex_none()
                        .text_role(TextRole::Meta, cx)
                        .child(word)
                        .into_any_element(),
                    "offline" => Label::new(word)
                        .flex_none()
                        .text_role(TextRole::Meta, cx)
                        .font_weight(FontWeight::MEDIUM)
                        .text_color(muted)
                        .into_any_element(),
                    // The state in its colour.
                    _ => Label::new(word)
                        .flex_none()
                        .text_role(TextRole::Meta, cx)
                        .font_weight(FontWeight::MEDIUM)
                        .text_color(tint(presence, cx))
                        .into_any_element(),
                }
            }))
            .child(
                Label::new(format!("· {}", details(&entry)))
                    .flex_1()
                    .min_w_0()
                    .truncate()
                    .text_role(TextRole::Meta, cx)
                    .text_color(muted),
            );
        // Always a third line, so every row is the same height: its newest answer, or its project before it has one.
        let third = Label::new(entry.preview.clone().unwrap_or_else(|| entry.group.clone()))
            .w_full()
            .truncate()
            .text_role(TextRole::Meta, cx)
            .text_color(muted);
        let opened = id.clone();
        let selector = format!("session-{id}");
        let status = Status::of(entry.completed_at);
        // The row's own surface says it's selected; the kit's item owns the hover and the selected fill.
        let item = ListItem::new(SharedString::from(format!("session:{id}")))
            .debug_selector(move || selector.clone())
            .selected(selected)
            .w_full()
            .px_3()
            .py(ROW_PADDING)
            .rounded(radius)
            .cursor_pointer()
            .on_click(move |_, window, cx| on_open(opened.clone(), route.clone(), window, cx))
            .children(unread)
            .child(v_flex().w_full().min_w_0().gap_0p5().child(first).child(second).child(third))
            .context_menu(move |mut menu, _, _| {
                for (label, to) in status.moves() {
                    let (id, on_mark) = (id.clone(), on_mark.clone());
                    menu = menu.item(
                        PopupMenuItem::new(label)
                            .on_click(move |_, _, cx| on_mark(id.clone(), to, cx)),
                    );
                }
                menu
            });
        div().w_full().px_2().py_px().child(item)
    }
}

#[cfg(test)]
#[path = "session_row_tests.rs"]
mod tests;
