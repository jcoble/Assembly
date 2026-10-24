//! Marked groups of a dead instance are stopped; on Linux the marker is read from `/proc` (TSK-1601).
use super::*;

#[cfg(any(target_os = "macos", target_os = "linux"))]
#[test]
fn reaper_kills_marked_group_with_dead_owner() {
    let current = current_identity();
    let dead_owner = InstanceIdentity {
        pid: current.pid,
        start_time_micros: current.start_time_micros.saturating_sub(1),
    };
    let mut group = TestGroup::spawn(Some(("reaper-dead-owner", &dead_owner)));
    let reaped =
        reap_candidate_processes(&[group.pid()], &current, &HashSet::new(), TEST_GRACE);

    assert_eq!(reaped, vec![group.pid()]);
    assert!(wait_for_exit(&mut group));
}

#[cfg(target_os = "linux")]
#[test]
fn an_orphaned_background_child_is_reaped_on_linux() {
    let current = current_identity();
    let dead_owner = InstanceIdentity {
        pid: current.pid,
        start_time_micros: current.start_time_micros.saturating_sub(1),
    };
    // The shell exits at once, leaving `sleep` in a group whose leader is gone.
    let output = Command::new("sh")
        .args(["-c", "sleep 600 >/dev/null 2>&1 & echo $!"])
        .env(SESSION_MARKER_ENV, "reaper-orphan")
        .env(OWNER_INSTANCE_ENV, dead_owner.marker())
        .process_group(0)
        .output()
        .expect("test shell should run");
    let sleep: u32 = String::from_utf8_lossy(&output.stdout).trim().parse().unwrap();
    let alive = |pid: u32| fs::read_to_string(format!("/proc/{pid}/stat"))
        .is_ok_and(|stat| !stat.contains(") Z "));
    assert!(alive(sleep));

    let reaped = reap_candidate_processes(&[sleep], &current, &HashSet::new(), TEST_GRACE);

    let gone = (0..40).any(|_| { thread::sleep(Duration::from_millis(25)); !alive(sleep) });
    if !gone { unsafe { libc::kill(sleep as i32, libc::SIGKILL); } }
    assert_eq!(reaped.len(), 1);
    assert!(gone, "the orphaned sleep {sleep} is still running");
}
