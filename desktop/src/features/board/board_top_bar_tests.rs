//! The top bar's way in: its key and its badge, in a window with the real feed.
use std::cell::RefCell;
use std::rc::Rc;

use gpui::TestAppContext;
use gpui_kit::component::dock::DockArea;
use gpui_kit::{AppContext as _, Entity, VisualTestContext, px, size};
use serde_json::{Value, json};

use super::BoardPanel;
use crate::core::engine::test_engine::{MACHINE, Seeded, TestEngine};
use crate::features::shell::default_layout::{LAYOUT_VERSION, default_state};
use crate::features::shell::placeholder_pane::PlaceholderPane;
use crate::features::shell::top_bar::TopBar;

fn window(cx: &mut TestAppContext) -> (Entity<TopBar>, &mut VisualTestContext) {
    cx.executor().allow_parking();
    cx.update(|cx| {
        gpui_kit::init(cx);
        PlaceholderPane::register_all(cx);
        BoardPanel::register(cx);
        crate::core::state::selected_session::SelectedSession::init(cx);
    });
    let held: Rc<RefCell<Option<Entity<DockArea>>>> = Rc::default();
    let capture = held.clone();
    let (bar, cx) = cx.add_window_view(move |window, cx| {
        let dock =
            cx.new(|cx| DockArea::new("board-top-bar-test", Some(LAYOUT_VERSION), window, cx));
        dock.update(cx, |dock, cx| _ = dock.load(default_state(), window, cx));
        *capture.borrow_mut() = Some(dock.clone());
        TopBar::new(dock, cx)
    });
    cx.simulate_resize(size(px(1400.), px(900.)));
    draw(cx);
    (bar, cx)
}

fn draw(cx: &mut VisualTestContext) {
    cx.run_until_parked();
    cx.update(|window, cx| window.draw(cx).clear(cx));
}

#[gpui::test]
async fn the_key_opens_and_closes_the_board(cx: &mut TestAppContext) {
    let (bar, cx) = window(cx);
    let open = |cx: &mut VisualTestContext| cx.read(|cx| bar.read(cx).board_reader().is_some());
    assert!(!open(cx));
    cx.simulate_keystrokes("secondary-shift-b");
    draw(cx);
    assert!(open(cx), "the key opens it");
    assert!(cx.debug_bounds("board-panel").is_some());
    cx.simulate_keystrokes("secondary-shift-b");
    draw(cx);
    assert!(!open(cx), "and closes it");
}

fn owner_need(id: &str) -> Value {
    json!({"entityKind":"needs_me","id":id,"machine":MACHINE,"version":1,"sessionId":"s","forOwner":true,"kind":"question","state":"open","text":"Which?","createdMs":1})
}

#[gpui::test]
async fn no_badge_with_nothing_open(cx: &mut TestAppContext) {
    let (_bar, cx) = window(cx);
    assert!(cx.debug_bounds("board-badge").is_none());
}

#[gpui::test]
async fn an_open_question_shows_the_badge(cx: &mut TestAppContext) {
    let seeded = Seeded {
        items: vec![owner_need("q1")],
        ..Seeded::default()
    };
    let engine = TestEngine::start(seeded, &[("needs_me", None)], cx).await;
    let (bar, cx) = window(cx);
    assert!(cx.debug_bounds("board-badge").is_some());
    assert_eq!(
        cx.read(|cx| bar.read(cx).badge_words()).as_deref(),
        Some("1")
    );
    drop(engine);
}
