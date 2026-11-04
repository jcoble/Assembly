//! What the field does when it's used: which gestures save, and how many times.
use std::cell::RefCell;
use std::rc::Rc;

use chrono::{NaiveDate, NaiveTime};
use gpui::TestAppContext;
use gpui_kit::component::date_picker::{DatePickerEvent, DateTime};
use gpui_kit::component::select::SelectEvent;
use gpui_kit::component::time_field::TimeFieldEvent;
use gpui_kit::{
    AppContext as _, Context, Entity, FocusHandle, Focusable as _, InteractiveElement as _,
    IntoElement, Modifiers, ParentElement as _, Render, Styled as _, Subscription,
    VisualTestContext, Window, div, px,
};

use super::super::editor::{ChoiceOption, Editor};
use super::super::kinds::NumberFormat;
use super::super::state::{SaveFailure, SaveRequest};
use super::{FieldRule, FieldValue, InlineField, SaveRequested};

type Saves = Rc<RefCell<Vec<(SaveRequest, FieldValue)>>>;

/// The field in a surface that tracks focus, as a real one is, counting the saves it asks for.
struct Host {
    field: Entity<InlineField>,
    surface: FocusHandle,
    saves: Saves,
    _heard: Subscription,
}

impl Render for Host {
    fn render(&mut self, _: &mut Window, _: &mut Context<Self>) -> impl IntoElement {
        div().size_full().track_focus(&self.surface).child(
            div()
                .w(px(300.))
                .debug_selector(|| "the-field".to_string())
                .child(self.field.clone()),
        )
    }
}

struct Open<'a> {
    host: Entity<Host>,
    field: Entity<InlineField>,
    saves: Saves,
    cx: &'a mut VisualTestContext,
}

fn open<'a>(
    cx: &'a mut TestAppContext,
    build: impl FnOnce(&mut Window, &mut Context<InlineField>) -> InlineField + 'static,
) -> Open<'a> {
    cx.update(gpui_kit::init);
    cx.update(super::init);
    let saves = Saves::default();
    let heard = saves.clone();
    let (host, cx) = cx.add_window_view(move |window, cx| {
        let field = cx.new(|cx| build(window, cx));
        let seen = heard.clone();
        let subscription = cx.subscribe(&field, move |_, _, save: &SaveRequested, _| {
            seen.borrow_mut().push((save.request, save.value.clone()));
        });
        Host {
            field,
            surface: cx.focus_handle(),
            saves: heard,
            _heard: subscription,
        }
    });
    let field = cx.update(|_, cx| host.read(cx).field.clone());
    let saves = cx.update(|_, cx| host.read(cx).saves.clone());
    // A window that isn't the active one has no focus to lose.
    cx.update(|window, _| window.activate_window());
    let mut open = Open {
        host,
        field,
        saves,
        cx,
    };
    open.draw();
    open
}

