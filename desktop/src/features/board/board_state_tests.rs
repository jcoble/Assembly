//! The Board's reads and answers (board#3, board#5, board#8): stale pages never land, a change reads fresh SQL order
//! without the old continuation, answers carry the shown question's identity, and a closed Board holds nothing.

use std::sync::{Arc, Mutex};

use futures_channel::mpsc::unbounded;
use gpui::TestAppContext;
use gpui_kit::AppContext as _;
use serde_json::{Value, json};

use super::super::board_pane::BoardPane;
use super::super::board_payload::{
    BoardAnswerResult, BoardAnswerValue, BoardChange, BoardCounts, BoardEvent, BoardMetrics,
    BoardPageResult, BoardQuestion, BoardSummary, BoardTask, BoardTaskArgs, BoardTaskResult,
};
use super::{BoardState, question_id};
use crate::core::socket::client::SocketClient;
use crate::core::socket::endpoint::Endpoint;
use crate::core::socket::fake_backend::{FakeBackend, Reply, hello};
use crate::core::socket::test_wait::until;
use crate::core::socket::update::Update;

pub(super) fn task(key: &str, state: &str) -> Value {
    json!({"machineId": "local", "taskKey": key, "creatorSessionId": "s1", "project": "Assembly",
        "title": format!("Task {key}"), "summary": "Working", "state": state, "taskUrl": null, "prUrl": null,
        "startedAt": 1000, "endedAt": null, "updatedAt": 2000, "revision": 1})
}

fn summary(key: &str, state: &str) -> Value {
    json!({"task": task(key, state), "attentionGroup": 1, "questionCount": 0,
        "metrics": {"stepsDone": null, "stepsFailed": null, "stepsSkipped": null, "files": null, "tools": null}})
}

fn page_json(keys: &[String], next: Option<&str>) -> Value {
    let items: Vec<Value> = keys.iter().map(|key| summary(key, "running")).collect();
    json!({"items": items, "counts": {"total": 90, "running": 90, "needsYou": 0, "terminal": 0},
        "nextCursor": next, "watermarks": [{"machineId": "local", "seq": 4}]})
}

pub(super) fn page(keys: &[&str], next: Option<&str>) -> BoardPageResult {
    let keys: Vec<String> = keys.iter().map(|key| key.to_string()).collect();
    serde_json::from_value(page_json(&keys, next)).expect("a page")
}

fn named(prefix: &str, range: std::ops::Range<usize>) -> Vec<String> {
    range.map(|n| format!("{prefix}{n}")).collect()
}

fn pages(keys: &[String], next: Option<&str>) -> BoardPageResult {
    serde_json::from_value(page_json(keys, next)).expect("a page")
}

fn keys(state: &BoardState) -> Vec<String> {
    state
        .items
        .iter()
        .map(|item| item.task.task_key.clone())
        .collect()
}

pub(super) fn question(machine: &str, generation: i32, answer: Value) -> Value {
    json!({"machineId": machine, "taskKey": "t1", "questionKey": "q1", "generation": generation, "kind": "text",
        "prompt": "Merge now?", "recommendation": "Merge now", "askedAt": 3000, "answer": answer,
        "answeredAt": null, "answerKey": null, "deliveryClientId": null, "deliveryTurnId": null,
        "deliveryState": "none", "acceptedRevision": null})
}

/// `task(key, state)` as the window reads it.
fn read_task(key: &str, state: &str) -> BoardTask {
    BoardTask {
        machine_id: "local".into(),
        task_key: key.into(),
        creator_session_id: "s1".into(),
        project: "Assembly".into(),
        title: format!("Task {key}"),
        summary: "Working".into(),
        state: state.into(),
        task_url: None,
        pr_url: None,
        started_at: 1000,
        ended_at: None,
    }
}

const NO_METRICS: &str = r#"{"stepsDone": null, "stepsFailed": null, "stepsSkipped": null, "files": null, "tools": null}"#;

