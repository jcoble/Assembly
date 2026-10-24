use std::collections::{BTreeMap, VecDeque};
use std::mem::ManuallyDrop;
use std::os::unix::process::CommandExt;
use std::path::{Path, PathBuf};
use std::process::Stdio;
use std::ptr;
use std::sync::{Arc, Mutex};
use std::time::Duration;

use tokio::io::{AsyncBufReadExt, AsyncWriteExt, BufReader, Lines};
use tokio::process::{Child, ChildStdin, ChildStdout, Command};

use super::super::protocol::AgentProviderManifest;
use super::super::reaper::{detached_descendant_identities, signal_process_identities};

const STDERR_LINE_CAP: usize = 200;

/// How long a stopped sidecar has to leave on its own before it is killed.
///
/// The Claude CLI writes a turn to its transcript about fifty milliseconds
/// after it has answered it, and is gone within a second once told to go.
/// Killing the group the moment the answer arrived lost that write on every
/// turn, and a session whose transcript was never written cannot be resumed.
pub(crate) const STOP_GRACE: Duration = Duration::from_secs(3);

#[derive(Clone, Debug, Default, Eq, PartialEq)]
pub struct SidecarEnvironment {
    values: BTreeMap<String, Option<String>>,
    skip_assembly_tool_approval: bool,
}

impl SidecarEnvironment {
    pub fn without_assembly_tool_approval(mut self) -> Self {
        self.skip_assembly_tool_approval = true;
        self
    }

    pub fn set(mut self, name: impl Into<String>, value: impl Into<String>) -> Self {
        self.values.insert(name.into(), Some(value.into()));
        self
    }

    pub fn remove(mut self, name: impl Into<String>) -> Self {
        self.values.insert(name.into(), None);
        self
    }

    fn apply(&self, command: &mut Command) {
        for (name, value) in &self.values {
            if let Some(value) = value {
                command.env(name, value);
            } else {
                command.env_remove(name);
            }
        }
    }
}

pub fn validated_conversation_cwd(value: &str) -> Result<PathBuf, String> {
    let value = value.trim();
    if value.is_empty() {
        return Err("Conversation project folder is required".to_string());
    }
    let path = PathBuf::from(value)
        .canonicalize()
        .map_err(|_| "Conversation project folder does not exist".to_string())?;
    if !path.is_dir() {
        return Err("Conversation project folder is not a directory".to_string());
    }
    Ok(path)
}

#[derive(Clone, Debug)]
pub struct BoundedStderr {
    capacity: usize,
    lines: Arc<Mutex<VecDeque<String>>>,
}

impl BoundedStderr {
    pub fn new(capacity: usize) -> Self {
        Self {
            capacity: capacity.max(1),
            lines: Arc::new(Mutex::new(VecDeque::new())),
        }
    }

    pub fn push(&self, line: String) {
        if let Ok(mut lines) = self.lines.lock() {
            lines.push_back(line);
            while lines.len() > self.capacity {
                lines.pop_front();
            }
        }
    }

    pub fn snapshot(&self) -> Vec<String> {
        self.lines
            .lock()
            .map(|lines| lines.iter().cloned().collect())
            .unwrap_or_default()
    }
}

pub struct SidecarProcess {
    child: Child,
    stdin: ChildStdin,
    stdout: Lines<BufReader<ChildStdout>>,
    stderr: BoundedStderr,
}

pub struct SidecarReadHalf {
    stdout: Lines<BufReader<ChildStdout>>,
    stderr: BoundedStderr,
}

pub struct SidecarWriteHalf {
    stdin: ChildStdin,
}

pub struct SidecarProcessHandle {
    child: Child,
}

impl SidecarProcess {
    #[cfg(test)]
    pub fn spawn(
        manifest: &AgentProviderManifest,
        cwd: &Path,
        owned_id: &str,
    ) -> Result<Self, String> {
        Self::spawn_with_environment(manifest, cwd, owned_id, &SidecarEnvironment::default())
    }

