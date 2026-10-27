//! The window tests' wait on the real backend (`RealBackend`): until a condition holds, woken by each change, and failing the test past a
//! deadline instead of hanging it.

use std::sync::mpsc;
use std::thread;
use std::time::{Duration, Instant};

use futures_channel::mpsc::{UnboundedReceiver, UnboundedSender};

const DEADLINE: Duration = Duration::from_secs(10);

/// Waits on `changes` until `done` holds. A thread sends on `changed` once the deadline passes, so the wait wakes
/// then and fails.
pub async fn until(
    what: &str,
    changes: &mut UnboundedReceiver<()>,
    changed: &UnboundedSender<()>,
    mut done: impl FnMut() -> bool,
) {
    let (wake, started) = (changed.clone(), Instant::now());
    thread::spawn(move || {
        let (_held, never) = mpsc::channel::<()>();
        _ = never.recv_timeout(DEADLINE);
        _ = wake.unbounded_send(());
    });
    while !done() {
        assert!(
            started.elapsed() < DEADLINE,
            "{what}: not within {DEADLINE:?}"
        );
        _ = changes.recv().await;
    }
}
