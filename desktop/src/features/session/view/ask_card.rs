//! `AskCard` draws a permission or a question the agent is waiting on: a permission's options as buttons, or a
//! question's form (choices, multi-choices and text, with Submit, Skip and Cancel), answered through `send/answer`.
//! An answered or cancelled one shows its answer. Replaces chat/Ask.svelte.

use std::rc::Rc;

use gpui_kit::component::button::{Button, ButtonVariants as _};
use gpui_kit::component::input::{Textarea, TextareaState};
use gpui_kit::component::{ActiveTheme as _, h_flex, v_flex};
use gpui_kit::prelude::FluentBuilder as _;
use gpui_kit::*;
use serde_json::{Map, Value, json};

use super::session_action::{OnAction, SessionAction};
use super::task_card::card;
use crate::features::session::rows::ask::{AskInput, AskPayload, AskSchema};

#[derive(IntoElement)]
pub struct AskCard {
    pub ask_id: String,
    pub ask: AskPayload,
    /// An open question's form; `None` for a permission or a settled card.
    pub form: Option<AskForm>,
    pub on_action: OnAction,
}

/// What a settled card says it was answered with: the option's name, "Skipped", "Cancelled", the form's values, or
/// "Answered".
pub fn outcome(ask: &AskPayload) -> String {
    if ask.state == "cancelled" {
        return "Cancelled".to_string();
    }
    let answer = ask.answer.as_ref().unwrap_or(&Value::Null);
    if let Some(option) = answer["optionId"].as_str() {
        return ask
            .options
            .iter()
            .find(|it| it.option_id == option)
            .map_or_else(|| option.to_string(), |it| it.name.clone());
    }
    match answer["action"].as_str() {
        Some("decline") => return "Skipped".to_string(),
        Some("cancel") => return "Cancelled".to_string(),
        _ => {}
    }
    let content = answer["content"].as_object();
    let picked: Vec<String> = content
        .into_iter()
        .flatten()
        .filter(|(name, value)| !name.ends_with("_custom") || truthy(value))
        .map(|(_, value)| match value {
            Value::Array(list) => list
                .iter()
                .map(|it| it.as_str().map_or_else(|| it.to_string(), String::from))
                .collect::<Vec<_>>()
                .join(", "),
            Value::String(text) => text.clone(),
            other => other.to_string(),
        })
        .collect();
    if picked.is_empty() {
        "Answered".to_string()
    } else {
        picked.join("; ")
    }
}

fn truthy(value: &Value) -> bool {
    match value {
        Value::Null | Value::Bool(false) => false,
        Value::String(text) => !text.is_empty(),
        _ => true,
    }
}

/// An open question's form: the choices picked, by field, and each text field's input.
#[derive(Clone)]
pub struct AskForm {
    pub picked: Map<String, Value>,
    pub texts: Vec<(String, Entity<TextareaState>)>,
}

impl AskForm {
    /// The form's inputs for an open question with fields; `None` for anything else.
    pub fn of(ask: &AskPayload, window: &mut Window, cx: &mut App) -> Option<AskForm> {
        let fields = ask.schema.as_ref().and_then(AskSchema::of)?;
        let texts = fields
            .properties
            .into_iter()
            .filter(|(_, field)| field.input() == AskInput::Text)
            .map(|(name, _)| {
                let other = if name.ends_with("_custom") {
                    "Other"
                } else {
                    ""
                };
                let input = cx.new(|cx| {
                    TextareaState::new(window, cx)
                        .auto_grow(1, 4)
                        .placeholder(other)
                });
                (name, input)
            })
            .collect();
        Some(AskForm {
            picked: Map::new(),
            texts,
        })
    }

    /// What Submit sends: each picked choice and typed text, leaving out the empty ones.
    pub fn content(picked: &Map<String, Value>, texts: Vec<(String, String)>) -> Value {
        let mut content: Map<String, Value> = picked
            .iter()
            .filter(|(_, value)| match value {
                Value::Array(list) => !list.is_empty(),
                Value::String(text) => !text.is_empty(),
                _ => true,
            })
            .map(|(name, value)| (name.clone(), value.clone()))
            .collect();
        for (name, text) in texts {
            let text = text.trim();
            if !text.is_empty() {
                content.insert(name, Value::String(text.to_string()));
            }
        }
        json!({ "action": "accept", "content": content })
    }
}