    pub fn spawn_with_environment(
        manifest: &AgentProviderManifest,
        cwd: &Path,
        owned_id: &str,
        environment: &SidecarEnvironment,
    ) -> Result<Self, String> {
        let owner_marker = super::super::reaper::owner_instance_marker()?;
        let visible_marker = super::super::reaper::visible_process_marker(owned_id, &owner_marker);
        let mut command = Command::new("/bin/sh");
        command
            .arg("-c")
            .arg("\"$@\"; command_status=$?; :; exit \"$command_status\"")
            .arg(visible_marker)
            .arg(&manifest.executable)
            .args(&manifest.args)
            .current_dir(cwd)
            .stdin(Stdio::piped())
            .stdout(Stdio::piped())
            .stderr(Stdio::piped())
            .kill_on_drop(true);
        environment.apply(&mut command);
        // The descriptor's launch arguments and environment, read-only in proof mode; settings probes skip pre-approval (TSK-1506).
        let launch = match super::descriptors::for_adapter(&manifest.id) {
            Some(descriptor) => crate::proof_mode::session_launch(descriptor, !environment.skip_assembly_tool_approval)?,
            None => {
                crate::proof_mode::refuse("An agent without a provider descriptor")?;
                Default::default()
            }
        };
        command.args(&launch.args);
        for (key, value) in &launch.env {
            let inherited = match environment.values.get(key) { Some(value) => value.clone(), None => std::env::var(key).ok() };
            command.env(key, super::descriptors::env_value(key, inherited.as_deref(), value)?);
        }
        command
            .env(super::super::reaper::SESSION_MARKER_ENV, owned_id)
            .env(super::super::reaper::OWNER_INSTANCE_ENV, owner_marker);
        command.as_std_mut().process_group(0);
        let mut child = command
            .spawn()
            .map_err(|error| format!("Could not start {}: {error}", manifest.display_name))?;
        let stdin = child
            .stdin
            .take()
            .ok_or_else(|| "ACP sidecar did not provide stdin".to_string())?;
        let stdout = child
            .stdout
            .take()
            .ok_or_else(|| "ACP sidecar did not provide stdout".to_string())?;
        let stderr_stream = child
            .stderr
            .take()
            .ok_or_else(|| "ACP sidecar did not provide stderr".to_string())?;
        let stderr = BoundedStderr::new(STDERR_LINE_CAP);
        let stderr_capture = stderr.clone();
        tokio::spawn(async move {
            let mut lines = BufReader::new(stderr_stream).lines();
            while let Ok(Some(line)) = lines.next_line().await {
                stderr_capture.push(line);
            }
        });
        Ok(Self {
            child,
            stdin,
            stdout: BufReader::new(stdout).lines(),
            stderr,
        })
    }

    pub fn split(self) -> (SidecarReadHalf, SidecarWriteHalf, SidecarProcessHandle) {
        let mut process = ManuallyDrop::new(self);
        // The process is deliberately transferred to independent transport
        // owners; ManuallyDrop prevents the original Drop impl from killing it
        // while these fields are moved out.
        let child = unsafe { ptr::read(&mut process.child) };
        let stdin = unsafe { ptr::read(&mut process.stdin) };
        let stdout = unsafe { ptr::read(&mut process.stdout) };
        let stderr = unsafe { ptr::read(&mut process.stderr) };
        (
            SidecarReadHalf { stdout, stderr },
            SidecarWriteHalf { stdin },
            SidecarProcessHandle { child },
        )
    }
}

impl SidecarWriteHalf {
    pub async fn write_json(&mut self, value: &serde_json::Value) -> Result<(), String> {
        let mut encoded = serde_json::to_vec(value).map_err(|error| error.to_string())?;
        encoded.push(b'\n');
        self.stdin
            .write_all(&encoded)
            .await
            .map_err(|error| format!("Could not write ACP request: {error}"))?;
        self.stdin
            .flush()
            .await
            .map_err(|error| format!("Could not flush ACP request: {error}"))
    }
}

impl SidecarReadHalf {
    pub async fn next_json(&mut self) -> Result<serde_json::Value, String> {
        loop {
            let line = self
                .stdout
                .next_line()
                .await
                .map_err(|error| format!("Could not read ACP response: {error}"))?
                .ok_or_else(|| {
                    format!("ACP sidecar exited: {}", self.stderr.snapshot().join(" | "))
                })?;
            if let Ok(value) = serde_json::from_str(&line) {
                return Ok(value);
            }
        }
    }
}

