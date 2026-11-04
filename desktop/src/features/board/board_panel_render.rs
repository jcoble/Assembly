//! The board's tabs and list.
use super::*;

impl Render for BoardPanel {
    fn render(&mut self, window: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        self.release_clock_if_idle(cx);
        if self.dirty {
            self.rebuild(cx);
        }
        let muted = cx.theme().muted_foreground;
        let open = open_owner_questions(&self.data).len();
        let counts = summary_counts(&self.data, local_midnight_ms());
        let mutes = self.mutes(cx);
        let note = mute_note(&mutes);
        let this = cx.weak_entity();
        let on_mute: OnMute = Rc::new(move |mute, until, cx| {
            let _ = this.update(cx, |panel, cx| panel.set_mute(mute, until, cx));
        });
        let header = h_flex()
            .px_3()
            .pt_3()
            .items_start()
            .child(
                v_flex()
                    .flex_1()
                    .min_w_0()
                    .child(div().text_role(TextRole::Title, cx).child(heading(open)))
                    .child(
                        div()
                            .text_role(TextRole::Meta, cx)
                            .text_color(muted)
                            .child(summary(counts)),
                    )
                    .children(note.map(|note| {
                        div()
                            .text_role(TextRole::Meta, cx)
                            .text_color(muted)
                            .child(note)
                    })),
            )
            .children((!mutes.is_empty()).then(|| mute_button(mutes, on_mute)));
        let bar = TabBar::new("board-tabs")
            .underline()
            .with_size(Size::Small)
            .selected_index(tab_index(self.tab))
            .children(BoardTab::ALL.into_iter().map(|tab| {
                let label = match tab {
                    BoardTab::Needs if open > 0 => format!("Needs you · {open}"),
                    BoardTab::Needs => "Needs you".to_owned(),
                    BoardTab::Workflows => "Workflows".to_owned(),
                    BoardTab::Sessions => "Sessions".to_owned(),
                };
                Tab::new()
                    .label(label)
                    .on_click(cx.listener(move |panel, _, _, cx| panel.show_tab(tab, cx)))
            }));
        let this = cx.weak_entity();
        let rows = list(self.list.clone(), move |index, window, cx| {
            this.update(cx, |panel, cx| panel.row(index, window, cx))
                .unwrap_or_else(|_| Empty.into_any_element())
        })
        .with_sizing_behavior(ListSizingBehavior::Infer)
        .flex_shrink(1.)
        .min_h_0();
        let height = (window.viewport_size().height - WINDOW_CLEARANCE).max(px(0.));
        v_flex()
            .debug_selector(|| "board-panel".into())
            .track_focus(&self.focus)
            .w((window.viewport_size().width - px(16.))
                .min(WIDTH)
                .max(px(0.)))
            .max_h(height)
            .overflow_hidden()
            .child(header)
            .child(div().px_3().child(bar))
            .children(self.open_failed.clone().map(|line| {
                div()
                    .px_3()
                    .text_role(TextRole::Meta, cx)
                    .text_color(cx.theme().danger)
                    .child(line)
            }))
            .child(
                div()
                    .id("board-list")
                    .px_3()
                    .pt_2()
                    .flex()
                    .flex_col()
                    .flex_shrink(1.)
                    .min_h_0()
                    .on_hover(cx.listener(|panel, hovered, _, cx| {
                        panel.hovering = *hovered;
                        if !*hovered {
                            panel.rebuild(cx);
                            cx.notify();
                        }
                    }))
                    .child(rows),
            )
    }
}
