//! The History tab over a held sessions list: folded projects, opening one, showing more, picking a session.

use gpui::TestAppContext;
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
    cx.update(gpui_kit::init);
    cx.update(SelectedSession::init);
    if let Some(items) = items {
        cx.update(|cx| Sessions::start_holding(items, cx));
    }
    let (pane, cx) = cx.add_window_view(|_, cx| HistoryPane::new(cx));
    draw(cx);
    (pane, cx)
}

fn session(id: &str, folder: &str, machine: Option<&str>) -> SessionItem {
    serde_json::from_value(
        json!({ "sessionId": id, "agent": "claude", "repoUrl": folder, "machine": machine,
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
fn a_click_on_a_session_opens_it(cx: &mut TestAppContext) {
    let (_pane, cx) = open(Some(two_projects()), cx);
    click("history-project-/w/notes", cx);
    click("history-session-s1", cx);
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
fn a_tick_draws_each_sessions_age(cx: &mut TestAppContext) {
    let (pane, cx) = open(Some(two_projects()), cx);
    click("history-project-/w/notes", cx);
    assert!(
        !drawn("history-age-s3", cx),
        "no age before the clock's first tick"
    );
    pane.update(cx, |pane, cx| pane.tick(5 * 60_000, cx));
    draw(cx);
    assert!(drawn("history-age-s3", cx));
}

// Pins: g0-shape#15
#[gpui::test]
fn it_says_when_there_are_no_sessions_or_no_backend_yet(cx: &mut TestAppContext) {
    let (_pane, cx) = open(Some(Vec::new()), cx);
    assert!(drawn("history-note-empty", cx));
}

// Pins: g0-shape#15
#[gpui::test]
fn before_the_backend_starts_it_says_so(cx: &mut TestAppContext) {
    let (_pane, cx) = open(None, cx);
    assert!(drawn("history-note-waiting", cx));
}
