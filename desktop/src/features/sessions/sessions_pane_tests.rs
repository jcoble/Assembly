//! The sessions column follows the backend: a column opened before the backend's first ready line follows the rail
//! topic once it starts, it redraws when the connection drops, and what an empty column says follows the connection.
//! Its reads: an answer to a replaced read is dropped, a change while a read is out reads again, and a selection
//! the tab no longer lists stays open.

use std::cell::Cell;
use std::rc::Rc;

use futures_channel::mpsc::unbounded;
use gpui::TestAppContext;
use gpui_kit::{Entity, Modifiers, VisualTestContext};
use gpui_kit::{ListOffset, px};
use serde_json::Value;

use super::{SessionsPane, empty_line};
use crate::core::socket::client::SocketClient;
use crate::core::socket::connection_state::ConnectionState;
use crate::core::socket::outcome::Outcome;
use crate::core::socket::real_backend::RealBackend;
use crate::core::socket::test_wait::until;
use crate::core::state::selected_session::SelectedSession;
use crate::core::state::sessions::Sessions;
use crate::features::sessions::rail_page::{
    Rail, RailEntry, RailPage, RailTotals, Tab, test_entry,
};

fn page(sessions: Vec<RailEntry>, helpers: Vec<RailEntry>) -> RailPage {
    RailPage {
        totals: RailTotals::default(),
        agents: vec!["claude".into()],
        sessions,
        next: None,
        helpers,
        helpers_next: None,
        start: 0,
        helper_start: 0,
    }
}

fn ids(rail: &Rail) -> Vec<&str> {
    rail.sessions
        .iter()
        .map(|entry| entry.session_id.as_str())
        .collect()
}

#[test]
fn late_tab_answer_is_ignored() {
    let mut rail = Rail::default();
    let (all, _) = rail.changed().unwrap();
    let (running, args) = rail.set_tab(Tab::Running);
    assert_eq!(args["tab"], "running");
    // All's answer comes after Running was asked for: it's dropped, and asks for nothing.
    assert!(
        rail.finish(all, Outcome::Ok(page(vec![test_entry("a", "/r")], vec![])))
            .is_none()
    );
    assert!(ids(&rail).is_empty());
    assert!(
        rail.finish(
            running,
            Outcome::Ok(page(vec![test_entry("b", "/r")], vec![]))
        )
        .is_none()
    );
    assert_eq!(ids(&rail), ["b"]);
}

#[test]
fn change_during_read_refreshes_again() {
    let mut rail = Rail::default();
    let (first, _) = rail.changed().unwrap();
    assert!(rail.changed().is_none(), "one read at a time");
    let (again, args) = rail
        .finish(
            first,
            Outcome::Ok(page(vec![test_entry("a", "/r")], vec![])),
        )
        .expect("the change that came meanwhile reads again");
    assert_eq!(
        (args["tab"].as_str(), args["after"].is_null()),
        (Some("all"), true)
    );
    assert!(
        rail.finish(again, Outcome::Ok(page(vec![], vec![])))
            .is_none()
    );
    assert!(ids(&rail).is_empty());
    // A failure says so, and the next change still reads.
    let (failed, _) = rail.changed().unwrap();
    rail.finish(failed, Outcome::Aborted);
    assert!(rail.changed().is_some());
}

#[test]
fn helper_only_change_invalidates_parent_tab() {
    // Needs you holds nothing; a helper's ask opens. The change names the helper, not its parent, and the window
    // doesn't decide: it reads the tab again, and the backend lists the parent.
    let mut rail = Rail::default();
    let (id, _) = rail.set_tab(Tab::NeedsYou);
    rail.finish(id, Outcome::Ok(page(vec![], vec![])));
    let (id, args) = rail.changed().expect("a helper's change reads the tab");
    assert_eq!(args["tab"], "needs");
    let parent = RailEntry {
        needs_you: true,
        helpers: 1,
        ..test_entry("p", "/r")
    };
    let mut answer = page(vec![parent], vec![]);
    answer.totals = RailTotals {
        all: 1,
        running: 0,
        needs_you: 1,
    };
    rail.finish(id, Outcome::Ok(answer));
    assert_eq!(ids(&rail), ["p"]);
    assert_eq!(Tab::NeedsYou.label(&rail.totals), "Needs you 1");
}

fn parents(from: usize, to: usize) -> Vec<RailEntry> {
    (from..to)
        .map(|i| test_entry(&format!("p{i:03}"), "/r"))
        .collect()
}