impl SidecarProcessHandle {
    /// Ask the process group to leave, and kill whatever has not left in time.
    ///
    /// The group leader answers SIGTERM at once and is waited for here. Its
    /// descendants finish what they were writing and leave on their own; a
    /// watchdog kills the group if any of them is still there when the grace
    /// runs out, so nothing idles on for long either way.
    pub async fn stop(&mut self) {
        let Some(pid) = self.child.id() else {
            return;
        };
        let detached = detached_descendant_identities(pid);
        signal_process_identities(&detached, libc::SIGTERM);
        let group = -(pid as i32);
        if unsafe { libc::kill(group, libc::SIGTERM) } != 0 {
            let _ = self.child.start_kill();
        }
        if tokio::time::timeout(STOP_GRACE, self.child.wait())
            .await
            .is_err()
        {
            stop_process_group(&mut self.child);
            let _ = self.child.wait().await;
        }
        tokio::spawn(async move {
            tokio::time::sleep(STOP_GRACE).await;
            signal_process_identities(&detached, libc::SIGKILL);
            if unsafe { libc::kill(group, 0) } == 0 {
                let _ = unsafe { libc::kill(group, libc::SIGKILL) };
            }
        });
    }

    pub fn process_id(&self) -> Option<u32> {
        self.child.id()
    }
}

impl Drop for SidecarProcessHandle {
    fn drop(&mut self) {
        stop_process_group(&mut self.child);
    }
}

impl Drop for SidecarProcess {
    fn drop(&mut self) {
        stop_process_group(&mut self.child);
    }
}

fn stop_process_group(child: &mut Child) {
    let Some(pid) = child.id() else {
        return;
    };
    let detached = detached_descendant_identities(pid);
    signal_process_identities(&detached, libc::SIGKILL);
    let target = -(pid as i32);
    // The child is placed in a fresh group during spawn, so this target cannot
    // include the app or another managed sidecar.
    if unsafe { libc::kill(target, libc::SIGKILL) } != 0 {
        let _ = child.start_kill();
    }
}

#[cfg(test)]
mod tests {
    use std::path::PathBuf;
    use std::time::{Duration, Instant};

    use super::*;
    use crate::agent_conversation::protocol::{
        AgentProviderManifest, ProviderSource, ProviderTransport,
    };

    #[test]
    fn stderr_diagnostics_are_bounded() {
        let stderr = BoundedStderr::new(2);
        stderr.push("one".into());
        stderr.push("two".into());
        stderr.push("three".into());
        assert_eq!(stderr.snapshot(), vec!["two", "three"]);
    }

    fn fixture_manifest(id: &str, script: String) -> AgentProviderManifest {
        AgentProviderManifest {
            id: id.into(),
            display_name: "Process fixture".into(),
            transport: ProviderTransport::AcpStdio,
            executable: PathBuf::from("/bin/sh"),
            args: vec!["-c".into(), script],
            version: "1".into(),
            content_hash: "0000000000000000000000000000000000000000000000000000000000000000".into(),
            trusted_source: ProviderSource::Bundled,
        }
    }

    #[tokio::test(flavor = "current_thread")]
    async fn codex_sessions_preapprove_assembly_tools() {
        let original = serde_json::json!({
            "model": "configured-model",
            "mcp_servers.other.default_tools_approval_mode": "prompt"
        });
        let environment = SidecarEnvironment {
            values: BTreeMap::from([("CODEX_CONFIG".into(), Some(original.to_string()))]),
            ..Default::default()
        };
        for provider in ["codex-acp", "claude-agent-acp"] {
            let manifest = fixture_manifest(provider, "printf '%s\\n' \"$CODEX_CONFIG\"".into());
            let process = SidecarProcess::spawn_with_environment(
                &manifest, &std::env::temp_dir(), "owned-preapprove", &environment,
            ).unwrap();
            let (mut reader, _writer, mut handle) = process.split();
            let actual = reader.next_json().await.unwrap();
            handle.stop().await;
            if provider == "codex-acp" {
                let mut expected = original.clone();
                expected["mcp_servers.assembly.default_tools_approval_mode"] = "approve".into();
                assert_eq!(actual, expected);
            } else {
                assert_eq!(actual, original);
            }
        }
    }

