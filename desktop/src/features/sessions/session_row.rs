//! `SessionRow` is one session in the column: its title, agent and machine. Selected, it's marked; a click selects
//! it. The still dot for a working session waits for the sessions list to carry that state.

use gpui_kit::component::{ActiveTheme as _, v_flex};
use gpui_kit::prelude::FluentBuilder as _;
use gpui_kit::*;

use crate::core::state::selected_session::SelectedSession;
use crate::core::state::session_item::SessionItem;

#[derive(IntoElement)]
pub struct SessionRow {
    pub session_id: String,
    pub title: SharedString,
    /// "claude · Workbox Test".
    pub place: SharedString,
    pub selected: bool,
}

impl SessionRow {
    pub fn of(item: &SessionItem, selected: bool) -> SessionRow {
        SessionRow {
            session_id: item.session_id.clone(),
            title: item.title().into(),
            place: item.place().into(),
            selected,
        }
    }
}

impl RenderOnce for SessionRow {
    fn render(self, _window: &mut Window, cx: &mut App) -> impl IntoElement {
        let theme = cx.theme();
        let session_id = self.session_id.clone();
        v_flex()
            .id(SharedString::from(format!("session:{}", self.session_id)))
            .w_full()
            .px_3()
            .py_2()
            .rounded(theme.radius)
            .cursor_pointer()
            .when(self.selected, |row| row.bg(theme.secondary))
            .hover(|row| row.bg(theme.muted))
            .on_click(move |_, _, cx| {
                let id = session_id.clone();
                SelectedSession::global(cx)
                    .update(cx, |selected, cx| selected.select(Some(id), cx));
            })
            .child(div().text_sm().truncate().child(self.title))
            .child(
                div()
                    .text_xs()
                    .text_color(theme.muted_foreground)
                    .truncate()
                    .child(self.place),
            )
    }
}
