//! The gallery draws every piece open and shut, since it's where anyone first sees one break.
use std::time::Duration;

use gpui::TestAppContext;
use gpui_kit::{AnyElement, AppContext as _, Entity, IntoElement as _, VisualTestContext, div};

use super::{ComponentGallery, NEED_KEYS};
use crate::activity::NeedsCard;

fn draw(cx: &mut VisualTestContext) {
    cx.run_until_parked();
    cx.update(|window, cx| window.draw(cx).clear(cx));
    cx.run_until_parked();
}

fn settle(cx: &mut VisualTestContext) {
    for _ in 0..60 {
        cx.executor().advance_clock(Duration::from_millis(16));
        draw(cx);
    }
}

fn open(cx: &mut TestAppContext) -> (Entity<ComponentGallery>, &mut VisualTestContext) {
    cx.update(gpui_kit::init);
    cx.update(crate::inline_field::init);
    let (gallery, cx) = cx.add_window_view(ComponentGallery::new);
    draw(cx);
    (gallery, cx)
}

fn plain_body(_: &mut gpui_kit::Window, _: &mut gpui_kit::App) -> AnyElement {
    div().into_any_element()
}

#[gpui::test]
fn every_row_can_open_with_the_rest_of_the_page_drawn(cx: &mut TestAppContext) {
    let (gallery, cx) = open(cx);
    let (runs, steps, sessions) = cx.update(|_, cx| {
        let gallery = gallery.read(cx);
        (
            gallery.runs.clone(),
            gallery.steps.clone(),
            gallery.sessions.clone(),
        )
    });
    for (drops, key) in [
        (&runs, "gallery-run-live"),
        (&steps, "gallery-steps:failed"),
        (&sessions, "gallery-session-full"),
    ] {
        drops.update(cx, |drops, cx| drops.toggle(key.into(), plain_body, cx));
    }
    settle(cx);
    cx.update(|_, cx| {
        assert!(runs.read(cx).is_open(&"gallery-run-live".into()));
        assert!(steps.read(cx).is_open(&"gallery-steps:failed".into()));
        assert!(sessions.read(cx).is_open(&"gallery-session-full".into()));
    });
}

#[gpui::test]
fn answering_shrinks_the_card_and_a_failed_send_keeps_it_open(cx: &mut TestAppContext) {
    let (gallery, cx) = open(cx);
    let needs = cx.update(|_, cx| gallery.read(cx).needs.clone());
    let first = NEED_KEYS[0].into();
    cx.update(|_, cx| {
        needs.update(cx, |drops, cx| {
            drops.toggle(NEED_KEYS[1].into(), plain_body, cx)
        });
        gallery.update(cx, |gallery, cx| gallery.send(true, cx));
    });
    settle(cx);
    cx.update(|_, cx| {
        assert!(
            needs.read(cx).is_open(&NEED_KEYS[1].into()),
            "the failed card stays open"
        );
        assert!(gallery.read(cx).failed.is_some());
        assert!(!gallery.read(cx).answered);
    });
    cx.update(|_, cx| {
        let body = std::rc::Rc::new(plain_body);
        NeedsCard::open(NEED_KEYS[0].into(), &needs, body, cx);
        gallery.update(cx, |gallery, cx| gallery.send(false, cx));
    });
    settle(cx);
    cx.update(|_, cx| {
        assert!(!needs.read(cx).is_open(&first));
        assert!(gallery.read(cx).answered);
    });
}
