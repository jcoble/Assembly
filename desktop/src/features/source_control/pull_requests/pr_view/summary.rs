//! The Summary page: what the pull request is, everything that happened on it, the comment box and merge card, and
//! the side column of threads, comments, reviews and checks.
use super::super::{
    pr_activity::{
        ActivityRow, activity_rows, check_summary, check_tone, check_word, words_of,
    },
    pr_parts::{PrHeading, age_label, avatar, loading_line},
    pull_request::{CheckTone, PullRequestComment},
};
use super::*;
use crate::core::ui::markdown::view::MarkdownView;
use gpui_kit::component::Icon;
use gpui_kit::component::scroll::ScrollableElement as _;

/// Below this the side column moves under the main one.
const TWO_COLUMNS: f32 = 1180.;

fn side_section(title: &'static str, body: impl IntoElement, cx: &App) -> impl IntoElement {
    let theme = cx.theme();
    v_flex()
        .gap_2()
        .pb_3()
        .border_b_1()
        .border_color(theme.border)
        .child(
            div()
                .text_sm()
                .text_color(theme.muted_foreground)
                .child(title),
        )
        .child(body)
}
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
                .on_click(move |_, _, cx| cx.dispatch_action(&OpenUrl { url: url.clone() })),
        )
}
impl PrDetail {
    pub(super) fn summary_page(
        &self,
        detail: Option<&PullRequestDetail>,
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
        let main = self.main_column(detail, wide, cx);
        let side = detail.map(|detail| self.side_column(detail, cx));
        let page = if wide {
            h_flex()
                .size_full()
                .min_h_0()
                .items_start()
                .child(
                    div()
                        .flex_1()
                        .min_w_0()
                        .max_w(px(868.))
                        .h_full()
                        .overflow_y_scrollbar()
                        .child(main),
                )
                .children(side.map(|side| {
                    div()
                        .flex_shrink(1.)
                        .w(px(300.))
                        .min_w(px(220.))
                        .h_full()
                        .pt_3()
                        .pr_5()
                        .ml_5()
                        .overflow_y_scrollbar()
                        .child(side)
                }))
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
    fn main_column(
        &self,
        detail: Option<&PullRequestDetail>,
        wide: bool,
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
            quiet("No description.", cx).into_any_element()
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
            .pt_3()
            .border_t_1()
            .border_color(border)
            .child(div().font_weight(FontWeight::SEMIBOLD).child("Activity"))
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
            .children(self.reply_box(cx))
            .when(open, |column| {
                column
                    .child(self.composer(cx))
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
        let (surface, muted, radius) = (theme.secondary, theme.muted_foreground, theme.radius_lg);
        let age = |at: &str| age_label(format!("pr-activity-age:{index}").into(), at, " ago");
        let event = |who: &str, words: &str, at: &str| {
            h_flex()
                .gap_2()
                .min_h(px(38.))
                .px_3()
                .text_sm()
                .text_color(muted)
                .children(avatar(who))
                .child(div().font_weight(FontWeight::SEMIBOLD).child(who.to_string()))
                .child(words.to_string())
                .children(age(at))
                .into_any_element()
        };
        match row {
            ActivityRow::Opened { at, author } => event(&author, "opened this pull request", &at),
            ActivityRow::Merged { at, author } => event(&author, "merged this pull request", &at),
            ActivityRow::Closed { at } => h_flex()
                .gap_2()
                .min_h(px(38.))
                .px_3()
                .text_sm()
                .text_color(muted)
                .child("This pull request was closed")
                .children(age(&at))
                .into_any_element(),
            ActivityRow::Commit {
                at,
                author,
                oid,
                headline,
            } => h_flex()
                .gap_2()
                .min_h(px(38.))
                .px_3()
                .text_sm()
                .text_color(muted)
                .child(Icon::new(IconName::GitCommitHorizontal).size(px(14.)))
                .child(div().flex_1().min_w_0().truncate().child(headline))
                .child(
                    div()
                        .flex_none()
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
                v_flex()
                    .gap_1()
                    .p_3()
                    .rounded(radius)
                    .bg(surface)
                    .child(
                        h_flex()
                            .gap_2()
                            .text_sm()
                            .text_color(muted)
                            .children(avatar(&comment.author))
                            .child(div().font_weight(FontWeight::SEMIBOLD).child(comment.author.clone()))
                            .child(if place.is_some() {
                                "commented on"
                            } else {
                                "commented"
                            })
                            .children(place)
                            .children(age(&at)),
                    )
                    .child(div().pl(px(40.)).child(MarkdownView {
                        id: id.into(),
                        blocks: comment.body_blocks.clone(),
                        session: None,
                        expected_source: None,
                    }))
                    .children(reply.map(|reply| div().pl(px(40.)).child(reply)))
                    .into_any_element()
            }
            ActivityRow::Review { at, review } => {
                let id = format!("pr-review-body:{index}");
                let has_body = !review.body.trim().is_empty();
                v_flex()
                    .gap_1()
                    .p_3()
                    .rounded(radius)
                    .bg(surface)
                    .child(
                        h_flex()
                            .gap_2()
                            .text_sm()
                            .text_color(muted)
                            .children(avatar(&review.author))
                            .child(div().font_weight(FontWeight::SEMIBOLD).child(review.author.clone()))
                            .child(words_of(&review.state))
                            .children(age(&at)),
                    )
                    .when(has_body, |card| {
                        card.child(div().pl(px(40.)).child(MarkdownView {
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
    fn side_column(&self, detail: &PullRequestDetail, cx: &mut Context<Self>) -> impl IntoElement {
        let theme = cx.theme();
        let (success, danger, warning) = (theme.success, theme.danger, theme.warning);
        let comments = if detail.comments.is_empty() {
            quiet("No comments", cx).into_any_element()
        } else {
            v_flex()
                .gap_2()
                .text_sm()
                .children(detail.comments.iter().map(|comment| {
                    h_flex()
                        .gap_2()
                        .min_w_0()
                        .children(avatar(&comment.author))
                        .child(
                            div()
                                .flex_none()
                                .font_weight(FontWeight::SEMIBOLD)
                                .child(comment.author.clone()),
                        )
                        .child(div().min_w_0().truncate().child(comment.body.clone()))
                }))
                .into_any_element()
        };
        let requested = (!detail.reviewers.is_empty())
            .then(|| quiet(format!("Requested: {}", detail.reviewers.join(", ")), cx));
        let reviews = v_flex()
            .gap_2()
            .text_sm()
            .when(detail.reviews.is_empty(), |list| {
                list.child(quiet("No reviews", cx))
            })
            .children(detail.reviews.iter().map(|review| {
                h_flex()
                    .gap_2()
                    .children(avatar(&review.author))
                    .child(div().font_weight(FontWeight::SEMIBOLD).child(review.author.clone()))
                    .child(words_of(&review.state))
            }))
            .children(requested);
        let checks = v_flex()
            .gap_1()
            .text_sm()
            .child(quiet(check_summary(&detail.checks), cx))
            .children(detail.checks.iter().enumerate().map(|(index, check)| {
                let color = match check_tone(check) {
                    CheckTone::Passing => success,
                    CheckTone::Failing => danger,
                    _ => warning,
                };
                let url = check.url.clone();
                let word = check_word(check);
                h_flex()
                    .gap_2()
                    .child(div().flex_none().size(px(8.)).rounded_full().bg(color))
                    .child(
                        div()
                            .flex_1()
                            .min_w_0()
                            .truncate()
                            .child(check.name.clone()),
                    )
                    .child(if url.is_empty() {
                        div().flex_none().child(word).into_any_element()
                    } else {
                        Button::new(SharedString::from(format!("pr-check:{index}")))
                            .link()
                            .small()
                            .label(word)
                            .on_click(move |_, _, cx| {
                                cx.dispatch_action(&OpenUrl { url: url.clone() })
                            })
                            .into_any_element()
                    })
            }));
        v_flex()
            .gap_3()
            .child(side_section(
                "Threads",
                quiet("Threads for this pull request aren't linked yet.", cx),
                cx,
            ))
            .child(side_section("Comments", comments, cx))
            .child(side_section("Reviews", reviews, cx))
            .child(side_section("Checks", checks, cx))
    }
}