/// Task t1's detail, with one open question at `generation` and nothing more to page.
fn detail(generation: i32) -> BoardTaskResult {
    serde_json::from_value(json!({"task": task("t1", "running"),
        "questions": [question("local", generation, Value::Null)], "feed": [],
        "metrics": serde_json::from_str::<Value>(NO_METRICS).unwrap(), "nextBeforeSeq": null, "nextQuestionCursor": null}))
    .expect("a detail")
}

fn text(text: &str) -> BoardAnswerValue {
    BoardAnswerValue {
        kind: "text".into(),
        text: Some(text.into()),
    }
}

// Pins: board#5
#[test]
fn late_page_cannot_replace_new_filter() {
    let mut state = BoardState::default();
    let running = state.set_filter(Some("running".into()));
    let failed = state.set_filter(Some("failed".into()));
    assert_eq!(failed.args.state.as_deref(), Some("failed"));
    state.apply_page(running.epoch, page(&["late"], None));
    assert!(
        state.items.is_empty(),
        "the running filter's late page landed"
    );
    state.apply_page(failed.epoch, page(&["new"], None));
    assert_eq!(keys(&state), ["new"]);
}

// Pins: board#5
#[test]
fn delta_discards_continuation() {
    let mut state = BoardState::default();
    let first = state.changed().expect("the first read");
    state.apply_page(first.epoch, page(&["a", "b"], Some("old-cursor")));
    assert_eq!(state.next_cursor.as_deref(), Some("old-cursor"));
    let fresh = state.changed().expect("a fresh read");
    assert_eq!(
        fresh.args.cursor, None,
        "a change reused the old continuation"
    );
    let (replaced, next) = state.apply_page(fresh.epoch, page(&["b", "a"], None));
    assert!(replaced && next.is_none());
    assert_eq!(keys(&state), ["b", "a"]);
    assert!(
        state.more().is_none(),
        "the old continuation outlived the change"
    );
}

// A change while 90 cards are held reads until it has 90 again, then swaps them in at once, in the new order; a
// change during that read asks for one more read after it.
// Pins: board#5
#[test]
fn change_keeps_every_held_card_in_the_new_order() {
    let mut state = BoardState::default();
    let first = state.changed().expect("the first read");
    state.apply_page(first.epoch, pages(&named("t", 0..40), Some("c1")));
    let more = state.more().expect("the second page");
    assert_eq!(more.args.cursor.as_deref(), Some("c1"));
    state.apply_page(more.epoch, pages(&named("t", 40..80), Some("c2")));
    let more = state.more().expect("the third page");
    state.apply_page(more.epoch, pages(&named("t", 80..90), None));
    assert_eq!(state.items.len(), 90);

    let fresh = state.changed().expect("a fresh read");
    assert_eq!(fresh.args.cursor, None);
    let newer: Vec<String> = (0..90).rev().map(|n| format!("t{n}")).collect();
    let (replaced, next) = state.apply_page(fresh.epoch, pages(&newer[0..40], Some("n1")));
    let next = next.expect("the read goes on");
    assert!(!replaced);
    assert_eq!(next.args.cursor.as_deref(), Some("n1"));
    assert_eq!(
        state.items[0].task.task_key, "t0",
        "the list changed before the read was done"
    );
    assert!(
        state.changed().is_none(),
        "a second read started during the first"
    );
    let (_, next) = state.apply_page(next.epoch, pages(&newer[40..80], Some("n2")));
    let next = next.expect("the read goes on");
    let (replaced, again) = state.apply_page(next.epoch, pages(&newer[80..90], None));
    assert!(replaced);
    assert_eq!(keys(&state), newer);
    assert_eq!(again.expect("one more read").args.cursor, None);
}

// Pins: board#3, board#7
#[test]
fn answer_uses_displayed_generation_and_source() {
    let q: BoardQuestion =
        serde_json::from_value(question("Workbox Test", 3, Value::Null)).unwrap();
    let mut state = BoardState::default();
    let args = state.answer(&q, text("Ship it"));
    uuid::Uuid::parse_str(&args.answer_key).expect("the backend takes only a GUID answer key");
    assert_eq!(
        serde_json::to_value(&args).unwrap(),
        json!({"machineId": "Workbox Test", "taskKey": "t1", "questionKey": "q1", "generation": 3,
            "answerKey": args.answer_key, "answer": {"kind": "text", "text": "Ship it"}})
    );
}

