//! A question's form, drawn by the kit's Questionnaire: one question at a time, whichever surface shows it. The kit
//! holds the answers, validation, navigation and keyboard; this maps the agent's schema onto it and turns what comes
//! back into the answer the owner sends. Cancel and Skip answer the whole ask, so they stay here, outside the kit.
//!
//! The kit fixes its schema once built. An ask that changes builds a new one in the same render, so no empty frame
//! shows, and carries over the text inputs themselves, the choices still offered and the question on screen. An ask
//! that changes anything but its schema builds nothing.

use crate::core::ui::access::Control as _;
use crate::core::ui::access::control_ids::ask_control;
use gpui_kit::component::Sizable as _;
use gpui_kit::component::button::{Button, ButtonVariants as _};
use gpui_kit::component::input::InputState;
use gpui_kit::component::questionnaire::*;
use gpui_kit::prelude::FluentBuilder as _;
use gpui_kit::*;
use serde_json::{Map, Value, json};
use std::rc::Rc;

use super::form::AskForm;
use super::payload::{AskChoice, AskField, AskInput, AskPayload, AskSchema};

/// What answering an ask calls: the owner's one answer path.
pub type OnAnswer = Rc<dyn Fn(Value, &mut Window, &mut App)>;

/// A field's heading, marked when it can be left out.
pub fn legend(label: &str, required: bool) -> String {
    if required {
        label.to_string()
    } else {
        format!("{label} (optional)")
    }
}

const FLAG_ON: &str = "yes";

/// A text input and the field its text answers: its own, or a choice's `<name>_custom`.
#[derive(Clone)]
struct TypedField {
    name: String,
    secret: bool,
    input: Entity<InputState>,
}

struct Question {
    name: String,
    label: String,
    required: bool,
    input: AskInput,
    typed: Option<TypedField>,
}

pub struct AskQuestionnaire {
    key: String,
    ask: AskPayload,
    questions: Vec<Question>,
    state: Entity<QuestionnaireState>,
    on_answer: OnAnswer,
    _submitted: Subscription,
}

impl AskQuestionnaire {
    /// The form for an open question with fields; `None` for anything else.
    pub fn of(
        key: String,
        ask: &AskPayload,
        on_answer: OnAnswer,
        window: &mut Window,
        cx: &mut App,
    ) -> Option<Entity<Self>> {
        let fields = ask.schema.as_ref().and_then(AskSchema::of)?;
        Some(cx.new(|cx| {
            let (questions, state) = build(&fields, &[], window, cx);
            let _submitted = submitted(&state, window, cx);
            Self {
                key,
                ask: ask.clone(),
                questions,
                state,
                on_answer,
                _submitted,
            }
        }))
    }

    /// Takes the ask as it now stands. Only a changed schema builds the kit's state again.
    pub fn sync(&mut self, ask: &AskPayload, window: &mut Window, cx: &mut Context<Self>) {
        let same = ask.schema == self.ask.schema;
        let fields = ask.schema.as_ref().and_then(AskSchema::of);
        self.ask = ask.clone();
        let (false, Some(fields)) = (same, fields) else {
            return;
        };
        let (questions, state) = build(&fields, &self.questions, window, cx);
        let (old, current) = {
            let old = self.state.read(cx);
            let answers = (self.questions.iter())
                .filter_map(|question| Some((question.name.clone(), old.answer(&question.name)?)))
                .collect::<Vec<_>>();
            (answers, old.current_item().cloned())
        };
        state.update(cx, |state, cx| {
            for (name, answer) in old {
                let offered = state.item_definition(&name).map(|item| {
                    let held = item.choices().iter().map(|choice| choice.value().clone());
                    held.filter(|value| answer.choices().contains(value))
                        .collect::<Vec<_>>()
                });
                if let Some(choices) = offered.filter(|choices| !choices.is_empty()) {
                    let picked = QuestionnaireAnswer::new().with_choices(choices);
                    let _ = state.set_answer(&name, picked, window, cx);
                }
            }
            if let Some(current) = current {
                let _ = state.set_current_item(&current, window, cx);
            }
        });
        self._submitted = submitted(&state, window, cx);
        self.questions = questions;
        self.state = state;
        cx.notify();
    }

