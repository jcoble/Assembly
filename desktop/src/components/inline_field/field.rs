use super::editor::{ChoiceOption, Editor};
use super::state::{EditState, SaveRequest};
use chrono::{NaiveDate, NaiveTime};
use gpui_kit::assets::IconName;
use gpui_kit::component::button::{Button, ButtonVariants as _};
use gpui_kit::component::date_picker::DatePickerEvent;
use gpui_kit::component::dialog::Cancel;
use gpui_kit::component::form::{Field, Form};
use gpui_kit::component::input::{Escape, InputEvent};
use gpui_kit::component::label::Label;
use gpui_kit::component::select::SelectEvent;
use gpui_kit::component::time_field::TimeFieldEvent;
use gpui_kit::component::{
    ActiveTheme as _, Disableable as _, Sizable as _, Size, StyleSized as _, h_flex, v_flex,
};
use gpui_kit::prelude::FluentBuilder as _;
use gpui_kit::*;

actions!(inline_field, [SaveEdit, CancelEdit]);
/// Register once with the application's other component initialization.
pub fn init(cx: &mut App) {
    cx.bind_keys([
        KeyBinding::new("enter", SaveEdit, Some("InlineField")),
        KeyBinding::new(
            if cfg!(target_os = "macos") {
                "cmd-enter"
            } else {
                "ctrl-enter"
            },
            SaveEdit,
            Some("InlineField"),
        ),
        KeyBinding::new("escape", CancelEdit, Some("InlineField")),
    ]);
}
#[derive(Clone, Debug, PartialEq, Eq)]
pub enum FieldValue {
    Text(String),
    Date(Option<NaiveDate>),
    Time(NaiveTime),
    Choice(Option<String>),
}
/// The host owns validation/persistence. No value commits until `complete_save` acknowledges this token.
#[derive(Clone, Debug)]
pub struct SaveRequested {
    pub request: SaveRequest,
    pub value: FieldValue,
}

