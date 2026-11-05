//! Links on the Terminal tab: hovering, and what a click does.

use std::cell::RefCell;
use std::rc::Rc;

use base64::Engine as _;
use base64::engine::general_purpose::STANDARD;
use gpui::TestAppContext;
use gpui_kit::{Modifiers, MouseButton, Pixels, Point};

use super::mouse::at;
use super::{Open, Terminals, following, open};
use crate::core::intents::open_url::OpenUrl;

const URL: &str = "https://example.com/a";
const TEXT: &str = "see https://example.com/a now";

/// The tab following a shell that has printed the link, and what `OpenUrl` was asked to open.
async fn printed<'a>(
    cx: &'a mut TestAppContext,
    modes: &str,
) -> (Open<'a>, Rc<RefCell<Vec<String>>>) {
    let terminals = Terminals {
        kept: format!("{TEXT}{modes}").into_bytes(),
        ..Default::default()
    };
    let mut open = open(cx, terminals, false).await;
    open.until("the output", |_, pane| {
        following(pane) && pane.grid.text() == TEXT
    })
    .await;
    let opened = Rc::new(RefCell::new(Vec::new()));
    let record = opened.clone();
    open.cx.update(|_, cx| {
        cx.on_action(move |intent: &OpenUrl, _| record.borrow_mut().push(intent.url.clone()));
    });
    (open, opened)
}

fn on_the_link(open: &mut Open<'_>) -> Point<Pixels> {
    at(open, 10.5, 0.5)
}

fn hover(open: &mut Open<'_>, position: Point<Pixels>) {
    open.cx
        .simulate_mouse_move(position, None, Modifiers::none());
    open.cx.run_until_parked();
}

fn hovered_url(open: &mut Open<'_>) -> Option<String> {
    (open.pane).read_with(open.cx, |pane, _| {
        pane.hovered.as_ref().map(|link| link.url.clone())
    })
}

fn modifier(open: &mut Open<'_>) -> Modifiers {
    open.cx.update(|_, cx| Modifiers::secondary_key(cx))
}

#[gpui::test]
async fn hover_underlines_and_shows_a_hand(cx: &mut TestAppContext) {
    let (mut open, _) = printed(cx, "").await;
    let position = on_the_link(&mut open);
    hover(&mut open, position);
    // Rendering underlines these cells and shows the hand while the pane holds a link.
    open.pane.read_with(open.cx, |pane, _| {
        let link = pane.hovered.as_ref().expect("the link under the mouse");
        assert_eq!(link.url, URL);
        assert_eq!(link.line_spans, vec![(0, 4, 4 + URL.len())]);
    });
}

#[gpui::test]
async fn leaving_clears_it(cx: &mut TestAppContext) {
    let (mut open, _) = printed(cx, "").await;
    let position = on_the_link(&mut open);
    hover(&mut open, position);
    assert_eq!(hovered_url(&mut open).as_deref(), Some(URL));
    let off = at(&mut open, 1.5, 0.5);
    hover(&mut open, off);
    assert_eq!(hovered_url(&mut open), None);
}

#[gpui::test]
async fn the_modifier_click_dispatches_open_url(cx: &mut TestAppContext) {
    let (mut open, opened) = printed(cx, "").await;
    let (position, modifiers) = (on_the_link(&mut open), modifier(&mut open));
    open.cx.simulate_click(position, modifiers);
    open.cx.run_until_parked();
    assert_eq!(*opened.borrow(), vec![URL.to_string()]);
}

#[gpui::test]
async fn a_plain_click_does_not(cx: &mut TestAppContext) {
    let (mut open, opened) = printed(cx, "").await;
    let position = on_the_link(&mut open);
    // It is a link: the same place hovers as one.
    hover(&mut open, position);
    assert_eq!(hovered_url(&mut open).as_deref(), Some(URL));
    open.cx.simulate_click(position, Modifiers::none());
    open.cx.run_until_parked();
    assert!(opened.borrow().is_empty());
}

#[gpui::test]
async fn with_a_program_holding_the_mouse_the_modifier_click_opens_and_reports_nothing(
    cx: &mut TestAppContext,
) {
    let (mut open, opened) = printed(cx, "\x1b[?1000h\x1b[?1006h").await;
    let (position, modifiers) = (on_the_link(&mut open), modifier(&mut open));
    open.cx
        .simulate_mouse_down(position, MouseButton::Left, modifiers);
    open.cx
        .simulate_mouse_up(position, MouseButton::Left, modifiers);
    open.cx.run_until_parked();
    assert_eq!(*opened.borrow(), vec![URL.to_string()]);
    let sent = open.terminals.lock().unwrap().of("write");
    let sent: Vec<_> = (sent.iter())
        .map(|args| STANDARD.decode(args["data"].as_str().unwrap()).unwrap())
        .collect();
    assert!(sent.is_empty(), "the program heard {sent:?}");
}

#[gpui::test]
async fn the_modifier_click_does_not_start_a_selection(cx: &mut TestAppContext) {
    let (mut open, opened) = printed(cx, "").await;
    let (position, modifiers) = (on_the_link(&mut open), modifier(&mut open));
    open.cx
        .simulate_mouse_down(position, MouseButton::Left, modifiers);
    open.pane.read_with(open.cx, |pane, _| {
        assert!(!pane.selecting, "it started a selection")
    });
    open.cx
        .simulate_mouse_up(position, MouseButton::Left, modifiers);
    open.cx.run_until_parked();
    assert_eq!(*opened.borrow(), vec![URL.to_string()]);
}
