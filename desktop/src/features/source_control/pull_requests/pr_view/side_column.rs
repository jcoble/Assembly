//! The Summary page's side column: short rows for the threads, comments, reviews and checks, in the old screen's shape.
use super::super::{
    pr_activity::{capitalized, check_summary, check_tone, check_word, words_of},
    pr_parts::avatar,
    pull_request::{CheckTone, PullRequestCheck, PullRequestComment, PullRequestReview},
};
use super::summary::quiet;
use super::threads::threads_body;
use super::*;

/// A section ends in a line unless it's the last; `meta` sits across from the heading.
fn section(title: &'static str, meta: Option<String>, first: bool, last: bool, cx: &App) -> Div {
    let theme = cx.theme();
    let (muted, border) = (theme.muted_foreground, theme.border);
    let heading = div().text_color(muted).child(title);
    v_flex()
        .py_3()
        .when(first, |section| section.pt_0())
        .when(!last, |section| section.border_b_1().border_color(border))
        .map(|section| match meta {
            Some(meta) => section.child(
                h_flex()
                    .items_baseline()
                    .justify_between()
                    .child(heading)
                    .child(div().flex_none().text_color(muted).child(meta)),
            ),
            None => section.child(heading.mb_2()),
        })
}
fn row() -> Div {
    h_flex().gap_2().w_full().min_w_0().py_1()
}
fn meta(word: String, cx: &App) -> Div {
    div()
        .ml_auto()
        .flex_none()
        .text_color(cx.theme().muted_foreground)
        .child(word)
}
fn comment_row(comment: &PullRequestComment) -> impl IntoElement + use<> {
    let body = comment
        .body
        .split_whitespace()
        .collect::<Vec<_>>()
        .join(" ");
    let author = comment.author.as_str();
    let text = format!("{author} {body}").trim_start().to_owned();
    let name = (!author.is_empty()).then(|| {
        (
            0..author.len(),
            HighlightStyle {
                font_weight: Some(FontWeight::MEDIUM),
                ..HighlightStyle::default()
            },
        )
    });
    row().children(avatar(author)).child(
        div()
            .flex_1()
            .min_w_0()
            .truncate()
            .child(StyledText::new(text).with_highlights(name)),
    )
}
fn review_row(review: &PullRequestReview, cx: &App) -> impl IntoElement + use<> {
    row()
        .children(avatar(&review.author))
        .child(
            div()
                .font_weight(FontWeight::MEDIUM)
                .child(review.author.clone()),
        )
        .child(meta(capitalized(&words_of(&review.state)), cx))
}
fn check_row(index: usize, check: &PullRequestCheck, cx: &App) -> impl IntoElement + use<> {
    let theme = cx.theme();
    let color = match check_tone(check) {
        CheckTone::Passing => theme.success,
        CheckTone::Failing => theme.danger,
        _ => theme.warning,
    };
    let name = if check.name.is_empty() {
        "Check".to_owned()
    } else {
        check.name.clone()
    };
    let word = meta(capitalized(&check_word(check)), cx);
    let url = check.url.clone();
    row()
        .child(div().flex_none().size(px(8.)).rounded_full().bg(color))
        .child(div().min_w_0().truncate().child(name))
        .child(if url.is_empty() {
            word.into_any_element()
        } else {
            word.id(SharedString::from(format!("pr-check:{index}")))
                .cursor_pointer()
                .on_click(move |_, window, cx| {
                    window.dispatch_action(Box::new(OpenInBrowser { url: url.clone() }), cx)
                })
                .into_any_element()
        })
}
impl PrDetail {
    pub(super) fn side_column(
        &self,
        detail: &PullRequestDetail,
        cx: &mut Context<Self>,
    ) -> impl IntoElement + use<> {
        let threads = threads_body(self.threads.read(cx).shown(), cx);
        let comments = if detail.comments.is_empty() {
            quiet("No comments", cx).into_any_element()
        } else {
            v_flex()
                .children(detail.comments.iter().map(comment_row))
                .into_any_element()
        };
        let requested = (!detail.reviewers.is_empty())
            .then(|| quiet(format!("Requested: {}", detail.reviewers.join(", ")), cx));
        let no_reviews = detail.reviews.is_empty().then(|| quiet("No reviews", cx));
        let reviews: Vec<_> = detail
            .reviews
            .iter()
            .map(|review| review_row(review, cx))
            .collect();
        let checks: Vec<_> = detail
            .checks
            .iter()
            .enumerate()
            .map(|(index, check)| check_row(index, check, cx))
            .collect();
        v_flex()
            .text_role(TextRole::Body, cx)
            .child(section("Threads", None, true, false, cx).children(threads))
            .child(section("Comments", None, false, false, cx).child(comments))
            .child(
                section("Reviews", None, false, false, cx)
                    .children(no_reviews)
                    .children(reviews)
                    .children(requested),
            )
            .child(
                section(
                    "Checks",
                    Some(check_summary(&detail.checks)),
                    false,
                    true,
                    cx,
                )
                .children(checks),
            )
    }
}
#[cfg(test)]
#[path = "side_column_tests.rs"]
mod tests;
