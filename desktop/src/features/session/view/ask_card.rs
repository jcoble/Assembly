//! `AskCard` draws a permission or a question the agent is waiting on: the agent's options as buttons, answered
//! through `send/answer`. A question's form is answered by Skip until its fields land. An answered or cancelled
//! one shows its answer. Replaces chat/Ask.svelte.

use gpui_kit::component::button::Button;
use gpui_kit::component::{ActiveTheme as _, h_flex, v_flex};
use gpui_kit::prelude::FluentBuilder as _;
use gpui_kit::*;
use serde_json::{Value, json};

use super::session_action::{OnAction, SessionAction};
use super::task_card::card;
use crate::features::session::rows::ask::{AskPayload, AskSchema};

#[derive(IntoElement)]
pub struct AskCard {
    pub ask_id: String,
    pub ask: AskPayload,
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

impl RenderOnce for AskCard {
    fn render(self, _window: &mut Window, cx: &mut App) -> impl IntoElement {
        let muted = cx.theme().muted_foreground;
        let open = self.ask.state == "open";
        let title = self
            .ask
            .title
            .clone()
            .or_else(|| self.ask.message.clone())
            .unwrap_or_else(|| "The agent is asking".to_string());
        let message = self
            .ask
            .message
            .clone()
            .filter(|_| self.ask.title.is_some());
        let button = |id: String, label: String, answer: Value| {
            let (ask_id, on_action) = (self.ask_id.clone(), self.on_action.clone());
            Button::new(SharedString::from(id))
                .compact()
                .label(label)
                .on_click(move |_, window, cx| {
                    let (ask_id, answer) = (ask_id.clone(), answer.clone());
                    on_action(SessionAction::Answer { ask_id, answer }, window, cx)
                })
        };
        let fields = self.ask.schema.as_ref().and_then(AskSchema::of);
        let mut buttons = h_flex().gap_2().flex_wrap();
        for option in &self.ask.options {
            let id = format!("ask:{}:{}", self.ask_id, option.option_id);
            buttons = buttons.child(button(
                id,
                option.name.clone(),
                json!({ "optionId": option.option_id }),
            ));
        }
        if fields.is_some() {
            let id = format!("ask:{}:skip", self.ask_id);
            buttons = buttons.child(button(id, "Skip".into(), json!({ "action": "decline" })));
        }
        card(cx).child(
            v_flex()
                .gap_2()
                .child(div().font_weight(FontWeight::MEDIUM).child(title))
                .when_some(message, |column, message| {
                    column.child(div().text_color(muted).child(message))
                })
                .when_some(fields.filter(|_| open), |column, fields| {
                    column.children(fields.properties.into_iter().map(|(name, field)| {
                        div()
                            .text_xs()
                            .text_color(muted)
                            .child(field.title.unwrap_or(name))
                    }))
                })
                .map(|column| {
                    if open {
                        column.child(buttons)
                    } else {
                        column.child(div().text_color(muted).child(outcome(&self.ask)))
                    }
                }),
        )
    }
}

#[cfg(test)]
#[path = "ask_card_tests.rs"]
mod tests;
