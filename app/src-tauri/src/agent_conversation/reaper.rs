use std::collections::HashSet;
use std::fs;
use std::path::{Path, PathBuf};
use std::sync::OnceLock;
use std::thread;
use std::time::{Duration, Instant};

use serde::{Deserialize, Serialize};
#[cfg(target_os = "macos")]
use sysinfo::{ProcessRefreshKind, UpdateKind};
use sysinfo::{ProcessesToUpdate, System};

use crate::debug_log::stderr_log;

pub const SESSION_MARKER_ENV: &str = "COMMANDBAR_SESSION_ID";
pub const OWNER_INSTANCE_ENV: &str = "COMMANDBAR_OWNER_INSTANCE_ID";
const VISIBLE_MARKER_PREFIX: &str = "mac-command-bar-adapter:";
const REAPER_TERM_GRACE: Duration = Duration::from_secs(2);

static CURRENT_INSTANCE: OnceLock<InstanceIdentity> = OnceLock::new();

#[derive(Clone, Debug, Deserialize, Eq, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct InstanceIdentity {
    pub pid: u32,
    pub start_time_micros: u64,
}

impl InstanceIdentity {
    fn marker(&self) -> String {
        format!("{}:{}", self.pid, self.start_time_micros)
    }

    #[cfg(any(target_os = "macos", target_os = "linux"))]
    fn from_marker(value: &str) -> Option<Self> {
        let (pid, start_time_micros) = value.split_once(':')?;
        Some(Self {
            pid: pid.parse().ok()?,
            start_time_micros: start_time_micros.parse().ok()?,
        })
    }
}

#[derive(Clone, Debug, Eq, PartialEq)]
struct ProcessSnapshot {
    identity: InstanceIdentity,
    parent_pid: u32,
    process_group_id: u32,
}

impl ProcessSnapshot {
    fn same_boundary_as(&self, other: &Self) -> bool {
        self.identity == other.identity
            && self.parent_pid == other.parent_pid
            && self.process_group_id == other.process_group_id
    }
}

#[derive(Clone, Debug, Eq, PartialEq)]
struct AdapterMarker {
    session_id: String,
    owner: Option<InstanceIdentity>,
    legacy: bool,
}

pub fn owner_instance_marker() -> Result<String, String> {
    current_instance_identity().map(|identity| identity.marker())
}

pub fn visible_process_marker(session_id: &str, owner_marker: &str) -> String {
    format!(
        "{VISIBLE_MARKER_PREFIX}{owner_marker}:{}",
        encode_hex(session_id.as_bytes())
    )
}

pub fn start_startup_reaper(
    app_data_dir: &Path,
    live_session_ids: HashSet<String>,
) -> Result<PathBuf, String> {
    let current = current_instance_identity()?;
    if let Some(existing) = CURRENT_INSTANCE.get() {
        if existing != &current {
            return Err("Application instance identity changed during startup".to_string());
        }
    } else {
        let _ = CURRENT_INSTANCE.set(current.clone());
    }

    let instance_path = write_instance_file(app_data_dir, &current)?;

    thread::Builder::new()
        .name("adapter-startup-reaper".to_string())
        .spawn(move || {
            reap_stale_adapter_groups(&current, &live_session_ids, REAPER_TERM_GRACE);
        })
        .map_err(|error| format!("Could not start adapter reconciliation: {error}"))?;

    Ok(instance_path)
}

fn write_instance_file(app_data_dir: &Path, current: &InstanceIdentity) -> Result<PathBuf, String> {
    let instance_directory = app_data_dir.join("instances");
    fs::create_dir_all(&instance_directory).map_err(|error| {
        format!(
            "Could not create application instance directory {}: {error}",
            instance_directory.display()
        )
    })?;
    let instance_path = instance_directory.join(format!(
        "{}-{}.json",
        current.pid, current.start_time_micros
    ));
    let encoded = serde_json::to_vec_pretty(&current)
        .map_err(|error| format!("Could not encode application instance identity: {error}"))?;
    fs::write(&instance_path, encoded).map_err(|error| {
        format!(
            "Could not write application instance file {}: {error}",
            instance_path.display()
        )
    })?;

    Ok(instance_path)
}

fn current_instance_identity() -> Result<InstanceIdentity, String> {
    if let Some(identity) = CURRENT_INSTANCE.get() {
        return Ok(identity.clone());
    }
    process_identity(std::process::id())
        .ok_or_else(|| "Could not read the application process identity".to_string())
}

