//! One ordinary session row, showing its own state and metadata. Started sessions sit one level in.

use std::rc::Rc;

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
/// Opens the row's session.
pub type OnSession = Rc<dyn Fn(String, &mut Window, &mut App)>;

/// What a row needs from the pane besides its entry: its place, and what its clicks do.
#[derive(Clone)]
pub struct RowContext {
    pub selected: bool,
    /// Its machine didn't answer last time (`Machines::offline`).
    pub offline: bool,
    /// The backend clock's last tick, for its age.
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

/// The session's provider, machine, recorded branch, age and offline state.
pub fn subtitle(entry: &RailEntry, now: Option<i64>, offline: bool) -> String {
    let mut parts = vec![
        entry.agent.clone(),
        entry.machine.clone().unwrap_or_else(|| "This Mac".into()),
    ];
    parts.extend(entry.branch.clone().filter(|branch| !branch.is_empty()));
    parts.extend(now.map(|now| age(now, entry.updated_at)));
    if offline {
        parts.push("offline".into());
    }
    parts.join(" · ")
}

impl RenderOnce for SessionRow {
    fn render(self, _window: &mut Window, cx: &mut App) -> impl IntoElement {
        let theme = cx.theme();
        let RowContext {
            selected,
            offline,
            now,
            on_open,
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
        let word = presence.word();
        let word = word.map(|word| {
            h_flex()
                .flex_none()
                .items_center()
                .gap_1()
                .text_xs()
                .text_color(tint)
                .h(px(20.))
                .px_2()
                .rounded_full()
                .border_1()
                .border_color(tint.opacity(0.4))
                .bg(tint.opacity(0.12))
                .child(div().size(px(6.)).rounded_full().bg(tint))
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
                    .child(
                        Icon::empty()
                            .path(self.entry.icon.clone())
                            .small()
                            .text_color(theme.muted_foreground),
                    )
                    .child(div().text_sm().truncate().child(title)),
            )
            .child(
                div()
                    .text_xs()
                    .truncate()
                    .text_color(if offline {
                        theme.warning
                    } else {
                        theme.muted_foreground
                    })
                    .child(subtitle(&self.entry, now, offline)),
            );
        let selector = format!("session-{id}");
        let row = h_flex()
            .id(SharedString::from(format!("session:{id}")))
            .debug_selector(move || selector.clone())
            .w_full()
            .min_h(px(48.))
            .gap_2()
            .py_1()
            .pr_2()
            .pl(if self.entry.depth == 1 {
                px(32.)
            } else {
                px(6.)
            })
            .rounded(theme.radius_lg)
            .cursor_pointer()
            .when(selected, |row| row.bg(theme.secondary))
            .hover(|row| row.bg(theme.muted))
            .on_click(move |_, window, cx| on_open(opened.clone(), window, cx))
            .child(text)
            .children(word);
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
