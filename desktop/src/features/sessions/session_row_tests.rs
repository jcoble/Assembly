use super::{details, short_details, state_icon, status_words};
use crate::features::sessions::presence::Presence;
use crate::features::sessions::rail_page::{RailEntry, test_entry};

fn entry() -> RailEntry {
    test_entry("s1", "/r")
}

// Pins: g0-session#30
#[test]
fn a_row_shows_needs_you_first_then_working_failed_done_and_unread() {
    let all = RailEntry {
        running: true,
        failed: true,
        completed_at: Some(5),
        unread: true,
        ..entry()
    };
    assert_eq!(Presence::of(&all,true), Presence::NeedsYou);
    assert_eq!(Presence::of(&all,false), Presence::Working);
    let all = RailEntry {
        running: false,
        ..all
    };
    assert_eq!(Presence::of(&all,false), Presence::Failed);
    let all = RailEntry {
        failed: false,
        ..all
    };
    assert_eq!(Presence::of(&all,false), Presence::Done);
    let all = RailEntry {
        completed_at: None,
        ..all
    };
    assert_eq!(Presence::of(&all,false), Presence::Unread);
    assert_eq!(Presence::of(&entry(),false), Presence::Idle);
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
    assert_eq!(Presence::of(&running,false), Presence::Working);
    assert_eq!(
        status_words(Presence::of(&running,false), true),
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
    assert_eq!(Presence::of(&finished,false), Presence::Idle);
    assert_eq!(status_words(Presence::of(&finished,false), false), ["Idle"]);
    let unread = RailEntry {
        unread: true,
        ..finished
    };
    assert_eq!(status_words(Presence::of(&unread,false), false), ["Idle"]);
}

#[test]
fn the_short_form_drops_this_mac_and_keeps_the_rest() {
    let remote = RailEntry {
        machine: Some("Workbox Test".into()),
        branch: Some("lane/rail".into()),
        ..entry()
    };
    assert_eq!(short_details(&remote), "Workbox Test · lane/rail");
    let local = RailEntry { branch: Some("lane/rail".into()), ..entry() };
    assert_eq!(short_details(&local), "lane/rail");
    assert_eq!(short_details(&entry()), "");
}

struct OneRow {
    entry: RailEntry,
    context: super::RowContext,
}

impl gpui_kit::Render for OneRow {
    fn render(&mut self, _: &mut gpui_kit::Window, _: &mut gpui_kit::Context<Self>) -> impl gpui_kit::IntoElement {
        use gpui_kit::{ParentElement as _, Styled as _};
        gpui_kit::div()
            .w(gpui_kit::px(300.))
            .child(super::SessionRow::new(self.entry.clone(), self.context.clone()))
    }
}

/// Clicks land where a person's would: on each of the row's three lines.
#[gpui::test]
fn clicking_any_line_of_a_row_opens_it_without_touching_its_arrow(cx: &mut gpui::TestAppContext) {
    use gpui_kit::{AppContext as _, Modifiers, point, px};
    use std::cell::Cell;
    use std::rc::Rc;
    cx.update(gpui_kit::init);
    let (opened, toggled) = (Rc::new(Cell::new(0)), Rc::new(Cell::new(0)));
    let (seen, flipped) = (opened.clone(), toggled.clone());
    let row = OneRow {
        entry: RailEntry { branch: Some("lane/rail".into()), ..entry() },
        context: super::RowContext {
            selected: false,
            needs_you: true,
            offline: false,
            now: None,
            arrow: Some(crate::core::ui::three_line_row::RowArrow {
                open: false,
                on_toggle: Rc::new(move |_| flipped.set(flipped.get() + 1)),
            }),
            on_open: Rc::new(move |_, _, _, _| seen.set(seen.get() + 1)),
            on_mark: Rc::new(|_, _, _| {}),
        },
    };
    let (_, cx) = cx.add_window_view(|_, _| row);
    cx.run_until_parked();
    let bounds = cx.debug_bounds("session-s1").expect("the row is drawn");
    assert!(bounds.size.height > px(0.));
    let left = bounds.origin.x + px(40.);
    cx.simulate_click(point(left, bounds.origin.y + px(12.)), Modifiers::default());
    assert_eq!(opened.get(), 1, "title line");
    cx.simulate_click(point(left, bounds.origin.y + bounds.size.height * 0.5), Modifiers::default());
    assert_eq!(opened.get(), 2, "second line, which has its own tooltip");
    cx.simulate_click(point(left, bounds.origin.y + bounds.size.height - px(10.)), Modifiers::default());
    assert_eq!(opened.get(), 3, "third line");
    assert_eq!(toggled.get(), 0);
}

#[test]
fn only_states_you_act_on_or_finished_have_an_icon() {
    assert!(state_icon(Presence::NeedsYou).is_some());
    assert!(state_icon(Presence::Failed).is_some());
    assert!(state_icon(Presence::Done).is_some());
    assert!(state_icon(Presence::Working).is_none());
    assert!(state_icon(Presence::Idle).is_none());
}
