//! The sessions column follows the backend: a column opened before the backend's first ready line follows the rail
//! topic once it starts, it redraws when the connection drops, and what an empty column says follows the connection.
//! Its reads: an answer to a replaced read is dropped, a change while a read is out reads again, and a selection
//! the current view no longer lists stays open.

use std::cell::Cell;
use std::rc::Rc;

use futures_channel::mpsc::unbounded;
use gpui::TestAppContext;
use gpui_kit::{Entity, Modifiers, VisualTestContext};
use gpui_kit::{ListOffset, px};
use serde_json::{Value, json};

use super::{SessionsPane, empty_line, needs_words, provider_options, saved_filters};
use crate::core::socket::client::SocketClient;
use crate::core::socket::connection_state::ConnectionState;
use crate::core::socket::outcome::Outcome;
use crate::core::socket::real_backend::RealBackend;
use crate::core::socket::test_wait::until;
use crate::core::socket::update::Update;
use crate::core::state::selected_session::SelectedSession;
use crate::core::state::sessions::Sessions;
use crate::features::sessions::rail_page::{Rail, RailEntry, RailPage, RailTotals, test_entry};
use crate::features::sessions::view_options::{Dir, GroupBy, Sort, View, saved_view};

fn page(sessions: Vec<RailEntry>) -> RailPage {
    RailPage {
        totals: RailTotals::default(),
        agents: vec!["claude".into()],
        sessions,
        next: None,
        start: 0,
    }
}

fn ids(rail: &Rail) -> Vec<&str> {
    rail.sessions
        .iter()
        .map(|entry| entry.session_id.as_str())
        .collect()
}

