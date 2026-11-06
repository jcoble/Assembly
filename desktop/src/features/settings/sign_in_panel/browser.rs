//! Only the OS launcher is awaited, never the browser's lifetime. The owning panel task keeps
//! this future (and its kill/reap-on-drop child); no detached thread retains a URL.
use std::process::{Command, Stdio};

fn owned(command: Command) -> async_process::Command {
    let mut command = async_process::Command::from(command);
    command
        .kill_on_drop(true)
        .reap_on_drop(true)
        .stdin(Stdio::null())
        .stdout(Stdio::null())
        .stderr(Stdio::null());
    command
}
async fn run(command: Command) -> bool {
    let Ok(mut child) = owned(command).spawn() else {
        return false;
    };
    child.status().await.is_ok_and(|status| status.success())
}
pub(super) async fn launch(original_url: String) -> bool {
    for command in open::commands(original_url) {
        if run(command).await {
            return true;
        }
    }
    false
}

#[cfg(test)]
mod tests {
    use super::*;
    use futures_executor::block_on;
    use std::process::Command;
    #[cfg(unix)]
    #[test]
    fn cancelling_the_owner_stops_its_exact_launcher_without_waiting_for_its_lifetime() {
        use std::os::fd::{AsRawFd, OwnedFd};
        let mut command = Command::new("/bin/sleep");
        command.arg("3600");
        let mut child = owned(command).stdout(Stdio::piped()).spawn().unwrap();
        let output: OwnedFd = child.stdout.take().unwrap().try_into().unwrap();
        let mut ready = libc::pollfd {
            fd: output.as_raw_fd(),
            events: libc::POLLHUP,
            revents: 0,
        };
        drop(child);
        // Event-driven pipe closure proves that this exact owned child exited; no process lookup or sleep.
        assert_eq!(unsafe { libc::poll(&mut ready, 1, 5000) }, 1);
        assert_ne!(ready.revents & libc::POLLHUP, 0);
    }

    #[cfg(unix)]
    #[gpui::test]
    fn dropping_the_panel_style_task_drops_and_stops_the_actual_launcher(
        cx: &mut gpui::TestAppContext,
    ) {
        use std::os::fd::{AsRawFd, OwnedFd};
        let mut command = Command::new("/bin/sleep");
        command.arg("3600");
        let mut child = owned(command).stdout(Stdio::piped()).spawn().unwrap();
        let output: OwnedFd = child.stdout.take().unwrap().try_into().unwrap();
        let task = cx
            .background_executor
            .spawn(async move { child.status().await });
        drop(task);
        cx.run_until_parked();
        let mut ready = libc::pollfd {
            fd: output.as_raw_fd(),
            events: libc::POLLHUP,
            revents: 0,
        };
        assert_eq!(unsafe { libc::poll(&mut ready, 1, 5000) }, 1);
        assert_ne!(ready.revents & libc::POLLHUP, 0);
    }

    #[test]
    fn native_handoff_reports_success_failure_and_spawn_failure() {
        assert!(block_on(run(Command::new("/usr/bin/true"))));
        assert!(!block_on(run(Command::new("/usr/bin/false"))));
        assert!(!block_on(run(Command::new(
            "/assembly-missing-browser-launcher"
        ))));
    }
}