/// Rows `from` to 150 of the tab, with `from` before them.
fn window(from: usize) -> RailPage {
    RailPage {
        start: from,
        ..page(parents(from, 150), vec![])
    }
}

#[test]
fn a_read_again_names_the_row_in_view() {
    let mut rail = Rail::default();
    let (id, _) = rail.changed().unwrap();
    rail.finish(id, Outcome::Ok(window(100)));
    rail.look_at(Some("p130".into()));
    let (id, args) = rail.changed().unwrap();
    let anchor = |args: &Value| {
        (
            args["anchor"].as_str().map(String::from),
            args["above"].as_u64(),
        )
    };
    assert_eq!(anchor(&args), (Some("p130".into()), Some(30)));
    assert_eq!(args["limit"], 100);
    rail.finish(id, Outcome::Aborted);
    let (id, args) = rail.earlier().unwrap();
    assert_eq!(anchor(&args), (Some("p100".into()), Some(50)));
    // At the tab's real start with its first row in view, a read again starts at the top.
    rail.finish(id, Outcome::Ok(window(0)));
    rail.look_at(Some("p000".into()));
    assert_eq!(anchor(&rail.changed().unwrap().1), (None, Some(0)));
}

// Pins: g0-session#30
#[test]
fn an_empty_column_says_what_the_connection_is_doing() {
    let connected = ConnectionState::Connected {
        machine: "m".into(),
    };
    assert_eq!(empty_line(None), "The backend isn't running.");
    assert_eq!(
        empty_line(Some(&ConnectionState::Connecting)),
        "Connecting to the backend…"
    );
    assert_eq!(empty_line(Some(&connected)), "No sessions yet.");
    assert_eq!(
        empty_line(Some(&ConnectionState::Disconnected)),
        "The backend isn't running."
    );
    assert_eq!(
        empty_line(Some(&ConnectionState::Refused { code: 4401 })),
        "The backend isn't running."
    );
}

fn open(cx: &mut TestAppContext) -> (Entity<SessionsPane>, &mut VisualTestContext) {
    cx.update(gpui_kit::init);
    cx.update(SelectedSession::init);
    let (pane, cx) = cx.add_window_view(SessionsPane::new);
    draw(cx);
    (pane, cx)
}

fn draw(cx: &mut VisualTestContext) {
    cx.run_until_parked();
    cx.update(|window, cx| window.draw(cx).clear(cx));
}

fn click(selector: &'static str, cx: &mut VisualTestContext) {
    let at = cx.debug_bounds(selector).expect("it's drawn");
    cx.simulate_click(at.center(), Modifiers::none());
    draw(cx);
}

fn selected(cx: &mut VisualTestContext) -> Option<String> {
    cx.update(|_, cx| {
        SelectedSession::global(cx)
            .read(cx)
            .session_id()
            .map(String::from)
    })
}

/// The pane takes `answer` as its read's.
fn answer(
    pane: &Entity<SessionsPane>,
    expand: Option<&str>,
    answer: RailPage,
    cx: &mut VisualTestContext,
) {
    pane.update(cx, |pane, cx| {
        let (id, _) = match expand {
            Some(parent) => pane.rail.toggle_expanded(parent),
            None => pane.rail.changed().unwrap(),
        };
        pane.answer(id, Outcome::Ok(answer), cx);
    });
    draw(cx);
}

#[gpui::test]
fn filtered_out_selection_stays_open(cx: &mut TestAppContext) {
    let (pane, cx) = open(cx);
    answer(
        &pane,
        None,
        page(vec![test_entry("a", "/r"), test_entry("b", "/r")], vec![]),
        cx,
    );
    click("session-a", cx);
    assert_eq!(selected(cx).as_deref(), Some("a"));
    // Running no longer lists it: the session stays open.
    answer(&pane, None, page(vec![test_entry("b", "/r")], vec![]), cx);
    assert!(cx.debug_bounds("session-a").is_none());
    assert_eq!(selected(cx).as_deref(), Some("a"));
}

#[gpui::test]
fn helper_opens_its_own_session(cx: &mut TestAppContext) {
    let (pane, cx) = open(cx);
    let parent = RailEntry {
        helpers: 1,
        ..test_entry("p", "/r")
    };
    answer(
        &pane,
        Some("p"),
        page(vec![parent], vec![test_entry("h1", "/r")]),
        cx,
    );
    click("session-h1", cx);
    assert_eq!(selected(cx).as_deref(), Some("h1"));
}

