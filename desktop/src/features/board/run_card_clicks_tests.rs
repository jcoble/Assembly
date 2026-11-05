//! A run card against the real socket clients and feed, driven by clicks. A fake backend serves the run and records
//! what the card sends; the engine's handling of those commands has its own socket tests.
use gpui::TestAppContext;
use gpui_kit::{
    AppContext as _, Context, Entity, IntoElement, Modifiers, Render, VisualTestContext, Window,
};
use serde_json::{Value, json};
use std::cell::RefCell;
use std::rc::Rc;

use super::{RunCard, RunCardEvent};
use crate::core::engine::EngineKey;
use crate::core::engine::test_engine::{MACHINE, Seeded, TestEngine};
use crate::core::socket::test_wait::until;
use crate::features::board::board_queries::BoardData;

fn run(state: &str) -> Value {
    json!({"entityKind":"run","machine":MACHINE,"id":"r","version":4,"state":state,"activeStepIds":[],"paused":false,"reference":false,"startedMs":0,"ownerSessionId":"s","ownerEpoch":3})
}

fn step(id: &str, state: &str, recovery: &[&str]) -> Value {
    json!({"entityKind":"step","machine":MACHINE,"id":id,"version":1,"runId":"r","name":id,"kind":"agent","state":state,
        "attempt":1,"maxAttempts":3,"required":1,"ruleSkippable":0,"labelPresent":format!("Doing {id}"),"labelPast":format!("Did {id}"),
        "badges":[],"scopeGranted":false,"closed":false,"dependencies":[],"needsMeIds":[],"recovery":recovery,"detail":"More to read"})
}

struct Holder {
    card: Entity<RunCard>,
}

impl Render for Holder {
    fn render(&mut self, _: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        let data = BoardData::of(&[], &[], &[], &[]);
        self.card.update(cx, |card, cx| card.draw(&data, cx))
    }
}

struct Card<'a> {
    engine: TestEngine,
    card: Entity<RunCard>,
    selected: Rc<RefCell<Vec<EngineKey>>>,
    cx: &'a mut VisualTestContext,
    _watching: [gpui_kit::Subscription; 2],
}

async fn open<'a>(run: Value, steps: Vec<Value>, cx: &'a mut TestAppContext) -> Card<'a> {
    let seeded = Seeded {
        items: vec![run.clone()],
        run: json!({"run":run,"steps":steps,"next":null}),
        ..Seeded::default()
    };
    opened(seeded, &[("runs", Some("r"))], "the run's steps", cx).await
}

/// The card once its read has settled, one way or the other.
async fn opened<'a>(
    seeded: Seeded,
    scopes: &[(&str, Option<&str>)],
    settled: &'static str,
    cx: &'a mut TestAppContext,
) -> Card<'a> {
    let mut engine = TestEngine::start(seeded, scopes, cx).await;
    let key = EngineKey {
        machine: MACHINE.into(),
        kind: "run".into(),
        id: "r".into(),
    };
    let card = cx.new(|cx| RunCard::new(key, cx));
    let selected = Rc::<RefCell<Vec<EngineKey>>>::default();
    let held = selected.clone();
    let seen = cx.update(|cx| {
        cx.subscribe(&card, move |_, event: &RunCardEvent, _| {
            let RunCardEvent::Select(key) = event;
            held.borrow_mut().push(key.clone());
        })
    });
    let wake = engine.changed.clone();
    let observed = cx.update(|cx| {
        cx.observe(&card, move |_, _| {
            let _ = wake.unbounded_send(());
        })
    });
    let held = card.clone();
    let (_, visual) = cx.add_window_view(move |_, _| Holder { card: held });
    visual.simulate_resize(gpui_kit::size(gpui_kit::px(900.), gpui_kit::px(1200.)));
    let changed = engine.changed.clone();
    draw(visual);
    until(settled, &mut engine.changes, &changed, || {
        draw(visual);
        visual.read(|cx| {
            let reader = card.read(cx).reader.read(cx);
            reader.view().is_some()
                || reader
                    .error()
                    .is_some_and(|words| !words.starts_with("Loading"))
        })
    })
    .await;
    draw(visual);
    Card {
        engine,
        card,
        selected,
        cx: visual,
        _watching: [seen, observed],
    }
}