// Pins: board#3
#[test]
fn failed_answer_preserves_draft() {
    let q: BoardQuestion = serde_json::from_value(question("local", 1, Value::Null)).unwrap();
    let mut state = BoardState::default();
    let first = state.answer(&q, text("Wait for review"));
    state.answer_failed(&question_id(&q), "Workbox Test is offline".into());
    let draft = state.drafts.get(&question_id(&q)).expect("the draft");
    assert_eq!(draft.value, Some(text("Wait for review")));
    assert_eq!(draft.error.as_deref(), Some("Workbox Test is offline"));
    assert!(!draft.sending);
    // Sending the same answer again keeps its key, so a first send that did land isn't refused as a second answer.
    assert_eq!(
        state.answer(&q, text("Wait for review")).answer_key,
        first.answer_key
    );
    // A reask is a new generation: the same answer to it is a new attempt, so the old key can't conflict with it.
    let reasked: BoardQuestion = serde_json::from_value(question("local", 2, Value::Null)).unwrap();
    state.answer_failed(&question_id(&q), "Workbox Test is offline".into());
    let again = state.answer(&reasked, text("Wait for review"));
    assert_eq!(again.generation, 2);
    assert_ne!(again.answer_key, first.answer_key);
    uuid::Uuid::parse_str(&again.answer_key).expect("a GUID");
}

// A question answered or asked again while the socket was down: the topic's state after the reconnect reads the
// expanded card's first detail page again, so its old generation isn't left to answer.
// Pins: board#8
#[test]
fn reconnect_rereads_the_expanded_card() {
    let mut state = BoardState::default();
    let read = state.select("local", "t1").expect("the detail read");
    state.apply_detail(&read, detail(1));
    let (page, again) = state.heard(Update::State(Vec::new()));
    assert!(page.is_some(), "the cards weren't read again");
    let again = again.expect("the expanded card's detail read again");
    assert_eq!(again, read);
    state.apply_detail(&again, detail(2));
    assert_eq!(state.detail.as_ref().unwrap().questions[0].generation, 2);
}

