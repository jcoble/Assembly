//! `BackendConfig` owns how the backend is started, from the lane file `shell/assembly.sh` writes
//! (`backend/lane.json`: port, home, tokenFile, remotePort, remoteTokenFile), as the old shell read it
//! (shell/src/main.rs `read_lane`). The program is `Assembly.Desktop` in the same backend folder. `ASSEMBLY_REMOTE`
//! names a remote machine (such as `workbox-test`) to pass as `--remote`.

use std::path::{Path, PathBuf};
use std::time::Duration;

use serde_json::Value;

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
    /// How long a backend gets to print its ready line before it's stopped.
    pub ready_limit: Duration,
}

impl BackendConfig {
    /// Reads `<backend>/lane.json`; the error names the file and the field that's missing.
    pub fn read(backend: &Path, remote: Option<String>) -> Result<Self, String> {
        let file = backend.join("lane.json");
        let text =
            std::fs::read_to_string(&file).map_err(|e| format!("{}: {e}", file.display()))?;
        let lane: Value =
            serde_json::from_str(&text).map_err(|e| format!("{}: {e}", file.display()))?;
        let missing = |name: &str| format!("{} has no {name}", file.display());
        let port = |name: &str| {
            lane[name]
                .as_u64()
                .and_then(|n| u16::try_from(n).ok())
                .ok_or_else(|| missing(name))
        };
        let path = |name: &str| {
            lane[name]
                .as_str()
                .map(PathBuf::from)
                .ok_or_else(|| missing(name))
        };
        Ok(Self {
            program: backend.join("Assembly.Desktop"),
            port: port("port")?,
            home: path("home")?,
            token_file: path("tokenFile")?,
            remote,
            remote_port: port("remotePort")?,
            remote_token_file: path("remoteTokenFile")?,
            ready_limit: Duration::from_secs(60),
        })
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn lane(json: &str) -> PathBuf {
        let dir = std::env::temp_dir().join(format!(
            "g0-host-config-{}-{}",
            std::process::id(),
            json.len()
        ));
        std::fs::create_dir_all(&dir).unwrap();
        std::fs::write(dir.join("lane.json"), json).unwrap();
        dir
    }

    #[test]
    fn reads_the_lane_file_the_shell_writes() {
        let dir = lane(
            r#"{ "port": 18877, "home": "/h", "tokenFile": "/h/token.env", "remotePort": 9877, "remoteTokenFile": "/r/token.env" }"#,
        );
        let config = BackendConfig::read(&dir, Some("workbox-test".into())).unwrap();
        assert_eq!(config.program, dir.join("Assembly.Desktop"));
        assert_eq!((config.port, config.remote_port), (18877, 9877));
        assert_eq!(config.home, PathBuf::from("/h"));
        assert_eq!(config.token_file, PathBuf::from("/h/token.env"));
        assert_eq!(config.remote.as_deref(), Some("workbox-test"));
    }

    #[test]
    fn names_the_missing_field() {
        let dir = lane(r#"{ "port": 18877, "home": "/h" }"#);
        let error = BackendConfig::read(&dir, None).err().unwrap();
        assert!(error.ends_with("has no tokenFile"), "{error}");
    }
}
