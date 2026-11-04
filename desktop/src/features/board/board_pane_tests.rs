//! A card grows and shrinks on the shared row motion, holding its own detail all the way shut.
use std::time::Duration;

use gpui::TestAppContext;
use gpui_kit::{Entity, Pixels, VisualTestContext};
use serde_json::json;

use super::BoardPane;
use crate::features::board::board_payload::{BoardPageResult, BoardTaskArgs, BoardTaskResult};

fn page() -> BoardPageResult {
    let card = |key: &str| {
        json!({"task": {"machineId": "local", "taskKey": key, "creatorSessionId": "s1", "project": "Assembly",
            "title": format!("Task {key}"), "summary": "Working", "state": "completed", "taskUrl": null, "prUrl": null,
            "startedAt": 1000, "endedAt": 2000, "updatedAt": 2000, "revision": 1},
            "attentionGroup": 1, "questionCount": 0,
            "metrics": {"stepsDone": null, "stepsFailed": null, "stepsSkipped": null, "files": null, "tools": null}})
    };
    serde_json::from_value(json!({"items": [card("a"), card("b")],
        "counts": {"total": 2, "running": 0, "needsYou": 0, "terminal": 2},
        "nextCursor": null, "watermarks": []}))
    .expect("a page")
}

/// A detail with three answered questions: several times taller than a card still reading.
fn tall_detail(key: &str) -> BoardTaskResult {
    let question = |at: &str| {
        json!({"machineId": "local", "taskKey": key, "questionKey": at, "generation": 1, "kind": "text",
            "prompt": "Merge now?", "recommendation": "Merge now", "askedAt": 3000,
            "answer": {"kind": "text", "text": "Yes"}, "answeredAt": 3100, "answerKey": null,
            "deliveryClientId": null, "deliveryTurnId": null, "deliveryState": "delivered", "acceptedRevision": null})
    };
    serde_json::from_value(
        json!({"task": null, "questions": [question("q1"), question("q2"), question("q3")],
        "feed": [], "nextBeforeSeq": null, "nextQuestionCursor": null}),
    )
    .expect("a detail")
}

fn draw(cx: &mut VisualTestContext) {
    cx.run_until_parked();
    cx.update(|window, cx| window.draw(cx).clear(cx));
    cx.run_until_parked();
}

fn settle(cx: &mut VisualTestContext) {
    for _ in 0..100 {
        cx.executor().advance_clock(Duration::from_millis(16));
        draw(cx);
    }
}

fn height(key: &str, cx: &mut VisualTestContext) -> Pixels {
    cx.debug_bounds(Box::leak(format!("board-card-{key}").into_boxed_str()))
        .expect("the card is drawn")
        .size
        .height
}

fn open_board(cx: &mut TestAppContext) -> (Entity<BoardPane>, &mut VisualTestContext) {
    cx.update(gpui_kit::init);
    let (pane, cx) = cx.add_window_view(|_, cx| BoardPane::new(cx));
    pane.update(cx, |pane, cx| {
        pane.failed = None;
        pane.state.counts = Some(page().counts);
        pane.state.items = page().items;
        pane.list.reset(2);
        cx.notify();
    });
    draw(cx);
    (pane, cx)
}

fn pick(pane: &Entity<BoardPane>, key: &str, cx: &mut VisualTestContext) {
    pane.update(cx, |pane, cx| pane.select("local", key, "s1", cx));
}

fn read_detail(pane: &Entity<BoardPane>, key: &str, cx: &mut VisualTestContext) {
    pane.update(cx, |pane, cx| {
        let args = BoardTaskArgs {
            machine_id: "local".into(),
            task_key: key.into(),
            before_seq: None,
            question_cursor: None,
        };
        pane.state.apply_detail(&args, tall_detail(key));
        cx.notify();
    });
}

#[gpui::test]
fn a_card_grows_over_several_frames_and_folds_back(cx: &mut TestAppContext) {
    let (pane, cx) = open_board(cx);
    let shut = height("a", cx);
    pick(&pane, "a", cx);
    read_detail(&pane, "a", cx);
    draw(cx);
    cx.executor().advance_clock(Duration::from_millis(48));
    draw(cx);
    let moving = height("a", cx);
    settle(cx);
    let open = height("a", cx);
    assert!(
        shut < moving && moving < open,
        "{shut:?} {moving:?} {open:?}"
    );
    pick(&pane, "a", cx);
    settle(cx);
    assert_eq!(height("a", cx), shut);
    pane.read_with(cx, |pane, cx| {
        assert!(
            !pane
                .drops
                .read(cx)
                .is_showing(&super::board_card::card_key("local", "a"))
        );
    });
}

/// By then the Board's current detail is the next card's, still being read.
#[gpui::test]
fn a_card_on_its_way_shut_keeps_its_own_detail(cx: &mut TestAppContext) {
    let (pane, cx) = open_board(cx);
    pick(&pane, "a", cx);
    read_detail(&pane, "a", cx);
    settle(cx);
    let open = height("a", cx);
    pick(&pane, "b", cx);
    draw(cx);
    assert!(
        height("a", cx) * 10. > open * 9.,
        "{:?} of {open:?}",
        height("a", cx)
    );
    settle(cx);
    pane.read_with(cx, |pane, cx| {
        let drops = pane.drops.read(cx);
        assert!(drops.is_open(&super::board_card::card_key("local", "b")));
        assert!(!drops.is_showing(&super::board_card::card_key("local", "a")));
    });
}

#[gpui::test]
fn a_page_without_a_closing_card_releases_it_and_keeps_the_selected_one(cx: &mut TestAppContext) {
    let (pane, cx) = open_board(cx);
    pick(&pane, "a", cx);
    settle(cx);
    pick(&pane, "b", cx);
    pane.update(cx, |pane, cx| {
        pane.state.items.clear();
        pane.release_gone_cards(cx);
        let drops = pane.drops.read(cx);
        assert!(!drops.is_showing(&super::board_card::card_key("local", "a")));
        assert!(drops.is_open(&super::board_card::card_key("local", "b")));
    });
}
