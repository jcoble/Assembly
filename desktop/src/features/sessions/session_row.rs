//! `SessionRow` is one session in the column: its title, agent and machine, and a still dot for what it's doing
//! (its `Presence`). Selected, it's marked; a click selects it, and its right-click menu moves it to another group
//! (`sessions/mark`).

use std::rc::Rc;

use gpui_kit::component::menu::{ContextMenuExt as _, PopupMenuItem};
use gpui_kit::component::{ActiveTheme as _, h_flex, v_flex};
use gpui_kit::prelude::FluentBuilder as _;
use gpui_kit::*;

use super::presence::Presence;
use super::status::Status;
use crate::core::state::selected_session::SelectedSession;
use crate::core::state::session_item::SessionItem;

/// A session's line in the list.
pub const ROW_HEIGHT: Pixels = px(48.);

/// Moves a session to a group: the pane's `mark`.
pub type OnMark = Rc<dyn Fn(String, Status, &mut App)>;

#[derive(IntoElement)]
pub struct SessionRow {
    pub session_id: String,
    pub title: SharedString,
    /// "claude · Workbox Test".
    pub place: SharedString,
    pub selected: bool,
    /// What it's doing: every presence but Idle has a still dot.
    pub presence: Presence,
    pub status: Status,
    on_mark: Option<OnMark>,
}

impl SessionRow {
    pub fn of(item: &SessionItem, selected: bool) -> SessionRow {
        SessionRow {
            session_id: item.session_id.clone(),
            title: item.title().into(),
            place: item.place().into(),
            selected,
            presence: Presence::of(item),
            status: Status::of(item),
            on_mark: None,
        }
    }

    pub fn on_mark(mut self, on_mark: OnMark) -> Self {
        self.on_mark = Some(on_mark);
        self
    }
}

impl RenderOnce for SessionRow {
    fn render(self, _window: &mut Window, cx: &mut App) -> impl IntoElement {
        let theme = cx.theme();
        let session_id = self.session_id.clone();
        let dot = match self.presence {
            Presence::NeedsYou => Some(theme.warning),
            Presence::Working => Some(theme.primary),
            Presence::Failed => Some(theme.danger),
            Presence::Unread => Some(theme.muted_foreground),
            Presence::Idle => None,
        };
        let place = match self.presence.word() {
            Some(word) => format!("{} · {word}", self.place),
            None => self.place.to_string(),
        };
        let (menu_id, status, on_mark) = (self.session_id.clone(), self.status, self.on_mark);
        v_flex()
            .id(SharedString::from(format!("session:{}", self.session_id)))
            .w_full()
            .h(ROW_HEIGHT)
            .justify_center()
            .px_3()
            .rounded(theme.radius)
            .cursor_pointer()
            .when(self.selected, |row| row.bg(theme.secondary))
            .hover(|row| row.bg(theme.muted))
            .on_click(move |_, _, cx| {
                let id = session_id.clone();
                SelectedSession::global(cx)
                    .update(cx, |selected, cx| selected.select(Some(id), cx));
            })
            .child(
                h_flex()
                    .gap_2()
                    .when_some(dot, |line, color| {
                        line.child(div().flex_none().size(px(6.)).rounded_full().bg(color))
                    })
                    .child(div().text_sm().truncate().child(self.title)),
            )
            .child(
                div()
                    .text_xs()
                    .text_color(match self.presence {
                        Presence::NeedsYou => theme.warning,
                        Presence::Failed => theme.danger,
                        _ => theme.muted_foreground,
                    })
                    .truncate()
                    .child(place),
            )
            .context_menu(move |mut menu, _, _| {
                for (label, to) in status.moves() {
                    let (id, on_mark) = (menu_id.clone(), on_mark.clone());
                    menu = menu.item(PopupMenuItem::new(label).on_click(move |_, _, cx| {
                        if let Some(on_mark) = &on_mark {
                            on_mark(id.clone(), to, cx)
                        }
                    }));
                }
                menu
            })
    }
}

#[cfg(test)]
#[path = "session_row_tests.rs"]
mod tests;
