use super::editor::{ChoiceOption, Editor};
use super::state::{EditState, SaveFailure, SaveRequest};
use crate::core::theme::text::{TextRole, TextRoleExt as _, role_size};
use chrono::{NaiveDate, NaiveDateTime, NaiveTime};
use gpui_kit::base::actions::Confirm;
use gpui_kit::component::button::{Button, ButtonVariants as _};
use gpui_kit::component::date_picker::DatePickerEvent;
use gpui_kit::component::dialog::Cancel;
use gpui_kit::component::form::{Field, Form};
use gpui_kit::component::input::{Escape, InputEvent};
use gpui_kit::component::label::Label;
use gpui_kit::component::select::SelectEvent;
use gpui_kit::component::time_field::TimeFieldEvent;
use gpui_kit::component::{ActiveTheme as _, Sizable as _, Size, StyleSized as _, v_flex};
use gpui_kit::prelude::FluentBuilder as _;
use gpui_kit::*;
use std::rc::Rc;

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
    DateTime(Option<NaiveDateTime>),
    Time(NaiveTime),
    Choice(Option<String>),
}
/// The host persists. No value commits until `complete_save` acknowledges this token.
#[derive(Clone, Debug)]
pub struct SaveRequested {
    pub request: SaveRequest,
    pub value: FieldValue,
}
/// Checks a value before it's sent. A failure keeps the editor open with this message, and nothing is sent.
pub type FieldRule = Rc<dyn Fn(&FieldValue) -> Result<(), String>>;

/// `Single` saves itself right away. `Form` holds its draft for the `InlineForm` it's in.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum SaveMode {
    Single,
    Form,
}

