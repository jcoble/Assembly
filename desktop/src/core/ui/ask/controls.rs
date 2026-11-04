//! One shared provider form renderer. The owning surface supplies option presentation and submits actual answers.
use crate::core::theme::text::{TextRole, TextRoleExt as _};
use crate::inline_field::field_row;
use std::rc::Rc;
use gpui_kit::*;
use gpui_kit::component::{ActiveTheme as _,h_flex,v_flex};
use gpui_kit::component::button::{Button,ButtonVariants as _};
use gpui_kit::component::checkbox::Checkbox;
use gpui_kit::component::input::Input;
use gpui_kit::prelude::FluentBuilder as _;
use serde_json::{Value,json};
use super::{form::AskForm,payload::{AskPayload,AskInput,AskSchema}};
#[derive(Clone)]
pub enum ControlAction {Answer(Value),Pick{field:String,value:String,many:bool},Flag{field:String,on:bool},Submit}
pub type OnControl=Rc<dyn Fn(ControlAction,&mut Window,&mut App)>;
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

pub fn render(key:&str,ask:&AskPayload,form:Option<&AskForm>,choices:&[(String,String,bool)],on_action:OnControl,cx:&App)->Div {
        let theme = cx.theme();
        let ask_id=key.to_string();
        let button = |id: &str, label: String, action: ControlAction| {
            let on_action = on_action.clone();
            let action = Rc::new(action);
            Button::new(SharedString::from(format!("ask:{ask_id}:{id}")))
                .compact()
                .h(px(28.))
                .px(px(12.))
                .rounded(px(8.))
                .label(label)
                .on_click(move |_, window, cx| on_action(action.as_ref().clone(), window, cx))
        };
        let answer = ControlAction::Answer;
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
            for (label, hint, choices) in asked(&ask) {
                let value = div()
                    .flex_1()
                    .text_color(theme.muted_foreground)
                    .child(choices);
                column = column.child(field_row(label, hint.map(Into::into), None, value, cx));
            }
            return column;
        }
        // A permission's options at the card's right, as the mock's: Deny, the agent's others, then Allow, solid.
        let Some(form) = form else {
            let mut row = h_flex().gap(px(6.)).flex_wrap().justify_end();
            for (id,label,solid) in choices {
                let pick=answer(json!({"optionId":id}));
                let choice=button(id,label.clone(),pick);
                row=row.child(match *solid {
                    true => choice.primary().font_weight(FontWeight::SEMIBOLD),
                    false => choice.outline().h(px(26.)).px(px(10.)),
                });
            }
            return column.child(row);
        };
        let fields = ask.schema.as_ref().and_then(AskSchema::of);
        for (name, field) in fields.map(|fields| fields.properties).unwrap_or_default() {
            let label = field.title.clone().unwrap_or_else(|| name.clone());
            let value = match field.input() {
                AskInput::Flag => {
                    let on = form.picked.get(&name).and_then(Value::as_bool).unwrap_or(false);
                    let on_action = on_action.clone();
                    let flag = ControlAction::Flag { field: name.clone(), on: !on };
                    h_flex().flex_1().child(
                        Checkbox::new(SharedString::from(format!("ask:{ask_id}:{name}")))
                            .checked(on)
                            .label("Yes")
                            .on_click(move |_, window, cx| on_action(flag.clone(), window, cx)),
                    )
                }
                AskInput::Text | AskInput::Secret => match form.texts.iter().find(|(text, _)| *text == name) {
                    Some((_, input)) => {
                        let on_action = on_action.clone();
                        let submit=ControlAction::Submit;
                        div()
                            .flex_1()
                            .capture_key_down(move |event, window, cx| {
                                if event.keystroke.key == "enter" && !event.keystroke.modifiers.shift && !event.is_held {
                                    on_action(submit.clone(), window, cx);
                                }
                            })
                            .child(Input::new(input).h(px(26.)).rounded(theme.radius_full()))
                    }
                    None => div(),
                },
                AskInput::One(choices) | AskInput::Many(choices) => {
                    let many = matches!(field.input(), AskInput::Many(_));
                    let mut row = h_flex().flex_1().gap_1().flex_wrap();
                    for choice in choices {
                        let picked = match form.picked.get(&name) {
                            Some(Value::Array(list)) => {
                                list.iter().any(|it| it.as_str() == Some(&choice.value))
                            }
                            Some(Value::String(value)) => *value == choice.value,
                            _ => false,
                        };
                        let pick = ControlAction::Pick {
                            field: name.clone(),
                            value: choice.value.clone(),
                            many,
                        };
                        let text = choice.title.clone().unwrap_or(choice.value.clone());
                        let chip = button(&format!("{name}:{}", choice.value), text, pick);
                        row = row.child(if picked {
                            chip.primary()
                        } else {
                            chip.secondary()
                        });
                    }
                    row
                }
            };
            column = column.child(field_row(label, field.description.clone().map(Into::into), None, value, cx));
        }
        column.child(
            h_flex()
                .gap_2()
                .child(
                    button(
                        "submit",
                        "Send".into(),
                        ControlAction::Submit,
                    )
                    .primary(),
                )
                .child(
                    button(
                        "skip",
                        "Skip".into(),
                        answer(json!({ "action": "decline" })),
                    )
                    .ghost(),
                )
                .child(
                    button(
                        "cancel",
                        "Cancel".into(),
                        answer(json!({ "action": "cancel" })),
                    )
                    .ghost(),
                ),
        )
    }

