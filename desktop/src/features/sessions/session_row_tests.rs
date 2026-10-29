use super::{details, status_words};
use crate::features::sessions::presence::Presence;
use crate::features::sessions::rail_page::{RailEntry, test_entry};

fn entry() -> RailEntry {
    test_entry("s1", "/r")
}

// Pins: g0-session#30
#[test]
fn a_row_shows_needs_you_first_then_working_failed_done_and_unread() {
    let all = RailEntry {
        needs_you: true,
        running: true,
        failed: true,
        completed_at: Some(5),
        unread: true,
        ..entry()
    };
    assert_eq!(Presence::of(&all), Presence::NeedsYou);
    let all = RailEntry {
        needs_you: false,
        ..all
    };
    assert_eq!(Presence::of(&all), Presence::Working);
    let all = RailEntry {
        running: false,
        ..all
    };
    assert_eq!(Presence::of(&all), Presence::Failed);
    let all = RailEntry {
        failed: false,
        ..all
    };
    assert_eq!(Presence::of(&all), Presence::Done);
    let all = RailEntry {
        completed_at: None,
        ..all
    };
    assert_eq!(Presence::of(&all), Presence::Unread);
    assert_eq!(Presence::of(&entry()), Presence::Idle);
    assert_eq!(Presence::NeedsYou.word(), Some("Needs you"));
    assert_eq!(Presence::Unread.word(), None);
}

// Pins: sessions-list#11
#[test]
fn a_session_on_a_machine_that_isnt_answering_keeps_its_state_and_says_offline() {
    let running = RailEntry {
        running: true,
        machine: Some("Workbox Test".into()),
        branch: Some("lane/rail".into()),
        ..entry()
    };
    assert_eq!(Presence::of(&running), Presence::Working);
    assert_eq!(
        status_words(Presence::of(&running), true),
        ["offline", "Working"]
    );
    assert_eq!(details(&running), "Workbox Test · lane/rail");
    // A local session is on This Mac.
    let local = RailEntry {
        machine: None,
        ..running
    };
    assert_eq!(details(&local), "This Mac · lane/rail");
}

#[test]
fn status_words_name_each_state_in_order() {
    let words = |presence| status_words(presence, false);
    assert_eq!(words(Presence::Working), ["Working"]);
    assert_eq!(words(Presence::NeedsYou), ["Needs you"]);
    assert_eq!(words(Presence::Done), ["Done"]);
    assert_eq!(words(Presence::Failed), ["Failed"]);
    assert_eq!(words(Presence::Unread), ["Idle"]);
    assert_eq!(words(Presence::Idle), ["Idle"]);
    assert_eq!(status_words(Presence::Idle, true), ["offline", "Idle"]);
}

#[test]
fn a_finished_row_that_isnt_marked_still_names_its_state() {
    // Its turn ended and the user hasn't marked it Done: it isn't working, waiting or failed, so it's Idle, not blank.
    let finished = RailEntry {
        machine: Some("workbox-test".into()),
        ..entry()
    };
    assert_eq!(Presence::of(&finished), Presence::Idle);
    assert_eq!(status_words(Presence::of(&finished), false), ["Idle"]);
    let unread = RailEntry {
        unread: true,
        ..finished
    };
    assert_eq!(status_words(Presence::of(&unread), false), ["Idle"]);
}
