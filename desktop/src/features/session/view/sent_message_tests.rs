use super::{FOLD_LINES, shown};

// Pins: g0-session#21
#[test]
fn a_prompt_longer_than_ten_lines_folds_until_opened() {
    let ten: String = (1..=FOLD_LINES).map(|n| format!("{n}\n")).collect();
    assert_eq!(shown(ten.trim_end(), false), (ten.trim_end(), false));
    let long = format!("{ten}11");
    let (text, more) = shown(&long, false);
    assert!(more);
    assert_eq!(text.lines().count(), FOLD_LINES);
    assert!(text.ends_with("10"));
    assert_eq!(shown(&long, true), (long.as_str(), true));
}

use super::{SentMessage, SessionAction};
use gpui::TestAppContext;
use gpui_kit::{
    Context, IntoElement, Modifiers, ParentElement as _, Render, Styled as _, Window, div,
};
use std::{cell::RefCell, rc::Rc};

struct SenderHost {
    from: Option<String>,
    label: Option<String>,
    opened: Rc<RefCell<Vec<String>>>,
}
impl Render for SenderHost {
    fn render(&mut self, _: &mut Window, _: &mut Context<Self>) -> impl IntoElement {
        let opened = self.opened.clone();
        div().size_full().child(SentMessage {
            key: "user".into(),
            text: "body".into(),
            sender: self.from.clone(),
            sender_label: self.label.clone(),
            images: vec![],
            open: false,
            on_action: Rc::new(move |action, _, _| {
                if let SessionAction::OpenSession { session_id } = action {
                    opened.borrow_mut().push(session_id);
                }
            }),
        })
    }
}

#[gpui::test]
fn sender_click_uses_identity_and_plain_label_has_no_action(cx: &mut TestAppContext) {
    cx.update(gpui_kit::init);
    let opened = Rc::new(RefCell::new(Vec::new()));
    let capture = opened.clone();
    let (host, cx) = cx.add_window_view(|_, _| SenderHost {
        from: Some("peer/x".into()),
        label: Some("Captured title (peer/x)".into()),
        opened: capture,
    });
    cx.run_until_parked();
    cx.update(|window, cx| window.draw(cx).clear(cx));
    let sender = cx
        .debug_bounds("sent-message-sender")
        .expect("sender drawn");
    cx.simulate_click(sender.center(), Modifiers::none());
    cx.run_until_parked();
    assert_eq!(*opened.borrow(), ["peer/x"]);
    host.update(cx, |host, cx| {
        host.from = None;
        host.label = Some("Assembly".into());
        cx.notify();
    });
    cx.run_until_parked();
    cx.update(|window, cx| window.draw(cx).clear(cx));
    let sender = cx
        .debug_bounds("sent-message-sender")
        .expect("plain label drawn");
    cx.simulate_click(sender.center(), Modifiers::none());
    cx.run_until_parked();
    assert_eq!(*opened.borrow(), ["peer/x"]);
    host.update(cx, |host, cx| {
        host.from = Some("peer/x".into());
        host.label = None;
        cx.notify();
    });
    cx.run_until_parked();
    cx.update(|window, cx| window.draw(cx).clear(cx));
    let sender = cx.debug_bounds("sent-message-sender").unwrap();
    cx.simulate_click(sender.center(), Modifiers::none());
    cx.run_until_parked();
    assert_eq!(*opened.borrow(), ["peer/x", "peer/x"]);
}
