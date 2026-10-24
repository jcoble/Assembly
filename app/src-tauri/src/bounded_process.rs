use std::io::{self, Read, Write};
use std::os::unix::process::CommandExt;
use std::process::{Command, Output, Stdio};
use std::thread;
use std::time::{Duration, Instant};

pub(crate) const LOCAL_COMMAND_TIMEOUT: Duration = Duration::from_secs(20);
pub(crate) const NETWORK_COMMAND_TIMEOUT: Duration = Duration::from_secs(120);
const WAIT_POLL_INTERVAL: Duration = Duration::from_millis(25);

/// Run one command without allowing prompts or an abandoned child tree.
pub(crate) fn output(
    command: &mut Command,
    description: &str,
    timeout: Duration,
) -> io::Result<Output> {
    output_with_input(command, description, timeout, None)
}

/// Run a bounded command with a request body that stays off the argument list.
pub(crate) fn output_with_input(
    command: &mut Command,
    description: &str,
    timeout: Duration,
    input: Option<&[u8]>,
) -> io::Result<Output> {
    command
        .env("GIT_TERMINAL_PROMPT", "0")
        .stdin(if input.is_some() { Stdio::piped() } else { Stdio::null() })
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .process_group(0);

    let mut child = command.spawn()?;
    let stdout = child
        .stdout
        .take()
        .ok_or_else(|| io::Error::other("command stdout was unavailable"))?;
    let stderr = child
        .stderr
        .take()
        .ok_or_else(|| io::Error::other("command stderr was unavailable"))?;
    let stdout_reader = thread::spawn(move || read_all(stdout));
    let stderr_reader = thread::spawn(move || read_all(stderr));
    let stdin_writer = input.map(|bytes| {
        let mut stdin = child.stdin.take().expect("piped stdin was unavailable");
        let bytes = bytes.to_vec();
        thread::spawn(move || stdin.write_all(&bytes))
    });
    let deadline = Instant::now() + timeout;

    let status = loop {
        match child.try_wait() {
            Ok(Some(status)) => break status,
            Ok(None) => {}
            Err(error) => {
                stop_process_group(&mut child);
                let _ = stdout_reader.join();
                let _ = stderr_reader.join();
                if let Some(writer) = stdin_writer { let _ = writer.join(); }
                return Err(error);
            }
        }
        if Instant::now() >= deadline {
                stop_process_group(&mut child);
                let _ = stdout_reader.join();
                let _ = stderr_reader.join();
                if let Some(writer) = stdin_writer { let _ = writer.join(); }
                return Err(io::Error::new(
                io::ErrorKind::TimedOut,
                format!(
                    "{description} timed out after {} seconds",
                    timeout.as_secs()
                ),
            ));
        }
        thread::sleep(WAIT_POLL_INTERVAL);
    };

    let stdout = join_reader(stdout_reader, "stdout")?;
    let stderr = join_reader(stderr_reader, "stderr")?;
    if let Some(writer) = stdin_writer {
        let wrote = writer.join().map_err(|_| io::Error::other("command stdin writer panicked"))?;
        if status.success() { wrote?; }
    }
    Ok(Output {
        status,
        stdout,
        stderr,
    })
}

fn read_all(mut reader: impl Read) -> io::Result<Vec<u8>> {
    let mut bytes = Vec::new();
    reader.read_to_end(&mut bytes)?;
    Ok(bytes)
}

fn join_reader(reader: thread::JoinHandle<io::Result<Vec<u8>>>, name: &str) -> io::Result<Vec<u8>> {
    reader
        .join()
        .map_err(|_| io::Error::other(format!("command {name} reader panicked")))?
}

fn stop_process_group(child: &mut std::process::Child) {
    let process_group = -(child.id() as i32);
    // SAFETY: `process_group(0)` made this child's PID its process-group ID.
    let group_stopped = unsafe { libc::kill(process_group, libc::SIGKILL) } == 0;
    if !group_stopped {
        let _ = child.kill();
    }
    let _ = child.wait();
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn timeout_stops_and_reaps_the_owned_process_group() {
        let started = Instant::now();
        let error = output(
            Command::new("sh").args(["-c", "sleep 5 & wait"]),
            "test command",
            Duration::from_millis(50),
        )
        .unwrap_err();

        assert_eq!(error.kind(), io::ErrorKind::TimedOut);
        assert!(started.elapsed() < Duration::from_secs(1));
    }
}
