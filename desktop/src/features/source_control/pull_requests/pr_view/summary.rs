//! The Summary page: what the pull request is, everything that happened on it, the comment box and merge card, and
//! the side column of threads, comments, reviews and checks.
use super::super::{
    pr_activity::{ActivityRow, PrTone, activity_rows, capitalized, merged_words, words_of},
    pr_parts::{PrHeading, age_label, avatar, loading_line, tone_color},
    pull_request::PullRequestComment,
};
use super::*;
use crate::core::ui::markdown::view::MarkdownView;
use gpui_kit::component::Icon;
use gpui_kit::component::scroll::ScrollableElement as _;

/// Below this the side column moves under the main one.
const TWO_COLUMNS: f32 = 1180.;

pub(super) fn quiet(text: impl Into<SharedString>, cx: &App) -> Div {
    div()
        .text_sm()
        .text_color(cx.theme().muted_foreground)
        .child(text.into())
}
fn link_notice(id: &'static str, text: &'static str, url: String, cx: &App) -> impl IntoElement {
    h_flex()
        .gap_1()
        .text_sm()
        .text_color(cx.theme().muted_foreground)
        .child(text)
        .child(
            Button::new(id)
                .link()
                .small()
                .label("View it on GitHub")
                .on_click(move |_, window, cx| {
                    window.dispatch_action(Box::new(OpenUrl { url: url.clone() }), cx)
                }),
        )
}
impl PrDetail {
    pub(super) fn summary_page(
        &self,
        detail: Option<&PullRequestDetail>,
        window: &Window,
        cx: &mut Context<Self>,
    ) -> AnyElement {
        let wide = self.width == px(0.) || self.width >= px(TWO_COLUMNS);
        let target = cx.entity().downgrade();
        let measure = canvas(
            move |bounds, _, cx| {
                _ = target.update(cx, |view, cx| {
                    if view.width != bounds.size.width {
                        view.width = bounds.size.width;
                        cx.notify();
                    }
                });
            },
            |_, _, _, _| {},
        )
        .absolute()
        .size_full();
        let main = self.main_column(detail, wide, window, cx);
        let side = detail.map(|detail| self.side_column(detail, cx));
        let page = if wide {
            h_flex()
                .relative()
                .size_full()
                .min_h_0()
                .items_start()
                .on_scroll(cx.listener(Self::wheel_beside_the_page))
                .child(
                    div()
                        .id("pr-summary-main")
                        .flex_1()
                        .min_w_0()
                        .max_w(px(868.))
                        .h_full()
                        .track_scroll(&self.main_scroll)
                        .overflow_y_scroll()
                        .child(main),
                )
                .children(side.map(|side| {
                    div()
                        .id("pr-summary-side")
                        .debug_selector(|| "pr-summary-side".into())
                        .flex_shrink(1.)
                        .w(px(300.))
                        .min_w(px(220.))
                        .h_full()
                        .pt_3()
                        .pr_5()
                        .ml_5()
                        .track_scroll(&self.side_scroll)
                        .overflow_y_scroll()
                        .child(side)
                }))
                .vertical_scrollbar(&self.main_scroll)
                .into_any_element()
        } else {
            div()
                .size_full()
                .overflow_y_scrollbar()
                .child(
                    v_flex()
                        .gap_5()
                        .pb_5()
                        .child(main)
                        .children(side.map(|side| div().px_5().child(side))),
                )
                .into_any_element()
        };
        div()
            .relative()
            .flex_1()
            .min_h_0()
            .child(measure)
            .child(page)
            .into_any_element()
    }
    /// The old page scrolled as one with its side column held still: a wheel beside the main column moves the main
    /// column, unless the side column has more of its own to show.
    fn wheel_beside_the_page(
        &mut self,
        event: &ScrollEvent,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        let over = |scroll: &ScrollHandle| scroll.bounds().contains(&event.position);
        if over(&self.main_scroll)
            || (over(&self.side_scroll) && self.side_scroll.max_offset().y > px(0.))
        {
            return;
        }
        let delta = event.delta.pixel_delta(window.line_height()).y;
        let at = self.main_scroll.offset();
        let y = (at.y + delta).clamp(-self.main_scroll.max_offset().y, px(0.));
        if y != at.y {
            self.main_scroll.set_offset(point(at.x, y));
            cx.notify();
        }
    }
    fn main_column(
        &self,
        detail: Option<&PullRequestDetail>,
        wide: bool,
        window: &Window,
        cx: &mut Context<Self>,
    ) -> AnyElement {
        let column = v_flex()
            .gap_3()
            .pt_3()
            .pb_5()
            .pr_5()
            .pl(if wide { px(88.) } else { px(20.) });
        let Some(detail) = detail else {
            return column
                .children(self.row.as_ref().map(|row| PrHeading::of_row(row).view(cx)))
                .when(self.loading, |column| column.child(loading_line(cx)))
                .into_any_element();
        };
        let theme = cx.theme();
        let border = theme.border;
        let heading = PrHeading::of_detail(detail).view(cx);
        let body = if detail.body.trim().is_empty() {
            div().child("No description.").into_any_element()
        } else {
            MarkdownView {
                id: "pr-body".into(),
                blocks: detail.body_blocks.clone(),
                session: None,
                expected_source: None,
            }
            .into_any_element()
        };
        let open = detail.state == "OPEN";
        let activity = v_flex()
            .gap_2()
            .pt_4()
            .border_t_1()
            .border_color(border)
            .child(
                div()
                    .text_size(px(15.))
                    .font_weight(FontWeight::SEMIBOLD)
                    .child("Activity"),
            )
            .children(
                activity_rows(detail)
                    .into_iter()
                    .enumerate()
                    .map(|(index, row)| self.activity_row(index, row, open, cx)),
            );
        column
            .child(heading)
            .when(self.loading, |column| column.child(loading_line(cx)))
            .children(
                self.posted
                    .clone()
                    .map(|url| link_notice("pr-posted", "Comment posted.", url, cx)),
            )
            .children(
                self.merged
                    .clone()
                    .map(|url| link_notice("pr-merged", "Pull request merged.", url, cx)),
            )
            .child(body)
            .child(activity)
            .children(self.reply_box(window, cx))
            .when(open, |column| {
                column
                    .child(self.composer(window, cx))
                    .child(self.merge_card(detail, cx))
            })
            .into_any_element()
    }
    fn activity_row(
        &self,
        index: usize,
        row: ActivityRow,
        open: bool,
        cx: &mut Context<Self>,
    ) -> AnyElement {
        let theme = cx.theme();
        let (surface, muted, text, radius, mono, success, danger) = (
            theme.secondary,
            theme.muted_foreground,
            theme.foreground,
            theme.radius_lg,
            theme.mono_font_family.clone(),
            theme.success,
            theme.danger,
        );
        let merged = tone_color(PrTone::Merged, cx);
        let age = |at: &str| {
            age_label(format!("pr-activity-age:{index}").into(), at, "")
                .map(|age| age.text_xs().text_color(muted))
        };
        let entry_age = |at: &str| age(at).map(|age| age.ml_auto());
        let event = |icon: IconName, color: Hsla, words: String| {
            h_flex()
                .gap_3()
                .min_h(px(38.))
                .px_3()
                .py_2()
                .rounded(radius)
                .bg(surface)
                .child(Icon::new(icon).size(px(15.)).text_color(color))
                .child(div().flex_1().min_w_0().truncate().child(words))
        };
        let entry = || v_flex().rounded(radius).bg(surface).overflow_hidden();
        let head = |who: &str| {
            h_flex()
                .flex_wrap()
                .gap_2()
                .px_3()
                .py_2()
                .text_sm()
                .text_color(muted)
                .children(avatar(who))
                .child(
                    div()
                        .font_weight(FontWeight::MEDIUM)
                        .text_color(text)
                        .child(who.to_string()),
                )
        };
        let body = || div().pl(px(40.)).pr_3().pb_3();
        match row {
            ActivityRow::Opened { at, author } => event(
                IconName::GitPullRequest,
                success,
                format!("{author} opened this pull request"),
            )
            .children(age(&at))
            .into_any_element(),
            ActivityRow::Merged { at, author } => {
                event(IconName::GitMerge, merged, merged_words(&author))
                    .children(age(&at))
                    .into_any_element()
            }
            ActivityRow::Closed { at } => event(
                IconName::GitPullRequestClosed,
                danger,
                "This pull request was closed".to_string(),
            )
            .children(age(&at))
            .into_any_element(),
            ActivityRow::Commit {
                at,
                author,
                oid,
                headline,
            } => event(IconName::GitCommitHorizontal, muted, headline)
                .child(
                    div()
                        .flex_none()
                        .text_xs()
                        .text_color(muted)
                        .font_family(mono)
                        .child(oid.chars().take(7).collect::<String>()),
                )
                .children(avatar(&author))
                .children(age(&at))
                .into_any_element(),
            ActivityRow::Comment { at, comment } => {
                let place = comment.path.as_ref().map(|path| match comment.line {
                    Some(line) => format!("{path}:{line}"),
                    None => path.clone(),
                });
                let can_reply = open && comment.reply_id().is_some();
                let id = format!("pr-comment-body:{index}");
                let reply = can_reply.then(|| {
                    let target = comment.clone();
                    Button::new(SharedString::from(format!("pr-reply:{index}")))
                        .ghost()
                        .small()
                        .label("Reply")
                        .on_click(cx.listener(move |view, _, _, cx| view.start_reply(&target, cx)))
                });
                entry()
                    .child(
                        head(&comment.author)
                            .child(if place.is_some() {
                                "commented on"
                            } else {
                                "commented"
                            })
                            .children(
                                place.map(|place| div().text_xs().font_family(mono).child(place)),
                            )
                            .children(entry_age(&at)),
                    )
                    .child(body().child(MarkdownView {
                        id: id.into(),
                        blocks: comment.body_blocks.clone(),
                        session: None,
                        expected_source: None,
                    }))
                    .children(reply.map(|reply| div().pl(px(36.)).mb_2().child(reply)))
                    .into_any_element()
            }
            ActivityRow::Review { at, review } => {
                let id = format!("pr-review-body:{index}");
                let has_body = !review.body.trim().is_empty();
                entry()
                    .child(
                        head(&review.author)
                            .child(capitalized(&words_of(&review.state)))
                            .children(entry_age(&at)),
                    )
                    .when(has_body, |card| {
                        card.child(body().child(MarkdownView {
                            id: id.into(),
                            blocks: review.body_blocks.clone(),
                            session: None,
                            expected_source: None,
                        }))
                    })
                    .into_any_element()
            }
        }
    }
    pub(super) fn start_reply(&mut self, comment: &PullRequestComment, cx: &mut Context<Self>) {
        let (Some(id), Some(path)) = (comment.reply_id(), comment.path.clone()) else {
            return;
        };
        self.reply = Some(ReplyTarget {
            id,
            author: comment.author.clone(),
            path,
            line: comment.line,
        });
        self.reply_input = None;
        cx.notify();
    }
}
