use std::os::unix::process::CommandExt;
use std::path::{Path, PathBuf};
use std::process::Stdio;

use tokio::io::{AsyncBufReadExt, BufReader, Lines};
use tokio::process::{Child, ChildStderr, ChildStdin, ChildStdout, Command};

pub(crate) struct ConversationProcess {
    child: Child,
    pub(crate) stdin: ChildStdin,
    pub(crate) stdout: Lines<BufReader<ChildStdout>>,
    pub(crate) stderr: Lines<BufReader<ChildStderr>>,
}

impl ConversationProcess {
    pub(crate) fn spawn(
        program: &str,
        args: &[String],
        cwd: &Path,
        owned_id: &str,
    ) -> Result<Self, String> {
        let mut command = Command::new(program);
        command
            .args(args)
            .current_dir(cwd)
            .env("COMMANDBAR_SESSION_ID", owned_id)
            .stdin(Stdio::piped())
            .stdout(Stdio::piped())
            .stderr(Stdio::piped())
            .kill_on_drop(true);
        command.as_std_mut().process_group(0);

        let mut child = command
            .spawn()
            .map_err(|error| format!("Could not start {program}: {error}"))?;
        let stdin = child
            .stdin
            .take()
            .ok_or_else(|| format!("{program} did not provide stdin"))?;
        let stdout = child
            .stdout
            .take()
            .ok_or_else(|| format!("{program} did not provide stdout"))?;
        let stderr = child
            .stderr
            .take()
            .ok_or_else(|| format!("{program} did not provide stderr"))?;
        Ok(Self {
            child,
            stdin,
            stdout: BufReader::new(stdout).lines(),
            stderr: BufReader::new(stderr).lines(),
        })
    }

    pub(crate) async fn stop(&mut self) {
        stop_process_group(&mut self.child);
        let _ = self.child.wait().await;
    }
}

impl Drop for ConversationProcess {
    fn drop(&mut self) {
        stop_process_group(&mut self.child);
    }
}

fn stop_process_group(child: &mut Child) {
    let Some(pid) = child.id() else {
        return;
    };
    let process_group = -(pid as i32);
    // SAFETY: spawn() makes the child PID the process-group ID. The negative
    // target therefore reaches only this registry-owned provider tree.
    let stopped = unsafe { libc::kill(process_group, libc::SIGKILL) } == 0;
    if !stopped {
        let _ = child.start_kill();
    }
}

pub(crate) fn validated_conversation_cwd(value: &str) -> Result<PathBuf, String> {
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
