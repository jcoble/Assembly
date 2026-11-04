//! The whole-form lifecycle as plain logic: all the values go in one request, and none commit unless it's acknowledged.
use super::field::FieldValue;
use super::state::{EditState, SaveFailure, SaveRequest};
use gpui_kit::SharedString;
use std::rc::Rc;

/// Every field's value, by its key, in the form's order.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct FormValues(pub Vec<(SharedString, FieldValue)>);

impl FormValues {
    pub fn get(&self, key: &str) -> Option<&FieldValue> {
        self.0
            .iter()
            .find(|(name, _)| name.as_ref() == key)
            .map(|(_, value)| value)
    }
}

/// Checks across fields, before anything is sent. Each failure names the field it lands on.
pub type FormRule = Rc<dyn Fn(&FormValues) -> Vec<(SharedString, String)>>;

/// Why a form's save didn't commit. `Refused` lands on the form, `Fields` on the fields it names, and `Uncertain`
/// means the owner can't tell whether it landed, so nothing more is sent until the owner has read what's there.
#[derive(Clone, Debug, PartialEq, Eq)]
pub enum FormFailure {
    Refused(String),
    Fields(Vec<(SharedString, String)>),
    Uncertain(String),
}

/// What asking to save did.
#[derive(Debug)]
pub(super) enum FormSave {
    /// It was already saving, or isn't open.
    Ignored,
    /// Nothing had changed.
    Closed,
    Blocked(Vec<(SharedString, String)>),
    Sent(SaveRequest, FormValues),
}

pub(super) struct FormState {
    edit: EditState<FormValues>,
    rule: Option<FormRule>,
    errors: Vec<(SharedString, String)>,
}

impl FormState {
    pub(super) fn new(values: FormValues) -> Self {
        Self {
            edit: EditState::new(values),
            rule: None,
            errors: Vec::new(),
        }
    }

    pub(super) fn with_rule(mut self, rule: FormRule) -> Self {
        self.rule = Some(rule);
        self
    }

    pub(super) fn committed(&self) -> &FormValues {
        self.edit.committed()
    }

    pub(super) fn draft(&self) -> &FormValues {
        self.edit.draft()
    }

    pub(super) fn editing(&self) -> bool {
        self.edit.editing()
    }

    pub(super) fn pending(&self) -> bool {
        self.edit.pending()
    }

    pub(super) fn uncertain(&self) -> bool {
        self.edit.uncertain()
    }

    pub(super) fn error(&self) -> Option<&str> {
        self.edit.error()
    }

    pub(super) fn errors(&self) -> &[(SharedString, String)] {
        &self.errors
    }

    /// The first field with an error, in the form's order.
    pub(super) fn focus_target(&self) -> Option<&SharedString> {
        self.edit
            .draft()
            .0
            .iter()
            .map(|(key, _)| key)
            .find(|key| self.errors.iter().any(|(name, _)| name == *key))
    }

    pub(super) fn begin(&mut self) -> bool {
        self.errors.clear();
        self.edit.edit()
    }

    /// `drafts` are what the fields hold now and `field_errors` what their own rules found.
    pub(super) fn save(
        &mut self,
        drafts: FormValues,
        mut field_errors: Vec<(SharedString, String)>,
    ) -> FormSave {
        if !self.edit.change(drafts.clone()) {
            return FormSave::Ignored;
        }
        if drafts == *self.edit.committed() {
            self.errors.clear();
            self.edit.cancel();
            return FormSave::Closed;
        }
        if let Some(rule) = &self.rule {
            field_errors.extend(rule(&drafts));
        }
        if !field_errors.is_empty() {
            self.errors = field_errors.clone();
            return FormSave::Blocked(field_errors);
        }
        self.errors.clear();
        match self.edit.save() {
            Some((request, values)) => FormSave::Sent(request, values),
            None => FormSave::Ignored,
        }
    }

    pub(super) fn complete(
        &mut self,
        request: SaveRequest,
        result: Result<FormValues, FormFailure>,
    ) -> bool {
        let result = match result {
            Ok(values) => Ok(values),
            Err(FormFailure::Refused(message)) => Err(SaveFailure::Refused(message)),
            Err(FormFailure::Uncertain(message)) => Err(SaveFailure::Uncertain(message)),
            Err(FormFailure::Fields(errors)) => {
                let applied = self
                    .edit
                    .complete(request, Err(SaveFailure::Refused(String::new())));
                if applied {
                    self.errors = errors;
                }
                return applied;
            }
        };
        self.edit.complete(request, result)
    }

    pub(super) fn cancel(&mut self) -> bool {
        let cancelled = self.edit.cancel();
        if cancelled {
            self.errors.clear();
        }
        cancelled
    }

    pub(super) fn set_committed(&mut self, values: FormValues) {
        self.edit.set_committed(values);
    }
}

#[cfg(test)]
#[path = "form_state_tests.rs"]
mod tests;
