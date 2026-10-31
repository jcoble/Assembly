//! One Board card. At rest it's at most four lines: the state and title, the project and machine, the summary, and a
//! line of facts when there are any. Its state is a plain word; Needs you leads only for an unresolved question (the
//! backend's attention group 0), and a PR link is only a link, never Completed or Merged. A remote card names its
//! machine, and says offline when the machines list does. The expanded card adds its run rows (board_rows), its
//! questions (board_question), its feed (board_feed) and the session that reported it. There are no retry, pause or
//! start controls.

use gpui_kit::component::button::{Button, ButtonVariants as _};
use gpui_kit::component::{ActiveTheme as _, Sizable as _, h_flex, v_flex};
use gpui_kit::prelude::FluentBuilder as _;
use gpui_kit::*;

use super::board_pane::BoardPane;
use super::{board_feed, board_question, board_rows};
use crate::core::intents::open_session::OpenSession;
use crate::core::intents::open_url::OpenUrl;
use crate::core::state::machines::Machines;

/// Each task state and its plain label, in the order the filters show them.
pub const STATES: [(&str, &str); 7] = [
    ("queued", "Queued"),
    ("running", "Running"),
    ("paused", "Paused"),
    ("waiting", "Waiting"),
    ("completed", "Completed"),
    ("failed", "Failed"),
    ("cancelled", "Cancelled"),
];

pub fn label(state: &str) -> &str {
    (STATES.iter().find(|(name, _)| *name == state)).map_or(state, |(_, label)| label)
}

/// A span of Unix ms, short: "<1m", "12m", "3h 5m".
fn span(ms: i64) -> String {
    let minutes = ms.max(0) / 60_000;
    match minutes {
        0 => "<1m".into(),
        1..60 => format!("{minutes}m"),
        _ => format!("{}h {}m", minutes / 60, minutes % 60),
    }
}

fn badge(text: &str, color: Hsla) -> Div {
    div()
        .flex_none()
        .px_1p5()
        .rounded(px(5.))
        .text_xs()
        .font_weight(FontWeight::MEDIUM)
        .text_color(color)
        .bg(color.opacity(0.14))
        .child(text.to_string())
}

pub fn card(
    pane: &mut BoardPane,
    ix: usize,
    window: &mut Window,
    cx: &mut Context<BoardPane>,
) -> AnyElement {
    let Some(item) = pane.state.items.get(ix) else {
        return div().into_any_element();
    };
    let (task, needs_you) = (item.task.clone(), item.attention_group == 0);
    let (questions, files) = (item.question_count, item.metrics.files);
    let key = (task.machine_id.clone(), task.task_key.clone());
    let open = pane.state.selected.as_ref() == Some(&key);
    let theme = cx.theme();
    let (muted, border, fill) = (theme.muted_foreground, theme.border, theme.secondary);
    let color = match task.state.as_str() {
        "running" => theme.info,
        "completed" => theme.success,
        "failed" => theme.danger,
        _ => muted,
    };
    let warning = theme.warning;
    let offline = task.machine_id != "local" && Machines::offline(Some(&task.machine_id), cx);
    let machine = match task.machine_id.as_str() {
        "local" => "This Mac".to_string(),
        name if offline => format!("{name} · offline"),
        name => name.to_string(),
    };
    let time = match task.ended_at {
        Some(ended) => span(ended - task.started_at),
        None => (pane.now).map_or_else(String::new, |now| span(now - task.started_at)),
    };
    let head = v_flex()
        .id("head")
        .gap_0p5()
        .cursor_pointer()
        .on_click(cx.listener(move |pane, _, _, cx| pane.select(&key.0, &key.1, cx)))
        .child(
            h_flex()
                .gap_1p5()
                .when(needs_you, |line| line.child(badge("Needs you", warning)))
                .child(badge(label(&task.state), color))
                .child(
                    div()
                        .flex_1()
                        .min_w_0()
                        .truncate()
                        .font_weight(FontWeight::SEMIBOLD)
                        .child(task.title.clone()),
                )
                .child(div().flex_none().text_xs().text_color(muted).child(time)),
        )
        .child(
            div()
                .text_xs()
                .truncate()
                .text_color(if offline { warning } else { muted })
                .child(format!("{} · {machine}", task.project)),
        )
        .child(div().text_sm().truncate().child(task.summary.clone()));
    let link = |id: &'static str, text: &'static str, url: Option<String>| {
        url.map(|url| {
            Button::new(id)
                .ghost()
                .xsmall()
                .label(text)
                .on_click(move |_, window, cx| {
                    window.dispatch_action(Box::new(OpenUrl { url: url.clone() }), cx)
                })
        })
    };
    let facts = [
        (questions > 0).then(|| {
            format!(
                "{questions} question{}",
                if questions == 1 { "" } else { "s" }
            )
        }),
        files.map(|files| format!("{files} file{}", if files == 1 { "" } else { "s" })),
    ];
    let has_facts =
        facts.iter().any(Option::is_some) || task.pr_url.is_some() || task.task_url.is_some();
    let facts = h_flex()
        .gap_2()
        .text_xs()
        .text_color(muted)
        .children(facts.into_iter().flatten())
        .children(link("pr", "PR", task.pr_url.clone()))
        .children(link("task", "Task", task.task_url.clone()));
    let body = open.then(|| detail(pane, &task.creator_session_id, window, cx));
    v_flex()
        .id(ElementId::Integer(ix as u64))
        .w_full()
        .px_3()
        .pb_2()
        .child(
            v_flex()
                .gap_1()
                .px_3()
                .py_2()
                .rounded(px(10.))
                .border_1()
                .border_color(border.opacity(0.6))
                .bg(fill.opacity(if open { 0.75 } else { 0.45 }))
                .when(needs_you, |card| card.border_l_2().border_color(warning))
                .child(head)
                .when(has_facts, |card| card.child(facts))
                .children(body),
        )
        .into_any_element()
}

