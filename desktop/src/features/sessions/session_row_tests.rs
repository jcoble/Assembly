use super::{helper_word, subtitle};
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
        helpers: 3,
        ..entry()
    };
    assert_eq!(Presence::of(&running), Presence::Working);
    assert_eq!(
        subtitle(&running, None, true, false),
        "claude · Workbox Test · lane/rail · 3 helpers · offline"
    );
    // Shown helpers aren't counted again, and a local session is on This Mac.
    let local = RailEntry {
        machine: None,
        ..running
    };
    assert_eq!(
        subtitle(&local, None, false, true),
        "claude · This Mac · lane/rail"
    );
}

#[test]
fn an_offline_helper_says_so_beside_its_kept_state() {
    // A helper on Workbox Test under a parent on this Mac: only the helper's machine is offline.
    let parent = RailEntry {
        running: true,
        ..entry()
    };
    assert!(!subtitle(&parent, None, false, true).contains("offline"));
    assert_eq!(
        helper_word(Presence::Working, true).as_deref(),
        Some("Working · offline")
    );
    assert_eq!(
        helper_word(Presence::Working, false).as_deref(),
        Some("Working")
    );
    assert_eq!(
        helper_word(Presence::Idle, true).as_deref(),
        Some("offline")
    );
}
