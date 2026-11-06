//! The History tab over a held sessions list: folded projects, opening one, showing more, picking a session.

use gpui::TestAppContext;
use gpui_kit::component::dock::BasePanel as _;
use gpui_kit::{Entity, Modifiers, VisualTestContext};
use serde_json::json;

use super::HistoryPane;
use crate::core::state::selected_session::SelectedSession;
use crate::core::state::session_item::SessionItem;
use crate::core::state::sessions::Sessions;

fn open(
    items: Option<Vec<SessionItem>>,
    cx: &mut TestAppContext,
) -> (Entity<HistoryPane>, &mut VisualTestContext) {
    open_with_older(items, false, false, cx)
}

fn open_with_older(
    items: Option<Vec<SessionItem>>,
    older: bool,
    settled: bool,
    cx: &mut TestAppContext,
) -> (Entity<HistoryPane>, &mut VisualTestContext) {
    cx.update(gpui_kit::init);
    cx.update(SelectedSession::init);
    if let Some(items) = items {
        cx.update(|cx| Sessions::start_holding(items, older, settled, cx));
    }
    let (pane, cx) = cx.add_window_view(|_, cx| HistoryPane::new(cx));
    draw(cx);
    (pane, cx)
}

fn session(id: &str, folder: &str, machine: Option<&str>) -> SessionItem {
    serde_json::from_value(
        json!({ "sessionId": id, "agent": "claude", "repoUrl": folder, "projectId": folder, "machine": machine,
        "updatedAt": 1 }),
    )
    .expect("a session item")
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

fn drawn(selector: &'static str, cx: &mut VisualTestContext) -> bool {
    cx.debug_bounds(selector).is_some()
}

fn two_projects() -> Vec<SessionItem> {
    vec![
        session("s3", "/w/notes", None),
        session("s2", "https://github.com/jcoble/Assembly", None),
        session("s1", "/w/notes", None),
    ]
}

// Pins: g0-shape#15
#[gpui::test]
fn projects_start_folded_and_a_click_opens_one_newest_first(cx: &mut TestAppContext) {
    let (_pane, cx) = open(Some(two_projects()), cx);
    assert!(drawn("history-project-/w/notes", cx));
    assert!(drawn(
        "history-project-https://github.com/jcoble/Assembly",
        cx
    ));
    assert!(!drawn("history-session-s3", cx), "projects start folded");

    click("history-project-/w/notes", cx);
    let s3 = cx.debug_bounds("history-session-s3").expect("s3 is listed");
    let s1 = cx.debug_bounds("history-session-s1").expect("s1 is listed");
    assert!(s3.top() < s1.top(), "newest first");
    assert!(
        !drawn("history-session-s2", cx),
        "the other project stays folded"
    );

    click("history-project-/w/notes", cx);
    assert!(!drawn("history-session-s3", cx), "a second click folds it");
}

// Pins: g0-shape#15
#[gpui::test]
async fn a_click_on_a_session_opens_it(cx: &mut TestAppContext) {
    cx.executor().allow_parking();
    let (_pane, cx) = open(Some(two_projects()), cx);
    let mut nav = cx.update(|window, cx| {
        crate::features::session::view::session_pane::test_navigation::NavigationFixture::new(
            false, window, cx,
        )
    });
    nav.ready(cx).await;
    click("history-project-/w/notes", cx);
    click("history-session-s1", cx);
    nav.opened("s1", cx).await;
    let selected = cx.update(|_, cx| {
        SelectedSession::global(cx)
            .read(cx)
            .session_id()
            .map(String::from)
    });
    assert_eq!(selected.as_deref(), Some("s1"));
}

// Pins: g0-shape#15
#[gpui::test]
fn an_open_project_shows_25_sessions_then_25_more(cx: &mut TestAppContext) {
    let items = (0..30)
        .rev()
        .map(|n| session(&format!("s{n}"), "/w/notes", None))
        .collect();
    let (_pane, cx) = open(Some(items), cx);
    click("history-project-/w/notes", cx);
    assert!(drawn("history-session-s29", cx));
    assert!(drawn("history-session-s5", cx));
    assert!(!drawn("history-session-s4", cx), "the 26th waits");

    click("history-more-/w/notes", cx);
    assert!(drawn("history-session-s0", cx));
    assert!(!drawn("history-more-/w/notes", cx), "nothing more to show");
}

// Pins: g0-shape#15
#[gpui::test]
fn a_tick_draws_each_sessions_age_and_hiding_the_tab_drops_it(cx: &mut TestAppContext) {
    let (pane, cx) = open(Some(two_projects()), cx);
    click("history-project-/w/notes", cx);
    assert!(
        !drawn("history-age-s3", cx),
        "no age before the clock's first tick"
    );
    let title = cx.debug_bounds("history-title-s3").expect("s3 is listed");
    pane.update(cx, |pane, cx| pane.tick(5 * 60_000, cx));
    draw(cx);
    assert!(drawn("history-age-s3", cx));
    assert_eq!(
        cx.debug_bounds("history-title-s3"),
        Some(title),
        "the row doesn't move when the age comes"
    );

    pane.update_in(cx, |pane, window, cx| pane.set_active(false, window, cx));
    draw(cx);
    assert!(
        !drawn("history-age-s3", cx),
        "shown again, it waits for a fresh tick"
    );
}

/// A tick redraws the window only when an age on screen changes: each redraw is a full frame, once a second for as
/// long as the tab was shown, with nothing new to show.
#[gpui::test]
fn a_tick_redraws_only_when_an_age_on_screen_changes(cx: &mut TestAppContext) {
    let (pane, cx) = open(Some(two_projects()), cx);
    let redraws = std::rc::Rc::new(std::cell::Cell::new(0));
    let counted = redraws.clone();
    let _heard = cx.update(|_, cx| cx.observe(&pane, move |_, _| counted.set(counted.get() + 1)));
    let tick = |at: i64, cx: &mut VisualTestContext| {
        pane.update(cx, |pane, cx| pane.tick(at, cx));
        cx.run_until_parked();
    };

    // Each session was updated at 1 ms, so its age says 5m from 5 min 1 ms.
    tick(5 * 60_000 + 1, cx);
    assert_eq!(
        redraws.get(),
        0,
        "folded, no age shows, so nothing to redraw"
    );

    click("history-project-/w/notes", cx);
    let opened = redraws.get();
    tick(5 * 60_000 + 1_001, cx);
    assert_eq!(redraws.get(), opened, "every age still says 5m");
    tick(6 * 60_000 + 1, cx);
    assert_eq!(redraws.get(), opened + 1, "they move on to 6m");
}

// Pins: g0-shape#15
#[gpui::test]
fn a_line_at_the_end_loads_older_sessions_while_there_are_some(cx: &mut TestAppContext) {
    let (_pane, cx) = open_with_older(Some(two_projects()), true, false, cx);
    assert!(drawn("history-older", cx));
}

// Pins: g0-shape#15
#[gpui::test]
fn with_the_open_sessions_loaded_the_line_stays_for_settled_ones(cx: &mut TestAppContext) {
    let (_pane, cx) = open_with_older(Some(two_projects()), false, true, cx);
    assert!(drawn("history-older", cx));
}

// Pins: g0-shape#15
#[gpui::test]
fn with_every_session_loaded_there_is_no_older_line(cx: &mut TestAppContext) {
    let (_pane, cx) = open(Some(two_projects()), cx);
    assert!(!drawn("history-older", cx));
}

// Pins: g0-shape#15
#[gpui::test]
fn it_says_when_there_are_no_sessions_or_no_backend_yet(cx: &mut TestAppContext) {
    let (_pane, cx) = open(Some(Vec::new()), cx);
    assert!(drawn("history-note-empty", cx));
}

// Pins: g0-shape#15
#[gpui::test]
fn with_no_open_sessions_the_line_still_loads_settled_ones(cx: &mut TestAppContext) {
    let (_pane, cx) = open_with_older(Some(Vec::new()), false, true, cx);
    assert!(drawn("history-older", cx));
    assert!(!drawn("history-note-empty", cx));
}

// Pins: g0-shape#15
#[gpui::test]
fn before_the_backend_starts_it_says_so(cx: &mut TestAppContext) {
    let (_pane, cx) = open(None, cx);
    assert!(drawn("history-note-waiting", cx));
}
