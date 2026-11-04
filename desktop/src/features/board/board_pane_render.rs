//! `BoardPane`'s render: the header with its counts and state filters, the card list and the "Load more" footer.
use super::BoardPane;
use crate::core::theme::text::{TextRole, TextRoleExt as _};
use crate::features::board::board_card::{self, STATES};
use gpui_kit::component::button::{Button, ButtonVariants as _};
use gpui_kit::component::tab::{Tab, TabBar};
use gpui_kit::component::{ActiveTheme as _, Sizable as _, h_flex, v_flex};
use gpui_kit::*;

impl Render for BoardPane {
    fn render(&mut self, _: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        let theme = cx.theme();
        let (muted, danger) = (theme.muted_foreground, theme.danger);
        let counts = self.state.counts.clone();
        let headline = match counts.as_ref().map_or(0, |counts| counts.needs_you) {
            0 => "Nothing needs you".to_string(),
            1 => "1 thing needs you".to_string(),
            n => format!("{n} things need you"),
        };
        let selected = self
            .state
            .filter
            .as_deref()
            .and_then(|filter| STATES.iter().position(|(state, _)| *state == filter))
            .map_or(0, |ix| ix + 1);
        let filters = [("All", None)]
            .into_iter()
            .chain(STATES.iter().map(|(state, label)| (*label, Some(*state))))
            .map(|(label, state)| {
                Tab::new()
                    .label(label)
                    .debug_selector(move || format!("board-filter-{label}"))
                    .on_click(cx.listener(move |pane, _, _, cx| pane.filter(state, cx)))
            });
        let header = v_flex()
            .px_4()
            .pt_3()
            .pb_2()
            .gap_0p5()
            .child(
                div()
                    .text_role(TextRole::Meta, cx)
                    .text_color(muted)
                    .child("BUILD BOARD"),
            )
            .child(
                div()
                    .text_role(TextRole::Heading, cx)
                    .font_weight(FontWeight::SEMIBOLD)
                    .child(headline),
            )
            .children(counts.as_ref().map(|c| {
                let line = format!(
                    "{} running · {} finished · {} in all",
                    c.running, c.terminal, c.total
                );
                div()
                    .text_role(TextRole::Body, cx)
                    .text_color(muted)
                    .child(line)
            }))
            .child(
                div().pt_1().min_w_0().child(
                    TabBar::new("board-filters")
                        .underline()
                        .small()
                        .selected_index(selected)
                        .children(filters),
                ),
            );
        let note = |text: String| {
            div()
                .px_4()
                .py_2()
                .text_role(TextRole::Body, cx)
                .text_color(muted)
                .child(text)
        };
        let body = match &self.failed {
            Some(message) => note(message.clone()).text_color(danger).into_any_element(),
            None if counts.is_none() => note("Reading the Board…".into()).into_any_element(),
            None if self.state.items.is_empty() => note(match self.state.filter {
                None => "No agent has reported a task yet.".into(),
                Some(_) => "No tasks in this state.".into(),
            })
            .into_any_element(),
            None => list(self.list.clone(), cx.processor(board_card::card))
                .flex_1()
                .min_h_0()
                .px_3()
                .into_any_element(),
        };
        let more = (self.state.next_cursor.is_some()).then(|| {
            let shown = self.state.items.len();
            let total = counts.as_ref().map_or(0, |counts| counts.total);
            h_flex()
                .px_4()
                .py_2()
                .gap_2()
                .text_role(TextRole::Body, cx)
                .text_color(muted)
                .child(format!("Showing {shown} of {total}"))
                .child(
                    Button::new("board-more")
                        .ghost()
                        .small()
                        .label("Load more")
                        .on_click(cx.listener(|pane, _, _, cx| pane.more(cx))),
                )
        });
        v_flex()
            .track_focus(&self.focus)
            .size_full()
            .min_h_0()
            .child(header)
            .child(body)
            .children(more)
    }
}