#[gpui::test]
fn filter_clears_on_escape_and_selection(cx: &mut TestAppContext) {
    let (pane, cx) = open(cx);
    answer(&pane, None, page(vec![test_entry("a", "/r")], vec![]), cx);
    let typed = |pane: &Entity<SessionsPane>, cx: &mut VisualTestContext| {
        pane.update_in(cx, |pane, window, cx| {
            pane.filter
                .update(cx, |input, cx| input.set_value("notes", window, cx));
            let read = pane.rail.set_filter("notes");
            pane.send(read, cx);
            assert_eq!(pane.rail.filter, "notes");
        });
    };
    let cleared = |pane: &Entity<SessionsPane>, cx: &mut VisualTestContext| {
        pane.read_with(cx, |pane, cx| {
            assert_eq!(pane.filter.read(cx).value(), "");
            assert_eq!(pane.rail.filter, "");
        });
    };
    // Escape in the filter.
    typed(&pane, cx);
    pane.update_in(cx, |pane, window, cx| pane.clear_filter(window, cx));
    cleared(&pane, cx);
    // Picking a session.
    typed(&pane, cx);
    answer(&pane, None, page(vec![test_entry("a", "/r")], vec![]), cx);
    click("session-a", cx);
    assert_eq!(selected(cx).as_deref(), Some("a"));
    cleared(&pane, cx);
}

// Pins: g0-session#30
#[gpui::test]
async fn a_column_opened_before_the_backend_was_ready_follows_it(cx: &mut TestAppContext) {
    let Some(backend) = RealBackend::start() else {
        return;
    };
    cx.executor().allow_parking();
    cx.update(gpui_kit::init);
    cx.update(SelectedSession::init);
    let (pane, cx) = cx.add_window_view(SessionsPane::new);
    pane.read_with(cx, |pane, _| assert!(!pane.following()));
    let (changed, mut changes) = unbounded();
    let tell = changed.clone();
    let (socket, _heard) = cx.update(|_, cx| {
        let socket = SocketClient::start(backend.endpoint(), cx);
        Sessions::start(cx);
        let heard = cx.observe(&socket, move |_, _| _ = tell.unbounded_send(()));
        (socket, heard)
    });
    pane.read_with(cx, |pane, _| assert!(pane.following()));
    let state =
        |cx: &mut gpui_kit::VisualTestContext| cx.update(|_, cx| socket.read(cx).state().clone());
    until("connected", &mut changes, &changed, || {
        matches!(state(cx), ConnectionState::Connected { .. })
    })
    .await;
    // Only the pane's own observer tells it the connection dropped.
    let (redrawn, mut redraws) = unbounded();
    let (drawn, tell) = (Rc::new(Cell::new(false)), redrawn.clone());
    let marked = drawn.clone();
    let _drawn = cx.update(|_, cx| {
        cx.observe(&pane, move |_, _| {
            marked.set(true);
            _ = tell.unbounded_send(());
        })
    });
    drop(backend);
    until(
        "the pane redraws for the drop",
        &mut redraws,
        &redrawn,
        || drawn.get() && !matches!(state(cx), ConnectionState::Connected { .. }),
    )
    .await;
    assert_ne!(empty_line(Some(&state(cx))), "No sessions yet.");
}

#[gpui::test]
fn earlier_rows_keep_the_line_at_a_windows_start(cx: &mut TestAppContext) {
    let (pane, cx) = open(cx);
    answer(&pane, None, window(100), cx);
    let top =
        |cx: &mut VisualTestContext| pane.read_with(cx, |pane, _| pane.lines.logical_scroll_top());
    let earlier = |page: RailPage, cx: &mut VisualTestContext| {
        pane.update(cx, |pane, cx| {
            let (id, _) = pane.rail.earlier().unwrap();
            pane.answer(id, Outcome::Ok(page), cx);
        });
        draw(cx);
    };
    // At the window's very top, p100 stays the line in view.
    assert_eq!(top(cx), ListOffset::default());
    earlier(window(50), cx);
    assert_eq!(
        top(cx),
        ListOffset {
            item_ix: 50,
            offset_in_item: px(0.)
        }
    );
    // Partly scrolled into the window's first row, it keeps that too.
    pane.update(cx, |pane, _| {
        pane.lines.scroll_to(ListOffset {
            item_ix: 0,
            offset_in_item: px(10.),
        })
    });
    earlier(window(0), cx);
    assert_eq!(
        top(cx),
        ListOffset {
            item_ix: 50,
            offset_in_item: px(10.)
        }
    );
}