impl Open<'_> {
    fn draw(&mut self) {
        self.cx.run_until_parked();
        self.cx.update(|window, cx| window.draw(cx).clear(cx));
        self.cx.run_until_parked();
    }

    fn begin(&mut self) {
        let field = self.field.clone();
        self.cx
            .update(|window, cx| field.update(cx, |field, cx| field.edit(window, cx)));
        self.draw();
    }

    /// What a keystroke in the field's text box does: the box's text changes and it says so.
    fn type_in(&mut self, text: &str) {
        let field = self.field.clone();
        let text = text.to_owned();
        self.cx.update(|window, cx| match &field.read(cx).editor {
            Editor::Text { state, .. } => state
                .clone()
                .update(cx, |state, cx| state.replace_all(text, window, cx)),
            Editor::Multiline(state) => state
                .clone()
                .update(cx, |state, cx| state.replace_all(text, window, cx)),
            _ => panic!("not a text box"),
        });
        self.draw();
    }

    fn press(&mut self, keys: &str) {
        self.cx.simulate_keystrokes(keys);
        self.draw();
    }

    /// Focus moves to the surface around the field, as a click on its blank space does.
    fn leave(&mut self) {
        let surface = self.cx.update(|_, cx| self.host.read(cx).surface.clone());
        self.cx.update(|window, cx| surface.focus(window, cx));
        self.draw();
    }

    fn pick(&mut self, value: &str) {
        let field = self.field.clone();
        let value = value.to_owned();
        self.cx.update(|_, cx| {
            let Editor::Combo { state, .. } = &field.read(cx).editor else {
                panic!("not a choice");
            };
            state
                .clone()
                .update(cx, |_, cx| cx.emit(SelectEvent::Confirm(Some(value))));
        });
        self.draw();
    }

    fn click_the_control(&mut self) {
        let bounds = self
            .cx
            .debug_bounds("the-field")
            .expect("the field is drawn");
        // The control sits under the field's label, along the bottom of the row.
        let point = gpui_kit::point(
            bounds.origin.x + px(40.),
            bounds.origin.y + bounds.size.height - px(10.),
        );
        self.cx.simulate_click(point, Modifiers::default());
        self.draw();
    }

    fn complete(&mut self, result: Result<FieldValue, SaveFailure>) -> bool {
        let field = self.field.clone();
        let request = self.saves.borrow().last().expect("a save was asked for").0;
        let applied = self.cx.update(|window, cx| {
            field.update(cx, |field, cx| {
                field.complete_save(request, result, window, cx)
            })
        });
        self.draw();
        applied
    }

    fn read<R>(&mut self, read: impl FnOnce(&InlineField) -> R) -> R {
        let field = self.field.clone();
        self.cx.update(|_, cx| read(field.read(cx)))
    }

    fn editing(&mut self) -> bool {
        self.read(|field| field.state.editing())
    }

    fn saved(&self) -> Vec<FieldValue> {
        self.saves
            .borrow()
            .iter()
            .map(|(_, value)| value.clone())
            .collect()
    }
}

fn text(value: &str) -> FieldValue {
    FieldValue::Text(value.into())
}

fn choice(value: &str) -> FieldValue {
    FieldValue::Choice(Some(value.into()))
}

fn plain(cx: &mut TestAppContext) -> Open<'_> {
    open(cx, |window, cx| {
        InlineField::text("Title", "before", window, cx)
    })
}

fn status(cx: &mut TestAppContext) -> Open<'_> {
    open(cx, |window, cx| {
        InlineField::combo(
            "Status",
            vec![
                ChoiceOption::new("a", "A"),
                ChoiceOption::new("b", "B"),
                ChoiceOption::new("c", "C"),
            ],
            Some("a".into()),
            window,
            cx,
        )
    })
}

fn required() -> FieldRule {
    Rc::new(|value| match value {
        FieldValue::Text(text) if text.is_empty() => Err("Required".to_owned()),
        _ => Ok(()),
    })
}

#[gpui::test]
fn enter_saves_once(cx: &mut TestAppContext) {
    let mut field = plain(cx);
    field.begin();
    field.type_in("after");
    field.press("enter");
    assert_eq!(field.saved(), [text("after")]);
    field.press("enter");
    assert_eq!(
        field.saved(),
        [text("after")],
        "Enter while saving sends nothing more"
    );
    assert!(field.complete(Ok(text("after"))));
    assert!(!field.editing());
    assert_eq!(field.read(|f| f.committed().clone()), text("after"));
}

#[gpui::test]
fn leaving_saves_text(cx: &mut TestAppContext) {
    let mut field = plain(cx);
    field.begin();
    field.type_in("after");
    field.leave();
    assert_eq!(field.saved(), [text("after")]);
}

#[gpui::test]
fn leaving_unchanged_just_closes(cx: &mut TestAppContext) {
    let mut field = plain(cx);
    field.begin();
    field.leave();
    assert!(field.saved().is_empty());
    assert!(!field.editing());
}

