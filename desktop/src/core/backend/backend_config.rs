//! `BackendConfig` owns how the backend is started, from the lane file `shell/assembly.sh` writes
//! (`backend/lane.json`: port, home, tokenFile, remotePort, remoteTokenFile), as the old shell read it
//! (shell/src/main.rs `read_lane`). The program is `Assembly.Desktop` in the same backend folder. `ASSEMBLY_REMOTE`
//! names a remote machine (such as `workbox-test`) to pass as `--remote`.

use std::path::{Path, PathBuf};

pub struct BackendConfig {
    pub program: PathBuf,
    pub port: u16,
    pub home: PathBuf,
    /// Where the backend keeps its token (an `ASSEMBLY_TOKEN=` line). Read only by the launcher, never printed.
    pub token_file: PathBuf,
    /// The machine from `ASSEMBLY_REMOTE`, with the lane's remote port and token file.
    pub remote: Option<String>,
    pub remote_port: u16,
    pub remote_token_file: PathBuf,
}

impl BackendConfig {
    /// Reads `<backend>/lane.json`; the error names the file and the field that's missing.
    pub fn read(_backend: &Path) -> Result<Self, String> {
        todo!()
    }
}
