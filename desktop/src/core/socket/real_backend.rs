//! `RealBackend` runs the built desktop backend (`dotnet/Assembly.Desktop`) for the tests against it: its own temp
//! home, token file and agents file, and port 0, ready on its "listening on " line. It never touches the default home.
//! The child is killed and waited on by its own handle when the backend is dropped, or when its start fails, and its
//! folder removed.
//!
//! With `ASSEMBLY_REAL_BACKEND` set (CI sets it), a backend that isn't built fails the test. Without it, a test on a
//! checkout where nobody built the backend returns early, which CI never does.

use std::env;
use std::fs;
use std::io::{BufRead, BufReader};
use std::path::{Path, PathBuf};
use std::process::{Child, Command, Stdio};
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::mpsc;
use std::thread;
use std::time::Duration;

use super::endpoint::Endpoint;

/// How long the backend may take to print its ready line.
const READY: Duration = Duration::from_secs(30);

pub(crate) struct RealBackend {
    running: Running,
    pub port: u16,
    token: String,
}

/// The child and its folder: dropping it kills and waits on the child and removes the folder, so a start that fails
/// partway leaves nothing behind.
struct Running {
    child: Child,
    folder: PathBuf,
}

impl RealBackend {
    /// Starts a backend with no agents, or `None` when it isn't built and `ASSEMBLY_REAL_BACKEND` isn't set.
    pub fn start() -> Option<Self> {
        Self::start_with_agents(|_| "[]".into())
    }

    /// As `start`, with the agents file `agents` gives; it gets the backend's folder for any files they need.
    pub fn start_with_agents(agents: impl FnOnce(&Path) -> String) -> Option<Self> {
        let binary = Path::new(env!("CARGO_MANIFEST_DIR"))
            .join("../dotnet/Assembly.Desktop/bin/Debug/net10.0/Assembly.Desktop");
        if !binary.exists() {
            assert!(
                env::var_os("ASSEMBLY_REAL_BACKEND").is_none(),
                "ASSEMBLY_REAL_BACKEND is set but the backend isn't built at {}",
                binary.display()
            );
            eprintln!("real backend: not built, so this test checks nothing here");
            return None;
        }
        let folder = folder();
        let listed = agents(&folder);
        let agents = folder.join("agents.json");
        fs::write(&agents, listed).expect("write the agents file");
        let mut command = Command::new(&binary);
        command
            .arg("serve")
            .args(["--port", "0"])
            .arg("--home")
            .arg(folder.join("home"))
            .arg("--token-file")
            .arg(folder.join("env"))
            .arg("--agents")
            .arg(&agents);
        Some(Self::spawn(command, folder, READY).unwrap_or_else(|error| panic!("{error}")))
    }

    /// Runs `command`, whose files are in `folder`, and waits up to `ready` for its ready line; the token is read
    /// from `folder/env`. On any failure the child is stopped and the folder removed.
    pub fn spawn(mut command: Command, folder: PathBuf, ready: Duration) -> Result<Self, String> {
        let child = command.stdin(Stdio::null()).stdout(Stdio::piped()).spawn();
        let child = match child {
            Ok(child) => child,
            Err(error) => {
                let _ = fs::remove_dir_all(&folder);
                return Err(format!("couldn't start the backend: {error}"));
            }
        };
        let mut running = Running { child, folder };
        // A thread reads stdout to its end, so the child never blocks on a full pipe, and hands each line on.
        let stdout = running.child.stdout.take().expect("its stdout");
        let (lines, ready_lines) = mpsc::channel();
        thread::spawn(move || {
            for line in BufReader::new(stdout).lines().map_while(Result::ok) {
                let _ = lines.send(line);
            }
        });
        let address = loop {
            let line = ready_lines.recv_timeout(ready).map_err(|_| {
                format!("no ready line from the backend within {ready:?} (or it exited)")
            })?;
            if let Some(address) = line.strip_prefix("listening on ") {
                break address.to_owned();
            }
        };
        let port = address
            .trim_end_matches('/')
            .rsplit(':')
            .next()
            .and_then(|port| port.parse().ok())
            .ok_or("no port in the ready line")?;
        let token = fs::read_to_string(running.folder.join("env"))
            .map_err(|error| format!("the backend's token file: {error}"))?
            .lines()
            .find_map(|line| {
                line.trim()
                    .strip_prefix("ASSEMBLY_TOKEN=")
                    .map(str::to_owned)
            })
            .ok_or("no ASSEMBLY_TOKEN line in the token file")?;
        Ok(Self {
            running,
            port,
            token,
        })
    }

    pub fn endpoint(&self) -> Endpoint {
        Endpoint {
            port: self.port,
            token: self.token.clone(),
        }
    }
}

/// A new, empty folder of its own under the temp folder.
pub(super) fn folder() -> PathBuf {
    static NEXT: AtomicU64 = AtomicU64::new(0);
    let folder = env::temp_dir().join(format!(
        "assembly-desktop-test-{}-{}",
        std::process::id(),
        NEXT.fetch_add(1, Ordering::Relaxed)
    ));
    fs::create_dir_all(&folder).expect("make the backend's folder");
    folder
}

impl Drop for Running {
    fn drop(&mut self) {
        let _ = self.child.kill();
        let _ = self.child.wait();
        let _ = fs::remove_dir_all(&self.folder);
    }
}
