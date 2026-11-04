//! The commit table's two end rows: the uncommitted changes row above the commits and the footer row below them.
use super::*;

impl CommitTimeline {
    pub(super) fn uncommitted_row(&mut self, cx: &mut Context<Self>) -> AnyElement {
        let Some(row) = self
            .uncommitted
            .clone()
            .filter(|_| !self.filter.hides_uncommitted())
        else {
            return Empty.into_any_element();
        };
        let theme = cx.theme();
        let (muted, hover, ground) = (
            theme.muted_foreground,
            theme.secondary_hover,
            theme.background,
        );
        let drawn = drawn_lanes(&self.graph);
        let graph = graph_width(drawn);
        // Source control shows the session's own folder, which isn't what this row counted on another checkout.
        let own_folder = self.checkout.is_none();
        let tip = if own_folder {
            format!("{} — open Source Control", row.detail)
        } else {
            row.detail.to_string()
        };
        h_flex()
            .id("history-uncommitted")
            .debug_selector(|| "history-uncommitted".into())
            .w_full()
            .h(px(ROW))
            .gap(px(COLUMN_GAP))
            .pr(px(COLUMN_GAP))
            .cursor_pointer()
            .hover(move |row| row.bg(hover))
            .tooltip(move |window, cx| Tooltip::new(tip.clone()).build(window, cx))
            .when(own_folder, |row| {
                row.on_click(|_, window, cx| {
                    window.dispatch_action(Box::new(ShowSourceControl), cx)
                })
            })
            .child(
                div().relative().flex_none().w(px(graph)).h(px(ROW)).child(
                    div()
                        .absolute()
                        .left(px(LANE_OFFSET - 3.5))
                        .top(px(ROW / 2. - 3.5))
                        .size(px(7.))
                        .rounded_full()
                        .border_1()
                        .border_dashed()
                        .border_color(muted)
                        .bg(ground),
                ),
            )
            .child(
                h_flex()
                    .flex_1()
                    .min_w_0()
                    .gap(px(5.))
                    .child(
                        div()
                            .min_w_0()
                            .truncate()
                            .italic()
                            .text_color(muted)
                            .child("Uncommitted Changes"),
                    )
                    .child(
                        Tag::secondary()
                            .xsmall()
                            .rounded_full()
                            .child(row.count_label),
                    ),
            )
            .child(cell(DATE_WIDTH, "Now".into(), muted, cx).text_right())
            .child(cell(AUTHOR_WIDTH, row.detail, muted, cx))
            .child(cell(HASH_WIDTH, "*".into(), muted, cx))
            .into_any_element()
    }

    pub(super) fn table_footer_row(&mut self, cx: &mut Context<Self>) -> AnyElement {
        let theme = cx.theme();
        let (muted, danger) = (theme.muted_foreground, theme.danger);
        let complete = self.history.next_cursor.is_none();
        let reading_more = self.reading && !self.history.commits.is_empty();
        let sentence = table_footer(
            &self.filter,
            self.visible.len(),
            self.history.commits.len(),
            reading_more,
            complete,
            self.pages,
        );
        let label = if reading_more {
            "Reading older commits…".to_string()
        } else if complete {
            "All commits loaded".to_string()
        } else {
            format!("Load {PAGE} older commits")
        };
        v_flex()
            .w_full()
            .children(self.error.clone().map(|error| {
                div()
                    .px(px(COLUMN_GAP))
                    .pt_2()
                    .text_role(TextRole::Body, cx)
                    .text_color(danger)
                    .child(error)
            }))
            .child(
                h_flex()
                    .gap_2()
                    .px(px(COLUMN_GAP))
                    .pt_2()
                    .pb_3()
                    .child(
                        div()
                            .min_w_0()
                            .text_role(TextRole::Body, cx)
                            .text_color(muted)
                            .child(sentence),
                    )
                    .child(
                        Button::new("history-more")
                            .ghost()
                            .compact()
                            .label(label)
                            .disabled(complete || reading_more)
                            .on_click(cx.listener(|timeline, _, _, cx| timeline.load_more(cx))),
                    ),
            )
            .into_any_element()
    }
}