    #[tokio::test(flavor = "current_thread")]
    async fn codex_probe_launch_carries_no_assembly_server_override() {
        let original = serde_json::json!({"model": "configured-model"});
        let environment = SidecarEnvironment {
            values: BTreeMap::from([("CODEX_CONFIG".into(), Some(original.to_string()))]),
            ..Default::default()
        }.without_assembly_tool_approval();
        let manifest = fixture_manifest("codex-acp", "printf '%s\\n' \"$CODEX_CONFIG\"".into());
        let process = SidecarProcess::spawn_with_environment(
            &manifest, &std::env::temp_dir(), "owned-probe", &environment,
        ).unwrap();
        let (mut reader, _writer, mut handle) = process.split();
        let actual = reader.next_json().await.unwrap();
        handle.stop().await;
        assert_eq!(actual, original);
        assert!(!actual.as_object().unwrap().keys().any(|key| key.starts_with("mcp_servers.assembly")));
    }

    #[tokio::test(flavor = "current_thread")]
    async fn stopping_an_exited_group_returns_without_grace_delay() {
        let manifest = fixture_manifest("exited-fixture", "exit 0".into());
        let process = SidecarProcess::spawn(
            &manifest, &std::env::temp_dir(), "owned-exited",
        ).unwrap();
        let (_read, _write, mut handle) = process.split();
        // Observe exit without reaping, so stop still exercises its wait path.
        loop {
            let mut status: libc::siginfo_t = unsafe { std::mem::zeroed() };
            assert_eq!(unsafe { libc::waitid(libc::P_PID, handle.process_id().unwrap(),
                &mut status, libc::WEXITED | libc::WNOWAIT | libc::WNOHANG) }, 0);
            if unsafe { status.si_pid() } != 0 { break; }
            tokio::time::sleep(Duration::from_millis(10)).await;
        }
        let started = Instant::now();
        handle.stop().await;
        let elapsed = started.elapsed();
        println!("already-exited stop: {elapsed:?}");
        assert!(elapsed < Duration::from_secs(1), "stop waited for grace");
    }

    // The Claude CLI writes a turn to its transcript only after it has answered
    // it. A sidecar that is asked to stop must get to finish that write.
    #[tokio::test(flavor = "current_thread")]
    async fn stopping_a_sidecar_lets_it_finish_what_it_was_writing() {
        let marker = std::env::temp_dir().join(format!("mcb-stop-grace-{}", std::process::id()));
        let _ = std::fs::remove_file(&marker);
        let manifest = fixture_manifest(
            "stop-grace-fixture",
            format!(
                "trap 'sleep 0.2; : > \"{}\"; exit 0' TERM; sleep 30 & wait",
                marker.display()
            ),
        );
        let process =
            SidecarProcess::spawn(&manifest, &std::env::temp_dir(), "owned-stop-grace").unwrap();
        let (_read, _write, mut handle) = process.split();
        let leader = handle.process_id().unwrap() as i32;
        // The trap is only in place once the shell has read the script.
        tokio::time::sleep(Duration::from_millis(150)).await;

        handle.stop().await;

        // Stopping waits for the leader, not for its descendants: they leave on
        // their own once they have finished. The whole group is gone before the
        // grace runs out, and the write it was in the middle of is on disk.
        let group = -leader;
        let deadline = Instant::now() + STOP_GRACE;
        while unsafe { libc::kill(group, 0) } == 0 && Instant::now() < deadline {
            tokio::time::sleep(Duration::from_millis(10)).await;
        }
        assert_ne!(
            unsafe { libc::kill(group, 0) },
            0,
            "sidecar group outlived the grace"
        );
        assert!(
            marker.is_file(),
            "the sidecar was killed before it finished writing"
        );
        let _ = std::fs::remove_file(&marker);
    }

    // Being asked is a courtesy with a deadline: a group that ignores it is
    // killed once the grace runs out, descendants included.
    #[tokio::test(flavor = "current_thread")]
    async fn stopping_a_sidecar_that_ignores_the_ask_kills_it() {
        let manifest = fixture_manifest(
            "stop-ignored-fixture",
            "trap '' TERM; sleep 30 & child=$!; echo $child >&2; wait".into(),
        );
        let process =
            SidecarProcess::spawn(&manifest, &std::env::temp_dir(), "owned-stop-ignored").unwrap();
        let deadline = Instant::now() + Duration::from_secs(2);
        let child = loop {
            if let Some(child) = process
                .stderr
                .snapshot()
                .first()
                .and_then(|line| line.parse::<i32>().ok())
            {
                break child;
            }
            assert!(
                Instant::now() < deadline,
                "fixture child pid was not captured"
            );
            tokio::time::sleep(Duration::from_millis(10)).await;
        };
        let (_read, _write, mut handle) = process.split();
        let leader = handle.process_id().unwrap() as i32;

        handle.stop().await;

        assert_ne!(
            unsafe { libc::kill(leader, 0) },
            0,
            "sidecar leader survived stop"
        );
        // The descendant ignored the ask; the watchdog kills it once the grace
        // is up, and not before.
        let reap_deadline = Instant::now() + STOP_GRACE + Duration::from_secs(2);
        while unsafe { libc::kill(child, 0) } == 0 && Instant::now() < reap_deadline {
            tokio::time::sleep(Duration::from_millis(10)).await;
        }
        assert_ne!(
            unsafe { libc::kill(child, 0) },
            0,
            "sidecar descendant survived stop"
        );
    }

