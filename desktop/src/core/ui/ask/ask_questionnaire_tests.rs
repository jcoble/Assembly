use super::{AskPayload, AskQuestionnaire, OnAnswer, legend};
use gpui::{TestAppContext, VisualTestContext};
use gpui_kit::{
    ClickEvent, Context, Entity, IntoElement, ParentElement as _, Render, Styled as _, Window, div,
};
use serde_json::{Value, json};
use std::cell::RefCell;
use std::rc::Rc;

type Sent = Rc<RefCell<Vec<Value>>>;

fn ask(required: &[&str], properties: Value) -> AskPayload {
    serde_json::from_value(json!({
        "kind": "question", "state": "open", "options": [],
        "schema": { "required": required, "properties": properties },
    }))
    .unwrap()
}

struct Host {
    form: Entity<AskQuestionnaire>,
}

impl Render for Host {
    fn render(&mut self, _: &mut Window, _: &mut Context<Self>) -> impl IntoElement {
        div().size_full().child(self.form.clone())
    }
}

fn opened<'a>(
    ask: &AskPayload,
    cx: &'a mut TestAppContext,
) -> (Entity<AskQuestionnaire>, Sent, &'a mut VisualTestContext) {
    cx.update(gpui_kit::init);
    let sent: Sent = Rc::default();
    let on_answer: OnAnswer = {
        let sent = sent.clone();
        Rc::new(move |answer, _, _| sent.borrow_mut().push(answer))
    };
    let ask = ask.clone();
    let (host, cx) = cx.add_window_view(|window, cx| Host {
        form: AskQuestionnaire::of("a1".into(), &ask, on_answer, window, cx).unwrap(),
    });
    let form = host.read_with(cx, |host, _| host.form.clone());
    cx.run_until_parked();
    (form, sent, cx)
}

fn type_in(form: &Entity<AskQuestionnaire>, text: &str, cx: &mut VisualTestContext) {
    cx.update(|window, cx| {
        let state = form.read(cx).state.clone();
        let item = state.read(cx).current_item().cloned().unwrap();
        state
            .update(cx, |state, cx| {
                state.set_input_value(&item, text, window, cx)
            })
            .unwrap();
    });
    cx.run_until_parked();
}

fn press_continue(form: &Entity<AskQuestionnaire>, cx: &mut VisualTestContext) {
    cx.update(|window, cx| form.update(cx, |form, cx| form.advance(window, cx)));
    cx.run_until_parked();
}

fn choose(form: &Entity<AskQuestionnaire>, item: &str, value: &str, cx: &mut VisualTestContext) {
    cx.update(|_, cx| {
        let state = form.read(cx).state.clone();
        state.update(cx, |state, cx| {
            state.activate_choice(item, value, cx).unwrap();
        });
    });
    cx.run_until_parked();
}

fn current(form: &Entity<AskQuestionnaire>, cx: &mut VisualTestContext) -> String {
    cx.update(|_, cx| {
        let state = form.read(cx).state.read(cx);
        state.current_item().unwrap().to_string()
    })
}

#[gpui::test]
fn a_field_that_can_be_left_out_says_so() {
    assert_eq!(legend("Zone", true), "Zone");
    assert_eq!(legend("Zone", false), "Zone (optional)");
}

// Every kind of field, one question at a time, answers as its own type through the one answer path.
#[gpui::test]
async fn each_field_type_answers_as_its_type(cx: &mut TestAppContext) {
    let ask = ask(
        &["zone", "token"],
        json!({
            "zone": { "type": "string", "enum": ["east", "west"] },
            "tags": { "type": "array", "items": { "enum": ["a", "b", "c"] } },
            "agree": { "type": "boolean" },
            "keep": { "type": "boolean", "default": true },
            "note": { "type": "string" },
            "count": { "type": "integer" },
            "token": { "type": "string", "format": "password" },
        }),
    );
    let (form, sent, cx) = opened(&ask, cx);
    choose(&form, "zone", "west", cx);
    press_continue(&form, cx);
    choose(&form, "tags", "c", cx);
    choose(&form, "tags", "a", cx);
    press_continue(&form, cx);
    choose(&form, "agree", "yes", cx);
    press_continue(&form, cx);
    press_continue(&form, cx);
    type_in(&form, "  hello ", cx);
    press_continue(&form, cx);
    type_in(&form, "12", cx);
    press_continue(&form, cx);
    assert_eq!(current(&form, cx), "token");
    assert!(sent.borrow().is_empty());
    type_in(&form, " s3 ", cx);
    press_continue(&form, cx);
    assert_eq!(
        *sent.borrow(),
        [json!({ "action": "accept", "content": {
            "zone": "west", "tags": ["a", "c"], "agree": true, "keep": true,
            "note": "hello", "count": 12, "token": " s3 ",
        } })]
    );
}

