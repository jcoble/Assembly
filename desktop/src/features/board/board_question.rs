//! One question on an expanded card: its prompt, the agent's recommendation and the answer. An open text question
//! takes typed text or the recommendation; an approval takes Approve or Deny. An answered one shows the answer and
//! whether it reached the agent. A failed answer keeps its text and says why.

use gpui_kit::component::button::{Button, ButtonVariants as _};
use gpui_kit::component::input::Input;
use gpui_kit::component::{ActiveTheme as _, Disableable as _, Sizable as _, h_flex, v_flex};
use gpui_kit::prelude::FluentBuilder as _;
use gpui_kit::*;

use super::board_pane::BoardPane;
use super::board_payload::{BoardAnswerValue, BoardQuestion};
use super::board_state::question_id;

/// Where an answer is, in plain words.
pub(super) fn delivery(state: &str) -> &str {
    match state {
        "pending" => "Sending to the agent…",
        "delivered" => "Delivered to the agent.",
        "delivery_unknown" => "Not known whether the agent received it; it isn't sent again.",
        "delivery_failed" => "Not delivered; the answer is kept.",
        "recorded_in_chat" => "Answered in the chat.",
        other => other,
    }
}

fn value(kind: &str, text: Option<String>) -> BoardAnswerValue {
    BoardAnswerValue {
        kind: kind.into(),
        text,
    }
}

pub fn question(
    pane: &mut BoardPane,
    q: &BoardQuestion,
    window: &mut Window,
    cx: &mut Context<BoardPane>,
) -> AnyElement {
    let theme = cx.theme();
    let (muted, danger, fill) = (theme.muted_foreground, theme.danger, theme.background);
    let draft = pane.state.drafts.get(&question_id(q));
    let sending = draft.is_some_and(|draft| draft.sending);
    let error = draft.and_then(|draft| draft.error.clone());
    let recommended = q.recommendation.clone().map(|text| {
        v_flex()
            .px_2()
            .py_1()
            .rounded(px(6.))
            .bg(fill.opacity(0.5))
            .child(
                div()
                    .text_xs()
                    .font_weight(FontWeight::SEMIBOLD)
                    .child("Recommended"),
            )
            .child(div().text_sm().child(text))
    });
    let shell = v_flex()
        .id(SharedString::from(q.question_key.clone()))
        .gap_1p5()
        .p_2()
        .rounded(px(8.))
        .bg(fill.opacity(0.3))
        .child(
            div()
                .text_sm()
                .font_weight(FontWeight::SEMIBOLD)
                .child(q.prompt.clone()),
        )
        .children(recommended);
    if let Some(answer) = &q.answer {
        let said = match answer.kind.as_str() {
            "approve" => "Approved".to_string(),
            "deny" => "Denied".to_string(),
            _ => answer.text.clone().unwrap_or_default(),
        };
        return shell
            .child(div().text_sm().child(format!("Answer: {said}")))
            .child(
                div()
                    .text_xs()
                    .text_color(muted)
                    .child(delivery(&q.delivery_state).to_string()),
            )
            .into_any_element();
    }
    let answer = |id: &'static str, text: &'static str, picked: BoardAnswerValue| {
        let q = q.clone();
        Button::new(id)
            .ghost()
            .small()
            .label(text)
            .disabled(sending)
            .on_click(cx.listener(move |pane, _, _, cx| pane.send(&q, picked.clone(), cx)))
    };
    let actions = if q.kind == "approval" {
        h_flex()
            .gap_1()
            .child(answer("approve", "Approve", value("approve", None)))
            .child(answer("deny", "Deny", value("deny", None)))
    } else {
        let recommended = (q.recommendation.clone())
            .map(|text| answer("recommended", "As recommended", value("text", Some(text))));
        let input = pane.input(q, window, cx);
        let typed = (q.clone(), input.clone());
        let send = Button::new("send")
            .primary()
            .small()
            .label(if sending { "Sending…" } else { "Send" })
            .disabled(sending)
            .on_click(cx.listener(move |pane, _, _, cx| {
                let text = typed.1.read(cx).value().trim().to_string();
                if !text.is_empty() {
                    pane.send(&typed.0, value("text", Some(text)), cx);
                }
            }));
        h_flex()
            .gap_1()
            .child(div().flex_1().child(Input::new(&input).small()))
            .child(send)
            .children(recommended)
    };
    shell
        .child(actions)
        .when_some(error, |shell, error| {
            shell.child(div().text_xs().text_color(danger).child(error))
        })
        .into_any_element()
}
