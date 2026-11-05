//! `AskRow` draws a permission or a question the agent is waiting on, answered through `send/answer`. Open, it's a
//! row on a lighter band, its message and controls under it: a permission's options as pill buttons, or a question's
//! fields as inline fields (a label beside its pill input or choices), with Send, Skip and Cancel; Enter in a text
//! field sends. Once answered it shrinks, growing in place backward, to one line: what it was answered with and its
//! title. It's an `ExpandRow` (pane_expand.rs), so its line opens and shuts it like any row that grows; a settled one
//! opens to what it asked. Replaces chat/Ask.svelte.

use crate::core::theme::text::{TextRole, TextRoleExt as _};
use std::rc::Rc;

use gpui_kit::assets::IconName as Glyph;
use gpui_kit::component::{ActiveTheme as _, Icon, Sizable as _, h_flex};
use gpui_kit::*;
use serde_json::Value;

use super::grow::chevron;
use super::session_action::{OnAction, SessionAction};
use crate::core::ui::ask::answer_words::option_words;
use crate::core::ui::ask::form::AskForm;
use crate::core::ui::ask::payload::{AskOption, AskPayload};

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
    let shown = AskForm::shown(ask.schema.as_ref(), answer);
    let content = shown["content"].as_object();
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

/// A permission's buttons as the mock's card has them: (option id, label), Deny first and Allow once last, the agent's
/// other options (an always-allow) between them.
pub fn permission_choices(options: &[AskOption]) -> Vec<(String, String)> {
    let rank = |option: &&AskOption| match option.kind.as_str() {
        "reject_once" => 0,
        "allow_once" => 2,
        _ => 1,
    };
    let mut options: Vec<&AskOption> = options.iter().collect();
    options.sort_by_key(rank);
    (options.into_iter())
        .map(|option| (option.option_id.clone(), option_words(option)))
        .collect()
}

/// A title on one line, as a tool line is (`Fold::tool_words`): a title of several lines (a heredoc command) shows its
/// first, ending in "…".
pub fn one_line(text: &str) -> String {
    match text.trim_end().split_once('\n') {
        Some((first, _)) => format!("{}…", first.trim_end()),
        None => text.trim_end().to_string(),
    }
}

fn truthy(value: &Value) -> bool {
    match value {
        Value::Null | Value::Bool(false) => false,
        Value::String(text) => !text.is_empty(),
        _ => true,
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
                .text_role(TextRole::Body, cx)
                .text_color(theme.muted_foreground)
                .child(Icon::new(mark).xsmall())
                .child(div().flex_none().max_w(px(240.)).truncate().child(word))
                .child(div().min_w_0().truncate().child(title))
                .child(chevron(grown));
        }
        // The mock's `.confirm` title: a 15 px warning mark, the title at medium weight; "Needs you" and the chevron
        // that shuts it at the right.
        let mark = if self.ask.kind == "permission" {
            Glyph::TriangleAlert
        } else {
            Glyph::CircleQuestionMark
        };
        h_flex()
            .h(px(28.))
            .gap_2()
            .text_role(TextRole::Body, cx)
            .child(Icon::new(mark).size(px(15.)).text_color(theme.warning))
            .child(
                div()
                    .flex_1()
                    .min_w_0()
                    .truncate()
                    .font_weight(FontWeight::MEDIUM)
                    .child(title),
            )
            .child(
                div()
                    .text_role(TextRole::Meta, cx)
                    .text_color(theme.muted_foreground)
                    .child("Needs you"),
            )
            .child(chevron(grown).text_color(theme.muted_foreground))
    }

    /// The message and what answers it, under the title; a settled one's message and fields, with nothing to answer.
    pub fn controls(&self, cx: &App) -> Div {
        use crate::core::ui::ask::controls;
        let choices = permission_choices(&self.ask.options)
            .into_iter()
            .map(|(id, label)| {
                let solid = label == "Allow";
                (id, label, solid)
            })
            .collect::<Vec<_>>();
        controls::render(
            &self.ask_id,
            &self.ask,
            self.form.as_ref(),
            &choices,
            self.on_control(),
            cx,
        )
    }

    /// What this ask's controls call, whichever surface draws them.
    pub fn on_control(&self) -> crate::core::ui::ask::controls::OnControl {
        use crate::core::ui::ask::controls::ControlAction;
        let ask_id = self.ask_id.clone();
        let on_action = self.on_action.clone();
        Rc::new(move |action, window, cx| {
            let action = match action {
                ControlAction::Answer(answer) => SessionAction::Answer {
                    ask_id: ask_id.clone(),
                    answer,
                },
                ControlAction::Pick { field, value, many } => SessionAction::Pick {
                    ask_id: ask_id.clone(),
                    field,
                    value,
                    many,
                },
                ControlAction::Flag { field, on } => SessionAction::Flag {
                    ask_id: ask_id.clone(),
                    field,
                    on,
                },
                ControlAction::Submit => SessionAction::SubmitAsk {
                    ask_id: ask_id.clone(),
                },
            };
            on_action(action, window, cx);
        })
    }
}
#[cfg(test)]
#[path = "tests/ask_row_tests.rs"]
mod tests;
