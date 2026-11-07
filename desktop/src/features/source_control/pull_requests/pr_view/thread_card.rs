//! A review thread in the Summary page's activity: the file and line, whether it's resolved or outdated, its comments,
//! and what can be done with it here: reply (the helper can write it), resolve or reopen, or hand it to an agent. A
//! resolved thread folds to its first comment and opens in place.
use super::super::{
    pr_parts::{age_label, avatar},
    pr_writes::PrAct,
    pull_request::{PullRequestComment, PullRequestThread},
};
use super::*;
use crate::activity::{StatusChip, Tone};
use crate::core::theme::text::TextRole;
use crate::core::ui::markdown::view::MarkdownView;
use crate::core::ui::row_drop_down::GrowingBody;
use crate::core::ui::three_line_row::row_height;
use gpui_kit::component::Icon;

fn place(thread: &PullRequestThread) -> String {
    match thread.line {
        Some(line) => format!("{}:{line}", thread.path),
        None => thread.path.clone(),
    }
}
fn thread_comment(key: &str, at: usize, comment: &PullRequestComment, cx: &App) -> AnyElement {
    let theme = cx.theme();
    let (muted, text) = (theme.muted_foreground, theme.foreground);
    v_flex()
        .child(
            h_flex()
                .gap_2()
                .px_3()
                .pt_2()
                .text_role(TextRole::Body, cx)
                .text_color(muted)
                .children(avatar(&comment.author))
                .child(
                    div()
                        .font_weight(FontWeight::MEDIUM)
                        .text_color(text)
                        .child(comment.author.clone()),
                )
                .children(
                    age_label(
                        format!("pr-thread-age:{key}:{at}").into(),
                        &comment.created_at,
                        "",
                    )
                    .map(|age| age.text_role(TextRole::Meta, cx).text_color(muted)),
                ),
        )
        .child(div().pl(px(40.)).pr_3().pb_2().child(MarkdownView {
            id: format!("pr-thread-body:{key}:{at}").into(),
            blocks: comment.body_blocks.clone(),
            session: None,
            expected_source: None,
        }))
        .into_any_element()
}
/// What an agent is asked to do about a thread: the place, the conversation, and the branch it's on.
pub(super) fn thread_prompt(
    detail: &PullRequestDetail,
    thread: &PullRequestThread,
    comments: &[PullRequestComment],
) -> String {
    let said: Vec<String> = comments
        .iter()
        .map(|comment| {
            format!(
                "{}:\n{}",
                comment.author,
                comment
                    .body
                    .lines()
                    .map(|line| format!("> {line}"))
                    .collect::<Vec<_>>()
                    .join("\n")
            )
        })
        .collect();
    format!(
        "A review thread on {} in pull request #{} ({}, branch {}):\n\n{}\n\nDeal with what it asks on this branch and push, or say why it shouldn't change. Then write the reply for the thread.",
        place(thread),
        detail.number,
        detail.repository,
        detail.head_branch,
        said.join("\n\n")
    )
}
impl PrDetail {
    pub(super) fn thread_card(
        &self,
        detail: &PullRequestDetail,
        thread: PullRequestThread,
        comments: Vec<PullRequestComment>,
        window: &Window,
        cx: &mut Context<Self>,
    ) -> AnyElement {
        let theme = cx.theme();
        let (surface, muted, radius, mono, border) = (
            theme.secondary,
            theme.muted_foreground,
            theme.radius_lg,
            theme.mono_font_family.clone(),
            theme.border,
        );
        let key = SharedString::from(format!("thread:{}", thread.id));
        let unfolded = self.thread_drops.read(cx).is_open(&key);
        let writes = self.writes.read(cx);
        let busy_here = self.acting_on.as_deref() == Some(thread.id.as_str())
            && matches!(writes.acting(cx), Some(PrAct::Resolve | PrAct::Reopen));
        let free = writes.free(cx) && self.read_identity().is_some();
        let first = comments.first().cloned();
        let replying = (self.reply.as_ref())
            .zip(first.as_ref().and_then(PullRequestComment::reply_id))
            .is_some_and(|(reply, id)| reply.id == id);
        let header = h_flex()
            .gap_2()
            .px_3()
            .py_2()
            .text_role(TextRole::Body, cx)
            .border_b_1()
            .border_color(border)
            .child(
                Icon::new(IconName::MessageSquare)
                    .size(px(14.))
                    .text_color(muted),
            )
            .child(
                div()
                    .min_w_0()
                    .truncate()
                    .font_family(mono)
                    .text_role(TextRole::Meta, cx)
                    .child(place(&thread)),
            )
            .when(thread.resolved, |row| {
                row.child(StatusChip::new("Resolved", Tone::Success))
            })
            .when(thread.outdated, |row| {
                row.child(StatusChip::new("Outdated", Tone::Neutral))
            })
            .child(div().flex_1())
            .child(
                div()
                    .flex_none()
                    .text_role(TextRole::Meta, cx)
                    .text_color(muted)
                    .child(match comments.len() {
                        1 => "1 comment".to_string(),
                        count => format!("{count} comments"),
                    }),
            );
        let card = v_flex()
            .rounded(radius)
            .bg(surface)
            .overflow_hidden()
            .child(header);
        let card = if thread.resolved {
            let rest: Vec<PullRequestComment> = comments.iter().skip(1).cloned().collect();
            let body_key = thread.id.clone();
            card.children(
                first
                    .as_ref()
                    .map(|comment| thread_comment(&thread.id, 0, comment, cx)),
            )
            .when(!rest.is_empty(), |card| {
                let drops = self.thread_drops.clone();
                let open_key = key.clone();
                card.child(
                    h_flex().pl(px(36.)).child(
                        Button::new(SharedString::from(format!("pr-thread-fold:{}", thread.id)))
                            .ghost()
                            .small()
                            .icon(if unfolded {
                                IconName::ChevronDown
                            } else {
                                IconName::ChevronRight
                            })
                            .label(if unfolded {
                                "Hide replies".to_string()
                            } else {
                                format!("Show {} more", rest.len())
                            })
                            .on_click(move |_, _, cx| {
                                let (rest, body_key) = (rest.clone(), body_key.clone());
                                drops.update(cx, |drops, cx| {
                                    drops.toggle(
                                        open_key.clone(),
                                        move |_, cx| {
                                            v_flex()
                                                .children(rest.iter().enumerate().map(
                                                    |(at, comment)| {
                                                        thread_comment(
                                                            &body_key,
                                                            at + 1,
                                                            comment,
                                                            cx,
                                                        )
                                                    },
                                                ))
                                                .into_any_element()
                                        },
                                        cx,
                                    )
                                })
                            }),
                    ),
                )
                .child(
                    GrowingBody::new(key.clone(), self.thread_drops.clone(), row_height(cx))
                        .max_rows(None),
                )
            })
        } else {
            card.children(
                comments
                    .iter()
                    .enumerate()
                    .map(|(at, comment)| thread_comment(&thread.id, at, comment, cx)),
            )
        };
        let reply = first
            .clone()
            .filter(|comment| comment.reply_id().is_some() && !replying)
            .map(|comment| {
                Button::new(SharedString::from(format!("pr-thread-reply:{}", thread.id)))
                    .ghost()
                    .small()
                    .icon(IconName::MessageSquare)
                    .label("Reply")
                    .on_click(cx.listener(move |view, _, _, cx| view.start_reply(&comment, cx)))
            });
        let (act, label) = if thread.resolved {
            (PrAct::Reopen, "Reopen")
        } else {
            (PrAct::Resolve, "Resolve")
        };
        let resolve = (!thread.id.is_empty()).then(|| {
            let id = thread.id.clone();
            Button::new(SharedString::from(format!(
                "pr-thread-resolve:{}",
                thread.id
            )))
            .ghost()
            .small()
            .icon(if thread.resolved {
                IconName::Undo2
            } else {
                IconName::CircleCheck
            })
            .label(if busy_here { act.doing() } else { label })
            .loading(busy_here)
            .disabled(!free)
            .on_click(cx.listener(move |view, _, _, cx| {
                view.acting_on = Some(id.clone());
                view.act(
                    act,
                    json!({"threadId":id,"resolved":act == PrAct::Resolve}),
                    cx,
                );
            }))
        });
        let prompt = thread_prompt(detail, &thread, &comments);
        let ask = Button::new(SharedString::from(format!("pr-thread-ask:{}", thread.id)))
            .ghost()
            .small()
            .icon(IconName::Bot)
            .label("Ask an agent")
            .on_click(
                cx.listener(move |view, _, window, cx| view.ask_agent(prompt.clone(), window, cx)),
            );
        card.child(
            h_flex()
                .gap_1()
                .pl(px(32.))
                .pr_2()
                .pb_2()
                .children(reply)
                .children(resolve)
                .when(!thread.resolved, |row| row.child(ask)),
        )
        .when(replying, |card| {
            card.children(
                self.reply_box(window, cx)
                    .map(|reply| div().p_2().child(reply)),
            )
        })
        .into_any_element()
    }
}