#[gpui::test]
fn leaving_while_pending_does_not_save_twice(cx: &mut TestAppContext) {
    let mut field = plain(cx);
    field.begin();
    field.type_in("after");
    field.press("enter");
    field.leave();
    assert_eq!(field.saved(), [text("after")]);
    assert!(field.editing(), "it's still waiting for its answer");
}

#[gpui::test]
fn a_save_that_finishes_after_leaving_does_not_take_focus_back(cx: &mut TestAppContext) {
    let mut field = plain(cx);
    field.begin();
    field.type_in("after");
    field.press("enter");
    field.leave();
    field.complete(Ok(text("after")));
    let surface = field.cx.update(|_, cx| field.host.read(cx).surface.clone());
    assert!(field.cx.update(|window, _| surface.is_focused(window)));
}

#[gpui::test]
fn invalid_sends_nothing_and_keeps_editor_on_leave(cx: &mut TestAppContext) {
    let mut field = open(cx, |window, cx| {
        InlineField::text("Title", "before", window, cx).rule(required())
    });
    field.begin();
    field.type_in("");
    field.leave();
    assert!(field.saved().is_empty());
    assert!(field.editing());
    assert_eq!(
        field.read(|f| f.state.error().map(str::to_owned)),
        Some("Required".to_owned())
    );
    field.press("enter");
    assert!(field.saved().is_empty());
}

#[gpui::test]
fn escape_restores(cx: &mut TestAppContext) {
    let mut field = plain(cx);
    field.begin();
    field.type_in("after");
    field.press("escape");
    assert!(field.saved().is_empty());
    assert!(!field.editing());
    assert_eq!(field.read(|f| f.draft().clone()), text("before"));
    field.leave();
    assert!(
        field.saved().is_empty(),
        "leaving after Escape saves nothing"
    );
}

#[gpui::test]
fn choice_saves_on_pick_with_no_enter(cx: &mut TestAppContext) {
    let mut field = status(cx);
    field.begin();
    field.pick("b");
    assert_eq!(field.saved(), [choice("b")]);
}

#[gpui::test]
fn second_pick_while_saving_is_refused(cx: &mut TestAppContext) {
    let mut field = status(cx);
    field.begin();
    field.pick("b");
    field.pick("c");
    assert_eq!(field.saved(), [choice("b")]);
    assert_eq!(field.read(|f| f.draft().clone()), choice("b"));
    field.complete(Ok(choice("b")));
    assert_eq!(field.read(|f| f.committed().clone()), choice("b"));
}

#[gpui::test]
fn date_saves_on_pick_with_no_enter(cx: &mut TestAppContext) {
    let date = NaiveDate::from_ymd_opt(2026, 11, 4).expect("a date");
    let mut field = open(cx, |window, cx| InlineField::date("Due", None, window, cx));
    field.begin();
    let target = field.field.clone();
    field.cx.update(|_, cx| {
        let Editor::Date(state) = &target.read(cx).editor else {
            panic!("not a date");
        };
        let picked = DateTime::Single(date.and_hms_opt(0, 0, 0));
        state
            .clone()
            .update(cx, |_, cx| cx.emit(DatePickerEvent::Change(picked)));
    });
    field.draw();
    assert_eq!(field.saved(), [FieldValue::Date(Some(date))]);
}

#[gpui::test]
fn date_time_saves_on_enter_not_on_each_change(cx: &mut TestAppContext) {
    let when = NaiveDate::from_ymd_opt(2026, 11, 4)
        .and_then(|date| date.and_hms_opt(9, 30, 0))
        .expect("a date and time");
    let mut field = open(cx, |window, cx| {
        InlineField::date_time("Starts", None, window, cx)
    });
    field.begin();
    let target = field.field.clone();
    field.cx.update(|window, cx| {
        let Editor::DateTime(state) = &target.read(cx).editor else {
            panic!("not a date and time");
        };
        let state = state.clone();
        state.read(cx).focus_handle(cx).focus(window, cx);
        state.update(cx, |_, cx| {
            cx.emit(DatePickerEvent::Change(DateTime::Single(Some(when))))
        });
    });
    field.draw();
    assert!(field.saved().is_empty());
    field.press("enter");
    assert_eq!(field.saved(), [FieldValue::DateTime(Some(when))]);
}

