use std::process::Command;

fn main() {
    // The head commit, printed by `--print-stamp` so the proof engine can check what it runs.
    // A proof build folder has no `.git`; the engine passes the commit in `ASSEMBLY_BUILD_STAMP`.
    println!("cargo:rerun-if-env-changed=ASSEMBLY_BUILD_STAMP");
    let git = |args: &[&str]| Command::new("git").args(args).output().ok()
        .filter(|output| output.status.success())
        .map(|output| String::from_utf8_lossy(&output.stdout).trim().to_string());
    if let Some(git_dir) = git(&["rev-parse", "--absolute-git-dir"]) {
        println!("cargo:rerun-if-changed={git_dir}/HEAD");
        // The reflog moves on every commit and checkout; a missing file would rerun every build.
        if std::path::Path::new(&format!("{git_dir}/logs/HEAD")).exists() {
            println!("cargo:rerun-if-changed={git_dir}/logs/HEAD");
        }
    }
    let stamp = std::env::var("ASSEMBLY_BUILD_STAMP").ok()
        .or_else(|| git(&["rev-parse", "HEAD"]))
        .unwrap_or_else(|| "unknown".to_string());
    println!("cargo:rustc-env=ASSEMBLY_BUILD_STAMP={stamp}");
    tauri_build::build()
}