fn draw(cx: &mut VisualTestContext) {
    cx.run_until_parked();
    cx.update(|window, cx| window.draw(cx).clear(cx));
}

impl Card<'_> {
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

    async fn sent(&mut self, count: usize) -> Vec<Value> {
        let changed = self.engine.changed.clone();
        let state = self.engine.state.clone();
        let card = self.card.clone();
        let cx = &mut *self.cx;
        until(
            "the command's answer",
            &mut self.engine.changes,
            &changed,
            || {
                draw(cx);
                state.lock().unwrap().commands.len() >= count
                    && cx.read(|cx| card.read(cx).action.is_none())
            },
        )
        .await;
        draw(self.cx);
        self.engine.commands()
    }
}

#[gpui::test]
async fn retry_on_a_stuck_row_sends_the_engines_command_and_a_row_without_the_word_has_no_button(
    cx: &mut TestAppContext,
) {
    let mut card = open(
        run("running"),
        vec![
            step("done", "succeeded", &[]),
            step("stuck", "waiting", &["retry"]),
        ],
        cx,
    )
    .await;
    assert!(!card.shows("step-action:r:done:Retry"));
    assert!(card.shows("step-action:r:stuck:Retry"));
    card.click("step-action:r:stuck:Retry");
    let commands = card.sent(1).await;
    assert_eq!(commands[0]["entityId"], "r");
    assert_eq!(commands[0]["expectedVersion"], 4);
    assert_eq!(
        commands[0]["command"],
        json!({"kind":"retry_workflow_step","stepId":"stuck"})
    );
    let open = card
        .cx
        .read(|cx| card.card.read(cx).steps.read(cx).is_open(&"r:stuck".into()));
    assert!(!open, "the button doesn't also open the row");
}

#[gpui::test]
async fn skip_and_continue_send_the_engines_decision(cx: &mut TestAppContext) {
    let mut card = open(
        run("running"),
        vec![
            step("later", "pending", &["skip"]),
            step("granted", "waiting", &["continue"]),
        ],
        cx,
    )
    .await;
    card.click("step-action:r:later:Skip");
    assert_eq!(
        card.sent(1).await[0]["command"],
        json!({"kind":"decide_workflow_step","stepId":"later","decision":"skip"})
    );
    card.click("step-action:r:granted:Continue");
    assert_eq!(
        card.sent(2).await[1]["command"],
        json!({"kind":"decide_workflow_step","stepId":"granted","decision":"continue"})
    );
}

#[gpui::test]
async fn a_refused_retry_says_why_and_keeps_the_button(cx: &mut TestAppContext) {
    let mut card = open(
        run("running"),
        vec![step("stuck", "failed", &["retry"])],
        cx,
    )
    .await;
    card.engine.state.lock().unwrap().refusal = Some("executor_quiescence_required");
    card.click("step-action:r:stuck:Retry");
    card.sent(1).await;
    let line = card.cx.read(|cx| card.card.read(cx).refusal.clone());
    assert_eq!(
        line.as_deref(),
        Some("The step is still stopping. Try again in a moment.")
    );
    assert!(card.shows("step-action:r:stuck:Retry"));
}

#[gpui::test]
async fn more_after_these_expands_on_click(cx: &mut TestAppContext) {
    let steps = (0..7)
        .map(|index| {
            let id = format!("s{index}");
            step(&id, "pending", if index == 6 { &["skip"] } else { &[] })
        })
        .collect();
    let mut card = open(run("running"), steps, cx).await;
    assert!(card.shows("run-more"));
    assert!(
        !card.shows("step-action:r:s6:Skip"),
        "folded under the button"
    );
    card.click("run-more");
    assert!(card.shows("step-action:r:s6:Skip"));
    assert!(!card.shows("run-more"));
}

