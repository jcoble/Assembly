//! What the agent is waiting on, drawn inside the composer's box above the draft: "Pending approval" with the action,
//! its details and the options; or "Input requested" with the question's fields and Cancel, Skip and Continue. Everything
//! answers through the owner's one answer path; this only draws. Replaces the old app's ComposerPendingApprovalPanel and
//! ComposerPendingUserInputPanel.

use gpui_kit::assets::IconName as Glyph;
use gpui_kit::component::button::{Button, ButtonVariants as _};
use gpui_kit::component::checkbox::Checkbox;
use gpui_kit::component::input::Input;
use gpui_kit::component::{ActiveTheme as _, Icon, h_flex, v_flex};
use gpui_kit::prelude::FluentBuilder as _;
use gpui_kit::*;
use serde_json::{Value, json};

use crate::core::ui::ask::controls::{ControlAction, OnControl};
use crate::core::ui::ask::form::AskForm;
use crate::core::ui::ask::payload::{AskChoice, AskInput, AskOption, AskPayload, AskSchema};
use crate::features::session::view::ask_row::one_line;

#[derive(Clone, IntoElement)]
pub struct AnswerPanel {
    pub ask_id: String,
    pub ask: AskPayload,
    /// A question's form; `None` for a permission.
    pub form: Option<AskForm>,
    /// How many asks of this kind are waiting, this one included.
    pub count: usize,
    pub on_action: OnControl,
}

fn refuses(kind: &str) -> bool {
    ["reject", "deny", "decline"]
        .iter()
        .any(|word| kind.contains(word))
}

/// An option's words. A refusal that lasts keeps the agent's own words, so it never reads as an allow.
pub fn option_label(option: &AskOption) -> String {
    let kind = option.kind.to_ascii_lowercase();
    let named = |fallback: &str| {
        if option.name.is_empty() {
            fallback.to_string()
        } else {
            option.name.clone()
        }
    };
    if refuses(&kind) {
        return if kind.contains("always") {
            named("Decline")
        } else {
            "Decline".to_string()
        };
    }
    if kind.contains("always") || kind.contains("session") {
        "Always allow this session".to_string()
    } else if kind.contains("allow") || kind.contains("accept") {
        "Approve once".to_string()
    } else {
        named("Continue")
    }
}

/// Whether an option turns the action down, which reads in the danger color.
pub fn is_deny(option: &AskOption) -> bool {
    let label = option_label(option).to_ascii_lowercase();
    refuses(&option.kind.to_ascii_lowercase()) || label.contains("cancel")
}

/// The action on one line, and the details box's text: the agent's message, or all of a title too long for the line.
pub fn approval_words(ask: &AskPayload) -> (String, Option<String>) {
    let title = ask.title.as_deref().filter(|title| !title.trim().is_empty());
    let message = ask.message.as_deref().filter(|message| !message.trim().is_empty());
    let tool = one_line(title.or(message).unwrap_or("Action requested"));
    let details = message
        .filter(|message| message.trim() != tool)
        .or(title.filter(|title| title.trim_end() != tool))
        .map(str::to_string);
    (tool, details)
}

/// A field's heading, marked when it can be left out.
pub fn legend(label: &str, required: bool) -> String {
    if required {
        label.to_string()
    } else {
        format!("{label} (optional)")
    }
}

fn kicker(words: &'static str) -> Div {
    div()
        .text_size(px(12.))
        .font_weight(FontWeight::BOLD)
        .child(words)
}

fn pill(id: String, label: String, color: Hsla, action: ControlAction, on_action: &OnControl) -> Button {
    let on_action = on_action.clone();
    Button::new(SharedString::from(id))
        .compact()
        .h(px(28.))
        .px(px(11.))
        .rounded(px(999.))
        .child(
            div()
                .text_size(px(12.))
                .font_weight(FontWeight::SEMIBOLD)
                .text_color(color)
                .child(label),
        )
        .on_click(move |_, window, cx| on_action(action.clone(), window, cx))
}