#[test]
fn late_view_answer_is_ignored() {
    let mut rail = Rail::default();
    let (all, _) = rail.changed().unwrap();
    let (grouped, args) = rail.set_view(View {
        group_by: GroupBy {
            status: true,
            project: false,
        },
        ..View::default()
    });
    assert_eq!(args["groupBy"], json!({"status": true, "project": false}));
    // The old view's answer comes after the new one was asked for: it is dropped.
    assert!(
        rail.finish(all, Outcome::Ok(page(vec![test_entry("a", "/r")])))
            .is_none()
    );
    assert!(ids(&rail).is_empty());
    assert!(
        rail.finish(grouped, Outcome::Ok(page(vec![test_entry("b", "/r")])))
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
        .finish(first, Outcome::Ok(page(vec![test_entry("a", "/r")])))
        .expect("the change that came meanwhile reads again");
    assert_eq!(
        (
            args["groupBy"]["project"].as_bool(),
            args["after"].is_null()
        ),
        (Some(true), true)
    );
    assert!(rail.finish(again, Outcome::Ok(page(vec![]))).is_none());
    assert!(ids(&rail).is_empty());
    // A failure says so, and the next change still reads.
    let (failed, _) = rail.changed().unwrap();
    rail.finish(failed, Outcome::Aborted);
    assert!(rail.changed().is_some());
}

fn parents(from: usize, to: usize) -> Vec<RailEntry> {
    (from..to)
        .map(|i| test_entry(&format!("p{i:03}"), "/r"))
        .collect()
}

/// Rows `from` to 150 of the rail, with `from` before them.
fn window(from: usize) -> RailPage {
    RailPage {
        start: from,
        ..page(parents(from, 150))
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
    // At the rail's real start with its first row in view, a read again starts at the top.
    rail.finish(id, Outcome::Ok(window(0)));
    rail.look_at(Some("p000".into()));
    assert_eq!(anchor(&rail.changed().unwrap().1), (None, Some(0)));
}

/// At the rail's real start, the first session sits under its headings: a read again still starts at the top, so a
/// new or moved row above it comes in with its heading.
#[test]
fn a_refresh_at_the_top_shows_new_rows_and_their_headings() {
    let header = |id: &str, kind: &str| RailEntry {
        kind: kind.into(),
        ..test_entry(id, "/r")
    };
    let mut rail = Rail::default();
    let (id, _) = rail.changed().unwrap();
    let held = vec![
        header("section:working", "section"),
        header("project:working::/r", "project"),
    ];
    rail.finish(id, Outcome::Ok(page([held, parents(0, 3)].concat())));
    rail.look_at(Some("p000".into()));
    let args = rail.changed().unwrap().1;
    assert_eq!(
        (&args["anchor"], &args["after"]),
        (&Value::Null, &Value::Null)
    );
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
fn answer(pane: &Entity<SessionsPane>, answer: RailPage, cx: &mut VisualTestContext) {
    pane.update(cx, |pane, cx| {
        let (id, _) = pane.rail.changed().unwrap();
        pane.answer(id, Outcome::Ok(answer), cx);
    });
    draw(cx);
}

#[gpui::test]
fn filtered_out_selection_stays_open(cx: &mut TestAppContext) {
    let (pane, cx) = open(cx);
    answer(
        &pane,
        page(vec![test_entry("a", "/r"), test_entry("b", "/r")]),
        cx,
    );
    click("session-a", cx);
    assert_eq!(selected(cx).as_deref(), Some("a"));
    // Running no longer lists it: the session stays open.
    answer(&pane, page(vec![test_entry("b", "/r")]), cx);
    assert!(cx.debug_bounds("session-a").is_none());
    assert_eq!(selected(cx).as_deref(), Some("a"));
}

#[gpui::test]
fn filter_clears_on_escape_and_selection(cx: &mut TestAppContext) {
    let (pane, cx) = open(cx);
    answer(&pane, page(vec![test_entry("a", "/r")]), cx);
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
    answer(&pane, page(vec![test_entry("a", "/r")]), cx);
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
    answer(&pane, window(100), cx);
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

// Pins: sessions-list#9
#[gpui::test]
fn changing_the_view_starts_at_the_top(cx: &mut TestAppContext) {
    let (pane, cx) = open(cx);
    answer(&pane, page(parents(0, 29)), cx);
    pane.update(cx, |pane, _| {
        pane.lines.scroll_to(ListOffset {
            item_ix: 20,
            offset_in_item: px(3.),
        })
    });
    draw(cx);
    for view in [
        View {
            group_by: GroupBy {
                status: true,
                project: false,
            },
            ..View::default()
        },
        View::default(),
    ] {
        let (id, _) = pane.update(cx, |pane, _cx| {
            pane.lines.scroll_to(ListOffset::default());
            pane.from_start = true;
            pane.rail.set_view(view)
        });
        draw(cx);
        pane.update(cx, |pane, cx| {
            pane.answer(id, Outcome::Ok(page(parents(0, 29))), cx);
        });
        draw(cx);
        pane.read_with(cx, |pane, _| {
            assert_eq!(pane.lines.logical_scroll_top(), ListOffset::default())
        });
    }
}

// Pins: sessions-list#7
#[test]
fn set_picks_sends_one_read_with_providers_locations_and_statuses() {
    let mut rail = Rail::default();
    let (_, args) = rail.set_picks(
        vec!["claude".into()],
        vec!["local".into()],
        vec!["working".into()],
    );
    assert_eq!(args["providers"], json!(["claude"]));
    assert_eq!(args["locations"], json!(["local"]));
    assert_eq!(args["statuses"], json!(["working"]));
    assert!(rail.changed().is_none(), "the one read is out");
}

// Pins: sessions-list#7
#[test]
fn saved_filters_reads_the_rail_filters_preference() {
    let saved = json!({"provider": ["claude"], "location": ["local"]});
    let state = Update::State(vec![
        ("theme".into(), json!({"key": "theme", "value": "dark"})),
        (
            "rail.filters".into(),
            json!({"key": "rail.filters", "value": saved}),
        ),
    ]);
    assert_eq!(
        saved_filters(&state),
        Some((vec!["claude".into()], vec!["local".into()], vec![]))
    );
    assert_eq!(saved_filters(&Update::State(Vec::new())), None);
}

// Pins: sessions-list#7
#[test]
fn a_saved_pick_not_yet_in_agents_still_shows() {
    let options = provider_options(&["claude".into()], &["codex".into()]);
    let shown: Vec<_> = (options.iter())
        .map(|option| (option.value.as_str(), option.label.as_str()))
        .collect();
    assert_eq!(shown, [("claude", "Claude"), ("codex", "Codex")]);
}

// Pins: sessions-list#6
#[test]
fn view_options_reach_the_rail_read() {
    let mut rail = Rail::default();
    let (_, args) = rail.set_view(View {
        group_by: GroupBy {
            status: true,
            project: false,
        },
        sort: Sort::Name,
        dir: Dir::Desc,
    });
    assert_eq!(args["groupBy"], json!({"status": true, "project": false}));
    assert_eq!(args["sort"], "name");
    assert_eq!(args["dir"], "desc");
}

// Pins: sessions-list#6
#[test]
fn saved_view_is_restored_into_the_rail() {
    let state = Update::State(vec![(
        "rail.view".into(),
        json!({"key": "rail.view", "value": {
            "groupBy": {"status": true, "project": false}, "sort": "name", "dir": "desc"
        }}),
    )]);
    assert_eq!(
        saved_view(&state),
        View {
            group_by: GroupBy {
                status: true,
                project: false,
            },
            sort: Sort::Name,
            dir: Dir::Desc,
        }
    );
    assert_eq!(saved_view(&Update::State(vec![])), View::default());
}

// Pins: sessions-list#7
#[test]
fn status_picks_are_saved_with_the_filters() {
    let saved = json!({"provider": ["claude"], "location": ["local"], "status": ["settled"]});
    let state = Update::State(vec![(
        "rail.filters".into(),
        json!({"key": "rail.filters", "value": saved}),
    )]);
    assert_eq!(
        saved_filters(&state),
        Some((
            vec!["claude".into()],
            vec!["local".into()],
            vec!["settled".into()]
        ))
    );
}

// Pins: sessions-list#9
#[test]
fn folding_settled_sends_one_read() {
    let mut rail = Rail::default();
    let (first, _) = rail.changed().unwrap();
    rail.finish(
        first,
        Outcome::Ok(page(vec![
            test_entry("above", "/r"),
            test_entry("settled-row", "/r"),
        ])),
    );
    rail.look_at(Some("settled-row".into()));
    let (_, args) = rail.toggle_fold("settled".into(), "section:settled".into());
    assert_eq!(args["folded"], json!(["settled"]));
    assert!(rail.changed().is_none(), "the one read is out");
}

// Pins: sessions-list#9
#[test]
fn folding_a_group_keeps_its_header_in_the_page() {
    let mut rail = Rail::default();
    let (first, _) = rail.changed().unwrap();
    let header = RailEntry {
        kind: "project".into(),
        header: 1,
        ..test_entry("project:::/r", "/r")
    };
    rail.finish(
        first,
        Outcome::Ok(page(vec![
            test_entry("above", "/q"),
            header,
            test_entry("member", "/r"),
        ])),
    );
    // The header is at the top, so the row in view is its first member, which folding removes.
    rail.look_at(Some("member".into()));
    let (_, args) = rail.toggle_fold("::/r".into(), "project:::/r".into());
    assert_eq!(
        (&args["anchor"], &args["above"]),
        (&json!("project:::/r"), &json!(1))
    );
}

// Pins: sessions-list#10
#[test]
fn the_needs_total_reads_as_words_above_zero() {
    assert_eq!(needs_words(0), None);
    assert_eq!(needs_words(1).as_deref(), Some("1 needs you"));
    assert_eq!(needs_words(3).as_deref(), Some("3 need you"));
}
