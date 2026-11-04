//! Who may load a picture now: two at a time, so slow pictures can't fill the background pool, and only while a screen
//! that shows pictures is open. A load with no turn parks without holding a thread. A load already asking can't be
//! stopped; its own time limit ends it.
use futures_channel::oneshot;
use std::collections::VecDeque;
use std::sync::{Arc, Mutex, MutexGuard, PoisonError};

const MOST_AT_ONCE: usize = 2;

#[derive(Default)]
struct State {
    watching: usize,
    running: usize,
    waiting: VecDeque<oneshot::Sender<Turn>>,
}
#[derive(Default)]
pub(super) struct Gate(Mutex<State>);
/// A load's place among those running; dropping it hands the place on. A turn sent to a load that's since gone is
/// dropped with its channel, so the place isn't lost.
pub(super) struct Turn(Arc<Gate>);
impl Gate {
    fn state(&self) -> MutexGuard<'_, State> {
        self.0.lock().unwrap_or_else(PoisonError::into_inner)
    }
    pub(super) fn wanted(&self) -> bool {
        self.state().watching > 0
    }
    pub(super) fn watch(self: &Arc<Self>) {
        self.state().watching += 1;
        self.hand_out();
    }
    pub(super) fn unwatch(&self) {
        self.state().watching -= 1;
    }
    pub(super) async fn turn(self: Arc<Self>) -> Turn {
        loop {
            let waiting = {
                let mut state = self.state();
                if state.watching > 0 && state.running < MOST_AT_ONCE {
                    state.running += 1;
                    drop(state);
                    return Turn(self);
                }
                let (send, waiting) = oneshot::channel();
                state.waiting.push_back(send);
                waiting
            };
            if let Ok(turn) = waiting.await {
                return turn;
            }
        }
    }
    fn hand_out(self: &Arc<Self>) {
        loop {
            let next = {
                let mut state = self.state();
                if state.watching == 0 || state.running >= MOST_AT_ONCE {
                    return;
                }
                let Some(next) = state.waiting.pop_front() else {
                    return;
                };
                state.running += 1;
                next
            };
            // Refused when that load is gone: the returned turn drops here and hands its place on.
            _ = next.send(Turn(self.clone()));
        }
    }
}
impl Drop for Turn {
    fn drop(&mut self) {
        self.0.state().running -= 1;
        self.0.hand_out();
    }
}
#[cfg(test)]
#[path = "gate_tests.rs"]
mod tests;
