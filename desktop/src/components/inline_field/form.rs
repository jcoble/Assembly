//! One Save for a whole form: nothing is sent while anything is invalid, every value goes in one request, and nothing
//! commits until the owner acknowledges it. Its fields are built with `InlineField::in_form`.
use super::field::{CancelEdit, FieldValue, InlineField};
use super::form_state::{FormFailure, FormRule, FormSave, FormState, FormValues};
use super::state::SaveRequest;
use crate::core::theme::text::{TextRole, TextRoleExt as _};
use gpui_kit::component::button::{Button, ButtonVariants as _};
use gpui_kit::component::dialog::Cancel;
use gpui_kit::component::input::Escape;
use gpui_kit::component::label::Label;
use gpui_kit::component::{ActiveTheme as _, Disableable as _, Sizable as _, h_flex, v_flex};
use gpui_kit::prelude::FluentBuilder as _;
use gpui_kit::*;

/// The host persists. No value commits until `complete_save` acknowledges this token.
#[derive(Clone, Debug)]
pub struct FormSaveRequested {
    pub request: SaveRequest,
    pub values: FormValues,
}

pub struct InlineForm {
    fields: Vec<(SharedString, Entity<InlineField>)>,
    state: FormState,
}

impl EventEmitter<FormSaveRequested> for InlineForm {}

impl InlineForm {
    pub fn new(fields: Vec<(SharedString, Entity<InlineField>)>, cx: &mut Context<Self>) -> Self {
        let values = FormValues(
            fields
                .iter()
                .map(|(key, field)| (key.clone(), field.read(cx).committed().clone()))
                .collect(),
        );
        Self {
            fields,
            state: FormState::new(values),
        }
    }

    pub fn rule(mut self, rule: FormRule) -> Self {
        self.state = self.state.with_rule(rule);
        self
    }

    pub fn editing(&self) -> bool {
        self.state.editing()
    }

    pub fn committed(&self) -> &FormValues {
        self.state.committed()
    }

    pub fn begin(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        if self.state.begin() {
            for (_, field) in &self.fields {
                field.update(cx, |field, cx| field.begin_edit(window, cx));
            }
            if let Some((_, first)) = self.fields.first() {
                first.update(cx, |field, cx| field.focus_editor(window, cx));
            }
            cx.notify();
        }
    }

    pub fn save(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        let drafts = FormValues(
            self.fields
                .iter()
                .map(|(key, field)| (key.clone(), field.read(cx).draft_value()))
                .collect(),
        );
        let field_errors = self
            .fields
            .iter()
            .filter_map(|(key, field)| {
                field.read(cx).validate().err().map(|message| (key.clone(), message))
            })
            .collect();
        match self.state.save(drafts, field_errors) {
            FormSave::Ignored => {}
            FormSave::Closed => self.restore_fields(window, cx),
            FormSave::Blocked(_) => self.show_errors(window, cx),
            FormSave::Sent(request, values) => {
                self.show_errors(window, cx);
                self.set_saving(true, cx);
                cx.emit(FormSaveRequested { request, values });
            }
        }
        cx.notify();
    }

    pub fn cancel(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        if self.state.cancel() {
            self.set_saving(false, cx);
            self.restore_fields(window, cx);
            cx.notify();
        }
    }

    /// Acknowledges only the current request. A failure leaves every draft open, on its reason.
    pub fn complete_save(
        &mut self,
        request: SaveRequest,
        result: Result<FormValues, FormFailure>,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) -> bool {
        if !self.state.complete(request, result) {
            return false;
        }
        if self.state.editing() {
            self.set_saving(self.state.uncertain(), cx);
            self.show_errors(window, cx);
        } else {
            self.commit_fields(window, cx);
        }
        cx.notify();
        true
    }