/// The expanded card's questions, feed and session link, each with its next page when there's more.
fn detail(
    pane: &mut BoardPane,
    session: &str,
    window: &mut Window,
    cx: &mut Context<BoardPane>,
) -> Div {
    let muted = cx.theme().muted_foreground;
    let session = session.to_string();
    let open_session = Button::new("open-session")
        .ghost()
        .xsmall()
        .label("Open session")
        .on_click(move |_, window, cx| {
            window.dispatch_action(
                Box::new(OpenSession {
                    id: session.clone(),
                }),
                cx,
            )
        });
    let body = v_flex().gap_2().pt_1();
    // The session link shows whether or not the detail could be read: it's the way back when it can't.
    let link = h_flex().child(open_session);
    if let Some(message) = pane.detail_failed.clone() {
        let error = div().text_sm().text_color(cx.theme().danger).child(message);
        return body.child(error).child(link);
    }
    let Some(detail) = pane.state.detail.as_ref() else {
        return body
            .child(div().text_sm().text_color(muted).child("Reading…"))
            .child(link);
    };
    let (questions, more_questions) = (
        detail.questions.clone(),
        detail.next_question_cursor.is_some(),
    );
    let (feed, earlier) = (
        board_feed::feed(&detail.feed, cx),
        detail.next_before_seq.is_some(),
    );
    let more =
        |id: &'static str, text: &'static str, go: fn(&mut BoardPane, &mut Context<BoardPane>)| {
            Button::new(id)
                .ghost()
                .xsmall()
                .label(text)
                .on_click(cx.listener(move |pane, _, _, cx| go(pane, cx)))
        };
    let (show_questions, show_events) = (
        more(
            "more-questions",
            "Show more questions",
            BoardPane::more_questions,
        ),
        more(
            "earlier-events",
            "Show earlier events",
            BoardPane::earlier_events,
        ),
    );
    body.children(board_rows::draw(&detail.steps, pane.now, cx))
        .children(
            questions
                .iter()
                .map(|q| board_question::question(pane, q, window, cx)),
        )
        .when(more_questions, |body| body.child(show_questions))
        .child(feed)
        .when(earlier, |body| body.child(show_events))
        .child(link)
}
