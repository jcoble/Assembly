//! `footprint_mb` reads this process's memory footprint: the kernel's phys_footprint on macOS (what Activity Monitor
//! shows), and resident memory from /proc/self/status on Linux.

pub fn footprint_mb() -> f64 {
    todo!()
}
