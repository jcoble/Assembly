//! `RealBackend` runs the built desktop backend (`dotnet/Assembly.Desktop`) for the tests against it: its own temp
//! home, token file and agents file, and port 0, ready on its "listening on " line. It never touches the default home.
//! The child is killed and waited on by its own handle when the backend is dropped, and its folder removed.
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

pub(super) struct RealBackend {
    child: Child,
    pub port: u16,
    token: String,
    folder: PathBuf,
}

impl RealBackend {
    /// Starts a backend, or `None` when it isn't built and `ASSEMBLY_REAL_BACKEND` isn't set.
    pub fn start() -> Option<Self> {
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
        static NEXT: AtomicU64 = AtomicU64::new(0);
        let folder = env::temp_dir().join(format!(
            "assembly-desktop-test-{}-{}",
            std::process::id(),
            NEXT.fetch_add(1, Ordering::Relaxed)
        ));
        fs::create_dir_all(&folder).expect("make the backend's folder");
        let agents = folder.join("agents.json");
        fs::write(&agents, "[]").expect("write the agents file");
        let token_file = folder.join("env");
        let mut child = Command::new(&binary)
            .arg("serve")
            .args(["--port", "0"])
            .arg("--home")
            .arg(folder.join("home"))
            .arg("--token-file")
            .arg(&token_file)
            .arg("--agents")
            .arg(&agents)
            .stdout(Stdio::piped())
            .spawn()
            .expect("start the backend");
        // A thread reads stdout to its end, so the child never blocks on a full pipe, and hands each line on.
        let stdout = child.stdout.take().expect("its stdout");
        let (lines, ready) = mpsc::channel();
        thread::spawn(move || {
            for line in BufReader::new(stdout).lines().map_while(Result::ok) {
                let _ = lines.send(line);
            }
        });
        let address = loop {
            let line = ready
                .recv_timeout(READY)
                .expect("the backend's ready line within the deadline (or it exited)");
            if let Some(address) = line.strip_prefix("listening on ") {
                break address.to_owned();
            }
        };
        let port = address
            .trim_end_matches('/')
            .rsplit(':')
            .next()
            .and_then(|port| port.parse().ok())
            .expect("a port in the ready line");
        let token = fs::read_to_string(&token_file)
            .expect("the backend's token file")
            .lines()
            .find_map(|line| {
                line.trim()
                    .strip_prefix("ASSEMBLY_TOKEN=")
                    .map(str::to_owned)
            })
            .expect("an ASSEMBLY_TOKEN line");
        Some(Self {
            child,
            port,
            token,
            folder,
        })
    }

    pub fn endpoint(&self) -> Endpoint {
        Endpoint {
            port: self.port,
            token: self.token.clone(),
        }
    }
}

impl Drop for RealBackend {
    fn drop(&mut self) {
        let _ = self.child.kill();
        let _ = self.child.wait();
        let _ = fs::remove_dir_all(&self.folder);
    }
}
