//! `Launcher` owns the backend child. It starts `Assembly.Desktop serve` with the lane's options, and one thread of
//! its own reads the child's stdout with blocking reads (no timeout) and appends every line to `<home>/backend.log`,
//! where the child's stderr goes too (as the old shell did). On the ready line (`listening on <address>`,
//! dotnet/Assembly.Desktop/Serve/DesktopServe.cs) it reads the token file and hands the token to the window, which
//! starts or reconnects the socket client (lane g0-socket). That thread owns the `Child` and reaps it when its
//! output ends.
//!
//! - A child that was ready and exits is started again at once; the new one's ready line reconnects. One that stops
//!   within 30 s of its ready line 3 times in a row isn't started again: the log says so, and "Restart backend"
//!   starts it.
//! - A child that exits before it was ever ready isn't started again: the log says so, and the palette's "Restart
//!   backend" command starts one when none is running.
//! - A child that prints no ready line within `ready_limit` (60 s; a hung remote tunnel, say) is stopped, and the
//!   log says why.
//! - On quit it sends the child SIGTERM and waits until the thread has reaped it; the backend finishes its queued
//!   writes and bounds that wait itself (8 s, ApiServer.ShutdownTimeout). One still running after `stop_grace` (10 s)
//!   gets SIGKILL, and the log says so. The wait is in the quit handler's body, not a quit future, which GPUI cuts off
//!   after 200 ms.
//!
//! - The child's stdin is a pipe held until it exits, so it sees end of file if the window dies without quitting.
//! - Dropping the launcher stops the child the same way, so nothing outlives the window.
//!
//! It never attaches to a backend it didn't start: a backend that can't take its port or its home folder exits
//! before it's ready, and backend.log says why. The token is never printed or logged.

use std::fs::{File, OpenOptions};
use std::io::{BufRead, BufReader, Write};
use std::process::{Command, Stdio};
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::mpsc::{self, RecvTimeoutError};
use std::sync::{Arc, Mutex};
use std::thread::JoinHandle;
use std::time::{Duration, Instant};

use gpui_kit::*;

use super::backend_config::BackendConfig;
use crate::core::commands::command_registry::CommandRegistry;

actions!(backend, [RestartBackend]);

/// Gets the token each time a backend is ready, and `None` when the launcher gives up (it starts no other backend until
/// "Restart backend").
type Ready = Arc<dyn Fn(Option<String>) + Send + Sync>;

pub struct Launcher {
    config: Arc<BackendConfig>,
    ready: Ready,
    /// The running child's process id, 0 when none runs. The thread clears it, under this lock, before it reaps the
    /// child, so a pid read under the lock always names our child (an exited child keeps its pid until reaped).
    pid: Arc<Mutex<u32>>,
    /// Set on quit, so the thread doesn't start another child.
    quitting: Arc<AtomicBool>,
    thread: Option<JoinHandle<()>>,
    /// Disconnects when the thread ends, so a stop can wait for it with a deadline.
    thread_done: Option<mpsc::Receiver<()>>,
    /// The main thread's loop over ready tokens, and the quit handler; held, never detached.
    _ready_loop: Option<Task<()>>,
    _quit: Option<Subscription>,
}

impl Global for Launcher {}

/// Dropping the launcher stops the backend, so nothing outlives the window.
impl Drop for Launcher {
    fn drop(&mut self) {
        self.stop();
    }
}

impl Launcher {
    /// Starts the backend and its output thread, calls `on_ready` on the main thread with the port and token each
    /// time a backend is ready, and `on_gave_up` each time the launcher gives up, and stops the backend when the app
    /// quits.
    pub fn start(
        config: BackendConfig,
        on_ready: impl Fn(u16, String, &mut App) + 'static,
        on_gave_up: impl Fn(&mut App) + 'static,
        cx: &mut App,
    ) {
        let port = config.port;
        let (sender, mut receiver) = futures_channel::mpsc::unbounded::<Option<String>>();
        let mut launcher = Self::new(
            config,
            Arc::new(move |token| _ = sender.unbounded_send(token)),
        );
        launcher._ready_loop = Some(cx.spawn(async move |cx| {
            while let Ok(token) = receiver.recv().await {
                cx.update(|cx| match token {
                    Some(token) => on_ready(port, token, cx),
                    None => on_gave_up(cx),
                });
            }
        }));
        launcher._quit = Some(cx.on_app_quit(|cx| {
            cx.global_mut::<Self>().stop();
            async {}
        }));
        cx.set_global(launcher);
        cx.on_action(|_: &RestartBackend, cx| cx.global_mut::<Self>().restart_if_stopped());
        CommandRegistry::add("Restart backend", Box::new(RestartBackend), cx);
    }

