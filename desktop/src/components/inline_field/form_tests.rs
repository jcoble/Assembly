//! The form applied to real fields: one Save, every value, and nothing half done.
use std::cell::RefCell;
use std::rc::Rc;

use gpui::TestAppContext;
use gpui_kit::{
    AppContext as _, Context, Entity, FocusHandle, InteractiveElement as _, IntoElement,
    ParentElement as _, Render, SharedString, Styled as _, Subscription, VisualTestContext, Window,
    div, px,
};

use super::super::editor::Editor;
use super::super::field::{FieldRule, FieldValue, InlineField};
use super::super::form_state::{FormFailure, FormValues};
use super::super::state::SaveRequest;
use super::{FormSaveRequested, InlineForm};

type Saves = Rc<RefCell<Vec<(SaveRequest, FormValues)>>>;

struct Host {
    form: Entity<InlineForm>,
    surface: FocusHandle,
    _heard: Subscription,
}

impl Render for Host {
    fn render(&mut self, _: &mut Window, _: &mut Context<Self>) -> impl IntoElement {
        div().size_full().track_focus(&self.surface).child(div().w(px(300.)).child(self.form.clone()))
    }
}

struct Open<'a> {
    host: Entity<Host>,
    form: Entity<InlineForm>,
    fields: Vec<Entity<InlineField>>,
    saves: Saves,
    cx: &'a mut VisualTestContext,
}

fn required() -> FieldRule {
    Rc::new(|value| match value {
        FieldValue::Text(text) if text.is_empty() => Err("Required".to_owned()),
        _ => Ok(()),
    })
}

fn open(cx: &mut TestAppContext) -> Open<'_> {
    cx.update(gpui_kit::init);
    cx.update(super::super::field::init);
    let saves = Saves::default();
    let seen = saves.clone();
    let (host, cx) = cx.add_window_view(move |window, cx| {
        let fields = ["name", "destination"].map(|key| {
            let field = cx.new(|cx| {
                InlineField::text(key.to_owned(), format!("old {key}"), window, cx)
                    .in_form()
                    .rule(required())
            });
            (SharedString::from(key), field)
        });
        let form = cx.new(|cx| InlineForm::new(fields.to_vec(), cx));
        let heard = cx.subscribe(&form, move |_, _, save: &FormSaveRequested, _| {
            seen.borrow_mut().push((save.request, save.values.clone()));
        });
        Host {
            form,
            surface: cx.focus_handle(),
            _heard: heard,
        }
    });
    cx.update(|window, _| window.activate_window());
    let form = cx.update(|_, cx| host.read(cx).form.clone());
    let fields = cx.update(|_, cx| form.read(cx).fields.iter().map(|(_, field)| field.clone()).collect());
    let mut open = Open { host, form, fields, saves, cx };
    open.draw();
    open
}

fn text(value: &str) -> FieldValue {
    FieldValue::Text(value.into())
}

fn values(name: &str, destination: &str) -> FormValues {
    FormValues(vec![("name".into(), text(name)), ("destination".into(), text(destination))])
}

impl Open<'_> {
    fn draw(&mut self) {
        self.cx.run_until_parked();
        self.cx.update(|window, cx| window.draw(cx).clear(cx));
        self.cx.run_until_parked();
    }

    fn act(&mut self, act: impl FnOnce(&mut InlineForm, &mut Window, &mut Context<InlineForm>)) {
        let form = self.form.clone();
        self.cx.update(|window, cx| form.update(cx, |form, cx| act(form, window, cx)));
        self.draw();
    }

    fn type_in(&mut self, index: usize, value: &str) {
        let field = self.fields[index].clone();
        let value = value.to_owned();
        self.cx.update(|window, cx| match &field.read(cx).editor {
            Editor::Text { state, .. } => {
                state.clone().update(cx, |state, cx| state.replace_all(value, window, cx))
            }
            _ => panic!("not a text box"),
        });
        self.draw();
    }

    fn committed(&mut self, index: usize) -> FieldValue {
        let field = self.fields[index].clone();
        self.cx.update(|_, cx| field.read(cx).committed().clone())
    }

    fn draft(&mut self, index: usize) -> FieldValue {
        let field = self.fields[index].clone();
        self.cx.update(|_, cx| field.read(cx).draft().clone())
    }

    fn error(&mut self, index: usize) -> Option<String> {
        let field = self.fields[index].clone();
        self.cx.update(|_, cx| field.read(cx).state.error().map(str::to_owned))
    }

    fn focused_inside(&mut self, index: usize) -> bool {
        let field = self.fields[index].clone();
        self.cx.update(|window, cx| {
            let focus = field.read(cx).focus.clone();
            focus.contains_focused(window, cx)
        })
    }

    fn sent(&self) -> Vec<FormValues> {
        self.saves.borrow().iter().map(|(_, values)| values.clone()).collect()
    }

    fn last_request(&self) -> SaveRequest {
        self.saves.borrow().last().expect("a save was asked for").0
    }
}