// Every field the window keeps, filled and null, compared whole, so a missing, misnamed or mistyped one fails.
// Pins: board#5
#[test]
fn payload_decodes_empty_and_populated_results() {
    let counts = json!({"total": 3, "running": 1, "needsYou": 0, "terminal": 2});
    let counted = BoardCounts {
        total: 3,
        running: 1,
        needs_you: 0,
        terminal: 2,
    };
    let empty: BoardPageResult = serde_json::from_value(
        json!({"items": [], "counts": counts, "nextCursor": null, "watermarks": []}),
    )
    .expect("an empty page");
    let none = BoardPageResult {
        items: Vec::new(),
        counts: counted.clone(),
        next_cursor: None,
    };
    assert_eq!(empty, none);

    let mut ended = task("t2", "failed");
    (ended["taskUrl"], ended["prUrl"], ended["endedAt"]) =
        (json!("https://tasks/2"), json!("https://pr/2"), json!(9000));
    let mut read_ended = read_task("t2", "failed");
    (read_ended.task_url, read_ended.pr_url, read_ended.ended_at) = (
        Some("https://tasks/2".into()),
        Some("https://pr/2".into()),
        Some(9000),
    );
    let mut metrics: Value = serde_json::from_str(NO_METRICS).unwrap();
    metrics["files"] = json!(4);
    let page: BoardPageResult = serde_json::from_value(json!({"items": [summary("t1", "running"),
        {"task": ended, "attentionGroup": 0, "questionCount": 2, "metrics": metrics}],
        "counts": counts, "nextCursor": "c1", "watermarks": [{"machineId": "local", "seq": 4}]}))
    .expect("a page");
    let items = vec![
        BoardSummary {
            task: read_task("t1", "running"),
            attention_group: 1,
            question_count: 0,
            metrics: BoardMetrics { files: None },
        },
        BoardSummary {
            task: read_ended.clone(),
            attention_group: 0,
            question_count: 2,
            metrics: BoardMetrics { files: Some(4) },
        },
    ];
    let next_cursor = Some("c1".into());
    assert_eq!(
        page,
        BoardPageResult {
            items,
            counts: counted,
            next_cursor
        }
    );

    let mut approved = question("Workbox Test", 2, json!({"kind": "approve", "text": null}));
    (
        approved["kind"],
        approved["recommendation"],
        approved["deliveryState"],
    ) = (json!("approval"), Value::Null, json!("delivered"));
    let read: BoardTaskResult = serde_json::from_value(json!({"task": ended,
        "questions": [question("local", 1, Value::Null), approved],
        "feed": [{"machineId": "local", "seq": 7, "taskKey": "t2", "kind": "task_reported", "occurredAt": 5000,
            "task": ended, "questions": [], "changedPaths": ["a.rs"]}],
        "metrics": metrics, "nextBeforeSeq": 6, "nextQuestionCursor": "q2"}))
    .expect("a populated detail");
    let open = BoardQuestion {
        machine_id: "local".into(),
        task_key: "t1".into(),
        question_key: "q1".into(),
        generation: 1,
        kind: "text".into(),
        prompt: "Merge now?".into(),
        recommendation: Some("Merge now".into()),
        answer: None,
        delivery_state: "none".into(),
    };
    let answered = BoardQuestion {
        machine_id: "Workbox Test".into(),
        generation: 2,
        kind: "approval".into(),
        recommendation: None,
        answer: Some(BoardAnswerValue {
            kind: "approve".into(),
            text: None,
        }),
        delivery_state: "delivered".into(),
        ..open.clone()
    };
    let event = BoardEvent {
        kind: "task_reported".into(),
        occurred_at: 5000,
        task: read_ended,
        questions: vec![],
    };
    let full = BoardTaskResult {
        questions: vec![open, answered],
        feed: vec![event],
        next_before_seq: Some(6),
        next_question_cursor: Some("q2".into()),
    };
    assert_eq!(read, full);
    let bare = detail(1);
    assert_eq!(
        (bare.next_before_seq, bare.next_question_cursor),
        (None, None)
    );

    let change: BoardChange = serde_json::from_value(
        json!({"machineId": "Workbox Test", "taskKey": "t1", "revision": 4, "seq": 9}),
    )
    .expect("a change");
    let changed = BoardChange {
        machine_id: "Workbox Test".into(),
        task_key: "t1".into(),
    };
    assert_eq!(change, changed);
    let _: BoardAnswerResult =
        serde_json::from_value(json!({"machineId": "local", "taskKey": "t1",
        "questionKey": "q1", "generation": 1, "answerKey": "k", "acceptedRevision": 4}))
        .expect("an answer result");
    let args = super::super::board_payload::BoardPageArgs {
        project: None,
        state: None,
        cursor: None,
    };
    assert_eq!(
        serde_json::to_value(args).unwrap(),
        json!({"project": null, "state": null, "cursor": null})
    );
    let args = BoardTaskArgs {
        machine_id: "Workbox Test".into(),
        task_key: "t1".into(),
        before_seq: Some(6),
        question_cursor: None,
    };
    assert_eq!(
        serde_json::to_value(args).unwrap(),
        json!({"machineId": "Workbox Test", "taskKey": "t1", "beforeSeq": 6, "questionCursor": null})
    );
}

/// What the backend saw: each sub's id by topic, the card detail call's id, and each unsub and cancel.
#[derive(Default)]
struct Seen {
    board: Option<Value>,
    clock: Option<Value>,
    detail: Option<Value>,
    failed_filter: bool,
    unsubs: Vec<Value>,
    cancels: Vec<Value>,
}