#[gpui::test]
fn failure_keeps_text_and_enter_retries(cx: &mut TestAppContext) {
    let mut field = plain(cx);
    field.begin();
    field.type_in("after");
    field.press("enter");
    field.complete(Err(SaveFailure::Refused("Notion said no".into())));
    assert!(field.editing());
    assert_eq!(field.read(|f| f.draft().clone()), text("after"));
    assert_eq!(
        field.read(|f| f.state.error().map(str::to_owned)),
        Some("Notion said no".to_owned())
    );
    field.press("enter");
    assert_eq!(field.saved(), [text("after"), text("after")]);
}

#[gpui::test]
fn pushed_value_keeps_open_draft(cx: &mut TestAppContext) {
    let mut field = plain(cx);
    field.begin();
    field.type_in("mine");
    let target = field.field.clone();
    field.cx.update(|window, cx| {
        target
            .update(cx, |f, cx| f.set_committed(text("theirs"), window, cx))
            .expect("same type");
    });
    field.draw();
    assert_eq!(field.read(|f| f.draft().clone()), text("mine"));
    assert_eq!(field.read(|f| f.committed().clone()), text("theirs"));
    let box_text = field.cx.update(|_, cx| match &target.read(cx).editor {
        Editor::Text { state, .. } => state.read(cx).value().to_string(),
        _ => panic!("not a text box"),
    });
    assert_eq!(box_text, "mine");
}

#[gpui::test]
fn read_only_never_opens(cx: &mut TestAppContext) {
    let mut field = open(cx, |window, cx| {
        InlineField::text("Title", "x", window, cx).read_only()
    });
    field.begin();
    assert!(!field.editing());
    let target = field.field.clone();
    field.cx.update(|window, cx| {
        let focus = target.read(cx).focus.clone();
        focus.focus(window, cx);
    });
    field.draw();
    field.press("enter");
    assert!(!field.editing());
}

#[gpui::test]
fn in_form_never_emits_on_enter_or_leave(cx: &mut TestAppContext) {
    let mut field = open(cx, |window, cx| {
        InlineField::text("Title", "before", window, cx).in_form()
    });
    let target = field.field.clone();
    field.cx.update(|window, cx| {
        target.update(cx, |f, cx| {
            f.state.edit();
            f.editor.focus(window, cx);
        });
    });
    field.draw();
    field.type_in("after");
    field.press("enter");
    field.leave();
    assert!(field.saved().is_empty());
    assert!(field.editing());
    assert_eq!(field.read(|f| f.draft().clone()), text("after"));
}

#[gpui::test]
fn time_saves_on_leaving_not_on_each_digit(cx: &mut TestAppContext) {
    let time = |hour| NaiveTime::from_hms_opt(hour, 0, 0).expect("a time");
    let mut field = open(cx, move |window, cx| {
        InlineField::time("Start", time(9), window, cx)
    });
    field.begin();
    let target = field.field.clone();
    for hour in [1, 10] {
        field.cx.update(|_, cx| {
            let Editor::Time(state) = &target.read(cx).editor else {
                panic!("not a time");
            };
            state
                .clone()
                .update(cx, |_, cx| cx.emit(TimeFieldEvent::Change(time(hour))));
        });
        field.draw();
    }
    assert!(field.saved().is_empty());
    field.leave();
    assert_eq!(field.saved(), [FieldValue::Time(time(10))]);
}

#[gpui::test]
fn opening_a_picker_popup_is_not_leaving(cx: &mut TestAppContext) {
    let mut field = status(cx);
    field.begin();
    field.click_the_control();
    assert!(field.editing(), "the popup is part of the field");
    assert!(field.saved().is_empty());
    let target = field.field.clone();
    let inside = field.cx.update(|window, cx| {
        let focus = target.read(cx).focus.clone();
        focus.contains_focused(window, cx)
    });
    assert!(inside);
}

