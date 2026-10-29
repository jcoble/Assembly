//! `AskRow` draws a permission or a question the agent is waiting on, answered through `send/answer`. Open, it's a
//! row on a lighter band, its message and controls under it: a permission's options as pill buttons, or a question's
//! fields as inline fields (a label beside its pill input or choices), with Send, Skip and Cancel; Enter in a text
//! field sends. Once answered it shrinks, growing in place backward, to one line: what it was answered with and its
//! title. It's an `ExpandRow` (pane_expand.rs), so its line opens and shuts it like any row that grows; a settled one
//! opens to what it asked. Replaces chat/Ask.svelte.

use std::rc::Rc;

use gpui_kit::assets::IconName as Glyph;
use gpui_kit::component::button::{Button, ButtonVariants as _};
use gpui_kit::component::input::{Input, InputState};
use gpui_kit::component::{ActiveTheme as _, Icon, Sizable as _, h_flex, v_flex};
use gpui_kit::prelude::FluentBuilder as _;
use gpui_kit::*;
use serde_json::{Map, Value, json};

use super::grow::chevron;
use super::session_action::{OnAction, SessionAction};
use crate::features::session::rows::ask::{AskInput, AskPayload, AskSchema};

#[derive(Clone)]
pub struct AskRow {
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

/// A settled card's word: "Allowed" or "Denied" for a permission answered with one of its options, else `outcome`.
pub fn settled(ask: &AskPayload) -> String {
    let picked = ask
        .answer
        .as_ref()
        .and_then(|answer| answer["optionId"].as_str());
    let option = picked.and_then(|id| ask.options.iter().find(|it| it.option_id == id));
    match option.map(|it| it.kind.as_str()) {
        Some(kind) if kind.starts_with("allow") => "Allowed".to_string(),
        Some(kind) if kind.starts_with("reject") => "Denied".to_string(),
        _ => outcome(ask),
    }
}

/// A title on one line, as a tool line is (`Fold::tool_words`): a title of several lines (a heredoc command) shows its
/// first, ending in "…".
pub fn one_line(text: &str) -> String {
    match text.trim_end().split_once('\n') {
        Some((first, _)) => format!("{}…", first.trim_end()),
        None => text.trim_end().to_string(),
    }
}

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
                AskInput::Text => String::new(),
            };
            (
                field.title.clone().unwrap_or(name),
                field.description.clone(),
                choices,
            )
        })
        .collect()
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

impl AskRow {
    /// Open, the 28 px line: a chevron turned `grown` of the way, a mark, the title, and "Needs you". Settled, a muted
    /// tool line: a check (a cross if denied or cancelled), what it was answered with, the title, then the chevron.
    pub fn head(&self, grown: f32, cx: &App) -> Div {
        let theme = cx.theme();
        let open = self.ask.state == "open";
        let kind = if self.ask.kind == "permission" {
            "Permission"
        } else {
            "Question"
        };
        let title = one_line(self.ask.title.as_deref().unwrap_or(kind));
        if !open {
            let word = settled(&self.ask);
            let crossed = self.ask.state == "cancelled" || word == "Denied";
            let mark = if crossed {
                Glyph::CircleX
            } else {
                Glyph::CircleCheck
            };
            return h_flex()
                .h(px(22.))
                .gap_2()
                .text_size(px(13.))
                .text_color(theme.muted_foreground)
                .child(Icon::new(mark).xsmall())
                .child(div().flex_none().max_w(px(240.)).truncate().child(word))
                .child(div().min_w_0().truncate().child(title))
                .child(chevron(grown));
        }
        let mark = if self.ask.kind == "permission" {
            Glyph::ShieldQuestionMark
        } else {
            Glyph::CircleQuestionMark
        };
        h_flex()
            .h(px(28.))
            .gap_2()
            .text_sm()
            .child(chevron(grown))
            .child(Icon::new(mark).xsmall().text_color(theme.warning))
            .child(div().flex_1().min_w_0().truncate().child(title))
            .child(
                div()
                    .text_xs()
                    .text_color(theme.muted_foreground)
                    .child("Needs you"),
            )
    }

    /// The message and what answers it, under the title; a settled one's message and fields, with nothing to answer.
    pub fn controls(&self, cx: &App) -> Div {
        let theme = cx.theme();
        let ask_id = self.ask_id.clone();
        let button = |id: &str, label: String, action: SessionAction| {
            let on_action = self.on_action.clone();
            let action = Rc::new(action);
            Button::new(SharedString::from(format!("ask:{ask_id}:{id}")))
                .compact()
                .rounded(theme.radius)
                .label(label)
                .on_click(move |_, window, cx| on_action(action.as_ref().clone(), window, cx))
        };
        let answer = |answer: Value| SessionAction::Answer {
            ask_id: ask_id.clone(),
            answer,
        };
        let mut column = v_flex()
            .gap_2()
            .pl(px(24.))
            .pr_2()
            .pb_2()
            .text_sm()
            .when_some(self.ask.message.clone(), |column, message| {
                column.child(
                    div()
                        .text_color(theme.foreground.opacity(0.9))
                        .child(message),
                )
            });
        // Settled, it shows what it asked (each field and its choices) with nothing to answer.
        if self.ask.state != "open" {
            for (label, hint, choices) in asked(&self.ask) {
                let value = div()
                    .flex_1()
                    .text_color(theme.muted_foreground)
                    .child(choices);
                column = column.child(inline_field(label, hint, value, cx));
            }
            return column;
        }
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
                                if event.keystroke.key == "enter" && !event.is_held {
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
