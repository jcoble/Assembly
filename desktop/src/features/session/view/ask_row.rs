//! `AskRow` draws a permission or a question the agent is waiting on, answered through `send/answer`. Open, it's a
//! row on a lighter band, its message and controls under it: a permission's options as pill buttons, or a question's
//! fields as inline fields (a label beside its pill input or choices), with Send, Skip and Cancel; Enter in a text
//! field sends. Once answered it shrinks, growing in place backward, to one line: what it was answered with and its
//! title. Replaces chat/Ask.svelte.

use std::rc::Rc;
use std::time::Instant;

use gpui_kit::assets::IconName as Glyph;
use gpui_kit::component::button::{Button, ButtonVariants as _};
use gpui_kit::component::input::{Input, InputState};
use gpui_kit::component::{ActiveTheme as _, Icon, Sizable as _, h_flex, v_flex};
use gpui_kit::prelude::FluentBuilder as _;
use gpui_kit::*;
use serde_json::{Map, Value, json};

use super::grow::{band, faded, grow, turn};
use super::session_action::{OnAction, SessionAction};
use crate::features::session::rows::ask::{AskInput, AskPayload, AskSchema};

#[derive(IntoElement)]
pub struct AskRow {
    pub ask_id: String,
    pub ask: AskPayload,
    /// When it settled while shown (`Kept::toggled_at`), so it shrinks from then; `None` for one drawn settled.
    pub toggled_at: Option<Instant>,
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
    pub texts: Vec<(String, Entity<InputState>)>,
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
                let input = cx.new(|cx| InputState::new(window, cx).placeholder(other));
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

impl RenderOnce for AskRow {
    fn render(self, window: &mut Window, cx: &mut App) -> impl IntoElement {
        let open = self.ask.state == "open";
        let turned = turn(open, self.toggled_at, window, cx);
        let faded = faded(open, self.toggled_at, cx);
        let head = self.head(open, cx);
        let body = (open || turned.1).then(|| {
            let controls = self.controls(cx);
            let id = format!("ask:{}:grow", self.ask_id).into();
            grow(id, turned, open, faded, controls, window, cx)
        });
        band(v_flex().child(head).children(body), turned.0, cx).text_sm()
    }
}

impl AskRow {
    /// The 28 px line: a mark, the title, and "Needs you" while open; settled, what it was answered with and the title.
    fn head(&self, open: bool, cx: &App) -> Div {
        let theme = cx.theme();
        let kind = if self.ask.kind == "permission" {
            "Permission"
        } else {
            "Question"
        };
        let title = self.ask.title.clone().unwrap_or_else(|| kind.to_string());
        let (mark, color) = match (open, self.ask.state.as_str()) {
            (true, _) if self.ask.kind == "permission" => {
                (Glyph::ShieldQuestionMark, theme.warning)
            }
            (true, _) => (Glyph::CircleQuestionMark, theme.warning),
            (false, "cancelled") => (Glyph::CircleX, theme.muted_foreground),
            (false, _) => (Glyph::CircleCheck, theme.success),
        };
        let line = h_flex()
            .h(px(28.))
            .gap_2()
            .child(Icon::new(mark).xsmall().text_color(color));
        if open {
            return line
                .child(div().flex_1().min_w_0().truncate().child(title))
                .child(
                    div()
                        .text_xs()
                        .text_color(theme.muted_foreground)
                        .child("Needs you"),
                );
        }
        line.child(
            div()
                .flex_1()
                .min_w_0()
                .truncate()
                .child(outcome(&self.ask))
                .child(
                    div()
                        .text_color(theme.muted_foreground)
                        .child(format!(" · {title}")),
                ),
        )
    }

    /// The message and what answers it, under the title.
    fn controls(&self, cx: &App) -> Div {
        let theme = cx.theme();
        let ask_id = self.ask_id.clone();
        let button = |id: &str, label: String, action: SessionAction| {
            let on_action = self.on_action.clone();
            let action = Rc::new(action);
            Button::new(SharedString::from(format!("ask:{ask_id}:{id}")))
                .compact()
                .rounded(px(999.))
                .label(label)
                .on_click(move |_, window, cx| on_action(action.as_ref().clone(), window, cx))
        };
        let answer = |answer: Value| SessionAction::Answer {
            ask_id: ask_id.clone(),
            answer,
        };
        let mut column = v_flex().gap_2().pl(px(24.)).pr_2().pb_2().when_some(
            self.ask.message.clone(),
            |column, message| {
                column.child(
                    div()
                        .text_color(theme.foreground.opacity(0.9))
                        .child(message),
                )
            },
        );
        let Some(form) = &self.form else {
            let mut row = h_flex().gap_2().flex_wrap();
            for option in &self.ask.options {
                let pick = answer(json!({ "optionId": option.option_id }));
                let choice = button(&option.option_id, option.name.clone(), pick);
                row = row.child(match option.kind.as_str() {
                    "allow_once" => choice.primary(),
                    _ => choice.secondary(),
                });
            }
            return column.child(row);
        };
        let fields = self.ask.schema.as_ref().and_then(AskSchema::of);
        for (name, field) in fields.map(|fields| fields.properties).unwrap_or_default() {
            let label = field.title.clone().unwrap_or_else(|| name.clone());
            let value = match field.input() {
                AskInput::Text => match form.texts.iter().find(|(text, _)| *text == name) {
                    Some((_, input)) => {
                        let on_action = self.on_action.clone();
                        let submit = SessionAction::SubmitAsk {
                            ask_id: ask_id.clone(),
                        };
                        div()
                            .flex_1()
                            .capture_key_down(move |event, window, cx| {
                                if event.keystroke.key == "enter" {
                                    on_action(submit.clone(), window, cx);
                                }
                            })
                            .child(Input::new(input).h(px(26.)).rounded(px(999.)))
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
                        let pick = SessionAction::Pick {
                            ask_id: ask_id.clone(),
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
            column = column.child(inline_field(label, field.description.clone(), value, cx));
        }
        column.child(
            h_flex()
                .gap_2()
                .child(
                    button(
                        "submit",
                        "Send".into(),
                        SessionAction::SubmitAsk {
                            ask_id: ask_id.clone(),
                        },
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
}

/// An inline field's editing row: its label in a 64 px column, beside the input or choices, and its hint under them.
fn inline_field(label: String, hint: Option<String>, value: Div, cx: &App) -> Div {
    let muted = cx.theme().muted_foreground;
    v_flex()
        .gap_1()
        .child(
            h_flex()
                .min_h(px(28.))
                .gap_2()
                .child(
                    div()
                        .w(px(64.))
                        .flex_none()
                        .text_xs()
                        .text_color(muted)
                        .truncate()
                        .child(label),
                )
                .child(value),
        )
        .when_some(hint, |column, hint| {
            column.child(div().pl(px(72.)).text_xs().text_color(muted).child(hint))
        })
}

#[cfg(test)]
#[path = "ask_row_tests.rs"]
mod tests;
