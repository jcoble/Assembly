//! The sessions column follows the backend: a column opened before the backend's first ready line follows the rail
//! topic once it starts, it redraws when the connection drops, and what an empty column says follows the connection.
//! Its reads: an answer to a replaced read is dropped, a change while a read is out reads again, and a selection
//! the tab no longer lists stays open.

use std::cell::Cell;
use std::rc::Rc;

use futures_channel::mpsc::unbounded;
use gpui::TestAppContext;
use gpui_kit::{Entity, Modifiers, VisualTestContext};
use serde_json::{Value, json};

use super::{SessionsPane, empty_line};
use crate::core::socket::client::SocketClient;
use crate::core::socket::connection_state::ConnectionState;
use crate::core::socket::outcome::Outcome;
use crate::core::socket::real_backend::RealBackend;
use crate::core::socket::test_wait::until;
use crate::core::state::selected_session::SelectedSession;
use crate::core::state::sessions::Sessions;
use crate::features::sessions::rail_page::{
    Rail, RailEntry, RailPage, RailTotals, Request, Tab, test_entry,
};

fn page(sessions: Vec<RailEntry>, helpers: Vec<RailEntry>) -> RailPage {
    RailPage {
        totals: RailTotals::default(),
        agents: vec!["claude".into()],
        sessions,
        next: None,
        helpers,
        helpers_next: None,
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

/// What the backend answers over `all` and the expanded parent's `helpers`, both in its order: each page after its
/// cursor, up to its limit.
fn serve(all: &[RailEntry], helpers: &[RailEntry], args: &Value) -> RailPage {
    fn after(list: &[RailEntry], cursor: &Value) -> usize {
        cursor["sessionId"].as_str().map_or(0, |id| {
            1 + list
                .iter()
                .position(|entry| entry.session_id == id)
                .unwrap()
        })
    }
    let limit = args["limit"].as_u64().unwrap() as usize;
    let helper_limit = args["helperLimit"].as_u64().map_or(limit, |n| n as usize);
    let sessions: Vec<RailEntry> = all[after(all, &args["after"])..]
        .iter()
        .take(limit)
        .cloned()
        .collect();
    let shown: Vec<RailEntry> = match args["expanded"].is_string() {
        true => helpers[after(helpers, &args["helpersAfter"])..]
            .iter()
            .take(helper_limit)
            .cloned()
            .collect(),
        false => vec![],
    };
    let cursor = |entry: &RailEntry| {
        json!({ "group": entry.group, "repoUrl": entry.repo_url, "updatedAt": entry.updated_at,
            "sessionId": entry.session_id })
    };
    RailPage {
        next: (sessions.len() == limit)
            .then(|| serde_json::from_value(cursor(&sessions[limit - 1])).unwrap()),
        helpers_next: (shown.len() == helper_limit)
            .then(|| serde_json::from_value(cursor(&shown[helper_limit - 1])).unwrap()),
        helpers: shown,
        ..page(sessions, vec![])
    }
}

/// Sends `read` and every read its answer asks for, to `serve`.
fn answer_all(rail: &mut Rail, all: &[RailEntry], helpers: &[RailEntry], read: Option<Request>) {
    let mut read = read;
    while let Some((id, args)) = read {
        read = rail.finish(id, Outcome::Ok(serve(all, helpers, &args)));
    }
}

fn parents(n: usize) -> Vec<RailEntry> {
    (0..n)
        .map(|i| test_entry(&format!("p{i:03}"), "/r"))
        .collect()
}

/// Every parent page read, as scrolling to the end does.
fn scroll_to_end(rail: &mut Rail, all: &[RailEntry]) {
    let first = rail.changed();
    answer_all(rail, all, &[], first);
    while let Some(read) = rail.more() {
        answer_all(rail, all, &[], Some(read));
    }
}

fn held(rail: &Rail, id: &str) -> bool {
    rail.sessions.iter().any(|entry| entry.session_id == id)
}

#[test]
fn a_refresh_keeps_the_rows_held_beyond_200() {
    let all = parents(250);
    let mut rail = Rail::default();
    scroll_to_end(&mut rail, &all);
    assert!(held(&rail, "p230") && rail.sessions.len() <= 200);
    let read = rail.changed();
    answer_all(&mut rail, &all, &[], read);
    assert!(held(&rail, "p230"), "the reader's row is still there");
    // Scrolling back up reads the rows before the held ones.
    let read = rail.earlier();
    assert!(read.is_some());
    answer_all(&mut rail, &all, &[], read);
    assert_eq!(ids(&rail)[0], "p050");
}

#[test]
fn a_session_new_ahead_of_the_visible_row_keeps_it() {
    let mut all = parents(60);
    let mut rail = Rail::default();
    let first = rail.changed();
    answer_all(&mut rail, &all, &[], first);
    assert_eq!(ids(&rail).last(), Some(&"p049"));
    all.insert(10, test_entry("new", "/r"));
    let read = rail.changed();
    answer_all(&mut rail, &all, &[], read);
    assert!(held(&rail, "new") && held(&rail, "p049"));
}

#[test]
fn more_helpers_keep_a_parent_beyond_200() {
    let all = parents(250);
    let helpers: Vec<RailEntry> = (0..60)
        .map(|i| test_entry(&format!("h{i:02}"), "/r"))
        .collect();
    let mut rail = Rail::default();
    scroll_to_end(&mut rail, &all);
    let read = Some(rail.toggle_expanded("p230"));
    answer_all(&mut rail, &all, &helpers, read);
    let read = rail.more_helpers();
    answer_all(&mut rail, &all, &helpers, read);
    assert!(held(&rail, "p230"), "the expanded parent is still there");
    assert_eq!(rail.helpers.len(), 60);
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
