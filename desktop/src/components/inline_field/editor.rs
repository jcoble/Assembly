use super::field::FieldValue;
use super::kinds::TextShape;
use gpui_kit::component::calendar::Date;
use gpui_kit::component::date_picker::{DatePicker, DatePickerState, DateTime};
use gpui_kit::component::input::{Input, InputState, Textarea, TextareaState};
use gpui_kit::component::searchable_list::SearchableListItem;
use gpui_kit::component::select::{Select, SelectState};
use gpui_kit::component::time_field::{TimeField, TimeFieldState, TimePrecision};
use gpui_kit::component::{Disableable as _, IndexPath, Sizable as _, Size};
use gpui_kit::prelude::FluentBuilder as _;
use gpui_kit::*;

#[derive(Clone, Debug)]
pub struct ChoiceOption {
    pub value: String,
    pub label: SharedString,
}
impl ChoiceOption {
    pub fn new(value: impl Into<String>, label: impl Into<SharedString>) -> Self {
        Self {
            value: value.into(),
            label: label.into(),
        }
    }
}
impl SearchableListItem for ChoiceOption {
    type Value = String;
    fn title(&self) -> SharedString {
        self.label.clone()
    }
    fn value(&self) -> &String {
        &self.value
    }
}
pub(super) enum Editor {
    Text {
        state: Entity<InputState>,
        shape: TextShape,
    },
    Multiline(Entity<TextareaState>),
    Date(Entity<DatePickerState>),
    DateTime(Entity<DatePickerState>),
    Time(Entity<TimeFieldState>),
    Combo {
        state: Entity<SelectState<Vec<ChoiceOption>>>,
        options: Vec<ChoiceOption>,
    },
}
impl Editor {
    pub(super) fn text(shape: TextShape, window: &mut Window, cx: &mut App) -> Self {
        let state = cx.new(|cx| match shape {
            TextShape::Plain => InputState::new(window, cx),
            _ => InputState::new(window, cx).mask_pattern(shape.mask()),
        });
        Self::Text { state, shape }
    }
    pub(super) fn multiline(window: &mut Window, cx: &mut App) -> Self {
        Self::Multiline(cx.new(|cx| {
            TextareaState::new(window, cx)
                .auto_grow(2, 6)
                .submit_on_enter(true)
        }))
    }
    pub(super) fn new(
        value: &FieldValue,
        options: Vec<ChoiceOption>,
        window: &mut Window,
        cx: &mut App,
    ) -> Self {
        match value {
            FieldValue::Text(_) => Self::text(TextShape::Plain, window, cx),
            FieldValue::Date(_) => Self::Date(cx.new(|cx| DatePickerState::new(window, cx))),
            FieldValue::DateTime(_) => Self::DateTime(cx.new(|cx| {
                DatePickerState::new(window, cx).time_precision(TimePrecision::Minute)
            })),
            FieldValue::Time(_) => Self::Time(
                cx.new(|cx| TimeFieldState::new(window, cx).precision(TimePrecision::Second)),
            ),
            FieldValue::Choice(value) => {
                let selected = value
                    .as_ref()
                    .and_then(|v| options.iter().position(|o| &o.value == v))
                    .map(IndexPath::new);
                let state = cx.new(|cx| SelectState::new(options.clone(), selected, window, cx));
                Self::Combo { state, options }
            }
        }
    }
    pub(super) fn accepts(&self, value: &FieldValue) -> bool {
        matches!(
            (self, value),
            (Self::Text { .. } | Self::Multiline(_), FieldValue::Text(_))
                | (Self::Date(_), FieldValue::Date(_))
                | (Self::DateTime(_), FieldValue::DateTime(_))
                | (Self::Time(_), FieldValue::Time(_))
                | (Self::Combo { .. }, FieldValue::Choice(_))
        )
    }
    pub(super) fn sync(&self, value: &FieldValue, window: &mut Window, cx: &mut App) {
        match (self, value) {
            (Self::Text { state, shape }, FieldValue::Text(value)) => {
                let masked = shape.mask().mask(value);
                state.update(cx, |state, cx| state.set_value(masked, window, cx))
            }
            (Self::Multiline(state), FieldValue::Text(value)) => {
                state.update(cx, |state, cx| state.set_value(value.clone(), window, cx))
            }
            (Self::Date(state), FieldValue::Date(value)) => state.update(cx, |state, cx| {
                state.set_date(Date::Single(*value), window, cx)
            }),
            (Self::DateTime(state), FieldValue::DateTime(value)) => {
                state.update(cx, |state, cx| {
                    state.set_date_time(DateTime::Single(*value), window, cx)
                })
            }
            (Self::Time(state), FieldValue::Time(value)) => {
                state.update(cx, |state, cx| state.set_time(*value, window, cx))
            }
            (Self::Combo { state, .. }, FieldValue::Choice(value)) => {
                state.update(cx, |state, cx| match value {
                    Some(value) => state.set_selected_value(value, window, cx),
                    None => state.set_selected_index(None, window, cx),
                })
            }
            _ => {}
        }
    }
    pub(super) fn focus(&self, window: &mut Window, cx: &mut App) {
        match self {
            Self::Text { state, .. } => state.update(cx, |s, cx| s.focus(window, cx)),
            Self::Multiline(state) => state.update(cx, |s, cx| s.focus(window, cx)),
            Self::Date(state) | Self::DateTime(state) => {
                state.read(cx).focus_handle(cx).focus(window, cx)
            }
            Self::Time(state) => state.read(cx).focus_handle(cx).focus(window, cx),
            Self::Combo { state, .. } => state.update(cx, |state, cx| state.focus(window, cx)),
        }
    }
    /// Whether the kit's control opens a popup, so opening the field leaves focus on the field for Enter, until the
    /// user clicks or Tabs into the control.
    pub(super) fn has_popup(&self) -> bool {
        matches!(self, Self::Date(_) | Self::DateTime(_) | Self::Combo { .. })
    }
    pub(super) fn render(&self, disabled: bool, size: Size) -> AnyElement {
        match self {
            Self::Text { state, shape } => Input::new(state)
                .with_size(size)
                .w_full()
                .disabled(disabled)
                .when_some(shape.prefix(), |input, prefix| input.prefix(prefix))
                .when_some(shape.suffix(), |input, suffix| input.suffix(suffix))
                .into_any_element(),
            Self::Multiline(state) => Textarea::new(state)
                .with_size(size)
                .w_full()
                .disabled(disabled)
                .into_any_element(),
            Self::Date(state) => DatePicker::new(state)
                .with_size(size)
                .max_w(px(192.))
                .cleanable(true)
                .disabled(disabled)
                .into_any_element(),
            Self::DateTime(state) => DatePicker::new(state)
                .with_size(size)
                .cleanable(true)
                .disabled(disabled)
                .into_any_element(),
            Self::Time(state) => TimeField::new(state)
                .with_size(size)
                .max_w(px(192.))
                .disabled(disabled)
                .into_any_element(),
            Self::Combo { state, .. } => Select::new(state)
                .with_size(size)
                .w_full()
                .cleanable(true)
                .disabled(disabled)
                .into_any_element(),
        }
    }
    /// What the value reads as when it isn't being edited, or `None` when it's empty.
    pub(super) fn display(&self, value: &FieldValue) -> Option<String> {
        match value {
            FieldValue::Text(value) if value.is_empty() => None,
            FieldValue::Text(value) => Some(match self {
                Self::Text { shape, .. } => shape.shown(value),
                _ => value.clone(),
            }),
            FieldValue::Date(value) => value.map(|date| date.format("%b %-d, %Y").to_string()),
            FieldValue::DateTime(value) => {
                value.map(|date| date.format("%b %-d, %Y %H:%M").to_string())
            }
            FieldValue::Time(value) => Some(value.format("%H:%M:%S").to_string()),
            FieldValue::Choice(value) => match (self, value) {
                (Self::Combo { options, .. }, Some(value)) => Some(
                    options
                        .iter()
                        .find(|o| &o.value == value)
                        .map(|o| o.label.to_string())
                        .unwrap_or_else(|| format!("{value} (unavailable)")),
                ),
                _ => None,
            },
        }
    }
}