pub struct InlineField {
    title: SharedString,
    state: EditState<FieldValue>,
    editor: Editor,
    focus: FocusHandle,
    _heard: Vec<Subscription>,
}
impl EventEmitter<SaveRequested> for InlineField {}
impl InlineField {
    pub fn text(
        title: impl Into<SharedString>,
        value: impl Into<String>,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) -> Self {
        Self::new(
            title.into(),
            FieldValue::Text(value.into()),
            Vec::new(),
            window,
            cx,
        )
    }
    pub fn date(
        title: impl Into<SharedString>,
        value: Option<NaiveDate>,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) -> Self {
        Self::new(
            title.into(),
            FieldValue::Date(value),
            Vec::new(),
            window,
            cx,
        )
    }
    pub fn time(
        title: impl Into<SharedString>,
        value: NaiveTime,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) -> Self {
        Self::new(
            title.into(),
            FieldValue::Time(value),
            Vec::new(),
            window,
            cx,
        )
    }
    pub fn combo(
        title: impl Into<SharedString>,
        options: Vec<ChoiceOption>,
        value: Option<String>,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) -> Self {
        Self::new(title.into(), FieldValue::Choice(value), options, window, cx)
    }
    fn new(
        title: SharedString,
        value: FieldValue,
        options: Vec<ChoiceOption>,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) -> Self {
        let editor = Editor::new(&value, options, window, cx);
        editor.sync(&value, window, cx);
        let heard = match &editor {
            Editor::Text(input) => vec![cx.subscribe_in(
                input,
                window,
                |field, input, event, window, cx| match event {
                    InputEvent::Change => {
                        field
                            .state
                            .change(FieldValue::Text(input.read(cx).value().to_string()));
                        cx.notify();
                    }
                    InputEvent::PressEnter { .. } => field.save(window, cx),
                    _ => {}
                },
            )],
            Editor::Date(input) => {
                vec![
                    cx.subscribe_in(input, window, |field, _, event, window, cx| {
                        let DatePickerEvent::Change(value) = event;
                        if field
                            .state
                            .change(FieldValue::Date(value.start().map(|v| v.date())))
                        {
                            // The Kit trigger consumes Enter to reopen its popup. Return focus
                            // to the inline owner after choosing so the next Enter saves.
                            field.focus.focus(window, cx);
                        }
                        cx.notify();
                    }),
                ]
            }
            Editor::Time(input) => {
                vec![cx.subscribe_in(input, window, |field, _, event, _, cx| {
                    let TimeFieldEvent::Change(value) = event;
                    field.state.change(FieldValue::Time(*value));
                    cx.notify();
                })]
            }
            Editor::Combo { state, .. } => {
                vec![
                    cx.subscribe_in(state, window, |field, _, event, window, cx| {
                        let SelectEvent::Confirm(value) = event;
                        if field.state.change(FieldValue::Choice(value.clone())) {
                            // Selection stays a draft; the next Enter confirms the inline edit.
                            field.focus.focus(window, cx);
                        }
                        cx.notify();
                    }),
                ]
            }
        };
        Self {
            title,
            state: EditState::new(value),
            editor,
            focus: cx.focus_handle().tab_stop(true),
            _heard: heard,
        }
    }
    pub fn committed(&self) -> &FieldValue {
        self.state.committed()
    }
    pub fn draft(&self) -> &FieldValue {
        self.state.draft()
    }
    pub fn pending(&self) -> bool {
        self.state.pending()
    }
    /// Sets an authoritative external value. An open draft stays intact; older save receipts become stale.
    pub fn set_committed(
        &mut self,
        value: FieldValue,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) -> Result<(), &'static str> {
        if !self.editor.accepts(&value) {
            return Err("The value does not match this editor's type.");
        }
        self.state.set_committed(value);
        self.editor.sync(self.state.draft(), window, cx);
        cx.notify();
        Ok(())
    }
    /// Acknowledges only the current request. Failure leaves the editor and draft open with the supplied reason.
    pub fn complete_save(
        &mut self,
        request: SaveRequest,
        result: Result<FieldValue, String>,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) -> bool {
        let result = result.and_then(|value| {
            if self.editor.accepts(&value) {
                Ok(value)
            } else {
                Err("The saved value does not match this editor's type.".into())
            }
        });
        let applied = self.state.complete(request, result);
        if applied {
            if !self.state.editing() {
                self.editor.sync(self.state.draft(), window, cx);
                self.focus.focus(window, cx);
            }
            cx.notify();
        }
        applied
    }
    fn edit(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        if self.state.edit() {
            self.editor.sync(self.state.draft(), window, cx);
            match &self.editor {
                // Keep Enter as inline confirmation until the user clicks or Tabs
                // into the native popup trigger, whose Enter opens its menu.
                Editor::Date(_) | Editor::Combo { .. } => self.focus.focus(window, cx),
                _ => self.editor.focus(window, cx),
            }
            cx.notify();
        }
    }
    fn cancel(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        if self.state.cancel() {
            self.editor.sync(self.state.draft(), window, cx);
            self.focus.focus(window, cx);
            cx.notify();
        }
    }
    fn save(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        if let Some((request, value)) = self.state.save() {
            cx.emit(SaveRequested { request, value });
        }
        if !self.state.editing() {
            self.focus.focus(window, cx);
        }
        cx.notify();
    }
    fn cancel_action(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        if self.state.editing() {
            self.cancel(window, cx);
            cx.stop_propagation();
        } else {
            cx.propagate();
        }
    }
}
impl Render for InlineField {
    fn render(&mut self, _: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        let content = if self.state.editing() {
            let pending = self.state.pending();
            let actions = h_flex()
                .gap_1()
                .child(
                    Button::new("inline-save")
                        .xsmall()
                        .ghost()
                        .icon(IconName::Check)
                        .accessibility_label(if pending { "Saving" } else { "Save" })
                        .tooltip(if pending { "Saving…" } else { "Save (Enter)" })
                        .disabled(pending)
                        .on_click(cx.listener(|f, _, w, cx| {
                            f.save(w, cx);
                            cx.stop_propagation();
                        })),
                )
                .child(
                    Button::new("inline-cancel")
                        .xsmall()
                        .ghost()
                        .icon(IconName::Close)
                        .accessibility_label("Cancel changes")
                        .tooltip("Cancel changes (Escape)")
                        .disabled(pending)
                        .on_click(cx.listener(|f, _, w, cx| {
                            f.cancel(w, cx);
                            cx.stop_propagation();
                        })),
                )
                .into_any_element();
            self.editor.render(pending, actions)
        } else {
            Button::new("inline-display")
                .small()
                .ghost()
                .w_full()
                .justify_start()
                .input_px(Size::Small)
                .input_h(Size::Small)
                .input_text_size(Size::Small)
                .accessibility_label(format!("Edit {}", self.title))
                .child(
                    Label::new(self.editor.display(self.state.committed()))
                        .w_full()
                        .min_w_0()
                        .text_left(),
                )
                .on_click(cx.listener(|f, _, w, cx| f.edit(w, cx)))
                .into_any_element()
        };
        let value = v_flex()
            .w_full()
            .max_w(px(288.))
            .min_w_0()
            .gap_1()
            .child(content)
            .when_some(self.state.error(), |field, error| {
                field.child(Label::new(error.to_owned()).text_color(cx.theme().danger))
            });
        v_flex()
            .w_full()
            .key_context("InlineField")
            .track_focus(&self.focus)
            .on_action(cx.listener(|f, _: &SaveEdit, w, cx| {
                if f.state.editing() {
                    f.save(w, cx);
                } else {
                    f.edit(w, cx);
                }
                cx.stop_propagation();
            }))
            .on_action(cx.listener(|f, _: &CancelEdit, w, cx| f.cancel_action(w, cx)))
            .on_action(cx.listener(|f, _: &Escape, w, cx| f.cancel_action(w, cx)))
            .on_action(cx.listener(|f, _: &Cancel, w, cx| f.cancel_action(w, cx)))
            .child(
                Form::vertical()
                    .w_full()
                    .small()
                    .child(Field::new().label(self.title.clone()).child(value)),
            )
    }
}
