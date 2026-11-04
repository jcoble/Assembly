use super::{Facts, Lock, Look, Slot, hint, look, pending_answer, slot};
use crate::features::session::model::item::{Item, ItemKind};
use gpui_kit::px;

fn wide() -> Facts {
    Facts {
        width: px(900.),
        ..Facts::default()
    }
}

#[test]
fn an_empty_box_at_rest_is_one_line_when_the_pane_is_wide_enough() {
    assert_eq!(
        look(wide()),
        Look {
            relaxed: false,
            one_line: true,
            compact: false
        }
    );
    let narrow = look(Facts {
        width: px(759.),
        ..Facts::default()
    });
    assert!(!narrow.relaxed && !narrow.one_line);
}

#[test]
fn content_a_new_turn_a_pending_answer_and_a_drag_each_open_it() {
    for facts in [
        Facts {
            content: true,
            ..wide()
        },
        Facts {
            sending: true,
            ..wide()
        },
        Facts {
            locked: true,
            ..wide()
        },
        Facts {
            dragging: true,
            ..wide()
        },
    ] {
        let shape = look(facts);
        assert!(shape.relaxed && !shape.one_line, "{facts:?}");
    }
}

#[test]
fn a_turn_past_its_five_seconds_lets_an_empty_box_go_back_to_its_line() {
    let settled = look(Facts {
        sending: true,
        settled: true,
        ..wide()
    });
    assert!(!settled.relaxed && settled.one_line);
}

#[test]
fn clearing_the_text_by_hand_keeps_the_box_open_until_something_is_sent() {
    assert!(
        look(Facts {
            opened: true,
            ..wide()
        })
        .relaxed
    );
}

#[test]
fn the_pills_collapse_only_when_they_stop_fitting_and_not_before_the_pane_is_measured() {
    assert!(
        look(Facts {
            width: px(440.),
            ..Facts::default()
        })
        .compact
    );
    assert!(
        !look(Facts {
            width: px(441.),
            ..Facts::default()
        })
        .compact
    );
    assert!(!look(Facts::default()).compact);
}

#[test]
fn one_slot_shows_send_or_stop_from_the_turn_the_draft_and_what_the_agent_can_do() {
    assert_eq!(slot(false, false, false), Slot::Send { steer: false });
    assert_eq!(slot(false, true, false), Slot::Send { steer: false });
    assert_eq!(slot(true, false, true), Slot::Stop);
    assert_eq!(slot(true, true, false), Slot::Stop);
    assert_eq!(slot(true, true, true), Slot::Send { steer: true });
}

#[test]
fn the_hint_names_the_agent_unless_something_is_waiting_for_an_answer() {
    assert_eq!(hint(Some("Claude"), None), "Message Claude");
    assert_eq!(hint(None, None), "Message");
    assert_eq!(
        hint(Some("Claude"), Some(Lock::Approval)),
        "Resolve the approval above to continue"
    );
    assert_eq!(
        hint(None, Some(Lock::Input)),
        "Complete the requested input above"
    );
}

fn ask(kind: &str, state: &str) -> Item {
    Item {
        key: format!("ask-{kind}-{state}"),
        run_id: None,
        kind: ItemKind::Ask {
            ask_id: "1".into(),
            ask: serde_json::from_value(serde_json::json!({ "kind": kind, "state": state }))
                .unwrap(),
        },
        at: 0,
    }
}

#[test]
fn an_open_ask_holds_the_draft_and_an_answered_one_lets_it_go() {
    assert_eq!(pending_answer(&[]), None);
    assert_eq!(pending_answer(&[ask("permission", "answered")]), None);
    assert_eq!(pending_answer(&[ask("question", "cancelled")]), None);
    assert_eq!(
        pending_answer(&[ask("permission", "open")]),
        Some(Lock::Approval)
    );
    assert_eq!(
        pending_answer(&[ask("question", "open")]),
        Some(Lock::Input)
    );
    assert_eq!(
        pending_answer(&[ask("permission", "answered"), ask("question", "open")]),
        Some(Lock::Input)
    );
}
