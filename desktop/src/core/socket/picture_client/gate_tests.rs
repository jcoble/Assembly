use super::{Gate, Turn};
use std::future::Future;
use std::pin::Pin;
use std::sync::Arc;
use std::task::{Context, Poll, Waker};

type Waiting = Pin<Box<dyn Future<Output = Turn>>>;

fn waiting(gate: &Arc<Gate>) -> Waiting {
    Box::pin(gate.clone().turn())
}
fn poll(load: &mut Waiting) -> Option<Turn> {
    match load.as_mut().poll(&mut Context::from_waker(Waker::noop())) {
        Poll::Ready(turn) => Some(turn),
        Poll::Pending => None,
    }
}
#[test]
fn two_loads_run_and_a_finished_one_lets_the_next_go() {
    let gate = Arc::new(Gate::default());
    gate.watch();
    let (mut first, mut second, mut third) = (waiting(&gate), waiting(&gate), waiting(&gate));
    let first = poll(&mut first).expect("the first runs");
    let _second = poll(&mut second).expect("the second runs");
    assert!(poll(&mut third).is_none(), "the third waits");
    drop(first);
    assert!(
        poll(&mut third).is_some(),
        "the third runs once one finishes"
    );
}
#[test]
fn with_no_screen_watching_a_load_waits_and_starts_when_one_opens() {
    let gate = Arc::new(Gate::default());
    let mut load = waiting(&gate);
    assert!(poll(&mut load).is_none());
    assert!(!gate.wanted());
    gate.watch();
    assert!(gate.wanted());
    assert!(poll(&mut load).is_some());
}
#[test]
fn closing_the_screen_stops_waiting_loads_from_starting() {
    let gate = Arc::new(Gate::default());
    gate.watch();
    let (mut first, mut second, mut third) = (waiting(&gate), waiting(&gate), waiting(&gate));
    let first = poll(&mut first).unwrap();
    let second = poll(&mut second).unwrap();
    assert!(poll(&mut third).is_none());
    gate.unwatch();
    drop(first);
    drop(second);
    assert!(poll(&mut third).is_none(), "nobody is watching");
    gate.watch();
    assert!(poll(&mut third).is_some());
}
#[test]
fn a_load_given_up_on_passes_its_turn_along() {
    let gate = Arc::new(Gate::default());
    gate.watch();
    let (mut first, mut second, mut abandoned, mut last) = (
        waiting(&gate),
        waiting(&gate),
        waiting(&gate),
        waiting(&gate),
    );
    let first = poll(&mut first).unwrap();
    let _second = poll(&mut second).unwrap();
    assert!(poll(&mut abandoned).is_none());
    assert!(poll(&mut last).is_none());
    drop(first);
    drop(abandoned);
    assert!(poll(&mut last).is_some());
}
