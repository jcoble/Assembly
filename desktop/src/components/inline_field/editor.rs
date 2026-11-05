use super::field::{FieldValue, InlineField, PasteHandler};
use super::kinds::TextShape;
use crate::activity::{StatusChip, Tone};
use crate::core::ui::access::Named as _;
use gpui_kit::component::button::{Button, ButtonVariants as _};
use gpui_kit::component::calendar::Date;
use gpui_kit::component::date_picker::{DatePicker, DatePickerState, DateTime};
use gpui_kit::component::input::{Input, InputState, Textarea, TextareaState};
use gpui_kit::component::menu::{DropdownMenu as _, PopupMenuItem};
use gpui_kit::component::searchable_list::SearchableListItem;
use gpui_kit::component::select::{Select, SelectState};
use gpui_kit::component::time_field::{TimeField, TimeFieldState, TimePrecision};
use gpui_kit::component::{Disableable as _, IndexPath, Sizable as _, Size};
use gpui_kit::prelude::FluentBuilder as _;
use gpui_kit::*;

/// How a date and time reads everywhere one is shown.
pub const DATE_TIME_SHOWN: &str = "%b %-d, %Y %H:%M";

#[derive(Clone, Debug)]
pub struct ChoiceOption {
    pub value: String,
    pub label: SharedString,
    tone: Tone,
}
impl ChoiceOption {
    pub fn new(value: impl Into<String>, label: impl Into<SharedString>) -> Self {
        Self {
            value: value.into(),
            label: label.into(),
            tone: Tone::Neutral,
        }
    }
    pub fn tone(mut self, tone: Tone) -> Self {
        self.tone = tone;
        self
    }
    /// What a value reads as on a chip. One that's no longer an option, such as a status renamed elsewhere, shows itself.
    pub fn shown(options: &[Self], value: Option<&str>) -> (SharedString, Tone) {
        let Some(value) = value else {
            return ("Empty".into(), Tone::Neutral);
        };
        options
            .iter()
            .find(|option| option.value == value)
            .map(|option| (option.label.clone(), option.tone))
            .unwrap_or_else(|| (value.to_owned().into(), Tone::Neutral))
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
    /// Holds no kit state: a menu on the chip, which has no edit mode and saves on one pick.
    Chip {
        options: Vec<ChoiceOption>,
    },
}

/// The chip as the trigger of its menu. A `Tag` can't be a trigger, and the kit's `Select` can't be opened from code.
fn chip_menu(
    options: &[ChoiceOption],
    value: Option<&str>,
    field: WeakEntity<InlineField>,
) -> AnyElement {
    let (label, tone) = ChoiceOption::shown(options, value);
    let (options, current) = (options.to_vec(), value.map(str::to_owned));
    Button::new("inline-chip")
        .named(label.clone())
        .ghost()
        .xsmall()
        .child(StatusChip::new(label, tone))
        .dropdown_menu(move |menu, _, _| {
            options.iter().fold(menu, |menu, option| {
                let (label, tone, picked) =
                    (option.label.clone(), option.tone, option.value.clone());
                let checked = current.as_deref() == Some(picked.as_str());
                let field = field.clone();
                menu.item(
                    PopupMenuItem::element(move |_, _| StatusChip::new(label.clone(), tone))
                        .checked(checked)
                        .on_click(move |_, window, cx| {
                            let picked = picked.clone();
                            let _ =
                                field.update(cx, |field, cx| field.pick_chip(picked, window, cx));
                        }),
                )
            })
        })
        .into_any_element()
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
            FieldValue::DateTime(_) => {
                Self::DateTime(cx.new(|cx| {
                    DatePickerState::new(window, cx).time_precision(TimePrecision::Minute)
                }))
            }
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
                | (
                    Self::Combo { .. } | Self::Chip { .. },
                    FieldValue::Choice(_)
                )
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
            (Self::DateTime(state), FieldValue::DateTime(value)) => state
                .update(cx, |state, cx| {
                    state.set_date_time(DateTime::Single(*value), window, cx)
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
            Self::Text { state, .. } => state.update(cx, |s, cx| s.focus(window, cx)),
            Self::Multiline(state) => state.update(cx, |s, cx| s.focus(window, cx)),
            Self::Date(state) | Self::DateTime(state) => {
                state.read(cx).focus_handle(cx).focus(window, cx)
            }
            Self::Time(state) => state.read(cx).focus_handle(cx).focus(window, cx),
            Self::Combo { state, .. } => state.update(cx, |state, cx| state.focus(window, cx)),
            Self::Chip { .. } => {}
        }
    }
    /// Whether the kit's control opens a popup, so opening the field leaves focus on the field for Enter, until the
    /// user clicks or Tabs into the control.
    pub(super) fn has_popup(&self) -> bool {
        matches!(
            self,
            Self::Date(_) | Self::DateTime(_) | Self::Combo { .. } | Self::Chip { .. }
        )
    }
    pub(super) fn render(
        &self,
        draft: &FieldValue,
        disabled: bool,
        size: Size,
        field: WeakEntity<InlineField>,
        pastes: Option<PasteHandler>,
    ) -> AnyElement {
        match self {
            Self::Chip { options } => {
                let value = match draft {
                    FieldValue::Choice(value) => value.as_deref(),
                    _ => None,
                };
                if disabled {
                    let (label, tone) = ChoiceOption::shown(options, value);
                    StatusChip::new(label, tone).into_any_element()
                } else {
                    chip_menu(options, value, field)
                }
            }
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
                .when_some(pastes, |area, pastes| {
                    area.on_paste(move |item, window, cx| pastes(item, window, cx))
                })
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
                value.map(|date| date.format(DATE_TIME_SHOWN).to_string())
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
