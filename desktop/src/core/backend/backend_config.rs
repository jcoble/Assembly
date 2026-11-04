//! `BackendConfig` owns how the backend is started, from the `backend/lane.json` file written by
//! `scripts/app-dev.ts` (port, home, tokenFile, remotePort, remoteTokenFile). The program is `Assembly.Desktop`
//! in the same backend folder. `ASSEMBLY_REMOTE` names a remote machine (such as `workbox-test`) to pass as
//! `--remote`.

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
    /// How long a backend gets to exit after SIGTERM before it's killed; longer than the backend's own 8 s bound on
    /// finishing its queued writes (ApiServer.ShutdownTimeout).
    pub stop_grace: Duration,
}

impl BackendConfig {
    /// Reads `<backend>/lane.json`, or a packaged app's `<backend>/packaged.json`, whose paths are under the user's
    /// home; the error names the file and the field that's missing.
    pub fn read(backend: &Path, remote: Option<String>) -> Result<Self, String> {
        let home = std::env::var_os("HOME").map(PathBuf::from);
        Self::read_under(backend, remote, home.as_deref())
    }

    fn read_under(
        backend: &Path,
        remote: Option<String>,
        user_home: Option<&Path>,
    ) -> Result<Self, String> {
        let packaged = backend.join("packaged.json");
        let under = packaged.is_file().then_some(user_home);
        let file = if under.is_some() {
            packaged
        } else {
            backend.join("lane.json")
        };
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
            let value = lane[name].as_str().ok_or_else(|| missing(name))?;
            match under {
                None => Ok(PathBuf::from(value)),
                Some(Some(home)) => Ok(home.join(value)),
                Some(None) => Err(format!("{} needs HOME", file.display())),
            }
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
            stop_grace: Duration::from_secs(10),
        })
    }
}

/// The app bundle's `Contents/Resources`, when `program` runs from a packaged app.
pub fn packaged_resources(program: &Path) -> Option<PathBuf> {
    let resources = program.parent()?.parent()?.join("Resources");
    resources
        .join("backend/packaged.json")
        .is_file()
        .then_some(resources)
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
    fn reads_the_lane_file_the_native_dev_launcher_writes() {
        let dir = lane(
            r#"{ "port": 18877, "home": "/h", "tokenFile": "/h/token.env", "remotePort": 9877, "remoteTokenFile": "/r/token.env" }"#,
        );
        let config = BackendConfig::read(&dir, Some("workbox-test".into())).unwrap();
        assert_eq!(config.program, dir.join("Assembly.Desktop"));
        assert_eq!((config.port, config.remote_port), (18877, 9877));
        assert_eq!(config.home, PathBuf::from("/h"));
        assert_eq!(config.token_file, PathBuf::from("/h/token.env"));
        assert_eq!(config.remote.as_deref(), Some("workbox-test"));
        assert_eq!(config.stop_grace, Duration::from_secs(10));
    }

    #[test]
    fn names_the_missing_field() {
        let dir = lane(r#"{ "port": 18877, "home": "/h" }"#);
        let error = BackendConfig::read(&dir, None).err().unwrap();
        assert!(error.ends_with("has no tokenFile"), "{error}");
    }

    fn packaged_app(name: &str) -> PathBuf {
        let app = std::env::temp_dir().join(format!(
            "g0-host-packaged-{}-{name}/Assembly.app/Contents",
            std::process::id()
        ));
        std::fs::create_dir_all(app.join("Resources/backend")).unwrap();
        std::fs::create_dir_all(app.join("MacOS")).unwrap();
        std::fs::write(
            app.join("Resources/backend/packaged.json"),
            r#"{ "port": 18797, "home": "assembly-dotnet/mac", "tokenFile": ".config/assembly-dotnet/env", "remotePort": 9797, "remoteTokenFile": ".config/assembly-dotnet/env" }"#,
        )
        .unwrap();
        app
    }

    #[test]
    fn a_packaged_app_starts_its_backend_under_the_users_home_without_a_lane_file() {
        let app = packaged_app("home");
        let backend = app.join("Resources/backend");
        let config =
            BackendConfig::read_under(&backend, None, Some(Path::new("/Users/someone"))).unwrap();
        assert_eq!(config.program, backend.join("Assembly.Desktop"));
        assert_eq!((config.port, config.remote_port), (18797, 9797));
        assert_eq!(
            config.home,
            PathBuf::from("/Users/someone/assembly-dotnet/mac")
        );
        assert_eq!(
            config.token_file,
            PathBuf::from("/Users/someone/.config/assembly-dotnet/env")
        );
    }

    #[test]
    fn a_packaged_app_without_a_home_says_so() {
        let backend = packaged_app("no-home").join("Resources/backend");
        let error = BackendConfig::read_under(&backend, None, None)
            .err()
            .unwrap();
        assert!(error.ends_with("needs HOME"), "{error}");
    }

    #[test]
    fn finds_the_resources_only_inside_a_packaged_app() {
        let app = packaged_app("resources");
        assert_eq!(
            packaged_resources(&app.join("MacOS/assembly-desktop")),
            Some(app.join("Resources"))
        );
        let checkout = lane(r#"{ "unused": "a checkout's target folder" }"#);
        assert_eq!(
            packaged_resources(&checkout.join("debug/assembly-desktop")),
            None
        );
    }
}