    /// Next or Continue: an empty question that can be left out moves on, as the old form's Continue did, where the
    /// kit alone would ask for it to be answered or skipped.
    fn advance(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        let state = self.state.clone();
        let Some(current) = state.read(cx).current_item().cloned() else {
            return;
        };
        let _ = state.update(cx, |state, cx| state.clear_external_error(&current, cx));
        let Some(question) = self.questions.iter().find(|q| q.name == current.as_ref()) else {
            return;
        };
        // The kit trims what it counts as typed, but spaces are a password: only an empty one is missing.
        if let Some(typed) = question.typed.as_ref().filter(|typed| typed.secret)
            && question.required
            && typed.input.read(cx).value().is_empty()
        {
            let why = format!("{} is required.", question.label);
            let _ = state.update(cx, |state, cx| state.set_external_error(&current, why, cx));
            return;
        }
        state.update(cx, |state, cx| {
            let answered = state.answer(&current).is_some_and(|a| !a.is_empty());
            let required = state.item_state(&current).is_some_and(|i| i.is_required());
            if answered || required {
                state.confirm_current(window, cx);
            } else {
                state.skip_current(window, cx);
            }
        });
    }

    fn finish(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        let (picked, texts) = self.filled(cx);
        match AskForm::answer(&self.ask, &picked, texts) {
            Ok(answer) => (self.on_answer.clone())(answer, window, cx),
            Err(why) => self.state.update(cx, |state, cx| {
                if let Some(current) = state.current_item().cloned() {
                    let _ = state.set_external_error(&current, why, cx);
                }
            }),
        }
    }

    /// What was chosen, by field, and each text exactly as typed.
    fn filled(&self, cx: &App) -> (Map<String, Value>, Vec<(String, String)>) {
        let state = self.state.read(cx);
        let (mut picked, mut texts) = (Map::new(), Vec::new());
        for question in &self.questions {
            let answer = state.answer(&question.name).unwrap_or_default();
            let chosen = answer.choices().iter().map(|choice| choice.to_string());
            match question.input {
                AskInput::Flag => {
                    picked.insert(question.name.clone(), json!(!answer.choices().is_empty()));
                }
                AskInput::Many(_) => {
                    picked.insert(question.name.clone(), json!(chosen.collect::<Vec<_>>()));
                }
                AskInput::One(_) => {
                    if let Some(choice) = chosen.into_iter().next() {
                        picked.insert(question.name.clone(), json!(choice));
                    }
                }
                AskInput::Text | AskInput::Secret => {}
            }
            if let Some(typed) = &question.typed {
                texts.push((typed.name.clone(), typed.input.read(cx).value().to_string()));
            }
        }
        (picked, texts)
    }

    /// Enter in a text field does what Continue does, whether or not the field is filled.
    fn enter(&mut self, event: &KeyDownEvent, window: &mut Window, cx: &mut Context<Self>) {
        let keys = &event.keystroke;
        if keys.key != "enter"
            || keys.modifiers.number_of_modifiers() != 0
            || event.is_held
            || keys.is_ime_in_progress()
            || !self.state.read(cx).is_current_input_focused(window)
        {
            return;
        }
        window.prevent_default();
        self.advance(window, cx);
    }

    fn button(&self, action: &str, label: &'static str) -> Button {
        Button::new(SharedString::from(format!("ask:{}:{action}", self.key)))
            .control(ask_control(&self.key, action), label)
            .small()
            .label(label)
    }