#[gpui::test]
fn one_save_sends_every_value_and_commits_them_together(cx: &mut TestAppContext) {
    let mut form = open(cx);
    form.act(|form, window, cx| form.begin(window, cx));
    form.type_in(0, "new name");
    form.type_in(1, "new destination");
    form.act(|form, window, cx| form.save(window, cx));
    assert_eq!(form.sent(), [values("new name", "new destination")]);
    assert_eq!(form.committed(0), text("old name"), "nothing commits before the answer");
    let request = form.last_request();
    form.act(|form, window, cx| {
        form.complete_save(request, Ok(values("New Name", "new destination")), window, cx);
    });
    assert_eq!(form.committed(0), text("New Name"));
    assert_eq!(form.committed(1), text("new destination"));
    assert!(!form.cx.update(|_, cx| form.form.read(cx).editing()));
}

#[gpui::test]
fn an_invalid_field_sends_nothing_shows_its_error_and_takes_focus(cx: &mut TestAppContext) {
    let mut form = open(cx);
    form.act(|form, window, cx| form.begin(window, cx));
    form.type_in(1, "");
    form.act(|form, window, cx| form.save(window, cx));
    assert!(form.sent().is_empty());
    assert_eq!(form.error(1), Some("Required".to_owned()));
    assert_eq!(form.error(0), None);
    assert!(form.focused_inside(1));
}

#[gpui::test]
fn leaving_a_form_field_saves_nothing(cx: &mut TestAppContext) {
    let mut form = open(cx);
    form.act(|form, window, cx| form.begin(window, cx));
    form.type_in(0, "new name");
    let surface = form.cx.update(|_, cx| form.host.read(cx).surface.clone());
    form.cx.update(|window, cx| surface.focus(window, cx));
    form.draw();
    assert!(form.sent().is_empty());
    assert_eq!(form.draft(0), text("new name"));
}

#[gpui::test]
fn escape_cancels_the_whole_form(cx: &mut TestAppContext) {
    let mut form = open(cx);
    form.act(|form, window, cx| form.begin(window, cx));
    form.type_in(0, "new name");
    form.type_in(1, "");
    form.cx.simulate_keystrokes("escape");
    form.draw();
    assert_eq!(form.draft(0), text("old name"));
    assert_eq!(form.draft(1), text("old destination"));
    assert!(!form.cx.update(|_, cx| form.form.read(cx).editing()));
}

#[gpui::test]
fn a_refused_save_keeps_every_draft_and_can_be_sent_again(cx: &mut TestAppContext) {
    let mut form = open(cx);
    form.act(|form, window, cx| form.begin(window, cx));
    form.type_in(0, "new name");
    form.act(|form, window, cx| form.save(window, cx));
    let request = form.last_request();
    form.act(|form, window, cx| {
        form.complete_save(request, Err(FormFailure::Refused("No".into())), window, cx);
    });
    assert_eq!(form.draft(0), text("new name"));
    form.act(|form, window, cx| form.save(window, cx));
    assert_eq!(form.sent().len(), 2);
}

#[gpui::test]
fn owner_field_errors_land_on_their_fields(cx: &mut TestAppContext) {
    let mut form = open(cx);
    form.act(|form, window, cx| form.begin(window, cx));
    form.type_in(1, "bad");
    form.act(|form, window, cx| form.save(window, cx));
    let request = form.last_request();
    form.act(|form, window, cx| {
        let errors = vec![("destination".into(), "Can't reach it".to_owned())];
        form.complete_save(request, Err(FormFailure::Fields(errors)), window, cx);
    });
    assert_eq!(form.error(1), Some("Can't reach it".to_owned()));
    assert!(form.focused_inside(1));
}

#[gpui::test]
fn an_owner_error_clears_when_the_form_is_sent_again(cx: &mut TestAppContext) {
    let mut form = open(cx);
    form.act(|form, window, cx| form.begin(window, cx));
    form.type_in(0, "new name");
    form.act(|form, window, cx| form.save(window, cx));
    let request = form.last_request();
    form.act(|form, window, cx| {
        let errors = vec![("destination".into(), "Can't reach it".to_owned())];
        form.complete_save(request, Err(FormFailure::Fields(errors)), window, cx);
    });
    form.act(|form, window, cx| form.save(window, cx));
    assert_eq!(form.sent().len(), 2);
    assert_eq!(form.error(1), None);
}

#[gpui::test]
fn a_pushed_value_keeps_open_drafts(cx: &mut TestAppContext) {
    let mut form = open(cx);
    form.act(|form, window, cx| form.begin(window, cx));
    form.type_in(0, "mine");
    form.act(|form, window, cx| form.set_committed(values("theirs", "there"), window, cx));
    assert_eq!(form.draft(0), text("mine"));
    assert_eq!(form.committed(0), text("theirs"));
    assert_eq!(form.committed(1), text("there"));
}
