//! The launcher against a fake backend: a bash script that prints what the real one prints. It counts its runs in a
//! file, and writes `stopped` when it gets SIGTERM. Pins: g0-host#1, g0-host#2, g0-host#3, g0-host#16.

use std::path::PathBuf;
use std::sync::Arc;
use std::sync::mpsc::{Receiver, channel};
use std::time::Duration;

use super::{BackendConfig, Launcher};

const TOKEN: &str = "0123456789abcdef";

/// A temp folder holding the fake backend `script` (its body; `$DIR` is the folder) and a token file.
fn fake(name: &str, script: &str) -> BackendConfig {
    let dir = std::env::temp_dir().join(format!("g0-host-launcher-{}-{name}", std::process::id()));
    _ = std::fs::remove_dir_all(&dir);
    std::fs::create_dir_all(&dir).unwrap();
    let program = dir.join("Assembly.Desktop");
    let body = format!(
        "#!/usr/bin/env bash\nDIR={dir}\nn=$(cat $DIR/runs 2>/dev/null || echo 0); n=$((n+1)); echo $n > $DIR/runs\ntrap 'echo stopped > $DIR/stopped; kill $! 2>/dev/null; exit 0' TERM\n{script}\n",
        dir = dir.display()
    );
    std::fs::write(&program, body).unwrap();
    std::process::Command::new("chmod")
        .arg("+x")
        .arg(&program)
        .status()
        .unwrap();
    std::fs::write(dir.join("token.env"), format!("ASSEMBLY_TOKEN={TOKEN}\n")).unwrap();
    BackendConfig {
        program,
        port: 18877,
        home: dir.join("home"),
        token_file: dir.join("token.env"),
        remote: None,
        remote_port: 9877,
        remote_token_file: dir.join("remote.env"),
        ready_limit: Duration::from_secs(60),
    }
}

/// Waits for SIGTERM (its trap is set before the script prints anything) with no wait on time.
const WAIT_FOR_TERM: &str = "tail -f /dev/null & wait";

fn launch(config: BackendConfig) -> (Launcher, Receiver<String>) {
    let (sender, receiver) = channel();
    let ready = move |token: Option<String>| {
        if let Some(token) = token {
            sender.send(token).unwrap()
        }
    };
    (Launcher::new(config, Arc::new(ready)), receiver)
}

/// Every call the launcher makes to the window: a token for each ready backend, `None` when it gives up.
fn launch_noting_give_ups(config: BackendConfig) -> (Launcher, Receiver<Option<String>>) {
    let (sender, receiver) = channel();
    let ready = move |token: Option<String>| sender.send(token).unwrap();
    (Launcher::new(config, Arc::new(ready)), receiver)
}

fn read(config: &BackendConfig, name: &str) -> String {
    let path: PathBuf = config.token_file.parent().unwrap().join(name);
    std::fs::read_to_string(path).unwrap_or_default()
}

#[test]
fn the_ready_line_hands_over_the_token_and_quit_stops_the_backend() {
    let config = fake(
        "ready",
        &format!("echo starting\necho 'listening on http://127.0.0.1:18877/'\n{WAIT_FOR_TERM}"),
    );
    let (mut launcher, ready) = launch(config);
    assert_eq!(ready.recv().unwrap(), TOKEN);
    launcher.stop();
    let home = launcher.config.home.clone();
    assert_eq!(read(&launcher.config, "stopped"), "stopped\n");
    assert_eq!(read(&launcher.config, "runs"), "1\n");
    assert_eq!(*launcher.pid.lock().unwrap(), 0);
    let log = std::fs::read_to_string(home.join("backend.log")).unwrap();
    assert!(
        log.contains("starting") && log.contains("listening on"),
        "{log}"
    );
}

#[test]
fn a_backend_that_was_ready_and_exits_is_started_again() {
    let script = format!(
        "echo 'listening on http://127.0.0.1:18877/'\n[ $n -ge 2 ] || exit 1\n{WAIT_FOR_TERM}"
    );
    let (mut launcher, ready) = launch(fake("restart", &script));
    assert_eq!(ready.recv().unwrap(), TOKEN);
    assert_eq!(ready.recv().unwrap(), TOKEN);
    launcher.stop();
    assert_eq!(read(&launcher.config, "runs"), "2\n");
    assert_eq!(read(&launcher.config, "stopped"), "stopped\n");
}

