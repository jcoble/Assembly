//! What answers an ask, on any surface: its message, a permission's options, or a question's form. The owning surface
//! supplies option presentation and sends the actual answers.
use super::{
    ask_questionnaire::{AskQuestionnaire, OnAnswer},
    payload::{AskInput, AskPayload, AskSchema},
};
use crate::core::theme::text::{TextRole, TextRoleExt as _};
use crate::inline_field::field_row;
use gpui_kit::component::button::{Button, ButtonVariants as _};
use gpui_kit::component::{ActiveTheme as _, h_flex, v_flex};
use gpui_kit::prelude::FluentBuilder as _;
use gpui_kit::*;
use serde_json::json;
/// What a question asked, for its settled card: each field's label, its hint, and its choices joined ("" for text).
pub fn asked(ask: &AskPayload) -> Vec<(String, Option<String>, String)> {
    let fields = ask.schema.as_ref().and_then(AskSchema::of);
    fields
        .map(|fields| fields.properties)
        .unwrap_or_default()
        .into_iter()
        .map(|(name, field)| {
            let choices = match field.input() {
                AskInput::One(choices) | AskInput::Many(choices) => choices
                    .iter()
                    .map(|choice| choice.title.clone().unwrap_or(choice.value.clone()))
                    .collect::<Vec<_>>()
                    .join(", "),
                AskInput::Text | AskInput::Secret | AskInput::Flag => String::new(),
            };
            (
                field.title.clone().unwrap_or(name),
                field.description.clone(),
                choices,
            )
        })
        .collect()
}

pub fn render(
    key: &str,
    ask: &AskPayload,
    form: Option<&Entity<AskQuestionnaire>>,
    choices: &[(String, String, bool)],
    on_answer: OnAnswer,
    cx: &App,
) -> Div {
    let theme = cx.theme();
    // The mock's `.confirm` body: the reason muted at 12.5 px under the title, in line with it.
    let mut column = v_flex()
        .gap_2()
        .pl(px(23.))
        .pb_1()
        .text_role(TextRole::Meta, cx)
        .when_some(ask.message.clone(), |column, message| {
            column.child(div().text_color(theme.muted_foreground).child(message))
        });
    // Settled, it shows what it asked (each field and its choices) with nothing to answer.
    if ask.state != "open" {
        for (label, hint, choices) in asked(ask) {
            let value = div()
                .flex_1()
                .text_color(theme.muted_foreground)
                .child(choices);
            column = column.child(field_row(label, hint.map(Into::into), None, value, cx));
        }
        return column;
    }
    if let Some(form) = form {
        return column.child(form.clone());
    }
    // A permission's options at the card's right, as the mock's: Deny, the agent's others, then Allow, solid.
    let mut row = h_flex().gap(px(6.)).flex_wrap().justify_end();
    for (id, label, solid) in choices {
        let on_answer = on_answer.clone();
        let answer = json!({ "optionId": id });
        let choice = Button::new(SharedString::from(format!("ask:{key}:{id}")))
            .compact()
            .h(px(28.))
            .px(px(12.))
            .rounded(px(8.))
            .label(label.clone())
            .on_click(move |_, window, cx| on_answer(answer.clone(), window, cx));
        row = row.child(match *solid {
            true => choice.primary().font_weight(FontWeight::SEMIBOLD),
            false => choice.outline().h(px(26.)).px(px(10.)),
        });
    }
    column.child(row)
}
