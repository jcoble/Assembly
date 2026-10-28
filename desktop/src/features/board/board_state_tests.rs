//! The Board's reads and answers (board#3, board#5, board#8): stale pages never land, a change reads fresh SQL order
//! without the old continuation, answers carry the shown question's identity, and a closed Board holds nothing.

use std::sync::{Arc, Mutex};

use futures_channel::mpsc::unbounded;
use gpui::TestAppContext;
use gpui_kit::AppContext as _;
use serde_json::{Value, json};

use super::super::board_pane::BoardPane;
use super::super::board_payload::{
    BoardAnswerValue, BoardChange, BoardPageResult, BoardQuestion, BoardTaskResult,
};
use super::{BoardState, question_id};
use crate::core::socket::client::SocketClient;
use crate::core::socket::endpoint::Endpoint;
use crate::core::socket::fake_backend::{FakeBackend, Reply, hello};
use crate::core::socket::test_wait::until;

fn task(key: &str, state: &str) -> Value {
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

fn page(keys: &[&str], next: Option<&str>) -> BoardPageResult {
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

fn question(machine: &str, generation: i32, answer: Value) -> Value {
    json!({"machineId": machine, "taskKey": "t1", "questionKey": "q1", "generation": generation, "kind": "text",
        "prompt": "Merge now?", "recommendation": "Merge now", "askedAt": 3000, "answer": answer,
        "answeredAt": null, "answerKey": null, "deliveryClientId": null, "deliveryTurnId": null,
        "deliveryState": "none", "acceptedRevision": null})
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
    assert!(!args.answer_key.is_empty());
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
}

// Pins: board#5
#[test]
fn payload_decodes_empty_and_populated_results() {
    let empty: BoardPageResult = serde_json::from_value(json!({"items": [],
        "counts": {"total": 3, "running": 1, "needsYou": 0, "terminal": 2}, "nextCursor": null, "watermarks": []}))
    .expect("an empty page");
    assert!(empty.items.is_empty() && empty.next_cursor.is_none());
    assert_eq!((empty.counts.total, empty.counts.terminal), (3, 2));

    let answered = question("local", 2, json!({"kind": "text", "text": "Merge now"}));
    let detail: BoardTaskResult = serde_json::from_value(json!({"task": task("t1", "failed"),
        "questions": [question("local", 1, Value::Null), answered],
        "feed": [{"machineId": "local", "seq": 7, "taskKey": "t1", "kind": "task_reported", "occurredAt": 5000,
            "task": task("t1", "failed"), "questions": [], "changedPaths": ["a.rs", "b.rs"]},
            {"machineId": "local", "seq": 6, "taskKey": "t1", "kind": "question_asked", "occurredAt": 4000,
            "task": task("t1", "running"), "questions": [], "changedPaths": null}],
        "metrics": {"stepsDone": null, "stepsFailed": null, "stepsSkipped": null, "files": 2, "tools": null},
        "nextBeforeSeq": 6, "nextQuestionCursor": null}))
    .expect("a populated detail");
    assert_eq!(detail.feed[0].task.state, "failed");
    assert!(detail.feed[0].task.ended_at.is_none() && detail.feed[0].task.pr_url.is_none());
    assert!(detail.questions[0].answer.is_none());
    assert_eq!(detail.questions[1].answer, Some(text("Merge now")));
    assert_eq!(
        detail.feed[0].changed_paths.as_deref().map(<[String]>::len),
        Some(2)
    );
    assert!(detail.feed[1].changed_paths.is_none());
    assert_eq!(
        (detail.next_before_seq, detail.next_question_cursor),
        (Some(6), None)
    );

    let change: BoardChange = serde_json::from_value(
        json!({"machineId": "Workbox Test", "taskKey": "t1", "revision": 4, "seq": 9}),
    )
    .expect("a change");
    assert_eq!(
        (change.machine_id.as_str(), change.task_key.as_str()),
        ("Workbox Test", "t1")
    );
    let args = super::super::board_payload::BoardPageArgs {
        project: None,
        state: None,
        cursor: None,
    };
    assert_eq!(
        serde_json::to_value(args).unwrap(),
        json!({"project": null, "state": null, "cursor": null})
    );
}

/// What the backend saw: each sub's id by topic, the card detail call's id, and each unsub and cancel.
#[derive(Default)]
struct Seen {
    board: Option<Value>,
    clock: Option<Value>,
    detail: Option<Value>,
    unsubs: Vec<Value>,
    cancels: Vec<Value>,
}

/// The board topic's state is empty, its first page is one running card, and the card's detail never answers.
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