#[gpui::test]
fn dismissing_a_picker_without_a_pick_cancels(cx: &mut TestAppContext) {
    let mut field = status(cx);
    field.begin();
    field.click_the_control();
    field.leave();
    assert!(field.saved().is_empty());
    assert!(!field.editing());
}

#[gpui::test]
fn shift_enter_adds_a_line_and_enter_saves_multiline(cx: &mut TestAppContext) {
    let mut field = open(cx, |window, cx| {
        InlineField::multiline("Notes", "", window, cx)
    });
    field.begin();
    field.type_in("one");
    field.press("shift-enter");
    assert!(field.saved().is_empty());
    // A text area's caret starts at the top.
    let drafted = field.read(|f| f.draft().clone());
    assert_eq!(drafted, text("\none"));
    field.press("enter");
    assert_eq!(field.saved(), [drafted]);
}

#[gpui::test]
fn number_saves_the_unmasked_value(cx: &mut TestAppContext) {
    let mut field = open(cx, |window, cx| {
        InlineField::number("Rent", "", NumberFormat::Currency, window, cx)
    });
    field.begin();
    field.type_in("1234567.89");
    field.press("enter");
    assert_eq!(field.saved(), [text("1234567.89")]);
}

fn chip_field(cx: &mut TestAppContext, in_form: bool) -> Open<'_> {
    open(cx, move |window, cx| {
        let field = InlineField::chip(
            "Status",
            vec![
                ChoiceOption::new("a", "A"),
                ChoiceOption::new("b", "B"),
                ChoiceOption::new("c", "C"),
            ],
            Some("a".into()),
            window,
            cx,
        );
        if in_form { field.in_form() } else { field }
    })
}

impl Open<'_> {
    fn pick_chip(&mut self, value: &str) {
        let field = self.field.clone();
        let value = value.to_owned();
        self.cx
            .update(|window, cx| field.update(cx, |field, cx| field.pick_chip(value, window, cx)));
        self.draw();
    }
}

#[gpui::test]
fn chip_saves_on_pick_with_one_click(cx: &mut TestAppContext) {
    let mut chip = chip_field(cx, false);
    assert!(!chip.editing(), "there's nothing to open first");
    chip.pick_chip("b");
    assert_eq!(chip.saved(), [choice("b")]);
    assert!(chip.complete(Ok(choice("b"))));
    assert_eq!(chip.read(|field| field.committed().clone()), choice("b"));
    assert!(!chip.editing());
}

#[gpui::test]
fn second_chip_pick_while_saving_is_refused(cx: &mut TestAppContext) {
    let mut chip = chip_field(cx, false);
    chip.pick_chip("b");
    chip.pick_chip("c");
    assert_eq!(chip.saved(), [choice("b")]);
    assert_eq!(chip.read(|field| field.draft().clone()), choice("b"));
}

#[gpui::test]
fn a_refused_chip_keeps_its_pick_and_can_be_picked_again(cx: &mut TestAppContext) {
    let mut chip = chip_field(cx, false);
    chip.pick_chip("b");
    chip.complete(Err(SaveFailure::Refused("No".into())));
    assert_eq!(
        chip.read(|field| field.state.error().map(str::to_owned)),
        Some("No".to_owned())
    );
    chip.pick_chip("c");
    assert_eq!(chip.saved(), [choice("b"), choice("c")]);
}

#[gpui::test]
fn a_chip_in_a_form_changes_the_draft_and_sends_nothing(cx: &mut TestAppContext) {
    let mut chip = chip_field(cx, true);
    let field = chip.field.clone();
    chip.cx
        .update(|window, cx| field.update(cx, |field, cx| field.begin_edit(window, cx)));
    chip.pick_chip("b");
    assert!(chip.saved().is_empty());
    assert_eq!(chip.read(|field| field.draft().clone()), choice("b"));
}