    fn leave(&self, action: &'static str) -> impl Fn(&ClickEvent, &mut Window, &mut App) + 'static {
        let on_answer = self.on_answer.clone();
        move |_, window, cx| on_answer(json!({ "action": action }), window, cx)
    }

    /// An optional single choice can be taken back, as picking it twice used to: the kit's own skip clears it.
    fn can_blank(&self, cx: &App) -> bool {
        let state = self.state.read(cx);
        let Some(current) = state.current_item() else {
            return false;
        };
        (self.questions.iter()).any(|question| {
            question.name == current.as_ref()
                && !question.required
                && matches!(question.input, AskInput::One(_))
        }) && state
            .answer(current)
            .is_some_and(|answer| !answer.choices().is_empty())
    }
}

/// One question's parts; the kit draws only the current one.
fn question_part(state: &Entity<QuestionnaireState>, question: &Question) -> QuestionnaireItem {
    let name = question.name.clone();
    let choices = match &question.input {
        AskInput::One(choices) | AskInput::Many(choices) => unique(choices),
        AskInput::Flag => vec![flag_choice()],
        AskInput::Text | AskInput::Secret => Vec::new(),
    };
    QuestionnaireItem::new(state, name.clone())
        .child(QuestionnaireTitle::new(state, name.clone()))
        .child(QuestionnaireDescription::new(state, name.clone()))
        .when(!choices.is_empty(), |item| {
            item.child(QuestionnaireChoices::new(state, name.clone()).children(
                choices.iter().map(|choice| {
                    QuestionnaireChoice::new(state, name.clone(), choice.value.clone())
                }),
            ))
        })
        .when(question.typed.is_some(), |item| {
            item.child(QuestionnaireInput::new(state, name.clone()))
        })
        .child(QuestionnaireError::new(state, name))
}

impl Render for AskQuestionnaire {
    fn render(&mut self, _: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        let state = self.state.clone();
        let many = state.read(cx).total() > 1;
        let navigation = state.read(cx).navigation_state();
        let on_continue = cx.listener(|form, _: &ClickEvent, window, cx| form.advance(window, cx));
        let on_previous = {
            let state = state.clone();
            move |_: &ClickEvent, window: &mut Window, cx: &mut App| {
                state.update(cx, |state, cx| state.go_previous(window, cx));
            }
        };
        let on_blank = {
            let state = state.clone();
            move |_: &ClickEvent, window: &mut Window, cx: &mut App| {
                state.update(cx, |state, cx| state.skip_current(window, cx));
            }
        };
        let forward = if navigation.is_next_visible() {
            self.button("continue", "Next")
        } else {
            self.button("continue", "Continue")
        };
        div()
            .w_full()
            .capture_key_down(cx.listener(|form, event, window, cx| form.enter(event, window, cx)))
            .child(
                Questionnaire::new(&state)
                    .small()
                    .when(many, |form| form.child(QuestionnaireProgress::new(&state)))
                    .children(
                        (self.questions.iter()).map(|question| question_part(&state, question)),
                    )
                    .child(
                        QuestionnaireActions::new(&state)
                            .flex_wrap()
                            .justify_end()
                            .child(
                                self.button("cancel", "Cancel")
                                    .outline()
                                    .on_click(self.leave("cancel")),
                            )
                            .child(
                                self.button("skip", "Skip")
                                    .outline()
                                    .on_click(self.leave("decline")),
                            )
                            .when(navigation.is_previous_visible(), |actions| {
                                actions.child(
                                    self.button("previous", "Previous")
                                        .outline()
                                        .on_click(on_previous),
                                )
                            })
                            .when(self.can_blank(cx), |actions| {
                                actions.child(
                                    self.button("blank", "Leave blank")
                                        .outline()
                                        .on_click(on_blank),
                                )
                            })
                            .child(forward.primary().on_click(on_continue)),
                    ),
            )
    }
}

fn submitted(
    state: &Entity<QuestionnaireState>,
    window: &mut Window,
    cx: &mut Context<AskQuestionnaire>,
) -> Subscription {
    cx.subscribe_in(state, window, |form, _, event, window, cx| {
        if matches!(event, QuestionnaireEvent::Submit(_)) {
            form.finish(window, cx);
        }
    })
}

/// The kit refuses a schema that repeats a choice; an agent's is not to be trusted that far.
fn unique(choices: &[AskChoice]) -> Vec<AskChoice> {
    let mut seen = Vec::new();
    (choices.iter())
        .filter(|choice| {
            let new = !seen.contains(&&choice.value);
            if new {
                seen.push(&choice.value);
            }
            new
        })
        .cloned()
        .collect()
}

fn flag_choice() -> AskChoice {
    AskChoice {
        value: FLAG_ON.into(),
        title: Some("Yes".into()),
        description: None,
    }
}

fn typed_field(
    name: String,
    secret: bool,
    placeholder: &'static str,
    old: &[Question],
    window: &mut Window,
    cx: &mut App,
) -> TypedField {
    let kept = (old.iter())
        .filter_map(|question| question.typed.as_ref())
        .find(|typed| typed.name == name && typed.secret == secret)
        .map(|typed| typed.input.clone());
    let input = kept.unwrap_or_else(|| {
        cx.new(|cx| {
            InputState::new(window, cx)
                .masked(secret)
                .placeholder(placeholder)
        })
    });
    TypedField {
        name,
        secret,
        input,
    }
}

/// The questions of a schema, and the kit's state for them. `<name>_custom` beside a field with choices is that
/// field's free-text answer, not a question of its own.
fn build(
    fields: &AskSchema,
    old: &[Question],
    window: &mut Window,
    cx: &mut App,
) -> (Vec<Question>, Entity<QuestionnaireState>) {
    let has_choices = |name: &str| {
        (fields.properties.iter()).any(|(field, about)| {
            field == name && matches!(about.input(), AskInput::One(_) | AskInput::Many(_))
        })
    };
    let (mut questions, mut items) = (Vec::new(), Vec::new());
    for (name, field) in &fields.properties {
        let parent = name.strip_suffix("_custom");
        if parent.is_some_and(has_choices) && field.input() == AskInput::Text {
            continue;
        }
        let input = field.input();
        let other = format!("{name}_custom");
        let typed = match input {
            AskInput::Text | AskInput::Secret => {
                let placeholder = if parent.is_some() { "Other" } else { "" };
                let secret = input == AskInput::Secret;
                Some(typed_field(
                    name.clone(),
                    secret,
                    placeholder,
                    old,
                    window,
                    cx,
                ))
            }
            AskInput::One(_) | AskInput::Many(_) => (fields.properties.iter())
                .any(|(field, about)| *field == other && about.input() == AskInput::Text)
                .then(|| typed_field(other, false, "Other", old, window, cx)),
            AskInput::Flag => None,
        };
        let required = fields.required.contains(name);
        let label = field.title.clone().unwrap_or_else(|| name.clone());
        items.push(definition(
            name,
            &label,
            required,
            field,
            &input,
            typed.as_ref(),
        ));
        questions.push(Question {
            name: name.clone(),
            label,
            required,
            input,
            typed,
        });
    }
    let state = cx.new(|cx| {
        QuestionnaireState::new(items, cx).expect("names and choices are unique, one default each")
    });
    (questions, state)
}

fn definition(
    name: &str,
    label: &str,
    required: bool,
    field: &AskField,
    input: &AskInput,
    typed: Option<&TypedField>,
) -> QuestionnaireItemDefinition {
    let heading = legend(label, required);
    let mut item = QuestionnaireItemDefinition::new(name.to_string(), heading.clone());
    if let Some(description) = field.description.clone() {
        item = item.with_description(description);
    }
    if let Some(typed) = typed {
        item = item.with_input(QuestionnaireInputDefinition::new(
            typed.input.clone(),
            heading,
        ));
    }
    match input {
        // A required choice starts on its first, as the old form's did: an answer the box shows is an answer it sends.
        AskInput::One(choices) => {
            item.with_required(required)
                .with_choices(unique(choices).iter().enumerate().map(|(at, choice)| {
                    choice_definition(choice).with_default_selected(required && at == 0)
                }))
        }
        AskInput::Many(choices) => item
            .with_required(required)
            .with_multiple(true)
            .with_choices(unique(choices).iter().map(choice_definition)),
        // A check box always answers, so the kit never needs it filled.
        AskInput::Flag => {
            let on = field.default.as_ref().and_then(Value::as_bool) == Some(true);
            item.with_multiple(true)
                .with_choice(choice_definition(&flag_choice()).with_default_selected(on))
        }
        // The kit counts spaces as nothing, so `advance` checks a password itself.
        AskInput::Secret => item,
        AskInput::Text if field.numeric() => {
            let whole = field.kind.as_deref() == Some("integer");
            let why: SharedString = format!("{label} must be a number.").into();
            item.with_required(required).with_validator(move |answer| {
                let typed = answer.answer().freeform().map_or("", |text| text.trim());
                match AskForm::number(typed, whole) {
                    Some(_) => Ok(()),
                    None => Err(why.clone()),
                }
            })
        }
        AskInput::Text => item.with_required(required),
    }
}

fn choice_definition(choice: &AskChoice) -> QuestionnaireChoiceDefinition {
    let label = choice.title.clone().unwrap_or(choice.value.clone());
    let definition = QuestionnaireChoiceDefinition::new(choice.value.clone(), label);
    match choice.description.clone() {
        Some(description) => definition.with_description(description),
        None => definition,
    }
}

#[cfg(test)]
impl AskQuestionnaire {
    pub fn kit_state(&self) -> Entity<QuestionnaireState> {
        self.state.clone()
    }
}

#[cfg(test)]
#[path = "ask_questionnaire_tests.rs"]
mod tests;
