//! `NativeViews` with fake surfaces that record their calls. Pins: g0-host#5, g0-host#6, g0-host#9.

use std::cell::RefCell;
use std::rc::Rc;

use gpui_kit::{Bounds, Pixels, point, px, size};

use super::{NativeSurface, NativeViews};

type Calls = Rc<RefCell<Vec<String>>>;

struct Fake(Calls);

impl NativeSurface for Fake {
    fn place(&mut self, frame: Option<Bounds<Pixels>>) {
        self.0.borrow_mut().push(match frame {
            Some(frame) => format!("show {}", f32::from(frame.origin.x)),
            None => "hide".into(),
        });
    }

    fn release(self: Box<Self>) {
        self.0.borrow_mut().push("release".into());
    }
}

fn pane(x: f32) -> Bounds<Pixels> {
    Bounds::new(point(px(x), px(40.)), size(px(300.), px(200.)))
}

#[test]
fn a_view_opens_hidden_and_only_its_laid_out_view_shows() {
    let (calls_a, calls_b) = (Calls::default(), Calls::default());
    let mut views = NativeViews::default();
    let a = views.open(Box::new(Fake(calls_a.clone())));
    views.open(Box::new(Fake(calls_b.clone())));
    views.views.get_mut(&a).unwrap().0.laid_out(pane(10.));
    views.end_frame(false);
    assert_eq!(*calls_a.borrow(), ["hide", "show 10"]);
    assert_eq!(*calls_b.borrow(), ["hide"]);
}

#[test]
fn every_view_hides_while_covered() {
    let calls = Calls::default();
    let mut views = NativeViews::default();
    let a = views.open(Box::new(Fake(calls.clone())));
    for covered in [false, true, false] {
        views.views.get_mut(&a).unwrap().0.laid_out(pane(10.));
        views.end_frame(covered);
    }
    assert_eq!(*calls.borrow(), ["hide", "show 10", "hide", "show 10"]);
}

#[test]
fn closing_a_view_hides_and_releases_it() {
    let calls = Calls::default();
    let mut views = NativeViews::default();
    let a = views.open(Box::new(Fake(calls.clone())));
    views.views.get_mut(&a).unwrap().0.laid_out(pane(10.));
    views.end_frame(false);
    views.remove(a);
    views.end_frame(false);
    assert_eq!(*calls.borrow(), ["hide", "show 10", "hide", "release"]);
}