fn process_identity(pid: u32) -> Option<InstanceIdentity> {
    process_snapshot(pid).map(|snapshot| snapshot.identity)
}

fn instance_is_alive(identity: &InstanceIdentity) -> bool {
    process_identity(identity.pid).as_ref() == Some(identity)
}

/// Capture tools that left their sidecar's group before shutdown can orphan them.
pub(crate) fn detached_descendant_identities(root_pid: u32) -> Vec<InstanceIdentity> {
    let processes = all_process_snapshots();
    let mut descendants = HashSet::from([root_pid]);
    loop {
        let before = descendants.len();
        for process in &processes {
            if descendants.contains(&process.parent_pid) {
                descendants.insert(process.identity.pid);
            }
        }
        if descendants.len() == before { break; }
    }
    processes.into_iter()
        .filter(|process| process.identity.pid != root_pid
            && descendants.contains(&process.identity.pid)
            && process.process_group_id != root_pid)
        .map(|process| process.identity)
        .collect()
}

pub(crate) fn signal_process_identities(processes: &[InstanceIdentity], signal: libc::c_int) {
    for identity in processes {
        // The recorded parent can be gone now; the birth identity must still match.
        if instance_is_alive(identity) {
            if let Ok(pid) = libc::pid_t::try_from(identity.pid) {
                unsafe { libc::kill(pid, signal); }
            }
        }
    }
}

fn reap_stale_adapter_groups(
    current_instance: &InstanceIdentity,
    live_session_ids: &HashSet<String>,
    term_grace: Duration,
) -> Vec<u32> {
    let candidate_pids = all_process_ids();
    reap_candidate_processes(
        &candidate_pids,
        current_instance,
        live_session_ids,
        term_grace,
    )
}

fn reap_candidate_processes(
    candidate_pids: &[u32],
    current_instance: &InstanceIdentity,
    live_session_ids: &HashSet<String>,
    term_grace: Duration,
) -> Vec<u32> {
    let process_table = all_process_snapshots();
    let mut seen_groups = HashSet::new();
    let mut reaped = Vec::new();

    for &pid in candidate_pids {
        let Some(snapshot) = process_snapshot(pid) else {
            continue;
        };
        // Any member stands for its group, so one whose leader exited (an orphaned `cmd &`) is still found.
        let group = snapshot.process_group_id;
        if group == unsafe { libc::getpgrp() } as u32 || seen_groups.contains(&group) {
            continue;
        }
        let Some(marker) = read_adapter_marker(pid) else {
            continue;
        };
        seen_groups.insert(group);
        if live_session_ids.contains(&marker.session_id)
            || !marker_has_dead_owner(&marker, &snapshot, current_instance)
        {
            continue;
        }

        // Re-read the immutable process identity and marker immediately before
        // signalling so a PID reused during discovery cannot become a target.
        if !process_snapshot(pid).is_some_and(|current| current.same_boundary_as(&snapshot))
            || read_adapter_marker(pid).as_ref() != Some(&marker)
        {
            continue;
        }

        let group_size = process_table
            .iter()
            .filter(|process| process.process_group_id == group)
            .count()
            .max(1);
        if signal_group(group, libc::SIGTERM) {
            wait_for_group_exit(group, term_grace);
            if process_group_is_alive(group) {
                signal_group(group, libc::SIGKILL);
            }
            stderr_log!(
                "startup adapter reaper: group {group}, group size {group_size}, session {}",
                marker.session_id
            );
            reaped.push(group);
        }
    }

    reaped
}

fn marker_has_dead_owner(
    marker: &AdapterMarker,
    root: &ProcessSnapshot,
    current_instance: &InstanceIdentity,
) -> bool {
    match &marker.owner {
        Some(owner) => !instance_is_alive(owner),
        None if marker.legacy => {
            root.parent_pid == 1
                && root.identity.start_time_micros < current_instance.start_time_micros
        }
        None => false,
    }
}

fn wait_for_group_exit(process_group_id: u32, grace: Duration) {
    let deadline = Instant::now() + grace;
    while process_group_is_alive(process_group_id) && Instant::now() < deadline {
        thread::sleep(Duration::from_millis(25));
    }
}

fn signal_group(process_group_id: u32, signal: libc::c_int) -> bool {
    let Ok(process_group_id) = libc::pid_t::try_from(process_group_id) else {
        return false;
    };
    if unsafe { libc::kill(-process_group_id, signal) } == 0 {
        return true;
    }
    std::io::Error::last_os_error().raw_os_error() == Some(libc::ESRCH)
}