#[gpui::test]
async fn rerun_selects_the_new_run_the_engine_applied_and_only_that(cx: &mut TestAppContext) {
    let mut card = open(run("succeeded"), vec![step("a", "succeeded", &[])], cx).await;
    card.engine.state.lock().unwrap().refusal = Some("invalid_transition");
    card.click("run-action:r:Rerun");
    card.sent(1).await;
    assert!(
        card.selected.borrow().is_empty(),
        "a refusal selects nothing"
    );
    card.click("run-action:r:Rerun");
    let commands = card.sent(2).await;
    assert_eq!(commands[1]["command"]["kind"], "rerun_workflow");
    assert_eq!(commands[1]["command"]["sourceRunId"], "r");
    assert_eq!(commands[1]["expectedVersion"], 0);
    assert_ne!(
        commands[0]["entityId"], commands[1]["entityId"],
        "a retry is its own new run"
    );
    let selected = card.selected.borrow().clone();
    assert_eq!(selected.len(), 1);
    assert_eq!(json!(selected[0].id), commands[1]["entityId"]);
}

#[gpui::test]
async fn undo_sends_keeps_own_command_id_and_ends_the_toast(cx: &mut TestAppContext) {
    let steps = vec![step("a", "succeeded", &[]), step("b", "failed", &[])];
    let mut card = open(run("failed"), steps, cx).await;
    card.click("run-action:r:Keep what succeeded");
    let keep = card.sent(1).await[0].clone();
    assert_eq!(keep["command"]["kind"], "keep_succeeded");
    let toast = card.cx.read(|cx| {
        card.card
            .read(cx)
            .toast
            .as_ref()
            .map(|toast| toast.words.clone())
    });
    assert_eq!(
        toast.as_deref(),
        Some("Kept what succeeded · 1 finished items kept, 1 closed")
    );
    card.click("run-action:r:Undo");
    let undo = card.sent(2).await[1].clone();
    assert_eq!(undo["command"]["kind"], "undo_keep_succeeded");
    assert_eq!(undo["command"]["token"], keep["commandId"]);
    card.cx.read(|cx| {
        assert!(card.card.read(cx).toast.is_none());
        assert!(card.card.read(cx).undo.is_none());
    });
}

#[gpui::test]
async fn keeps_undo_ends_with_its_toast_after_five_seconds(cx: &mut TestAppContext) {
    let steps = vec![step("a", "succeeded", &[]), step("b", "failed", &[])];
    let executor = cx.executor();
    let mut card = open(run("failed"), steps, cx).await;
    card.click("run-action:r:Keep what succeeded");
    card.sent(1).await;
    executor.advance_clock(super::TOAST);
    draw(card.cx);
    card.cx.read(|cx| {
        assert!(card.card.read(cx).toast.is_none());
        assert!(card.card.read(cx).undo.is_none());
    });
}

/// The menu by keyboard: its items carry no selectors of their own. `presses` moves down from the first.
fn pick(card: &mut Card<'_>, presses: usize) {
    card.click("run-menu");
    let keys = format!("{}enter", "down ".repeat(presses + 1));
    card.cx.simulate_keystrokes(&keys);
    draw(card.cx);
}

#[gpui::test]
async fn menu_has_open_copy_stop(cx: &mut TestAppContext) {
    let mut card = open(run("running"), vec![step("a", "running", &[])], cx).await;
    pick(&mut card, 0);
    assert_eq!(
        card.selected
            .borrow()
            .iter()
            .map(|key| key.id.as_str())
            .collect::<Vec<_>>(),
        ["r"],
        "Open in Workflows shows this run"
    );
    pick(&mut card, 1);
    let copied = card.cx.read_from_clipboard().and_then(|item| item.text());
    assert_eq!(copied.as_deref(), Some("r"), "Copy run id");
    pick(&mut card, 2);
    assert_eq!(
        card.sent(1).await[0]["command"]["kind"],
        "cancel_run",
        "Stop"
    );
}

