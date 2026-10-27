//! `footprint_mb` reads this process's memory footprint: the kernel's phys_footprint on macOS (what Activity Monitor
//! shows), and resident memory from /proc/self/status on Linux. 0 where it can't be read.

#[cfg(target_os = "macos")]
pub fn footprint_mb() -> f64 {
    let mut info = std::mem::MaybeUninit::<libc::rusage_info_v2>::zeroed();
    // SAFETY: proc_pid_rusage fills a rusage_info_v2 for RUSAGE_INFO_V2, and `info` is one.
    let read = unsafe {
        libc::proc_pid_rusage(
            std::process::id() as i32,
            libc::RUSAGE_INFO_V2,
            info.as_mut_ptr().cast(),
        )
    };
    if read != 0 {
        return 0.0;
    }
    // SAFETY: proc_pid_rusage returned 0, so it filled `info`.
    unsafe { info.assume_init() }.ri_phys_footprint as f64 / (1024.0 * 1024.0)
}

#[cfg(not(target_os = "macos"))]
pub fn footprint_mb() -> f64 {
    let status = std::fs::read_to_string("/proc/self/status").unwrap_or_default();
    status
        .lines()
        .find_map(|line| line.strip_prefix("VmRSS:"))
        .and_then(|kb| kb.trim().trim_end_matches("kB").trim().parse::<f64>().ok())
        .map_or(0.0, |kb| kb / 1024.0)
}

#[cfg(test)]
mod tests {
    use super::footprint_mb;

    // Pins: g0-host#14
    #[test]
    fn the_footprint_is_read() {
        assert!(footprint_mb() > 1.0);
    }
}
