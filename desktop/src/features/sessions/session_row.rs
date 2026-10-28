//! `SessionRow` is one session in the rail. A parent shows its title over its provider, machine, recorded branch, age
//! and helpers, with its state in a pill; one with helpers has a chevron that shows or hides them. A helper sits
//! indented under its parent on one line, with its state as a word and "offline" beside it while its machine isn't
//! answering. A parent's provider glyph sits before its title. A click opens the row's own session, a helper's
//! too; a parent's right-click menu moves it between Working and Done or settles it (`sessions/mark`).

use std::rc::Rc;

use gpui_kit::assets::IconName;
use gpui_kit::component::menu::{ContextMenuExt as _, PopupMenuItem};
use gpui_kit::component::{ActiveTheme as _, Icon, Sizable as _, h_flex, v_flex};
use gpui_kit::prelude::FluentBuilder as _;
use gpui_kit::*;

use super::history::age::age;
use super::presence::Presence;
use super::rail_page::RailEntry;
use super::status::Status;

/// Moves a session to a group: the pane's `mark`.
pub type OnMark = Rc<dyn Fn(String, Status, &mut App)>;
/// Opens a session (the pane's `open`), or shows or hides a parent's helpers: given the row's session id.
pub type OnSession = Rc<dyn Fn(String, &mut Window, &mut App)>;

/// What a row needs from the pane besides its entry: its place, and what its clicks do.
#[derive(Clone)]
pub struct RowContext {
    pub selected: bool,
    /// Its machine didn't answer last time (`Machines::offline`).
    pub offline: bool,
    /// Its helpers show under it.
    pub expanded: bool,
    /// The backend clock's last tick, for its age.
    pub now: Option<i64>,
    pub on_open: OnSession,
    pub on_toggle: OnSession,
    pub on_mark: OnMark,
}

#[derive(IntoElement)]
pub struct SessionRow {
    entry: RailEntry,
    helper: bool,
    context: RowContext,
}

impl SessionRow {
    pub fn new(entry: RailEntry, helper: bool, context: RowContext) -> Self {
        SessionRow {
            entry,
            helper,
            context,
        }
    }
}

/// The line under a parent's title: "claude · Workbox Test · lane/rail · 2m · 3 helpers", with "offline" when its
/// machine isn't answering. Helpers are counted only while they're hidden.
pub fn subtitle(entry: &RailEntry, now: Option<i64>, offline: bool, expanded: bool) -> String {
    let mut parts = vec![
        entry.agent.clone(),
        entry.machine.clone().unwrap_or_else(|| "This Mac".into()),
    ];
    parts.extend(entry.branch.clone().filter(|branch| !branch.is_empty()));
    parts.extend(now.map(|now| age(now, entry.updated_at)));
    match entry.helpers {
        0 => {}
        _ if expanded => {}
        1 => parts.push("1 helper".into()),
        n => parts.push(format!("{n} helpers")),
    }
    if offline {
        parts.push("offline".into());
    }
    parts.join(" · ")
}

/// A helper's one line ends with its state, and "offline" beside it while its own machine isn't answering.
pub fn helper_word(presence: Presence, offline: bool) -> Option<String> {
    match (presence.word(), offline) {
        (Some(word), true) => Some(format!("{word} · offline")),
        (word, false) => word.map(String::from),
        (None, true) => Some("offline".into()),
    }
}

impl RenderOnce for SessionRow {
    fn render(self, _window: &mut Window, cx: &mut App) -> impl IntoElement {
        let theme = cx.theme();
        let RowContext {
            selected,
            offline,
            expanded,
            now,
            on_open,
            on_toggle,
            on_mark,
        } = self.context;
        let presence = Presence::of(&self.entry);
        let tint = match presence {
            Presence::NeedsYou => theme.warning,
            Presence::Working => theme.primary,
            Presence::Failed => theme.danger,
            Presence::Done => theme.success,
            Presence::Unread | Presence::Idle => theme.muted_foreground,
        };
        let title = match self.entry.title.as_deref().map(str::trim) {
            Some(title) if !title.is_empty() => title.to_string(),
            _ => self.entry.group.clone(),
        };
        let id = self.entry.session_id.clone();
        let opened = id.clone();
        let chevron = (!self.helper && self.entry.helpers > 0).then(|| {
            let id = id.clone();
            div()
                .id(SharedString::from(format!("helpers:{id}")))
                .cursor_pointer()
                .child(
                    Icon::new(if expanded {
                        IconName::ChevronDown
                    } else {
                        IconName::ChevronRight
                    })
                    .xsmall()
                    .text_color(theme.muted_foreground),
                )
                .on_click(move |_, window, cx| {
                    cx.stop_propagation();
                    on_toggle(id.clone(), window, cx);
                })
        });
        let helper = self.helper;
        let word = match helper {
            true => helper_word(presence, offline),
            false => presence.word().map(String::from),
        };
        let word = word.map(|word| {
            h_flex()
                .flex_none()
                .items_center()
                .gap_1()
                .text_xs()
                .text_color(tint)
                .when(!helper, |pill| {
                    pill.h(px(20.))
                        .px_2()
                        .rounded_full()
                        .border_1()
                        .border_color(tint.opacity(0.4))
                        .bg(tint.opacity(0.12))
                        .child(div().size(px(6.)).rounded_full().bg(tint))
                })
                .child(word)
        });
        let unread = (presence == Presence::Unread).then(|| {
            div()
                .flex_none()
                .size(px(6.))
                .rounded_full()
                .bg(theme.muted_foreground)
        });
        let text = v_flex()
            .flex_1()
            .min_w_0()
            .child(
                h_flex()
                    .gap_2()
                    .children(unread)
                    .when(!helper, |line| {
                        line.child(
                            Icon::empty()
                                .path(self.entry.icon.clone())
                                .small()
                                .text_color(theme.muted_foreground),
                        )
                    })
                    .child(div().text_sm().truncate().child(title)),
            )
            .when(!helper, |text| {
                text.child(
                    div()
                        .text_xs()
                        .truncate()
                        .text_color(if offline {
                            theme.warning
                        } else {
                            theme.muted_foreground
                        })
                        .child(subtitle(&self.entry, now, offline, expanded)),
                )
            });
        let selector = format!("session-{id}");
        let row = h_flex()
            .id(SharedString::from(format!("session:{id}")))
            .debug_selector(move || selector.clone())
            .w_full()
            .min_h(if helper { px(30.) } else { px(48.) })
            .gap_2()
            .py_1()
            .pr_2()
            .pl(if helper { px(32.) } else { px(6.) })
            .rounded(theme.radius_lg)
            .cursor_pointer()
            .when(selected, |row| row.bg(theme.secondary))
            .hover(|row| row.bg(theme.muted))
            .on_click(move |_, window, cx| on_open(opened.clone(), window, cx))
            .when(!helper, |row| {
                row.child(div().flex_none().w(px(14.)).children(chevron))
            })
            .child(text)
            .children(word);
        if helper {
            return row.into_any_element();
        }
        let status = Status::of(self.entry.completed_at);
        row.context_menu(move |mut menu, _, _| {
            for (label, to) in status.moves() {
                let (id, on_mark) = (id.clone(), on_mark.clone());
                menu = menu.item(
                    PopupMenuItem::new(label).on_click(move |_, _, cx| on_mark(id.clone(), to, cx)),
                );
            }
            menu
        })
        .into_any_element()
    }
}

#[cfg(test)]
#[path = "session_row_tests.rs"]
mod tests;