#[gpui::test]
async fn an_offline_machine_says_since_when_and_its_rows_controls_send_nothing(
    cx: &mut TestAppContext,
) {
    use crate::core::state::machines::Machines;
    use crate::features::board::board_sections::{group_heading, offline_reason, offline_since};
    use chrono::{Local, TimeZone};
    let since = Local
        .with_ymd_and_hms(2026, 11, 4, 14, 2, 0)
        .unwrap()
        .timestamp_millis();
    let seeded_machine = json!({"name":"Friendly","kind":"remote","reachable":false,"reason":"down","checked":true,"offlineSinceMs":since});
    let mut card = open(
        run("running"),
        vec![step("stuck", "failed", &["retry"])],
        cx,
    )
    .await;
    card.engine.state.lock().unwrap().machines = vec![seeded_machine];
    card.cx.update(|_, cx| {
        Machines::start(cx);
        let feed = card.engine.feed.clone();
        feed.update(cx, |feed, _| {
            feed.name_machine(MACHINE.into(), Some("Friendly".into()))
        });
    });
    let (changed, feed) = (card.engine.changed.clone(), card.engine.feed.clone());
    let cx = &mut *card.cx;
    let _woken = cx.update(|_, cx| {
        let machines = Machines::try_global(cx).unwrap();
        let wake = changed.clone();
        cx.observe(&machines, move |_, _| {
            let _ = wake.unbounded_send(());
        })
    });
    until(
        "the machines list",
        &mut card.engine.changes,
        &changed,
        || cx.read(|cx| offline_reason(MACHINE, feed.read(cx), cx).is_some()),
    )
    .await;
    let line = cx.read(|cx| {
        let feed = feed.read(cx);
        let since = offline_since(MACHINE, feed, cx);
        group_heading("Workbox Test", true, since, since.unwrap() - 1)
    });
    assert_eq!(line, "Workbox Test · offline since 14:02");
    card.click("step-action:r:stuck:Retry");
    // A command would have reached the same socket before this answers.
    card.engine.barrier(card.cx).await;
    assert!(card.engine.commands().is_empty());
    assert!(card.cx.read(|cx| card.card.read(cx).action.is_none()));
}

fn last_known(refused: (&'static str, &'static str)) -> Seeded {
    Seeded {
        items: vec![
            run("running"),
            step("second", "pending", &[]),
            step("first", "succeeded", &[]),
        ],
        run_refused: Some(refused),
        ..Seeded::default()
    }
}

#[gpui::test]
async fn a_machine_that_cant_be_reached_shows_its_last_known_steps_and_no_error(
    cx: &mut TestAppContext,
) {
    let seeded = last_known(("unreachable", "Workbox Test is reconnecting"));
    let scopes = [("runs", None), ("runs", Some("r"))];
    let card = opened(seeded, &scopes, "the last known steps", cx).await;
    card.cx.read(|cx| {
        let reader = card.card.read(cx).reader.read(cx);
        assert!(reader.is_last_known());
        assert_eq!(reader.error(), None);
        assert_eq!(reader.view().unwrap().steps.len(), 2);
    });
}

#[gpui::test]
async fn a_machine_that_really_differs_is_the_one_case_that_says_so(cx: &mut TestAppContext) {
    let words = "Workbox Test is now a different machine from the one this belongs to";
    let seeded = last_known(("engine_differs", words));
    let scopes = [("runs", None), ("runs", Some("r"))];
    let card = opened(seeded, &scopes, "the machine's words", cx).await;
    card.cx.read(|cx| {
        let reader = card.card.read(cx).reader.read(cx);
        assert!(!reader.is_last_known());
        assert_eq!(reader.error(), Some(words));
    });
}