#[test]
fn a_backend_that_exits_before_it_was_ready_is_not_started_again_until_asked() {
    let (mut launcher, ready) = launch(fake("early", "echo 'no agents.json' >&2\nexit 3"));
    launcher.thread.take().unwrap().join().unwrap();
    assert!(ready.try_recv().is_err());
    assert_eq!(read(&launcher.config, "runs"), "1\n");
    let log = std::fs::read_to_string(launcher.config.home.join("backend.log")).unwrap();
    assert!(
        log.contains("no agents.json") && log.contains("stopped before it was ready"),
        "{log}"
    );

    launcher.restart_if_stopped();
    launcher.thread.take().unwrap().join().unwrap();
    assert_eq!(read(&launcher.config, "runs"), "2\n");
}

#[test]
fn a_backend_that_stops_soon_after_ready_three_times_in_a_row_is_not_started_again_until_asked() {
    let config = fake(
        "flapping",
        "echo 'listening on http://127.0.0.1:18877/'\nexit 1",
    );
    let (mut launcher, ready) = launch(config);
    launcher.thread.take().unwrap().join().unwrap();
    assert_eq!(ready.try_iter().count(), 3);
    assert_eq!(read(&launcher.config, "runs"), "3\n");
    let log = std::fs::read_to_string(launcher.config.home.join("backend.log")).unwrap();
    assert!(log.contains("3 times in a row"), "{log}");

    launcher.restart_if_stopped();
    launcher.thread.take().unwrap().join().unwrap();
    assert_eq!(read(&launcher.config, "runs"), "6\n");
}

#[test]
fn the_token_never_reaches_the_log() {
    let script = format!("echo 'listening on http://127.0.0.1:18877/'\n{WAIT_FOR_TERM}");
    let (mut launcher, ready) = launch(fake("secret", &script));
    ready.recv().unwrap();
    launcher.stop();
    let log = std::fs::read_to_string(launcher.config.home.join("backend.log")).unwrap();
    assert!(!log.contains(TOKEN), "{log}");
}

#[test]
fn dropping_the_launcher_stops_the_backend() {
    let script = format!("echo 'listening on http://127.0.0.1:18877/'\n{WAIT_FOR_TERM}");
    let (launcher, ready) = launch(fake("drop", &script));
    ready.recv().unwrap();
    let pid = *launcher.pid.lock().unwrap();
    let config = launcher.config.clone();
    drop(launcher);
    assert_eq!(read(&config, "stopped"), "stopped\n");
    // SAFETY: signal 0 only asks whether the process exists.
    assert_eq!(
        unsafe { libc::kill(pid as i32, 0) },
        -1,
        "the child is gone"
    );
}

#[test]
fn a_backend_with_no_ready_line_in_time_is_stopped() {
    let mut config = fake("late", WAIT_FOR_TERM);
    config.ready_limit = Duration::from_millis(200);
    let (mut launcher, ready) = launch(config);
    launcher.thread.take().unwrap().join().unwrap();
    assert!(ready.try_recv().is_err());
    assert_eq!(read(&launcher.config, "stopped"), "stopped\n");
    let log = std::fs::read_to_string(launcher.config.home.join("backend.log")).unwrap();
    assert!(log.contains("no ready line within 200ms"), "{log}");
}

// Pins: g0-shape#3
#[test]
fn a_launcher_that_gives_up_tells_the_window_once() {
    let (mut launcher, calls) = launch_noting_give_ups(fake("gives-up", "exit 3"));
    launcher.thread.take().unwrap().join().unwrap();
    assert_eq!(calls.try_iter().collect::<Vec<_>>(), [None]);
}

// Pins: g0-shape#3
#[test]
fn quitting_is_not_giving_up() {
    let script = format!("echo 'listening on http://127.0.0.1:18877/'\n{WAIT_FOR_TERM}");
    let (mut launcher, calls) = launch_noting_give_ups(fake("quits", &script));
    assert_eq!(calls.recv().unwrap(), Some(TOKEN.to_string()));
    launcher.stop();
    assert_eq!(calls.try_iter().count(), 0);
}
