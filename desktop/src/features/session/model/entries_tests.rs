//! The timeline's rows: fold headings in place of hidden work, the loading and working lines, and a cancelled run's
//! error left out. Ported from session/entries.ts.

use std::collections::{HashMap, HashSet};

use super::entries::Entry;
use super::fold::Fold;
use super::item::{Item, ItemKind};

fn item(key: &str, kind: ItemKind) -> Item {
    Item {
        key: key.into(),
        run_id: Some("r1".into()),
        kind,
    }
}

fn note(key: &str) -> Item {
    item(key, ItemKind::Note { text: key.into() })
}

fn items() -> Vec<Item> {
    vec![
        item(
            "u",
            ItemKind::User {
                text: "go".into(),
                at: 1,
                from: None,
            },
        ),
        item(
            "th",
            ItemKind::Thinking {
                text: "hm".into(),
                done: true,
            },
        ),
        note("before"),
        item(
            "reply",
            ItemKind::Message {
                blocks: vec![],
                done: true,
                at: 1,
            },
        ),
    ]
}

fn folds(work: &[&str]) -> HashMap<String, Fold> {
    let fold = Fold {
        run_id: "r1".into(),
        work: work.iter().map(|key| key.to_string()).collect(),
        reply: Some("reply".into()),
        summary: "Thought".into(),
        duration_ms: None,
    };
    HashMap::from([("r1".into(), fold)])
}

fn keys(items: &[Item], entries: &[Entry]) -> Vec<String> {
    entries.iter().map(|entry| entry.key(items)).collect()
}

// Pins: g0-session#9
#[test]
fn a_closed_fold_shows_one_heading_where_its_first_hidden_item_was() {
    let items = items();
    let entries = Entry::all(
        &items,
        &folds(&["th", "before"]),
        &HashSet::new(),
        false,
        false,
    );
    assert_eq!(keys(&items, &entries), ["u", "fold:r1", "reply"]);
    assert_eq!(
        entries[1],
        Entry::FoldHeading {
            run_id: "r1".into(),
            first: 1,
            ends_reply: false
        }
    );
}

// Pins: g0-session#9
#[test]
fn an_open_fold_keeps_its_heading_and_shows_its_work_under_it() {
    let items = items();
    let open = HashSet::from(["r1".to_string()]);
    let entries = Entry::all(&items, &folds(&["th", "before"]), &open, false, false);
    assert_eq!(
        keys(&items, &entries),
        ["u", "fold:r1", "th", "before", "reply"]
    );
}

#[test]
fn a_closed_fold_that_hides_the_end_of_a_reply_says_so() {
    let mut items = items();
    items.pop();
    let entries = Entry::all(
        &items,
        &folds(&["th", "before"]),
        &HashSet::new(),
        false,
        false,
    );
    assert_eq!(
        entries[1],
        Entry::FoldHeading {
            run_id: "r1".into(),
            first: 1,
            ends_reply: true
        }
    );
}

// Pins: g0-session#2, g0-session#12
#[test]
fn the_loading_line_goes_first_and_the_working_line_last() {
    let items = items();
    let entries = Entry::all(&items, &HashMap::new(), &HashSet::new(), true, true);
    assert_eq!(
        keys(&items, &entries),
        ["line:loading", "u", "th", "before", "reply", "line:working"]
    );
}

// Pins: g0-session#19
#[test]
fn a_cancelled_runs_error_is_left_out_unless_it_ends_a_reply() {
    let cancelled = |key: &str| {
        item(
            key,
            ItemKind::Error {
                message: "cancelled".into(),
                code: Some("cancelled".into()),
            },
        )
    };
    let items = vec![note("a"), cancelled("e1"), note("b"), cancelled("e2")];
    let entries = Entry::all(&items, &HashMap::new(), &HashSet::new(), false, false);
    assert_eq!(keys(&items, &entries), ["a", "b", "e2"]);
}