    #[tokio::test(flavor = "current_thread")]
    async fn stopping_sidecar_cleans_tools_in_separate_process_groups() {
        for graceful in [true, false] {
            let mut manifest = fixture_manifest(
                "detached-tool-fixture",
                "set -m; trap '' TERM; sleep 60 & child=$!; echo $child >&2; wait".into(),
            );
            // Bash enables job-control groups without a tty on both supported platforms.
            manifest.executable = PathBuf::from("/bin/bash");
            let process = SidecarProcess::spawn(
                &manifest, &std::env::temp_dir(), "owned-detached-tool",
            ).unwrap();
            let deadline = Instant::now() + Duration::from_secs(2);
            let tool = loop {
                if let Some(pid) = process.stderr.snapshot().first()
                    .and_then(|line| line.parse::<i32>().ok()) {
                    break pid;
                }
                assert!(Instant::now() < deadline, "fixture did not report its tool");
                tokio::time::sleep(Duration::from_millis(10)).await;
            };
            let (_read, _write, mut handle) = process.split();
            assert_ne!(unsafe { libc::getpgid(tool) }, handle.process_id().unwrap() as i32);
            if graceful { handle.stop().await; }
            drop(handle);
            let deadline = Instant::now() + STOP_GRACE + Duration::from_secs(2);
            while unsafe { libc::kill(tool, 0) } == 0 && Instant::now() < deadline {
                tokio::time::sleep(Duration::from_millis(20)).await;
            }
            let survived = unsafe { libc::kill(tool, 0) } == 0;
            // A failing regression must not leave its test tool running.
            if survived { unsafe { libc::kill(tool, libc::SIGKILL); } }
            assert!(!survived, "tool in a separate process group survived sidecar stop");
        }
    }

    #[tokio::test(flavor = "current_thread")]
    async fn sidecar_close_kills_the_entire_process_group() {
        let manifest = AgentProviderManifest {
            id: "process-tree-fixture".into(),
            display_name: "Process tree fixture".into(),
            transport: ProviderTransport::AcpStdio,
            executable: PathBuf::from("/bin/sh"),
            args: vec![
                "-c".into(),
                "sleep 30 & child=$!; echo $child >&2; wait".into(),
            ],
            version: "1".into(),
            content_hash: "0000000000000000000000000000000000000000000000000000000000000000".into(),
            trusted_source: ProviderSource::Bundled,
        };
        let mut process =
            SidecarProcess::spawn(&manifest, &std::env::temp_dir(), "owned-process-tree").unwrap();
        let parent = process.child.id().unwrap();
        let deadline = Instant::now() + Duration::from_secs(2);
        let child = loop {
            if let Some(child) = process
                .stderr
                .snapshot()
                .first()
                .and_then(|line| line.parse::<i32>().ok())
            {
                break child;
            }
            assert!(
                Instant::now() < deadline,
                "fixture child pid was not captured"
            );
            tokio::time::sleep(Duration::from_millis(10)).await;
        };
        stop_process_group(&mut process.child);
        let _ = process.child.wait().await;
        assert_ne!(
            unsafe { libc::kill(parent as i32, 0) },
            0,
            "sidecar parent survived close"
        );
        let reap_deadline = Instant::now() + Duration::from_secs(2);
        while unsafe { libc::kill(child, 0) } == 0 && Instant::now() < reap_deadline {
            tokio::time::sleep(Duration::from_millis(10)).await;
        }
        assert_ne!(
            unsafe { libc::kill(child, 0) },
            0,
            "sidecar descendant survived close"
        );
    }
}