#[gpui::test]
async fn a_required_choice_starts_on_its_first_and_a_check_box_as_the_schema_says(
    cx: &mut TestAppContext,
) {
    let ask = ask(
        &["zone"],
        json!({
            "zone": { "type": "string", "enum": ["east", "west"] },
            "tag": { "type": "string", "enum": ["a", "b"] },
            "agree": { "type": "boolean" },
            "keep": { "type": "boolean", "default": true },
        }),
    );
    let (form, sent, cx) = opened(&ask, cx);
    press_continue(&form, cx);
    press_continue(&form, cx);
    press_continue(&form, cx);
    press_continue(&form, cx);
    assert_eq!(
        *sent.borrow(),
        [
            json!({ "action": "accept", "content": { "zone": "east", "agree": false, "keep": true } })
        ]
    );
}

#[gpui::test]
async fn a_password_field_is_masked_and_spaces_count_but_nothing_does_not(cx: &mut TestAppContext) {
    let ask = ask(
        &["token"],
        json!({ "token": { "type": "string", "title": "Token", "writeOnly": true } }),
    );
    let (form, sent, cx) = opened(&ask, cx);
    cx.update(|_, cx| {
        let state = form.read(cx).state.read(cx);
        let input = state.input_state("token").unwrap();
        assert!(input.read(cx).presentation().is_masked());
    });
    press_continue(&form, cx);
    assert!(sent.borrow().is_empty());
    cx.update(|_, cx| {
        let state = form.read(cx).state.read(cx);
        assert_eq!(
            state
                .error("token")
                .and_then(|why| why.message())
                .map(|why| why.to_string()),
            Some("Token is required.".to_string())
        );
    });
    type_in(&form, "   ", cx);
    press_continue(&form, cx);
    assert_eq!(
        *sent.borrow(),
        [json!({ "action": "accept", "content": { "token": "   " } })]
    );
}

#[gpui::test]
async fn an_empty_required_field_waits_and_an_empty_optional_one_moves_on(cx: &mut TestAppContext) {
    let ask = ask(
        &["note"],
        json!({
            "extra": { "type": "string" },
            "note": { "type": "string", "title": "Note" },
        }),
    );
    let (form, sent, cx) = opened(&ask, cx);
    press_continue(&form, cx);
    assert_eq!(current(&form, cx), "note");
    press_continue(&form, cx);
    assert!(sent.borrow().is_empty());
    type_in(&form, "hi", cx);
    press_continue(&form, cx);
    assert_eq!(
        *sent.borrow(),
        [json!({ "action": "accept", "content": { "note": "hi" } })]
    );
}

#[gpui::test]
async fn a_number_field_refuses_words_where_the_person_is(cx: &mut TestAppContext) {
    let ask = ask(
        &[],
        json!({ "count": { "type": "integer", "title": "Count" } }),
    );
    let (form, sent, cx) = opened(&ask, cx);
    type_in(&form, "many", cx);
    press_continue(&form, cx);
    assert!(sent.borrow().is_empty());
    cx.update(|_, cx| {
        let state = form.read(cx).state.read(cx);
        assert_eq!(
            state
                .error("count")
                .and_then(|why| why.message())
                .map(|why| why.to_string()),
            Some("Count must be a number.".to_string())
        );
    });
    type_in(&form, "3", cx);
    press_continue(&form, cx);
    assert_eq!(
        *sent.borrow(),
        [json!({ "action": "accept", "content": { "count": 3 } })]
    );
}

fn with_other(required: &[&str]) -> AskPayload {
    ask(
        required,
        json!({
            "question_0": { "type": "string", "oneOf": [
                { "const": "Postgres", "title": "Postgres" }, { "const": "SQLite", "title": "SQLite" },
            ] },
            "question_0_custom": { "type": "string", "title": "Other" },
        }),
    )
}

// The provider's "Other" is the question's own free-text answer, not a question after it.
#[gpui::test]
async fn other_is_the_free_text_answer_of_its_question(cx: &mut TestAppContext) {
    let (form, sent, cx) = opened(&with_other(&[]), cx);
    assert_eq!(cx.update(|_, cx| form.read(cx).state.read(cx).total()), 1);
    type_in(&form, "MySQL", cx);
    press_continue(&form, cx);
    assert_eq!(
        *sent.borrow(),
        [json!({ "action": "accept", "content": { "question_0_custom": "MySQL" } })]
    );
}

// The backend refuses an accept without a required field, so the text doesn't replace it.
#[gpui::test]
async fn other_on_a_required_question_still_answers_the_required_field(cx: &mut TestAppContext) {
    let (form, sent, cx) = opened(&with_other(&["question_0"]), cx);
    type_in(&form, "MySQL", cx);
    press_continue(&form, cx);
    assert_eq!(
        *sent.borrow(),
        [
            json!({ "action": "accept", "content": { "question_0": "Postgres", "question_0_custom": "MySQL" } })
        ]
    );
}