    /// A new authoritative value. Open drafts stay, unless the owner was finding out whether this save landed and it did.
    pub fn set_committed(&mut self, values: FormValues, window: &mut Window, cx: &mut Context<Self>) {
        self.state.set_committed(values);
        if self.state.editing() {
            let committed = self.state.committed().clone();
            for (key, field) in &self.fields {
                if let Some(value) = committed.get(key) {
                    field.update(cx, |field, cx| {
                        let _ = field.set_committed(value.clone(), window, cx);
                        field.set_saving(false, cx);
                    });
                }
            }
        } else {
            self.commit_fields(window, cx);
        }
        cx.notify();
    }

    fn commit_fields(&self, window: &mut Window, cx: &mut Context<Self>) {
        let committed = self.state.committed().clone();
        for (key, field) in &self.fields {
            let value: Option<&FieldValue> = committed.get(key);
            if let Some(value) = value {
                field.update(cx, |field, cx| field.commit(value.clone(), window, cx));
            }
        }
    }

    fn restore_fields(&self, window: &mut Window, cx: &mut Context<Self>) {
        for (_, field) in &self.fields {
            field.update(cx, |field, cx| field.restore(window, cx));
        }
    }

    fn set_saving(&self, saving: bool, cx: &mut Context<Self>) {
        for (_, field) in &self.fields {
            field.update(cx, |field, cx| field.set_saving(saving, cx));
        }
    }

    /// Every field shows only its current error, and focus goes to the first of them.
    fn show_errors(&self, window: &mut Window, cx: &mut Context<Self>) {
        for (key, field) in &self.fields {
            let message = self
                .state
                .errors()
                .iter()
                .find(|(name, _)| name == key)
                .map(|(_, message)| message.clone());
            field.update(cx, |field, cx| field.show_error(message, cx));
        }
        let target = self.state.focus_target();
        if let Some((_, field)) = self.fields.iter().find(|(name, _)| Some(name) == target) {
            field.update(cx, |field, cx| field.focus_editor(window, cx));
        }
    }

    fn escape(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        if self.state.editing() {
            self.cancel(window, cx);
        } else {
            cx.propagate();
        }
    }
}

impl Render for InlineForm {
    fn render(&mut self, _: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        let pending = self.state.pending();
        let actions = if self.state.editing() {
            h_flex()
                .gap_2()
                .items_center()
                .child(
                    Button::new("form-save")
                        .small()
                        .primary()
                        .label("Save")
                        .disabled(pending || self.state.uncertain())
                        .on_click(cx.listener(|form, _, window, cx| form.save(window, cx))),
                )
                .child(
                    Button::new("form-cancel")
                        .small()
                        .ghost()
                        .label("Cancel")
                        .disabled(pending)
                        .on_click(cx.listener(|form, _, window, cx| form.cancel(window, cx))),
                )
                .when(pending, |row| {
                    row.child(
                        Label::new("Saving…")
                            .text_role(TextRole::Meta, cx)
                            .text_color(cx.theme().muted_foreground),
                    )
                })
        } else {
            h_flex().child(
                Button::new("form-edit")
                    .small()
                    .label("Edit")
                    .on_click(cx.listener(|form, _, window, cx| form.begin(window, cx))),
            )
        };
        v_flex()
            .w_full()
            .gap_3()
            .on_action(cx.listener(|form, _: &CancelEdit, window, cx| form.escape(window, cx)))
            .on_action(cx.listener(|form, _: &Escape, window, cx| form.escape(window, cx)))
            .on_action(cx.listener(|form, _: &Cancel, window, cx| form.escape(window, cx)))
            .children(self.fields.iter().map(|(_, field)| field.clone()))
            .when_some(self.state.error().filter(|error| !error.is_empty()), |form, error| {
                form.child(
                    Label::new(error.to_owned())
                        .text_role(TextRole::Meta, cx)
                        .text_color(cx.theme().danger),
                )
            })
            .child(actions)
    }
}

#[cfg(test)]
#[path = "form_tests.rs"]
mod tests;