impl RenderOnce for AskCard {
    fn render(self, _window: &mut Window, cx: &mut App) -> impl IntoElement {
        let theme = cx.theme();
        let (muted, accent) = (theme.muted_foreground, theme.primary);
        let open = self.ask.state == "open";
        let label = if self.ask.kind == "permission" {
            "Permission"
        } else {
            "Question"
        };
        let ask_id = self.ask_id.clone();
        let button = |id: &str, label: String, action: SessionAction| {
            let on_action = self.on_action.clone();
            let action = Rc::new(action);
            Button::new(SharedString::from(format!("ask:{ask_id}:{id}")))
                .compact()
                .label(label)
                .on_click(move |_, window, cx| on_action(action.as_ref().clone(), window, cx))
        };
        let answer = |answer: Value| SessionAction::Answer {
            ask_id: ask_id.clone(),
            answer,
        };
        let mut body = v_flex()
            .gap_2()
            .child(div().text_xs().text_color(muted).child(label))
            .when_some(self.ask.title.clone(), |column, title| {
                column.child(div().font_weight(FontWeight::MEDIUM).child(title))
            })
            .when_some(self.ask.message.clone(), |column, message| {
                column.child(message)
            });
        if !open {
            body = body.child(div().text_color(muted).child(outcome(&self.ask)));
        } else if let Some(form) = &self.form {
            let fields = self.ask.schema.as_ref().and_then(AskSchema::of);
            for (name, field) in fields.map(|fields| fields.properties).unwrap_or_default() {
                let mut fieldset = v_flex()
                    .gap_1()
                    .when_some(field.title.clone(), |column, title| {
                        column.child(div().font_weight(FontWeight::MEDIUM).child(title))
                    })
                    .when_some(field.description.clone(), |column, hint| {
                        column.child(div().text_xs().text_color(muted).child(hint))
                    });
                let (choices, many) = match field.input() {
                    AskInput::One(choices) => (choices, false),
                    AskInput::Many(choices) => (choices, true),
                    AskInput::Text => {
                        if let Some((_, input)) = form.texts.iter().find(|(text, _)| *text == name)
                        {
                            fieldset = fieldset.child(Textarea::new(input));
                        }
                        body = body.child(fieldset);
                        continue;
                    }
                };
                let mut row = h_flex().gap_1().flex_wrap();
                for choice in choices {
                    let picked = match form.picked.get(&name) {
                        Some(Value::Array(list)) => {
                            list.iter().any(|it| it.as_str() == Some(&choice.value))
                        }
                        Some(Value::String(value)) => *value == choice.value,
                        _ => false,
                    };
                    let mark = match (many, picked) {
                        (true, true) => "☑ ",
                        (true, false) => "☐ ",
                        (false, true) => "● ",
                        (false, false) => "○ ",
                    };
                    let text = format!(
                        "{mark}{}",
                        choice.title.clone().unwrap_or(choice.value.clone())
                    );
                    let pick = SessionAction::Pick {
                        ask_id: ask_id.clone(),
                        field: name.clone(),
                        value: choice.value.clone(),
                        many,
                    };
                    let id = format!("{name}:{}", choice.value);
                    row = row.child(
                        div()
                            .when(picked, |chip| chip.text_color(accent))
                            .child(button(&id, text, pick).ghost()),
                    );
                }
                body = body.child(fieldset.child(row));
            }
            body = body.child(
                h_flex()
                    .gap_2()
                    .child(
                        button(
                            "submit",
                            "Submit".into(),
                            SessionAction::SubmitAsk {
                                ask_id: ask_id.clone(),
                            },
                        )
                        .primary(),
                    )
                    .child(button(
                        "skip",
                        "Skip".into(),
                        answer(json!({ "action": "decline" })),
                    ))
                    .child(button(
                        "cancel",
                        "Cancel".into(),
                        answer(json!({ "action": "cancel" })),
                    )),
            );
        } else {
            let mut row = h_flex().gap_2().flex_wrap();
            for option in &self.ask.options {
                let pick = answer(json!({ "optionId": option.option_id }));
                let mut choice = button(&option.option_id, option.name.clone(), pick);
                if option.kind == "allow_once" {
                    choice = choice.primary();
                }
                row = row.child(choice);
            }
            body = body.child(row);
        }
        card(cx)
            .when(open, |card| card.border_color(accent))
            .child(body)
    }
}

#[cfg(test)]
#[path = "ask_card_tests.rs"]
mod tests;
