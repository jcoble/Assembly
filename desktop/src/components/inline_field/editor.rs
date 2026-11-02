use super::field::FieldValue;
use gpui_kit::component::calendar::Date;
use gpui_kit::component::date_picker::{DatePicker, DatePickerState};
use gpui_kit::component::input::{Input, InputState};
use gpui_kit::component::searchable_list::SearchableListItem;
use gpui_kit::component::select::{Select, SelectState};
use gpui_kit::component::time_field::{TimeField, TimeFieldState, TimePrecision};
use gpui_kit::component::{Disableable as _, IndexPath, Sizable as _, h_flex};
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
    Text(Entity<InputState>),
    Date(Entity<DatePickerState>),
    Time(Entity<TimeFieldState>),
    Combo {
        state: Entity<SelectState<Vec<ChoiceOption>>>,
        options: Vec<ChoiceOption>,
    },
}
impl Editor {
    pub(super) fn new(
        value: &FieldValue,
        options: Vec<ChoiceOption>,
        window: &mut Window,
        cx: &mut App,
    ) -> Self {
        match value {
            FieldValue::Text(_) => Self::Text(cx.new(|cx| InputState::new(window, cx))),
            FieldValue::Date(_) => Self::Date(cx.new(|cx| DatePickerState::new(window, cx))),
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
            (Self::Text(_), FieldValue::Text(_))
                | (Self::Date(_), FieldValue::Date(_))
                | (Self::Time(_), FieldValue::Time(_))
                | (Self::Combo { .. }, FieldValue::Choice(_))
        )
    }
    pub(super) fn sync(&self, value: &FieldValue, window: &mut Window, cx: &mut App) {
        match (self, value) {
            (Self::Text(state), FieldValue::Text(value)) => {
                state.update(cx, |state, cx| state.set_value(value.clone(), window, cx))
            }
            (Self::Date(state), FieldValue::Date(value)) => state.update(cx, |state, cx| {
                state.set_date(Date::Single(*value), window, cx)
            }),
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
            Self::Text(state) => state.update(cx, |s, cx| s.focus(window, cx)),
            Self::Date(state) => state.read(cx).focus_handle(cx).focus(window, cx),
            Self::Time(state) => state.read(cx).focus_handle(cx).focus(window, cx),
            Self::Combo { state, .. } => state.update(cx, |state, cx| state.focus(window, cx)),
        }
    }
    pub(super) fn render(&self, pending: bool, actions: AnyElement) -> AnyElement {
        match self {
            Self::Text(state) => Input::new(state)
                .small()
                .w_full()
                .disabled(pending)
                .suffix(actions)
                .into_any_element(),
            // These Kit controls have no public suffix slot; keep their own frame and
            // compose the same compact actions immediately at the trailing edge.
            Self::Date(state) => h_flex()
                .w_full()
                .max_w(rems(12.))
                .gap_1()
                .child(
                    DatePicker::new(state)
                        .small()
                        .flex_1()
                        .min_w_0()
                        .cleanable(true)
                        .disabled(pending),
                )
                .child(actions)
                .into_any_element(),
            Self::Time(state) => h_flex()
                .w_full()
                .max_w(rems(12.))
                .gap_1()
                .child(
                    TimeField::new(state)
                        .small()
                        .flex_1()
                        .min_w_0()
                        .disabled(pending),
                )
                .child(actions)
                .into_any_element(),
            Self::Combo { state, .. } => h_flex()
                .w_full()
                .max_w(rems(18.))
                .gap_1()
                .child(
                    Select::new(state)
                        .small()
                        .flex_1()
                        .min_w_0()
                        .cleanable(true)
                        .disabled(pending),
                )
                .child(actions)
                .into_any_element(),
        }
    }
    pub(super) fn display(&self, value: &FieldValue) -> String {
        match value {
            FieldValue::Text(value) => {
                if value.is_empty() {
                    "Not set".into()
                } else {
                    value.clone()
                }
            }
            FieldValue::Date(value) => value
                .map(|date| date.format("%b %-d, %Y").to_string())
                .unwrap_or_else(|| "Not set".into()),
            FieldValue::Time(value) => value.format("%H:%M:%S").to_string(),
            FieldValue::Choice(value) => match (self, value) {
                (Self::Combo { options, .. }, Some(value)) => options
                    .iter()
                    .find(|o| &o.value == value)
                    .map(|o| o.label.to_string())
                    .unwrap_or_else(|| format!("{value} (unavailable)")),
                _ => "Not set".into(),
            },
        }
    }
}
