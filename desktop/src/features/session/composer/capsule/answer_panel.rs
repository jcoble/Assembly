//! What the agent is waiting on, drawn inside the composer's box above the draft: "Pending approval" with the action,
//! its details and the options; or "Input requested" with the question's form, the kit's Questionnaire. Everything
//! answers through the owner's one answer path; this only draws. Replaces the old app's ComposerPendingApprovalPanel and
//! ComposerPendingUserInputPanel.

use crate::core::theme::text::{TextRole, TextRoleExt as _};
use crate::core::ui::access::Named as _;
use gpui_kit::assets::IconName as Glyph;
use gpui_kit::component::button::{Button, ButtonVariants as _};
use gpui_kit::component::{ActiveTheme as _, Icon, h_flex, v_flex};
use gpui_kit::prelude::FluentBuilder as _;
use gpui_kit::*;
use serde_json::json;

use crate::core::ui::ask::answer_words::{is_deny, option_words};
use crate::core::ui::ask::ask_questionnaire::{AskQuestionnaire, OnAnswer};
use crate::core::ui::ask::payload::AskPayload;
use crate::features::session::view::ask_row::one_line;

#[derive(Clone, IntoElement)]
pub struct AnswerPanel {
    pub ask_id: String,
    pub ask: AskPayload,
    /// A question's form; `None` for a permission.
    pub form: Option<Entity<AskQuestionnaire>>,
    /// How many asks of this kind are waiting, this one included.
    pub count: usize,
    pub on_answer: OnAnswer,
}

/// The action on one line, and the details box's text: the agent's message, or all of a title too long for the line.
pub fn approval_words(ask: &AskPayload) -> (String, Option<String>) {
    let title = ask
        .title
        .as_deref()
        .filter(|title| !title.trim().is_empty());
    let message = ask
        .message
        .as_deref()
        .filter(|message| !message.trim().is_empty());
    let tool = one_line(title.or(message).unwrap_or("Action requested"));
    let details = message
        .filter(|message| message.trim() != tool)
        .or(title.filter(|title| title.trim_end() != tool))
        .map(str::to_string);
    (tool, details)
}

fn kicker(words: &'static str, cx: &App) -> Div {
    div()
        .text_role(TextRole::Meta, cx)
        .font_weight(FontWeight::BOLD)
        .child(words)
}

fn pill(
    id: String,
    label: String,
    color: Hsla,
    answer: serde_json::Value,
    on_answer: &OnAnswer,
    cx: &App,
) -> Button {
    let on_answer = on_answer.clone();
    Button::new(SharedString::from(id))
        .named(label.clone())
        .compact()
        .h(px(28.))
        .px(px(11.))
        .rounded(px(999.))
        .child(
            div()
                .text_role(TextRole::Meta, cx)
                .font_weight(FontWeight::SEMIBOLD)
                .text_color(color)
                .child(label),
        )
        .on_click(move |_, window, cx| on_answer(answer.clone(), window, cx))
}

