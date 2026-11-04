//! The card's life: it opens, it's answered and shrinks, or the answer fails and it stays.
use std::rc::Rc;
use std::time::Duration;

use gpui::TestAppContext;
use gpui_kit::component::input::{Input, InputState};
use gpui_kit::{
    AppContext as _, Context, Entity, IntoElement, Modifiers, ParentElement as _, Pixels, Render,
    SharedString, Styled as _, Subscription, VisualTestContext, Window, div, px,
};

use super::{CardBody, NeedsCard, NeedsState};
use crate::core::ui::row_drop_down::RowDropDowns;

struct Host {
    drops: Entity<RowDropDowns>,
    state: NeedsState,
    body: CardBody,
    _heard: Subscription,
}

impl Render for Host {
    fn render(&mut self, _: &mut Window, _: &mut Context<Self>) -> impl IntoElement {
        div().w(px(400.)).child(NeedsCard {
            key: key(),
            kind: "Question".into(),
            title: "Which branch?".into(),
            why: "The worktree has two candidates.".into(),
            next_step: Some("Then it opens the PR.".into()),
            chips: Vec::new(),
            time: Some("2m".into()),
            state: self.state.clone(),
            drops: self.drops.clone(),
            body: self.body.clone(),
        })
    }
}

fn key() -> SharedString {
    "need".into()
}

struct Open<'a> {
    host: Entity<Host>,
    drops: Entity<RowDropDowns>,
    cx: &'a mut VisualTestContext,
}

fn open(
    cx: &mut TestAppContext,
    body: impl FnOnce(&mut Window, &mut Context<Host>) -> CardBody + 'static,
) -> Open<'_> {
    cx.update(gpui_kit::init);
    let (host, cx) = cx.add_window_view(move |window, cx| {
        let drops = cx.new(|_| RowDropDowns::default());
        let heard = cx.observe(&drops, |_, _, cx| cx.notify());
        Host {
            drops,
            state: NeedsState::Open,
            body: body(window, cx),
            _heard: heard,
        }
    });
    let drops = cx.update(|_, cx| host.read(cx).drops.clone());
    let mut open = Open { host, drops, cx };
    open.draw();
    open
}

fn small_body(_: &mut Window, _: &mut Context<Host>) -> CardBody {
    Rc::new(|_, _| div().h(px(20.)).child("Answer").into_any_element())
}

impl Open<'_> {
    fn draw(&mut self) {
        self.cx.run_until_parked();
        self.cx.update(|window, cx| window.draw(cx).clear(cx));
        self.cx.run_until_parked();
    }

    fn settle_motion(&mut self) {
        for _ in 0..100 {
            self.cx.executor().advance_clock(Duration::from_millis(16));
            self.draw();
        }
    }

    fn open_from_a_link(&mut self) {
        let (drops, body) = (
            self.drops.clone(),
            self.cx.update(|_, cx| self.host.read(cx).body.clone()),
        );
        self.cx
            .update(|_, cx| NeedsCard::open(key(), &drops, body, cx));
        self.draw();
    }

    fn is_open(&mut self) -> bool {
        let drops = self.drops.clone();
        self.cx.update(|_, cx| drops.read(cx).is_open(&key()))
    }

    fn is_showing(&mut self) -> bool {
        let drops = self.drops.clone();
        self.cx.update(|_, cx| drops.read(cx).is_showing(&key()))
    }

    fn body_height(&mut self) -> Pixels {
        self.cx
            .debug_bounds("needs-body:need")
            .expect("the card is drawn")
            .size
            .height
    }

    fn answer(&mut self) {
        let (host, drops) = (self.host.clone(), self.drops.clone());
        self.cx.update(|_, cx| {
            host.update(cx, |host, cx| {
                host.state = NeedsState::Answered {
                    summary: "Answered: main".into(),
                    delivery: Some("Delivered".into()),
                };
                cx.notify();
            });
            NeedsCard::settle(&key(), &drops, cx);
        });
        self.draw();
    }
}

#[gpui::test]
fn opens_from_a_click_or_a_link(cx: &mut TestAppContext) {
    let mut card = open(cx, small_body);
    let header = card
        .cx
        .debug_bounds("needs-header:need")
        .expect("the card is drawn");
    card.cx
        .simulate_click(header.center(), Modifiers::default());
    card.draw();
    assert!(card.is_open(), "a click opens it");
    card.cx
        .simulate_click(header.center(), Modifiers::default());
    card.draw();
    assert!(!card.is_open(), "a second click shuts it");
    card.open_from_a_link();
    assert!(card.is_open(), "a link opens it");
    card.open_from_a_link();
    assert!(card.is_open(), "a second link leaves it open");
}

#[gpui::test]
fn answering_shuts_it_and_keeps_the_summary_line(cx: &mut TestAppContext) {
    let mut card = open(cx, small_body);
    card.open_from_a_link();
    card.settle_motion();
    card.answer();
    assert!(!card.is_open());
    assert!(card.cx.debug_bounds("needs-summary:need").is_some());
    card.settle_motion();
    assert!(!card.is_showing(), "the body is released once it has shut");
    assert_eq!(card.body_height(), px(0.));
}

#[gpui::test]
fn settling_leaves_another_open_card_alone(cx: &mut TestAppContext) {
    let mut card = open(cx, small_body);
    let drops = card.drops.clone();
    card.cx.update(|_, cx| {
        drops.update(cx, |drops, cx| {
            drops.toggle("other".into(), |_, _| div().into_any_element(), cx)
        });
        NeedsCard::settle(&key(), &drops, cx);
        assert!(drops.read(cx).is_open(&"other".into()));
    });
}

#[gpui::test]
fn failed_answer_stays_open_and_keeps_its_text(cx: &mut TestAppContext) {
    let mut answer = None;
    let seen = &mut answer;
    let mut card = {
        let (sender, receiver) = std::sync::mpsc::channel();
        let card = open(cx, move |window, cx| {
            let input = cx.new(|cx| InputState::new(window, cx));
            sender.send(input.clone()).expect("the test is listening");
            Rc::new(move |_, _| Input::new(&input).into_any_element())
        });
        *seen = receiver.recv().ok();
        card
    };
    let input = answer.expect("the body made its input");
    card.open_from_a_link();
    card.settle_motion();
    card.cx.update(|window, cx| {
        input.update(cx, |input, cx| input.set_value("main", window, cx));
    });
    // The send failed: nothing settles the card, so its body and draft are as they were.
    card.settle_motion();
    assert!(card.is_open());
    assert!(card.body_height() > px(0.));
    assert_eq!(
        card.cx.update(|_, cx| input.read(cx).value().to_string()),
        "main"
    );
}

#[gpui::test]
fn a_body_taller_than_three_rows_is_not_capped(cx: &mut TestAppContext) {
    let mut card = open(cx, |_, _| {
        Rc::new(|_, _| div().h(px(600.)).into_any_element())
    });
    card.open_from_a_link();
    card.settle_motion();
    assert!(card.body_height() >= px(600.), "{:?}", card.body_height());
}

#[gpui::test]
fn removed_item_releases_its_body_via_retain_keys(cx: &mut TestAppContext) {
    let mut card = open(cx, small_body);
    card.open_from_a_link();
    card.settle_motion();
    let drops = card.drops.clone();
    card.cx
        .update(|_, cx| drops.update(cx, |drops, cx| drops.retain_keys(|_| false, cx)));
    card.draw();
    assert!(!card.is_open());
    assert!(!card.is_showing());
}
