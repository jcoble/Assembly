//! The field kinds beyond plain text, dates, times and choices, as RentalCommand's forms have them: each is an
//! ordinary `InlineField` with its own mask, display and default rule.
use super::editor::{ChoiceOption, Editor};
use super::field::{FieldValue, InlineField};
use chrono::NaiveDateTime;
use gpui_kit::component::input::MaskPattern;
use gpui_kit::*;
use std::rc::Rc;

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum NumberFormat {
    Currency,
    Percent,
    Integer,
    Decimal,
    Zip,
}

/// How a text value is masked while editing and shown when it isn't. The saved value is always the unmasked one.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(super) enum TextShape {
    Plain,
    Phone,
    Number(NumberFormat),
}

impl TextShape {
    pub(super) fn mask(self) -> MaskPattern {
        let number = |fraction| MaskPattern::Number {
            separator: Some(','),
            fraction,
        };
        match self {
            Self::Plain => MaskPattern::None,
            Self::Phone => MaskPattern::new("999-999-9999"),
            Self::Number(NumberFormat::Currency) => number(Some(2)),
            Self::Number(NumberFormat::Percent) => number(Some(4)),
            Self::Number(NumberFormat::Integer) => number(Some(0)),
            Self::Number(NumberFormat::Decimal) => number(None),
            Self::Number(NumberFormat::Zip) => MaskPattern::new("99999-9999"),
        }
    }

    pub(super) fn prefix(self) -> Option<&'static str> {
        (self == Self::Number(NumberFormat::Currency)).then_some("$")
    }

    pub(super) fn suffix(self) -> Option<&'static str> {
        (self == Self::Number(NumberFormat::Percent)).then_some("%")
    }

    /// The kit's unmask drops a fraction's trailing zeros and can leave its point behind ("100." from "100.00").
    pub(super) fn saved(self, unmasked: &str) -> String {
        match self {
            Self::Plain | Self::Phone => unmasked.to_owned(),
            Self::Number(_) => unmasked.trim_end_matches('.').to_owned(),
        }
    }

    pub(super) fn shown(self, value: &str) -> String {
        match self {
            Self::Plain => value.to_owned(),
            Self::Number(NumberFormat::Currency) => {
                let cents = value.parse::<f64>().map_or_else(|_| value.to_owned(), |v| format!("{v:.2}"));
                format!("${}", self.mask().mask(&cents))
            }
            Self::Number(NumberFormat::Percent) => format!("{}%", self.mask().mask(value)),
            Self::Phone | Self::Number(_) => self.mask().mask(value).to_string(),
        }
    }
}

fn email_rule(value: &FieldValue) -> Result<(), String> {
    let FieldValue::Text(text) = value else {
        return Ok(());
    };
    let text = text.trim();
    let well_formed = text.split_once('@').is_some_and(|(name, domain)| {
        !name.is_empty() && !domain.contains('@') && domain.contains('.') && !domain.starts_with('.') && !domain.ends_with('.')
    });
    if text.is_empty() || (well_formed && !text.contains(char::is_whitespace)) {
        Ok(())
    } else {
        Err("Enter an email address.".into())
    }
}

fn digits_rule(value: &FieldValue, lengths: &[usize], message: &str) -> Result<(), String> {
    let FieldValue::Text(text) = value else {
        return Ok(());
    };
    if text.is_empty() || lengths.contains(&text.len()) {
        Ok(())
    } else {
        Err(message.to_owned())
    }
}

impl InlineField {
    /// A status shown as its chip. One click opens the menu and one pick saves. In a form, the pick changes the draft.
    pub fn chip(
        title: impl Into<SharedString>,
        options: Vec<ChoiceOption>,
        value: Option<String>,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) -> Self {
        Self::with_editor(title.into(), FieldValue::Choice(value), Editor::Chip { options }, window, cx)
    }

    /// Enter saves and Shift-Enter adds a line, as in the answer box.
    pub fn multiline(
        title: impl Into<SharedString>,
        value: impl Into<String>,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) -> Self {
        let editor = Editor::multiline(window, cx);
        Self::with_editor(title.into(), FieldValue::Text(value.into()), editor, window, cx)
    }

    pub fn email(
        title: impl Into<SharedString>,
        value: impl Into<String>,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) -> Self {
        Self::text(title, value, window, cx).rule(Rc::new(email_rule))
    }

    /// Saves the digits and shows them as 555-123-4567.
    pub fn phone(
        title: impl Into<SharedString>,
        value: impl Into<String>,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) -> Self {
        let editor = Editor::text(TextShape::Phone, window, cx);
        Self::with_editor(title.into(), FieldValue::Text(value.into()), editor, window, cx)
            .rule(Rc::new(|value| digits_rule(value, &[10], "Enter a 10-digit phone number.")))
    }

    /// Saves the plain number and shows it in `format`.
    pub fn number(
        title: impl Into<SharedString>,
        value: impl Into<String>,
        format: NumberFormat,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) -> Self {
        let editor = Editor::text(TextShape::Number(format), window, cx);
        let field = Self::with_editor(title.into(), FieldValue::Text(value.into()), editor, window, cx);
        if format == NumberFormat::Zip {
            return field.rule(Rc::new(|value| digits_rule(value, &[5, 9], "Enter a 5- or 9-digit ZIP code.")));
        }
        field
    }

    /// One picker for the date and the time. It saves on Enter or on leaving, since it reports every edit.
    pub fn date_time(
        title: impl Into<SharedString>,
        value: Option<NaiveDateTime>,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) -> Self {
        let value = FieldValue::DateTime(value);
        let editor = Editor::new(&value, Vec::new(), window, cx);
        Self::with_editor(title.into(), value, editor, window, cx)
    }
}

#[cfg(test)]
#[path = "kinds_tests.rs"]
mod tests;