/// The board topic's state is empty, its first page is one running card, the failed filter's read fails, and the
/// card's detail never answers.
fn backend(seen: Arc<Mutex<Seen>>, heard: impl Fn() + Send + 'static) -> FakeBackend {
    FakeBackend::start(move |frame| {
        if let Some(replies) = hello(frame) {
            return replies;
        }
        let id = frame["id"].clone();
        let mut seen = seen.lock().unwrap();
        let replies = match (
            frame["t"].as_str(),
            frame["topic"].as_str(),
            frame["method"].as_str(),
        ) {
            (Some("sub"), Some("board"), _) => {
                seen.board = Some(id.clone());
                vec![Reply::Frame(json!({"t": "state", "sub": id, "items": []}))]
            }
            (Some("sub"), Some("clock"), _) => {
                seen.clock = Some(id);
                Vec::new()
            }
            (Some("call"), _, Some("page")) if frame["args"]["state"] == "failed" => {
                seen.failed_filter = true;
                vec![Reply::Frame(
                    json!({"t": "error", "id": id, "code": "failed", "message": "down", "machine": null}),
                )]
            }
            (Some("call"), _, Some("page")) => {
                let page = page_json(&["t1".to_string()], None);
                vec![Reply::Frame(
                    json!({"t": "result", "id": id, "result": page}),
                )]
            }
            (Some("call"), _, Some("task")) => {
                seen.detail = Some(id);
                Vec::new()
            }
            (Some("unsub"), _, _) => {
                seen.unsubs.push(id);
                Vec::new()
            }
            (Some("cancel"), _, _) => {
                seen.cancels.push(id);
                Vec::new()
            }
            _ => Vec::new(),
        };
        drop(seen);
        heard();
        replies
    })
}

// Pins: board#8
#[gpui::test]
async fn closing_board_drops_owned_work(cx: &mut TestAppContext) {
    let seen = Arc::new(Mutex::new(Seen::default()));
    let (changed, mut changes) = unbounded();
    let tell = changed.clone();
    let backend = backend(seen.clone(), move || _ = tell.unbounded_send(()));
    cx.executor().allow_parking();
    cx.update(|cx| {
        gpui_kit::init(cx);
        SocketClient::start(
            Endpoint {
                port: backend.port,
                token: "secret".into(),
            },
            cx,
        );
    });
    let pane = cx.new(BoardPane::new);
    until(
        "the board topic and the clock held",
        &mut changes,
        &changed,
        || {
            cx.run_until_parked();
            let seen = seen.lock().unwrap();
            seen.board.is_some() && seen.clock.is_some()
        },
    )
    .await;
    // A filter shows no card until its read lands, so the clock goes at once, even when that read fails.
    pane.update(cx, |pane, cx| pane.filter(Some("failed"), cx));
    until(
        "the clock released by a failed filter read",
        &mut changes,
        &changed,
        || {
            cx.run_until_parked();
            let seen = seen.lock().unwrap();
            seen.failed_filter
                && seen
                    .clock
                    .as_ref()
                    .is_some_and(|id| seen.unsubs.contains(id))
        },
    )
    .await;
    // Back to All: a running card shows again, so a fresh clock is held, and closing must be what releases it.
    let released = seen.lock().unwrap().clock.clone();
    pane.update(cx, |pane, cx| pane.filter(None, cx));
    until("a fresh clock held for All", &mut changes, &changed, || {
        cx.run_until_parked();
        let seen = seen.lock().unwrap();
        seen.clock != released
            && seen
                .clock
                .as_ref()
                .is_some_and(|id| !seen.unsubs.contains(id))
    })
    .await;
    pane.update(cx, |pane, cx| pane.select("local", "t1", cx));
    until(
        "the card's detail read sent",
        &mut changes,
        &changed,
        || {
            cx.run_until_parked();
            seen.lock().unwrap().detail.is_some()
        },
    )
    .await;

    cx.update(|_| drop(pane));
    until(
        "the topic, clock and detail read released",
        &mut changes,
        &changed,
        || {
            cx.run_until_parked();
            let seen = seen.lock().unwrap();
            let unsubbed =
                |id: &Option<Value>| id.as_ref().is_some_and(|id| seen.unsubs.contains(id));
            unsubbed(&seen.board)
                && unsubbed(&seen.clock)
                && seen
                    .detail
                    .as_ref()
                    .is_some_and(|id| seen.cancels.contains(id))
        },
    )
    .await;
    drop(backend);
}
