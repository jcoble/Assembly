//! `SessionView`'s edge cases: a row it couldn't read, empty fields, and the newest seq it has applied.

use serde_json::json;

use super::change::Change;
use super::item::ItemKind;
use super::session_view::SessionView;
use super::test_rows::{all, keys, live, newest_form, row};

// Pins: g0-session#3
#[test]
fn a_readable_row_after_an_unreadable_one_draws_the_item_in_its_place() {
    let mut view = live(
        SessionView::default(),
        &[row(1, "r", Some("t"), "tool", json!({ "name": 3 }))],
    );
    let good = row(
        2,
        "r",
        Some("t"),
        "tool",
        json!({ "name": "Bash", "state": "completed", "hasDetail": false }),
    );
    assert_eq!(view.apply(&good), Change::Updated(0));
    assert_eq!(keys(&view), ["tool:t"]);
    assert!(matches!(&view.items[0].kind, ItemKind::Tool { tool, .. } if tool.name == "Bash"));
}

#[test]
fn an_empty_reason_adds_no_note() {
    let view = live(
        SessionView::default(),
        &[row(
            1,
            "r",
            None,
            "turn",
            json!({ "state": "started", "reason": "" }),
        )],
    );
    assert!(view.items.is_empty());
}

// Pins: g0-session#1
#[test]
fn the_newest_seq_is_the_highest_applied_live_or_paged() {
    let all = all();
    let mut view = SessionView::default();
    view.prepend_page(
        newest_form(
            &all.iter()
                .filter(|r| r.seq <= 15)
                .cloned()
                .collect::<Vec<_>>(),
        ),
        false,
    );
    assert_eq!(view.newest, 15);
    let view = live(
        view,
        &all.iter()
            .filter(|r| r.seq > 15)
            .cloned()
            .collect::<Vec<_>>(),
    );
    assert_eq!(view.newest, all.iter().map(|r| r.seq).max().unwrap());
}
