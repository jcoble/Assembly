//! Spec section 7: Copy deeplink on a session row's menu.

use std::cell::Cell;
use std::rc::Rc;
use std::time::Duration;

use gpui::TestAppContext;
use gpui_kit::{
    Context, IntoElement, Modifiers, MouseButton, MouseDownEvent, MouseUpEvent, ParentElement as _,
    Pixels, Point, Render, Styled as _, VisualTestContext, Window, div,
};

use crate::features::sessions::rail_page::{RailEntry, test_entry};

struct OneRow {
    entry: RailEntry,
    context: super::RowContext,
}

impl Render for OneRow {
    fn render(&mut self, _: &mut Window, _: &mut Context<Self>) -> impl IntoElement {
        div().size_full().child(super::SessionRow::new(
            self.entry.clone(),
            self.context.clone(),
        ))
    }
}

fn settle(cx: &mut VisualTestContext) {
    cx.run_until_parked();
    cx.update(|window, cx| window.draw(cx).clear(cx));
    cx.run_until_parked();
    cx.update(|window, cx| window.draw(cx).clear(cx));
    cx.run_until_parked();
}

/// Draws one row and right-clicks it, so its menu is open; returns how many status moves ran so far.
fn open_menu(entry: RailEntry, cx: &mut TestAppContext) -> (Rc<Cell<u32>>, &mut VisualTestContext) {
    cx.update(gpui_kit::init);
    let marks = Rc::new(Cell::new(0));
    let marked = marks.clone();
    let row = OneRow {
        entry,
        context: super::RowContext {
            selected: false,
            needs_you: false,
            offline: false,
            now: None,
            arrow: None,
            on_open: Rc::new(|_, _, _, _| {}),
            on_mark: Rc::new(move |_, _, _| marked.set(marked.get() + 1)),
        },
    };
    let (_, cx) = cx.add_window_view(|_, _| row);
    cx.update(|window, _| window.activate_window());
    settle(cx);
    let at = cx.debug_bounds("session-s1").expect("the row").center();
    click(at, MouseButton::Right, cx);
    (marks, cx)
}

fn click(at: Point<Pixels>, button: MouseButton, cx: &mut VisualTestContext) {
    let modifiers = Modifiers::default();
    cx.simulate_mouse_move(at, None, modifiers);
    cx.simulate_event(MouseDownEvent {
        position: at,
        button,
        modifiers,
        click_count: 1,
        first_mouse: false,
    });
    cx.simulate_event(MouseUpEvent {
        position: at,
        button,
        modifiers,
        click_count: 1,
    });
    settle(cx);
}

fn clipboard(cx: &mut VisualTestContext) -> Option<String> {
    cx.update(|_, cx| cx.read_from_clipboard().and_then(|item| item.text()))
}

/// Right-clicks the row and presses `keys`; returns what the clipboard holds and how many status moves ran.
fn pick(entry: RailEntry, keys: &str, cx: &mut TestAppContext) -> (Option<String>, u32) {
    let (marks, cx) = open_menu(entry, cx);
    cx.simulate_keystrokes(keys);
    settle(cx);
    (clipboard(cx), marks.get())
}

/// The Copy deeplink row in the open menu, with the pointer resting on it past the tooltip delay.
fn rest_on_copy_deeplink(cx: &mut TestAppContext) -> (Point<Pixels>, &mut VisualTestContext) {
    let (_, cx) = open_menu(test_entry("s1", "/r"), cx);
    let row = cx
        .debug_bounds("copy-deeplink")
        .expect("Copy deeplink in the open menu")
        .center();
    cx.simulate_mouse_move(row, None, Modifiers::default());
    settle(cx);
    cx.executor().advance_clock(Duration::from_millis(600));
    settle(cx);
    (row, cx)
}

/// Two status moves, then, past the separator, the third item.
const THIRD_ITEM: &str = "down down down enter";

#[gpui::test]
fn copy_deeplink_comes_after_the_moves_and_copies_a_local_sessions_link(cx: &mut TestAppContext) {
    let (copied, marks) = pick(test_entry("s1", "/r"), THIRD_ITEM, cx);
    assert_eq!(copied.as_deref(), Some("assembly://session/s1"));
    assert_eq!(marks, 0);
}

#[gpui::test]
fn a_remote_sessions_link_names_its_machine(cx: &mut TestAppContext) {
    let entry = RailEntry {
        machine: Some("Workbox Test".into()),
        ..test_entry("s1", "/r")
    };
    let (copied, _) = pick(entry, THIRD_ITEM, cx);
    let copied = copied.expect("a link");
    assert_eq!(
        crate::core::navigation::external_urls::parse_external(&copied),
        crate::core::navigation::external_urls::ExternalRoute::Session {
            id: "s1".into(),
            machine: Some("Workbox Test".into()),
        }
    );
}

#[gpui::test]
fn the_status_moves_stay_where_they_were(cx: &mut TestAppContext) {
    let (copied, marks) = pick(test_entry("s1", "/r"), "down enter", cx);
    assert_eq!(copied, None);
    assert_eq!(marks, 1);
}

#[gpui::test]
fn resting_on_copy_deeplink_says_what_the_link_does(cx: &mut TestAppContext) {
    let (_, cx) = rest_on_copy_deeplink(cx);
    assert!(
        cx.debug_bounds(
            "copy-deeplink-hint: An assembly:// link that opens this session from anywhere"
        )
        .is_some()
    );
}

#[gpui::test]
fn clicking_copy_deeplink_copies_the_link(cx: &mut TestAppContext) {
    let (row, cx) = rest_on_copy_deeplink(cx);
    click(row, MouseButton::Left, cx);
    assert_eq!(clipboard(cx).as_deref(), Some("assembly://session/s1"));
}