fn choice_copy(choice: &AskChoice, muted: Hsla) -> Div {
    v_flex()
        .min_w_0()
        .gap(px(2.))
        .items_start()
        .child(
            div()
                .text_size(px(13.))
                .font_weight(FontWeight::SEMIBOLD)
                .child(choice.title.clone().unwrap_or(choice.value.clone())),
        )
        .when_some(choice.description.clone(), |copy, description| {
            copy.child(div().text_size(px(12.)).text_color(muted).child(description))
        })
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
                option_label(option),
                color,
                ControlAction::Answer(json!({ "optionId": option.option_id })),
                &self.on_action,
            );
            actions = actions.child(if solid { button.primary() } else { button.outline() });
        }
        v_flex()
            .child(
                h_flex()
                    .gap(px(8.))
                    .text_color(theme.warning)
                    .child(Icon::new(Glyph::ShieldAlert).size(px(16.)))
                    .child(kicker("PENDING APPROVAL"))
                    .when(self.count > 1, |heading| {
                        heading.child(
                            div()
                                .px(px(6.))
                                .py(px(2.))
                                .rounded(px(6.))
                                .bg(theme.warning.opacity(0.13))
                                .text_size(px(12.))
                                .text_color(theme.muted_foreground)
                                .child(format!("1/{}", self.count)),
                        )
                    }),
            )
            .child(
                div()
                    .mt(px(9.))
                    .text_size(px(14.))
                    .font_weight(FontWeight::SEMIBOLD)
                    .child("Approval needed"),
            )
            .child(
                div()
                    .mt(px(4.))
                    .text_size(px(12.))
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
                                .text_size(px(12.))
                                .font_weight(FontWeight::SEMIBOLD)
                                .text_color(theme.muted_foreground)
                                .child("Details"),
                        )
                        .child(
                            div()
                                .id(SharedString::from(format!("answer:{}:details", self.ask_id)))
                                .mt(px(6.))
                                .max_h(px(150.))
                                .overflow_y_scroll()
                                .text_size(px(12.))
                                .font_family(theme.mono_font_family.clone())
                                .child(details),
                        ),
                )
            })
            .child(actions)
    }

    fn one_of(&self, name: &str, choices: Vec<AskChoice>, picked: Option<&Value>, required: bool, cx: &App) -> Div {
        let theme = cx.theme();
        let mut list = v_flex().mt(px(6.)).gap(px(5.));
        for choice in choices {
            let selected = picked.and_then(Value::as_str) == Some(&choice.value);
            // Picking the picked one again clears a field that can be left out.
            let value = if selected && !required {
                String::new()
            } else {
                choice.value.clone()
            };
            let action = ControlAction::Pick { field: name.to_string(), value, many: false };
            let on_action = self.on_action.clone();
            list = list.child(
                Button::new(SharedString::from(format!("answer:{}:{name}:{}", self.ask_id, choice.value)))
                    .ghost()
                    .w_full()
                    .h_auto()
                    .min_h(px(32.))
                    .px(px(9.))
                    .py(px(6.))
                    .rounded(px(9.))
                    .when(selected, |row| {
                        row.bg(theme.primary.opacity(0.10))
                            .border_1()
                            .border_color(theme.primary.opacity(0.36))
                    })
                    .child(
                        h_flex()
                            .w_full()
                            .justify_between()
                            .gap(px(10.))
                            .child(choice_copy(&choice, theme.muted_foreground))
                            .when(selected, |row| row.child(Icon::new(Glyph::Check).size(px(14.)))),
                    )
                    .on_click(move |_, window, cx| on_action(action.clone(), window, cx)),
            );
        }
        list
    }

    fn many_of(&self, name: &str, choices: Vec<AskChoice>, picked: Option<&Value>, cx: &App) -> Div {
        let theme = cx.theme();
        let mut list = v_flex().mt(px(6.)).gap(px(5.));
        for choice in choices {
            let selected = picked
                .and_then(Value::as_array)
                .is_some_and(|list| list.iter().any(|item| item.as_str() == Some(&choice.value)));
            let action = ControlAction::Pick {
                field: name.to_string(),
                value: choice.value.clone(),
                many: true,
            };
            let on_action = self.on_action.clone();
            list = list.child(
                v_flex()
                    .min_h(px(32.))
                    .px(px(9.))
                    .py(px(6.))
                    .gap(px(2.))
                    .rounded(px(9.))
                    .when(selected, |row| {
                        row.bg(theme.primary.opacity(0.10))
                            .border_1()
                            .border_color(theme.primary.opacity(0.36))
                    })
                    .child(
                        Checkbox::new(SharedString::from(format!("answer:{}:{name}:{}", self.ask_id, choice.value)))
                            .checked(selected)
                            .label(choice.title.clone().unwrap_or(choice.value.clone()))
                            .on_click(move |_, window, cx| on_action(action.clone(), window, cx)),
                    )
                    .when_some(choice.description.clone(), |row, description| {
                        row.child(
                            div()
                                .pl(px(24.))
                                .text_size(px(12.))
                                .text_color(theme.muted_foreground)
                                .child(description),
                        )
                    }),
            );
        }
        list
    }

    fn input(&self, form: &AskForm, cx: &App) -> Div {
        let theme = cx.theme();
        let schema = self.ask.schema.as_ref().and_then(AskSchema::of);
        let (properties, required) =
            schema.map_or_else(Default::default, |schema| (schema.properties, schema.required));
        let mut fields = v_flex().mt(px(13.)).gap(px(10.));
        for (name, field) in properties {
            let needed = required.contains(&name);
            let label = field.title.clone().unwrap_or_else(|| name.clone());
            let picked = form.picked.get(&name);
            let answer = match field.input() {
                AskInput::One(choices) => self.one_of(&name, choices, picked, needed, cx),
                AskInput::Many(choices) => self.many_of(&name, choices, picked, cx),
                AskInput::Flag => {
                    let on = picked.and_then(Value::as_bool).unwrap_or(false);
                    let action = ControlAction::Flag { field: name.clone(), on: !on };
                    let on_action = self.on_action.clone();
                    div().mt(px(7.)).child(
                        Checkbox::new(SharedString::from(format!("answer:{}:{name}", self.ask_id)))
                            .checked(on)
                            .label("Yes")
                            .on_click(move |_, window, cx| on_action(action.clone(), window, cx)),
                    )
                }
                AskInput::Text | AskInput::Secret => match form.texts.iter().find(|(text, _)| *text == name) {
                    Some((_, input)) => {
                        let on_action = self.on_action.clone();
                        div()
                            .mt(px(6.))
                            .capture_key_down(move |event, window, cx| {
                                if event.keystroke.key == "enter"
                                    && !event.keystroke.modifiers.shift
                                    && !event.is_held
                                {
                                    on_action(ControlAction::Submit, window, cx);
                                }
                            })
                            .child(Input::new(input).h(px(30.)).rounded(px(8.)))
                    }
                    None => div(),
                },
            };
            fields = fields.child(
                v_flex()
                    .min_w_0()
                    .child(
                        div()
                            .text_size(px(13.))
                            .font_weight(FontWeight::SEMIBOLD)
                            .child(legend(&label, needed)),
                    )
                    .when_some(field.description.clone(), |column, description| {
                        column.child(
                            div()
                                .mt(px(3.))
                                .text_size(px(12.))
                                .text_color(theme.muted_foreground)
                                .child(description),
                        )
                    })
                    .child(answer),
            );
        }
        let button = |id: &str, label: &str, color: Hsla, action: ControlAction| {
            pill(format!("answer:{}:{id}", self.ask_id), label.to_string(), color, action, &self.on_action)
        };
        let muted = theme.muted_foreground;
        v_flex()
            .child(
                h_flex()
                    .gap(px(8.))
                    .text_color(muted)
                    .child(kicker("INPUT REQUESTED"))
                    .when(self.count > 1, |heading| {
                        heading.child(
                            div()
                                .px(px(6.))
                                .py(px(2.))
                                .rounded(px(6.))
                                .bg(theme.background.opacity(0.8))
                                .text_size(px(12.))
                                .child(format!("1/{}", self.count)),
                        )
                    }),
            )
            .child(
                div()
                    .mt(px(9.))
                    .text_size(px(14.))
                    .font_weight(FontWeight::SEMIBOLD)
                    .child(self.ask.title.clone().unwrap_or_else(|| "Input requested".into())),
            )
            .when_some(self.ask.message.clone(), |panel, message| {
                panel.child(div().mt(px(5.)).text_size(px(13.)).text_color(muted).child(message))
            })
            .child(fields)
            .child(
                h_flex()
                    .justify_end()
                    .gap(px(7.))
                    .mt(px(12.))
                    .child(button("cancel", "Cancel", muted, ControlAction::Answer(json!({ "action": "cancel" }))).outline())
                    .child(button("skip", "Skip", muted, ControlAction::Answer(json!({ "action": "decline" }))).outline())
                    .child(button("submit", "Continue", theme.primary_foreground, ControlAction::Submit).primary()),
            )
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
