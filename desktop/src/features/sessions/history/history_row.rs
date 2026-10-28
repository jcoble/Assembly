//! `history_row` is one past session in the History tab: its title, agent and age. A click opens it, as the sessions
//! column's row does; the one open is marked.

use gpui_kit::component::{ActiveTheme as _, h_flex};
use gpui_kit::prelude::FluentBuilder as _;
use gpui_kit::*;

use super::age::age;
use crate::core::state::selected_session::SelectedSession;
use crate::core::state::session_item::SessionItem;

/// `now` is the clock's last tick, `None` before the first.
pub fn history_row(item: &SessionItem, now: Option<i64>, picked: bool, cx: &App) -> Stateful<Div> {
    let theme = cx.theme();
    let id = item.session_id.clone();
    let (row, title_id, age_id) = (
        format!("history-session-{id}"),
        format!("history-title-{id}"),
        format!("history-age-{id}"),
    );
    h_flex()
        .id(SharedString::from(row.clone()))
        .debug_selector(move || row)
        .h(px(28.))
        .pl(px(26.))
        .pr_2()
        .gap_2()
        .rounded(px(6.))
        .cursor_pointer()
        .when(picked, |line| line.bg(theme.secondary))
        .when(!picked, |line| line.hover(|line| line.bg(theme.muted)))
        .on_click(move |_, _, cx| {
            let id = id.clone();
            SelectedSession::global(cx).update(cx, |selected, cx| selected.select(Some(id), cx));
        })
        .child(
            div()
                .debug_selector(move || title_id)
                .flex_1()
                .min_w_0()
                .truncate()
                .child(item.title()),
        )
        .child(
            div()
                .flex_none()
                .text_color(theme.muted_foreground)
                .child(item.agent.clone()),
        )
        .child(
            div()
                .flex_none()
                // Room for "11mo" on one line, so the ages line up, kept before the first tick so the titles don't
                // move when it comes.
                .min_w(px(32.))
                .whitespace_nowrap()
                .text_right()
                .text_color(theme.muted_foreground)
                .when_some(now, |cell, now| {
                    cell.debug_selector(move || age_id)
                        .child(age(now, item.updated_at))
                }),
        )
}
