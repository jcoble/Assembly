//! `Launcher` owns the backend child. It starts `Assembly.Desktop serve` with the lane's options, and one thread of
//! its own reads the child's stdout and stderr with blocking reads (no timeout), appends every line to
//! `<home>/backend.log` (as the old shell did), and on the ready line (`listening on <address>`,
//! dotnet/Assembly.Desktop/Serve/DesktopServe.cs) reads the token file and calls the socket client's `reconnect()`
//! (lane g0-socket). That thread owns the `Child` and reaps it when its output ends.
//!
//! - A child that was ready and exits is started again at once; the new one's ready line reconnects.
//! - A child that exits before it was ever ready isn't started again: the window shows the failure and the log's
//!   path, and the palette's "Restart backend" command starts one when none is running. There's no time limit on
//!   the ready line (no timers): until it comes the window shows "starting backend".
//! - On quit it sends the child SIGTERM and waits until the thread has reaped it; the backend finishes its queued
//!   writes and bounds that wait itself (8 s, ApiServer.ShutdownTimeout). The wait is in the quit handler's body, not
//!   a quit future, which GPUI cuts off after 200 ms.
//!
//! The token is never printed or logged.

use std::sync::atomic::{AtomicBool, AtomicU32};
use std::sync::mpsc::Receiver;
use std::sync::Arc;

use gpui_kit::*;

use super::backend_config::BackendConfig;
use super::backend_ready::BackendReady;

pub struct Launcher {
    config: Arc<BackendConfig>,
    reconnect: Arc<dyn Fn(BackendReady) + Send + Sync>,
    /// The running child's process id, 0 when none runs. Quit signals it without taking any lock.
    pid: Arc<AtomicU32>,
    /// Set on quit, so the thread doesn't start another child.
    quitting: Arc<AtomicBool>,
    /// Gets a message each time the thread reaps a child.
    reaped: Receiver<()>,
}

impl Global for Launcher {}

impl Launcher {
    /// Starts the backend and its output thread, and sets the quit handler.
    pub fn start(
        _config: BackendConfig,
        _reconnect: impl Fn(BackendReady) + Send + Sync + 'static,
        _cx: &mut App,
    ) -> Result<(), String> {
        todo!()
    }

    /// Starts a backend when none is running (the palette's "Restart backend"); does nothing when one is.
    pub fn restart(_cx: &mut App) {
        todo!()
    }
}