    fn new(config: BackendConfig, ready: Ready) -> Self {
        let mut launcher = Self {
            config: Arc::new(config),
            ready,
            pid: Arc::new(Mutex::new(0)),
            quitting: Arc::new(AtomicBool::new(false)),
            thread: None,
            thread_done: None,
            _ready_loop: None,
            _quit: None,
        };
        launcher.restart_if_stopped();
        launcher
    }

    /// Starts a backend when none is running; does nothing when one is. The palette's "Restart backend" runs it.
    fn restart_if_stopped(&mut self) {
        if self
            .thread
            .as_ref()
            .is_some_and(|thread| !thread.is_finished())
        {
            return;
        }
        let (config, ready, pid, quitting) = (
            self.config.clone(),
            self.ready.clone(),
            self.pid.clone(),
            self.quitting.clone(),
        );
        let (done, thread_done) = mpsc::channel();
        self.thread_done = Some(thread_done);
        self.thread = Some(
            std::thread::Builder::new()
                .name("backend".into())
                .spawn(move || {
                    let _done = done;
                    run(&config, &*ready, &pid, &quitting);
                    if !quitting.load(Ordering::SeqCst) {
                        ready(None);
                    }
                })
                .expect("start the backend's thread"),
        );
    }

    /// Sends the child SIGTERM, then SIGKILL if it's still running after `stop_grace`, and waits until it's reaped; no
    /// other child starts after.
    fn stop(&mut self) {
        self.quitting.store(true, Ordering::SeqCst);
        self.signal(libc::SIGTERM);
        if let Some(done) = self.thread_done.take()
            && done.recv_timeout(self.config.stop_grace) == Err(RecvTimeoutError::Timeout)
            && self.signal(libc::SIGKILL)
        {
            let log = OpenOptions::new()
                .append(true)
                .open(self.config.home.join("backend.log"));
            if let Ok(mut log) = log {
                let grace = self.config.stop_grace;
                _ = writeln!(
                    log,
                    "the backend didn't stop within {grace:?} of SIGTERM, so it was killed"
                );
            }
        }
        if let Some(thread) = self.thread.take() {
            _ = thread.join();
        }
    }

    /// Sends the running child `signal`; false when none runs.
    fn signal(&self, signal: i32) -> bool {
        let pid = self.pid.lock().unwrap();
        // SAFETY: kill only sends a signal, and the pid is our unreaped child's (see `Launcher::pid`).
        *pid != 0 && unsafe { libc::kill(*pid as i32, signal) } == 0
    }
}

/// A child that stops this soon after its ready line, `QUICK_EXITS` times in a row, isn't started again.
const QUICK_EXIT: Duration = Duration::from_secs(30);
const QUICK_EXITS: u32 = 3;

/// The thread: starts children until one exits before it was ready, children keep stopping soon after ready, or the
/// app quits.
fn run(
    config: &BackendConfig,
    ready: &(dyn Fn(Option<String>) + Send + Sync),
    pid: &Mutex<u32>,
    quitting: &AtomicBool,
) {
    let log_path = config.home.join("backend.log");
    let log = std::fs::create_dir_all(&config.home)
        .and_then(|_| OpenOptions::new().create(true).append(true).open(&log_path));
    let Ok(mut log) = log else {
        eprintln!("the backend's log {} can't be opened", log_path.display());
        return;
    };
    let mut quick_exits = 0;
    while !quitting.load(Ordering::SeqCst) {
        let ready_at = run_child(config, &mut log, ready, pid, quitting);
        if quitting.load(Ordering::SeqCst) {
            return;
        }
        let stopped = match ready_at {
            None => "the backend stopped before it was ready",
            Some(at) if at.elapsed() < QUICK_EXIT => {
                quick_exits += 1;
                if quick_exits < QUICK_EXITS {
                    continue;
                }
                "the backend stopped within 30 s of being ready 3 times in a row"
            }
            Some(_) => {
                quick_exits = 0;
                continue;
            }
        };
        let line = format!(
            "{stopped}, so it isn't started again until the palette's \"Restart backend\"; see {}",
            log_path.display()
        );
        _ = writeln!(log, "{line}");
        eprintln!("{line}");
        return;
    }
}