impl AnswerPanel {
    fn approval(&self, cx: &App) -> Div {
        let theme = cx.theme();
        let (tool, details) = approval_words(&self.ask);
        let last = self.ask.options.len().saturating_sub(1);
        let mut actions = h_flex().flex_wrap().justify_end().gap(px(7.)).mt(px(13.));
        for (at, option) in self.ask.options.iter().enumerate() {
            let deny = is_deny(option);
            let solid = !deny && at == last;
            let color = match (deny, solid) {
                (true, _) => theme.danger,
                (_, true) => theme.primary_foreground,
                _ => theme.foreground,
            };
            let button = pill(
                format!("answer:{}:{}", self.ask_id, option.option_id),
                option_words(option),
                color,
                json!({ "optionId": option.option_id }),
                &self.on_answer,
                cx,
            );
            actions = actions.child(if solid {
                button.primary()
            } else {
                button.outline()
            });
        }
        v_flex()
            .child(
                h_flex()
                    .gap(px(8.))
                    .text_color(theme.warning)
                    .child(Icon::new(Glyph::ShieldAlert).size(px(16.)))
                    .child(kicker("PENDING APPROVAL", cx))
                    .when(self.count > 1, |heading| {
                        heading.child(
                            div()
                                .px(px(6.))
                                .py(px(2.))
                                .rounded(px(6.))
                                .bg(theme.warning.opacity(0.13))
                                .text_role(TextRole::Meta, cx)
                                .text_color(theme.muted_foreground)
                                .child(format!("1/{}", self.count)),
                        )
                    }),
            )
            .child(
                div()
                    .mt(px(9.))
                    .text_role(TextRole::Body, cx)
                    .font_weight(FontWeight::SEMIBOLD)
                    .child("Approval needed"),
            )
            .child(
                div()
                    .mt(px(4.))
                    .text_role(TextRole::Meta, cx)
                    .font_family(theme.mono_font_family.clone())
                    .text_color(theme.muted_foreground)
                    .child(tool),
            )
            .when_some(details, |panel, details| {
                panel.child(
                    v_flex()
                        .mt(px(11.))
                        .px(px(10.))
                        .py(px(9.))
                        .border_1()
                        .border_color(theme.border.opacity(0.72))
                        .rounded(px(9.))
                        .bg(theme.background.opacity(0.76))
                        .child(
                            div()
                                .text_role(TextRole::Meta, cx)
                                .font_weight(FontWeight::SEMIBOLD)
                                .text_color(theme.muted_foreground)
                                .child("Details"),
                        )
                        .child(
                            div()
                                .id(SharedString::from(format!(
                                    "answer:{}:details",
                                    self.ask_id
                                )))
                                .mt(px(6.))
                                .max_h(px(150.))
                                .overflow_y_scroll()
                                .text_role(TextRole::Meta, cx)
                                .font_family(theme.mono_font_family.clone())
                                .child(details),
                        ),
                )
            })
            .child(actions)
    }

    fn input(&self, form: &Entity<AskQuestionnaire>, cx: &App) -> Div {
        let theme = cx.theme();
        let muted = theme.muted_foreground;
        v_flex()
            .child(
                h_flex()
                    .gap(px(8.))
                    .text_color(muted)
                    .child(kicker("INPUT REQUESTED", cx))
                    .when(self.count > 1, |heading| {
                        heading.child(
                            div()
                                .px(px(6.))
                                .py(px(2.))
                                .rounded(px(6.))
                                .bg(theme.background.opacity(0.8))
                                .text_role(TextRole::Meta, cx)
                                .child(format!("1/{}", self.count)),
                        )
                    }),
            )
            .child(
                div()
                    .mt(px(9.))
                    .text_role(TextRole::Body, cx)
                    .font_weight(FontWeight::SEMIBOLD)
                    .child(
                        self.ask
                            .title
                            .clone()
                            .unwrap_or_else(|| "Input requested".into()),
                    ),
            )
            .when_some(self.ask.message.clone(), |panel, message| {
                panel.child(
                    div()
                        .mt(px(5.))
                        .text_role(TextRole::Meta, cx)
                        .text_color(muted)
                        .child(message),
                )
            })
            .child(div().mt(px(13.)).child(form.clone()))
    }
}

impl RenderOnce for AnswerPanel {
    fn render(self, _: &mut Window, cx: &mut App) -> impl IntoElement {
        let theme = cx.theme();
        let tint = match self.form {
            Some(_) => theme.primary.opacity(0.06),
            None => theme.warning.opacity(0.08),
        };
        let body = match &self.form {
            Some(form) => self.input(form, cx),
            None => self.approval(cx),
        };
        div()
            .w_full()
            .pt(px(16.))
            .px(px(18.))
            .pb(px(14.))
            .border_b_1()
            .border_color(theme.border.opacity(0.68))
            .rounded_t(px(11.))
            .bg(tint)
            .child(body)
    }
}

#[cfg(test)]
#[path = "answer_panel_tests.rs"]
mod tests;