/// Saves as Notion does: Enter or leaving saves, a picked choice or date saves, Escape restores. Leaving is focus
/// moving out of the field, so a field sits in a surface that tracks focus and a click on its blank space counts.
pub struct InlineField {
    title: SharedString,
    state: EditState<FieldValue>,
    editor: Editor,
    focus: FocusHandle,
    rules: Vec<FieldRule>,
    mode: SaveMode,
    read_only: bool,
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
    /// Adds a check, on top of any the kind has. The first to fail gives the message.
    pub fn rule(mut self, rule: FieldRule) -> Self {
        self.rules.push(rule);
        self
    }
    pub fn read_only(mut self) -> Self {
        self.read_only = true;
        self
    }
    /// Hands saving to the `InlineForm` this field is in.
    pub fn in_form(mut self) -> Self {
        self.mode = SaveMode::Form;
        self
    }
    fn new(
        title: SharedString,
        value: FieldValue,
        options: Vec<ChoiceOption>,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) -> Self {
        let editor = Editor::new(&value, options, window, cx);
        Self::with_editor(title, value, editor, window, cx)
    }
    pub(super) fn with_editor(
        title: SharedString,
        value: FieldValue,
        editor: Editor,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) -> Self {
        editor.sync(&value, window, cx);
        let focus = cx.focus_handle().tab_stop(true);
        let left = cx.on_focus_out(&focus, window, |field, _, window, cx| field.left(window, cx));
        Self {
            title,
            state: EditState::new(value),
            focus,
            rules: Vec::new(),
            mode: SaveMode::Single,
            read_only: false,
            _heard: vec![left, Self::hear(&editor, window, cx)],
            editor,
        }
    }
    fn hear(editor: &Editor, window: &mut Window, cx: &mut Context<Self>) -> Subscription {
        match editor {
            Editor::Text { state, shape } => {
                let shape = *shape;
                cx.subscribe_in(state, window, move |field, input, event, window, cx| {
                    match event {
                        InputEvent::Change => {
                            let value = shape.saved(input.read(cx).unmask_value().as_ref());
                            field.changed(FieldValue::Text(value), window, cx);
                        }
                        InputEvent::PressEnter { .. } => field.save_if_single(window, cx),
                        _ => {}
                    }
                })
            }
            Editor::Multiline(state) => {
                cx.subscribe_in(state, window, |field, input, event, window, cx| match event {
                    InputEvent::Change => {
                        let value = input.read(cx).value().to_string();
                        field.changed(FieldValue::Text(value), window, cx);
                    }
                    // Shift-Enter has already added its line.
                    InputEvent::PressEnter { shift: false, .. } => {
                        field.save_if_single(window, cx)
                    }
                    _ => {}
                })
            }
            Editor::Date(state) => {
                cx.subscribe_in(state, window, |field, _, event, window, cx| {
                    let DatePickerEvent::Change(value) = event;
                    field.changed(FieldValue::Date(value.start().map(|v| v.date())), window, cx);
                    field.save_if_single(window, cx);
                })
            }
            Editor::DateTime(state) => {
                cx.subscribe_in(state, window, |field, _, event, window, cx| {
                    let DatePickerEvent::Change(value) = event;
                    field.changed(FieldValue::DateTime(value.start()), window, cx);
                })
            }
            Editor::Time(state) => cx.subscribe_in(state, window, |field, _, event, window, cx| {
                let TimeFieldEvent::Change(value) = event;
                field.changed(FieldValue::Time(*value), window, cx);
            }),
            Editor::Combo { state, .. } => {
                cx.subscribe_in(state, window, |field, _, event, window, cx| {
                    let SelectEvent::Confirm(value) = event;
                    field.changed(FieldValue::Choice(value.clone()), window, cx);
                    field.save_if_single(window, cx);
                })
            }
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
        let was_editing = self.state.editing();
        self.state.set_committed(value);
        // An open editor keeps its text, and its caret, under a pushed value.
        if !self.state.editing() {
            self.closed(was_editing, window, cx);
        }
        cx.notify();
        Ok(())
    }
    /// Acknowledges only the current request. A failure leaves the editor and draft open with its reason.
    pub fn complete_save(
        &mut self,
        request: SaveRequest,
        result: Result<FieldValue, SaveFailure>,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) -> bool {
        let result = result.and_then(|value| {
            if self.editor.accepts(&value) {
                Ok(value)
            } else {
                Err(SaveFailure::Refused(
                    "The saved value does not match this editor's type.".into(),
                ))
            }
        });
        let applied = self.state.complete(request, result);
        if applied {
            if !self.state.editing() {
                self.closed(true, window, cx);
            }
            cx.notify();
        }
        applied
    }
    /// The editor is going away. Focus that was in it stays on the field; focus that has moved on isn't pulled back.
    fn closed(&mut self, was_editing: bool, window: &mut Window, cx: &mut Context<Self>) {
        self.editor.sync(self.state.draft(), window, cx);
        if was_editing && self.focus.contains_focused(window, cx) {
            self.focus.focus(window, cx);
        }
    }
    fn edit(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        if self.read_only || self.mode == SaveMode::Form {
            return;
        }
        if self.state.edit() {
            self.editor.sync(self.state.draft(), window, cx);
            if self.editor.has_popup() {
                self.focus.focus(window, cx);
            } else {
                self.editor.focus(window, cx);
            }
            cx.notify();
        }
    }
    fn changed(&mut self, value: FieldValue, window: &mut Window, cx: &mut Context<Self>) {
        // A pick while saving is refused, and the control goes back to what's being saved.
        if !self.state.change(value) && self.state.editing() {
            self.editor.sync(self.state.draft(), window, cx);
        }
        cx.notify();
    }
    fn save_if_single(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        if self.mode == SaveMode::Single {
            self.save(window, cx);
        }
    }
    /// Sends the draft if it passes its rules. An unchanged draft just closes.
    fn save(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        if !self.state.editing() || self.state.pending() {
            return;
        }
        if self.state.draft() != self.state.committed()
            && let Err(message) = self.check()
        {
            self.state.reject(message);
            cx.notify();
            return;
        }
        if let Some((request, value)) = self.state.save() {
            // Saving is what closes the date and time popup: the value in it is the one the user wanted.
            if matches!(self.editor, Editor::DateTime(_)) {
                self.editor.sync(&value, window, cx);
            }
            cx.emit(SaveRequested { request, value });
        } else if !self.state.editing() {
            self.closed(true, window, cx);
        }
        cx.notify();
    }
    fn check(&self) -> Result<(), String> {
        self.rules
            .iter()
            .try_for_each(|rule| rule(self.state.draft()))
    }
    fn left(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        self.save_if_single(window, cx);
    }
    fn save_action(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        if self.read_only || self.mode == SaveMode::Form {
            cx.propagate();
        } else if self.state.editing() {
            self.save(window, cx);
        } else {
            self.edit(window, cx);
        }
    }
    fn cancel_action(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        if self.mode == SaveMode::Single && self.state.editing() {
            if self.state.cancel() {
                self.closed(true, window, cx);
                cx.notify();
            }
        } else {
            cx.propagate();
        }
    }
    /// The date and time picker takes Enter to open and close its popup. Once its value has changed, Enter saves.
    fn confirm_action(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        if self.mode == SaveMode::Single && self.state.draft() != self.state.committed() {
            self.save(window, cx);
            cx.stop_propagation();
        }
    }
    fn display_label(&self, cx: &App) -> Label {
        match self.editor.display(self.state.committed()) {
            Some(text) => Label::new(text),
            None => Label::new("Empty").text_color(cx.theme().muted_foreground),
        }
        .text_role(TextRole::Body, cx)
        .w_full()
        .min_w_0()
        .text_left()
    }
}
impl Render for InlineField {
    fn render(&mut self, _: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        let content = if self.state.editing() {
            let disabled = self.state.pending() || self.state.uncertain();
            self.editor
                .render(disabled, role_size(TextRole::Body, cx))
                .into_any_element()
        } else if self.read_only || self.mode == SaveMode::Form {
            self.display_label(cx).into_any_element()
        } else {
            Button::new("inline-display")
                .small()
                .ghost()
                .w_full()
                .justify_start()
                .input_px(Size::Small)
                .input_h(Size::Small)
                .input_text_size(role_size(TextRole::Body, cx))
                .accessibility_label(format!("Edit {}", self.title))
                .child(self.display_label(cx))
                .on_click(cx.listener(|f, _, w, cx| f.edit(w, cx)))
                .into_any_element()
        };
        let value = v_flex()
            .w_full()
            .max_w(px(288.))
            .min_w_0()
            .gap_1()
            .child(content)
            .when(self.state.pending(), |field| {
                field.child(
                    Label::new("Saving…")
                        .text_role(TextRole::Meta, cx)
                        .text_color(cx.theme().muted_foreground),
                )
            })
            .when_some(self.state.error(), |field, error| {
                field.child(
                    Label::new(error.to_owned())
                        .text_role(TextRole::Meta, cx)
                        .text_color(cx.theme().danger),
                )
            });
        v_flex()
            .w_full()
            .key_context("InlineField")
            .track_focus(&self.focus)
            .when(matches!(self.editor, Editor::DateTime(_)), |root| {
                root.capture_action(cx.listener(|f, _: &Confirm, w, cx| f.confirm_action(w, cx)))
            })
            .on_action(cx.listener(|f, _: &SaveEdit, w, cx| f.save_action(w, cx)))
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
#[cfg(test)]
#[path = "field_tests.rs"]
mod tests;