/// Runs one child to its end; when it printed its ready line, the time it did.
fn run_child(
    config: &BackendConfig,
    log: &mut File,
    ready: &(dyn Fn(Option<String>) + Send + Sync),
    pid: &Mutex<u32>,
    quitting: &AtomicBool,
) -> Option<Instant> {
    let mut command = Command::new(&config.program);
    command
        .arg("serve")
        .arg("--port")
        .arg(config.port.to_string());
    command
        .arg("--home")
        .arg(&config.home)
        .arg("--token-file")
        .arg(&config.token_file);
    if let Some(remote) = &config.remote {
        command
            .arg("--remote")
            .arg(remote)
            .arg("--remote-port")
            .arg(config.remote_port.to_string());
        command
            .arg("--remote-token-file")
            .arg(&config.remote_token_file);
    }
    let stderr = match log.try_clone() {
        Ok(file) => Stdio::from(file),
        Err(_) => Stdio::null(),
    };
    let mut child = match command
        .stdin(Stdio::piped())
        .stdout(Stdio::piped())
        .stderr(stderr)
        .spawn()
    {
        Ok(child) => child,
        Err(error) => {
            _ = writeln!(
                log,
                "the backend {} didn't start: {error}",
                config.program.display()
            );
            return None;
        }
    };
    {
        let mut pid = pid.lock().unwrap();
        *pid = child.id();
        // A quit that came while this child was starting found no pid to signal.
        if quitting.load(Ordering::SeqCst) {
            terminate(*pid);
        }
    }
    let child_id = child.id();
    let mut ready_at = None;
    let late = std::thread::scope(|scope| {
        // Stops a child that prints no ready line in time, such as one whose remote tunnel hangs.
        let (seen, watch) = mpsc::channel::<()>();
        let watchdog = scope.spawn(move || {
            let late = watch.recv_timeout(config.ready_limit) == Err(RecvTimeoutError::Timeout);
            if late && *pid.lock().unwrap() == child_id {
                terminate(child_id);
            }
            late
        });
        if let Some(stdout) = child.stdout.take() {
            for line in BufReader::new(stdout).lines().map_while(Result::ok) {
                _ = writeln!(log, "{line}");
                if line.starts_with("listening on ") {
                    match read_token(config) {
                        Ok(token) => {
                            ready_at = Some(Instant::now());
                            _ = seen.send(());
                            ready(Some(token));
                        }
                        Err(error) => _ = writeln!(log, "{error}"),
                    }
                }
            }
        }
        drop(seen);
        watchdog.join().unwrap_or(false)
    });
    if late {
        _ = writeln!(
            log,
            "the backend printed no ready line within {:?}, so it was stopped",
            config.ready_limit
        );
    }
    wait_for_exit(child_id);
    *pid.lock().unwrap() = 0;
    _ = child.wait();
    ready_at
}

fn terminate(pid: u32) {
    // SAFETY: kill only sends a signal, and the pid is our unreaped child's (see `Launcher::pid`).
    unsafe { libc::kill(pid as i32, libc::SIGTERM) };
}

/// Blocks until the child exits, without reaping it, so its pid stays ours until `Child::wait`.
fn wait_for_exit(pid: u32) {
    // SAFETY: waitid only writes the zeroed siginfo it's given.
    unsafe {
        let mut info: libc::siginfo_t = std::mem::zeroed();
        while libc::waitid(
            libc::P_PID,
            pid as libc::id_t,
            &mut info,
            libc::WEXITED | libc::WNOWAIT,
        ) == -1
            && std::io::Error::last_os_error().kind() == std::io::ErrorKind::Interrupted
        {}
    }
}

/// The one `ASSEMBLY_TOKEN=<hex>` line the backend made on its first start (TokenFile.cs).
fn read_token(config: &BackendConfig) -> Result<String, String> {
    let file = &config.token_file;
    let text = std::fs::read_to_string(file).map_err(|e| format!("{}: {e}", file.display()))?;
    text.lines()
        .find_map(|line| line.trim().strip_prefix("ASSEMBLY_TOKEN="))
        .map(str::to_owned)
        .ok_or_else(|| format!("no ASSEMBLY_TOKEN in {}", file.display()))
}

#[cfg(test)]
#[path = "launcher_tests.rs"]
mod tests;