fn process_group_is_alive(process_group_id: u32) -> bool {
    let Ok(process_group_id) = libc::pid_t::try_from(process_group_id) else {
        return false;
    };
    if unsafe { libc::kill(-process_group_id, 0) } == 0 {
        return true;
    }
    std::io::Error::last_os_error().raw_os_error() != Some(libc::ESRCH)
}

fn all_process_ids() -> Vec<u32> {
    let mut system = System::new();
    system.refresh_processes(ProcessesToUpdate::All, true);
    system.processes().keys().map(|pid| pid.as_u32()).collect()
}

fn all_process_snapshots() -> Vec<ProcessSnapshot> {
    all_process_ids()
        .into_iter()
        .filter_map(process_snapshot)
        .collect()
}

#[cfg(target_os = "macos")]
fn process_snapshot(pid: u32) -> Option<ProcessSnapshot> {
    let mut info = std::mem::MaybeUninit::<libc::proc_bsdinfo>::zeroed();
    let expected_size = std::mem::size_of::<libc::proc_bsdinfo>() as libc::c_int;
    let read_size = unsafe {
        libc::proc_pidinfo(
            pid as libc::c_int,
            libc::PROC_PIDTBSDINFO,
            0,
            info.as_mut_ptr().cast(),
            expected_size,
        )
    };
    if read_size != expected_size {
        return None;
    }
    let info = unsafe { info.assume_init() };
    Some(ProcessSnapshot {
        identity: InstanceIdentity {
            pid,
            start_time_micros: info
                .pbi_start_tvsec
                .saturating_mul(1_000_000)
                .saturating_add(info.pbi_start_tvusec),
        },
        parent_pid: info.pbi_ppid,
        process_group_id: info.pbi_pgid,
    })
}

#[cfg(target_os = "linux")]
fn process_snapshot(pid: u32) -> Option<ProcessSnapshot> {
    let stat = fs::read_to_string(format!("/proc/{pid}/stat")).ok()?;
    let fields = stat
        .rsplit_once(") ")?
        .1
        .split_whitespace()
        .collect::<Vec<_>>();
    Some(ProcessSnapshot {
        identity: InstanceIdentity {
            pid,
            // Linux exposes this as clock ticks since boot. It is an opaque,
            // stable process-birth marker here; no wall-clock conversion is needed.
            start_time_micros: fields.get(19)?.parse().ok()?,
        },
        parent_pid: fields.get(1)?.parse().ok()?,
        process_group_id: fields.get(2)?.parse().ok()?,
    })
}

#[cfg(not(any(target_os = "macos", target_os = "linux")))]
fn process_snapshot(_pid: u32) -> Option<ProcessSnapshot> {
    None
}

#[cfg(target_os = "macos")]
fn read_adapter_marker(pid: u32) -> Option<AdapterMarker> {
    // sysinfo's macOS environment refresh reads KERN_PROCARGS2 and keeps the
    // platform-specific argument and environment layout handling in one place.
    let mut system = System::new();
    let process_id = sysinfo::Pid::from_u32(pid);
    system.refresh_processes_specifics(
        ProcessesToUpdate::Some(&[process_id]),
        true,
        ProcessRefreshKind::nothing()
            .with_cmd(UpdateKind::Always)
            .with_environ(UpdateKind::Always),
    );
    let process = system.process(process_id)?;
    if let Some(marker) = process
        .cmd()
        .iter()
        .filter_map(|argument| argument.to_str())
        .find_map(parse_visible_process_marker)
    {
        return Some(marker);
    }

    let environment = process.environ();
    let session_id = environment
        .iter()
        .filter_map(|entry| entry.to_str())
        .find_map(|entry| {
            entry
                .strip_prefix(SESSION_MARKER_ENV)
                .and_then(|value| value.strip_prefix('='))
                .map(str::to_string)
        })?;
    if session_id.is_empty() {
        return None;
    }
    let owner_marker = environment
        .iter()
        .filter_map(|entry| entry.to_str())
        .find_map(|entry| {
            entry
                .strip_prefix(OWNER_INSTANCE_ENV)
                .and_then(|value| value.strip_prefix('='))
        });
    let owner = match owner_marker {
        Some(value) => Some(InstanceIdentity::from_marker(value)?),
        None => None,
    };
    Some(AdapterMarker {
        session_id,
        legacy: owner.is_none(),
        owner,
    })
}

