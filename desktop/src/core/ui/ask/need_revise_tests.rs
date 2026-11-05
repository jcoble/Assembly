//! A gate's Revise against the real socket clients and feed. A fake backend records what the row sends; the engine's
//! handling of the answer has its own socket test.
use gpui::TestAppContext;
use gpui_kit::{Entity, Modifiers, VisualTestContext};
use serde_json::{Value, json};

use super::{NeedRow, NeedRowState};
use crate::core::engine::test_engine::{MACHINE, Seeded, TestEngine};
use crate::core::engine::{EngineKey, EngineScope};
use crate::core::socket::test_wait::until;

fn gate(options: &[&str]) -> Value {
    let options = (options.iter())
        .map(|id| json!({"id":id,"label":id,"kind":"choice"}))
        .collect::<Vec<_>>();
    json!({"entityKind":"needs_me","id":"gate","machine":MACHINE,"version":1,"sessionId":"s","stepId":"gate-step","kind":"workflow_gate","state":"open","text":"Ship it?","inputMode":"choice","options":options,"freeText":false,"createdMs":1})
}

struct Row<'a> {
    engine: TestEngine,
    row: Entity<NeedRow>,
    cx: &'a mut VisualTestContext,
}

async fn open<'a>(need: Value, cx: &'a mut TestAppContext) -> Row<'a> {
    let seeded = Seeded {
        items: vec![
            need,
            json!({"entityKind":"session","machine":MACHINE,"id":"s","version":1,"provider":"fixture","folder":"/display","state":"idle","activityState":"idle","epoch":3}),
        ],
        ..Seeded::default()
    };
    let engine = TestEngine::start(seeded, &[("needs_me", None), ("sessions", None)], cx).await;
    let feed = engine.feed.clone();
    let key = EngineKey {
        machine: MACHINE.into(),
        kind: "needs_me".into(),
        id: "gate".into(),
    };
    let scope = EngineScope {
        machine: MACHINE.into(),
        topic: "needs_me".into(),
        key: None,
    };
    let (row, visual) = cx.add_window_view(|window, cx| {
        let need = feed.read(cx).need(&key).unwrap();
        NeedRow::build(
            key.clone(),
            scope.clone(),
            feed.clone(),
            "board",
            need,
            window,
            cx,
        )
    });
    row.update_in(visual, |row, window, cx| row.open(window, cx));
    draw(visual);
    Row {
        engine,
        row,
        cx: visual,
    }
}

fn draw(cx: &mut VisualTestContext) {
    cx.run_until_parked();
    cx.update(|window, cx| window.draw(cx).clear(cx));
}

impl Row<'_> {
    fn shows(&mut self, selector: &'static str) -> bool {
        draw(self.cx);
        self.cx.debug_bounds(selector).is_some()
    }

    fn click(&mut self, selector: &'static str) {
        draw(self.cx);
        let bounds =
            (self.cx.debug_bounds(selector)).unwrap_or_else(|| panic!("{selector} isn't there"));
        self.cx.simulate_click(bounds.center(), Modifiers::none());
        draw(self.cx);
    }

    fn type_text(&mut self, words: &str) {
        let row = self.row.clone();
        let words = words.to_owned();
        row.update_in(self.cx, move |row, window, cx| {
            row.text
                .update(cx, |text, cx| text.set_value(words, window, cx));
        });
    }

    async fn sent(&mut self, count: usize) -> Vec<Value> {
        let changed = self.engine.changed.clone();
        let state = self.engine.state.clone();
        let cx = &mut *self.cx;
        until("the answer", &mut self.engine.changes, &changed, || {
            draw(cx);
            state.lock().unwrap().commands.len() >= count
        })
        .await;
        draw(self.cx);
        self.engine.commands()
    }
}

#[gpui::test]
async fn revise_opens_the_field_and_sends_the_typed_words(cx: &mut TestAppContext) {
    let mut row = open(gate(&["approve", "revise", "deny"]), cx).await;
    assert!(row.shows("need-choice:approve"));
    assert!(row.shows("need-choice:deny"));
    assert!(
        !row.shows("need-send-revision"),
        "the field waits for Revise"
    );
    row.click("need-choice:revise");
    assert!(row.shows("need-send-revision"));
    assert!(row.engine.commands().is_empty(), "opening it sends nothing");
    row.type_text("Use the smaller schema");
    row.click("need-send-revision");
    let commands = row.sent(1).await;
    assert_eq!(commands[0]["command"]["kind"], "resolve_need");
    assert_eq!(
        commands[0]["command"]["answer"],
        json!({"choice":"revise","text":"Use the smaller schema"})
    );
}

#[gpui::test]
async fn blank_words_send_nothing(cx: &mut TestAppContext) {
    let mut row = open(gate(&["approve", "revise", "deny"]), cx).await;
    row.click("need-choice:revise");
    row.type_text("   \n");
    row.click("need-send-revision");
    assert!(row.engine.commands().is_empty());
    assert!(row.shows("need-send-revision"));
}

async fn refused(code: &'static str, words: &str, cx: &mut TestAppContext) -> String {
    let mut row = open(gate(&["approve", "revise", "deny"]), cx).await;
    row.engine.state.lock().unwrap().refusal = Some(code);
    row.click("need-choice:revise");
    row.type_text(words);
    row.click("need-send-revision");
    row.sent(1).await;
    let (changed, held) = (row.engine.changed.clone(), row.row.clone());
    let _woken = row.cx.update(|_, cx| {
        let wake = changed.clone();
        cx.observe(&held, move |_, _| {
            let _ = wake.unbounded_send(());
        })
    });
    let cx = &mut *row.cx;
    until("the refusal", &mut row.engine.changes, &changed, || {
        draw(cx);
        cx.read(|cx| {
            let row = held.read(cx);
            row.reconciled && !matches!(row.state, NeedRowState::Sending)
        })
    })
    .await;
    assert_eq!(
        cx.read(|cx| held.read(cx).text.read(cx).value().to_string()),
        words
    );
    assert_eq!(
        row.engine.commands().len(),
        1,
        "never sent again on its own"
    );
    let line = cx.read(|cx| match &held.read(cx).state {
        NeedRowState::Failed { message } => message.clone(),
        other => panic!("{other:?}"),
    });
    row.click("need-review");
    assert!(row.shows("need-send-revision"), "the field comes back");
    line
}

#[gpui::test]
async fn nothing_earlier_to_send_it_to_is_said_plainly(cx: &mut TestAppContext) {
    let line = refused("revision_target_undefined", "Try again", cx).await;
    assert_eq!(line, "There's no earlier step to send this back to.");
}

#[gpui::test]
async fn a_gate_the_engine_gives_nothing_to_revise_has_no_button(cx: &mut TestAppContext) {
    let mut row = open(gate(&["approve", "deny"]), cx).await;
    assert!(row.shows("need-choice:approve"));
    assert!(!row.shows("need-choice:revise"));
}

#[gpui::test]
async fn approve_still_sends_its_word(cx: &mut TestAppContext) {
    let mut row = open(gate(&["approve", "revise", "deny"]), cx).await;
    row.click("need-choice:approve");
    assert_eq!(row.sent(1).await[0]["command"]["answer"], "approve");
}