#[gpui::test]
async fn enter_in_a_text_field_does_what_continue_does(cx: &mut TestAppContext) {
    let ask = ask(
        &[],
        json!({ "first": { "type": "string" }, "second": { "type": "string" } }),
    );
    let (form, sent, cx) = opened(&ask, cx);
    let focus = |form: &Entity<AskQuestionnaire>, cx: &mut VisualTestContext| {
        cx.update(|window, cx| {
            let state = form.read(cx).state.clone();
            let item = state.read(cx).current_item().cloned().unwrap();
            state.update(cx, |state, cx| state.focus_input(&item, window, cx));
        });
        cx.run_until_parked();
    };
    focus(&form, cx);
    cx.simulate_keystrokes("h i enter");
    cx.run_until_parked();
    assert_eq!(current(&form, cx), "second");
    assert!(sent.borrow().is_empty());
    // Moved on by keyboard, the next text field is ready: the second Enter leaves it empty and sends.
    cx.simulate_keystrokes("enter");
    cx.run_until_parked();
    assert_eq!(
        *sent.borrow(),
        [json!({ "action": "accept", "content": { "first": "hi" } })]
    );
}

// Picking a single choice is the kit's own confirm: on the last question it sends, after the moment that shows the pick.
#[gpui::test]
async fn a_click_on_the_last_single_choice_sends_it_through_the_form(cx: &mut TestAppContext) {
    let ask = ask(
        &[],
        json!({ "zone": { "type": "string", "enum": ["east", "west"] } }),
    );
    let (form, sent, cx) = opened(&ask, cx);
    cx.update(|window, cx| {
        let state = form.read(cx).state.clone();
        state
            .update(cx, |state, cx| state.choose("zone", "west", window, cx))
            .unwrap();
    });
    cx.executor()
        .advance_clock(std::time::Duration::from_secs(2));
    cx.run_until_parked();
    assert_eq!(
        *sent.borrow(),
        [json!({ "action": "accept", "content": { "zone": "west" } })]
    );
}

#[gpui::test]
async fn cancel_and_skip_answer_the_whole_ask(cx: &mut TestAppContext) {
    let ask = ask(&["note"], json!({ "note": { "type": "string" } }));
    let (form, sent, cx) = opened(&ask, cx);
    let click =
        |form: &Entity<AskQuestionnaire>, action: &'static str, cx: &mut VisualTestContext| {
            cx.update(|window, cx| {
                let leave = form.read(cx).leave(action);
                leave(&ClickEvent::default(), window, cx);
            });
        };
    click(&form, "cancel", cx);
    click(&form, "decline", cx);
    assert_eq!(
        *sent.borrow(),
        [
            json!({ "action": "cancel" }),
            json!({ "action": "decline" })
        ]
    );
}

// The kit's schema is fixed: a changed ask builds another in place, keeping what's typed and where the person is.
#[gpui::test]
async fn a_changed_ask_keeps_what_was_typed_and_picked(cx: &mut TestAppContext) {
    let first = ask(
        &["zone"],
        json!({
            "zone": { "type": "string", "enum": ["east", "west"] },
            "note": { "type": "string" },
        }),
    );
    let (form, sent, cx) = opened(&first, cx);
    choose(&form, "zone", "west", cx);
    press_continue(&form, cx);
    type_in(&form, "half typed", cx);
    let input_before = cx.update(|_, cx| form.read(cx).state.read(cx).input_state("note").unwrap());
    let state_before = cx.update(|_, cx| form.read(cx).state.clone());

    // The same schema with new words builds nothing.
    let mut reworded = first.clone();
    reworded.message = Some("Still waiting".into());
    cx.update(|window, cx| form.update(cx, |form, cx| form.sync(&reworded, window, cx)));
    assert_eq!(
        cx.update(|_, cx| form.read(cx).state.entity_id()),
        state_before.entity_id()
    );

    let changed = ask(
        &["zone"],
        json!({
            "zone": { "type": "string", "enum": ["north", "west"] },
            "note": { "type": "string" },
            "more": { "type": "string" },
        }),
    );
    cx.update(|window, cx| form.update(cx, |form, cx| form.sync(&changed, window, cx)));
    cx.run_until_parked();
    assert_ne!(
        cx.update(|_, cx| form.read(cx).state.entity_id()),
        state_before.entity_id()
    );
    assert_eq!(current(&form, cx), "note");
    let input_after = cx.update(|_, cx| form.read(cx).state.read(cx).input_state("note").unwrap());
    assert_eq!(input_after.entity_id(), input_before.entity_id());
    press_continue(&form, cx);
    press_continue(&form, cx);
    assert_eq!(
        *sent.borrow(),
        [json!({ "action": "accept", "content": { "zone": "west", "note": "half typed" } })]
    );
}

#[gpui::test]
async fn a_repeated_choice_in_a_schema_does_not_break_the_form(cx: &mut TestAppContext) {
    let ask = ask(
        &[],
        json!({ "zone": { "type": "string", "enum": ["east", "east"] } }),
    );
    let (form, _, cx) = opened(&ask, cx);
    assert_eq!(current(&form, cx), "zone");
}