/// The same environment marker as on macOS, read from `/proc`; a background child inherits it.
#[cfg(target_os = "linux")]
fn read_adapter_marker(pid: u32) -> Option<AdapterMarker> {
    let environment = fs::read(format!("/proc/{pid}/environ")).ok()?;
    let value = |name: &str| environment.split(|byte| *byte == 0).filter_map(|entry| std::str::from_utf8(entry).ok())
        .find_map(|entry| entry.strip_prefix(name)?.strip_prefix('=').map(str::to_string));
    let session_id = value(SESSION_MARKER_ENV).filter(|id| !id.is_empty())?;
    let owner = value(OWNER_INSTANCE_ENV).map(|value| InstanceIdentity::from_marker(&value).ok_or(())).transpose().ok()?;
    Some(AdapterMarker { session_id, legacy: owner.is_none(), owner })
}

#[cfg(not(any(target_os = "macos", target_os = "linux")))]
fn read_adapter_marker(_pid: u32) -> Option<AdapterMarker> {
    None
}

#[cfg(target_os = "macos")]
fn parse_visible_process_marker(value: &str) -> Option<AdapterMarker> {
    let value = value.strip_prefix(VISIBLE_MARKER_PREFIX)?;
    let (pid, remainder) = value.split_once(':')?;
    let (start_time_micros, encoded_session_id) = remainder.split_once(':')?;
    let owner = InstanceIdentity {
        pid: pid.parse().ok()?,
        start_time_micros: start_time_micros.parse().ok()?,
    };
    let session_id = String::from_utf8(decode_hex(encoded_session_id)?).ok()?;
    if session_id.is_empty() {
        return None;
    }
    Some(AdapterMarker {
        session_id,
        owner: Some(owner),
        legacy: false,
    })
}

fn encode_hex(bytes: &[u8]) -> String {
    const HEX: &[u8; 16] = b"0123456789abcdef";
    let mut encoded = String::with_capacity(bytes.len() * 2);
    for byte in bytes {
        encoded.push(HEX[(byte >> 4) as usize] as char);
        encoded.push(HEX[(byte & 0x0f) as usize] as char);
    }
    encoded
}

#[cfg(target_os = "macos")]
fn decode_hex(value: &str) -> Option<Vec<u8>> {
    if !value.len().is_multiple_of(2) {
        return None;
    }
    value
        .as_bytes()
        .chunks_exact(2)
        .map(|pair| {
            let high = (pair[0] as char).to_digit(16)?;
            let low = (pair[1] as char).to_digit(16)?;
            Some(((high << 4) | low) as u8)
        })
        .collect()
}

#[cfg(test)]
mod tests {
    use std::os::unix::process::CommandExt;
    use std::process::{Child, Command, Stdio};
    use std::thread;
    use std::time::Duration;

    use super::*;

    const TEST_GRACE: Duration = Duration::from_millis(100);

    mod marked_groups;

    struct TestGroup {
        child: Child,
    }

    impl TestGroup {
        fn spawn(marker: Option<(&str, &InstanceIdentity)>) -> Self {
            let mut command = Command::new("/bin/sleep");
            command
                .arg("30")
                .stdin(Stdio::null())
                .stdout(Stdio::null())
                .stderr(Stdio::null())
                .process_group(0);
            if let Some((session_id, owner)) = marker {
                let owner_marker = owner.marker();
                command
                    .env(SESSION_MARKER_ENV, session_id)
                    .env(OWNER_INSTANCE_ENV, &owner_marker)
                    .arg0(visible_process_marker(session_id, &owner_marker));
            }
            let group = Self {
                child: command.spawn().expect("test sleep should start"),
            };
            // spawn() returns as soon as fork succeeds. Wait briefly for the
            // marker-bearing post-exec process image to become observable.
            thread::sleep(Duration::from_millis(25));
            group
        }

        fn pid(&self) -> u32 {
            self.child.id()
        }

        fn is_alive(&mut self) -> bool {
            self.child.try_wait().expect("test sleep status").is_none()
        }
    }

    impl Drop for TestGroup {
        fn drop(&mut self) {
            if self.is_alive() {
                let target = -(self.pid() as i32);
                unsafe {
                    libc::kill(target, libc::SIGKILL);
                }
                let _ = self.child.wait();
            }
        }
    }

    fn current_identity() -> InstanceIdentity {
        process_identity(std::process::id()).expect("test process identity")
    }

    fn wait_for_exit(group: &mut TestGroup) -> bool {
        for _ in 0..40 {
            if !group.is_alive() {
                return true;
            }
            thread::sleep(Duration::from_millis(25));
        }
        false
    }

    #[test]
    fn reaper_spares_unmarked_process() {
        let current = current_identity();
        let mut group = TestGroup::spawn(None);

        let reaped =
            reap_candidate_processes(&[group.pid()], &current, &HashSet::new(), TEST_GRACE);

        assert!(reaped.is_empty());
        assert!(group.is_alive());
    }

    #[test]
    fn reaper_spares_group_with_live_owner() {
        let current = current_identity();
        let mut group = TestGroup::spawn(Some(("reaper-live-owner", &current)));

        let reaped =
            reap_candidate_processes(&[group.pid()], &current, &HashSet::new(), TEST_GRACE);

        assert!(reaped.is_empty());
        assert!(group.is_alive());
    }

    #[test]
    fn reaper_spares_current_live_session_even_with_dead_owner() {
        let current = current_identity();
        let dead_owner = InstanceIdentity {
            pid: current.pid,
            start_time_micros: current.start_time_micros.saturating_sub(1),
        };
        let mut group = TestGroup::spawn(Some(("reaper-current-session", &dead_owner)));
        let live_sessions = HashSet::from(["reaper-current-session".to_string()]);

        let reaped = reap_candidate_processes(&[group.pid()], &current, &live_sessions, TEST_GRACE);

        assert!(reaped.is_empty());
        assert!(group.is_alive());
    }

    #[test]
    fn instance_liveness_requires_pid_and_start_time_match() {
        let current = current_identity();
        assert!(instance_is_alive(&current));

        let reused_pid_identity = InstanceIdentity {
            pid: current.pid,
            start_time_micros: current.start_time_micros.saturating_sub(1),
        };
        assert!(!instance_is_alive(&reused_pid_identity));
    }

    #[test]
    fn detached_cleanup_does_not_signal_a_reused_pid() {
        let current = current_identity();
        let mut unrelated = TestGroup::spawn(Some(("unrelated-sidecar", &current)));
        let mut stale = process_identity(unrelated.pid()).unwrap();
        stale.start_time_micros = stale.start_time_micros.saturating_sub(1);
        signal_process_identities(&[stale], libc::SIGKILL);
        assert!(unrelated.is_alive(), "a mismatched process identity was signaled");
    }

    #[test]
    fn instance_file_records_pid_and_start_time_in_existing_state_directory() {
        let current = current_identity();
        let root = std::env::temp_dir().join(format!(
            "mac-command-bar-reaper-instance-test-{}",
            std::process::id()
        ));
        let _ = fs::remove_dir_all(&root);

        let path = write_instance_file(&root, &current).expect("write instance file");
        let recorded: InstanceIdentity =
            serde_json::from_slice(&fs::read(&path).expect("read instance file"))
                .expect("decode instance file");

        assert_eq!(recorded, current);
        assert_eq!(path.parent(), Some(root.join("instances").as_path()));
        fs::remove_dir_all(root).expect("remove test instance directory");
    }

    #[test]
    fn legacy_marker_requires_orphaned_root_older_than_current_instance() {
        let current = current_identity();
        let marker = AdapterMarker {
            session_id: "legacy-session".to_string(),
            owner: None,
            legacy: true,
        };
        let stale_root = ProcessSnapshot {
            identity: InstanceIdentity {
                pid: 42,
                start_time_micros: current.start_time_micros.saturating_sub(1),
            },
            parent_pid: 1,
            process_group_id: 42,
        };

        assert!(marker_has_dead_owner(&marker, &stale_root, &current));
        assert!(!marker_has_dead_owner(
            &marker,
            &ProcessSnapshot {
                parent_pid: current.pid,
                ..stale_root.clone()
            },
            &current
        ));
        assert!(!marker_has_dead_owner(
            &marker,
            &ProcessSnapshot {
                identity: InstanceIdentity {
                    pid: 42,
                    start_time_micros: current.start_time_micros,
                },
                ..stale_root
            },
            &current
        ));
    }

    #[cfg(target_os = "macos")]
    #[test]
    fn macos_process_marker_readback_preserves_provider_scope() {
        let current = current_identity();
        let group = TestGroup::spawn(Some(("provider:codex", &current)));
        let marker = read_adapter_marker(group.pid()).expect("read back adapter marker");
        assert_eq!(marker.session_id, "provider:codex");
        assert_eq!(marker.owner, Some(current));
    }
}
