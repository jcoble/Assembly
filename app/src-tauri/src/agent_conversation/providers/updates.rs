use std::collections::HashSet;
use std::path::{Component, Path, PathBuf};
use std::sync::Arc;
use std::time::Duration;

use base64::Engine;
use minisign_verify::{PublicKey, Signature};
use reqwest::Client;
use semver::Version;
use serde::{Deserialize, Serialize};
use sha2::{Digest, Sha256};
use tauri::{Emitter, Manager};
use tokio::io::AsyncWriteExt;

use super::packaged::{self, AdapterPairs, PackagedAdapter};
use super::ProviderRegistry;

const RELEASE_BASE_URL: &str = "https://github.com/jcoble/mac-command-bar/releases/download/assembly-providers-stable";
const SIGNING_PUBLIC_KEY: &str = "RWQZvQJuc5RPnp9xO8+V9ppE3cCiodEFHPYqJpIMMVRhAnaxo0udp8xh";
pub(crate) static INSTALL_LOCK: tokio::sync::Mutex<()> = tokio::sync::Mutex::const_new(());

const MAX_MANIFEST_BYTES: usize = 1024 * 1024;
// Google’s official Linux server is 919,951,920 bytes in the signed 1.2.1 release.
const MAX_ADAPTER_FILE_BYTES: u64 = 1024 * 1024 * 1024;

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
struct RemoteManifest {
    schema_version: u32,
    target: String,
    adapters: Vec<PackagedAdapter>,
}

#[derive(Debug, Deserialize, Serialize)]
struct ActiveAdapterDirectory {
    directory: String,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ProviderUpdateVersion {
    provider: String,
    current_version: Option<String>,
    available_version: String,
    update_available: bool,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ProviderUpdateStatus {
    target: String,
    update_available: bool,
    restart_required: bool,
    providers: Vec<ProviderUpdateVersion>,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ProviderDownloadProgress {
    pub provider: String,
    pub file_index: usize,
    pub file_count: usize,
    pub downloaded: u64,
    pub total: Option<u64>,
    pub profile_id: Option<String>,
}

pub(crate) type ProgressSink = Arc<dyn Fn(ProviderDownloadProgress) + Send + Sync>;

pub(super) fn discover_active(app_data_dir: &Path) -> Result<AdapterPairs, String> {
    let root = app_data_dir.join("provider-adapters");
    let pointer_path = root.join("active.json");
    if !pointer_path.is_file() {
        return Ok((None, None, None));
    }
    let pointer: ActiveAdapterDirectory = serde_json::from_slice(
        &std::fs::read(&pointer_path)
            .map_err(|error| format!("Could not read active provider adapters: {error}"))?,
    )
    .map_err(|error| format!("Active provider adapter pointer is invalid: {error}"))?;
    if !safe_file_name(&pointer.directory) {
        return Err("Active provider adapter directory is invalid".to_string());
    }
    packaged::discover_in(&root.join(pointer.directory), false)
}

#[tauri::command]
pub async fn check_provider_updates(
    app: tauri::AppHandle,
    manager: tauri::State<'_, crate::agent_conversation::manager::AgentRuntimeManager>,
) -> Result<ProviderUpdateStatus, String> {
    let app_data_dir = app
        .path()
        .app_data_dir()
        .map_err(|error| format!("Application data directory is unavailable: {error}"))?;
    check_at(&app_data_dir, manager.providers()).await
}

#[tauri::command]
pub async fn install_provider_updates(
    app: tauri::AppHandle,
    manager: tauri::State<'_, crate::agent_conversation::manager::AgentRuntimeManager>,
    provider: String,
) -> Result<ProviderUpdateStatus, String> {
    let app_data_dir = app
        .path()
        .app_data_dir()
        .map_err(|error| format!("Application data directory is unavailable: {error}"))?;
    let progress_app = app.clone();
    install_at(&app_data_dir, manager.providers(), &provider, Arc::new(move |progress| {
        let _ = progress_app.emit("provider-download-progress", progress);
    })).await
}

pub(crate) async fn check_at(
    app_data_dir: &Path,
    providers: &ProviderRegistry,
) -> Result<ProviderUpdateStatus, String> {
    let (_, manifest) = fetch_signed_manifest().await?;
    status_for(app_data_dir, &manifest, &running_versions(providers)?).await
}

pub(crate) async fn install_at(
    app_data_dir: &Path,
    providers: &ProviderRegistry,
    provider: &str,
    progress: ProgressSink,
) -> Result<ProviderUpdateStatus, String> {
    // Native commands and remote clients share the same installation owner.
    // Reject overlap instead of queueing an obsolete second activation.
    let _installation = INSTALL_LOCK.try_lock()
        .map_err(|_| "A provider adapter installation is already running".to_string())?;
    let (_, manifest) = fetch_signed_manifest().await?;
    let running = running_versions(providers)?;
    let before = status_for(app_data_dir, &manifest, &running).await?;
    let selected = before.providers.iter().find(|version| version.provider == provider)
        .ok_or_else(|| format!("Unknown provider adapter: {provider}"))?;
    if !selected.update_available {
        return Ok(before);
    }
    ensure_no_downgrade(selected)?;
    install_manifest(app_data_dir, &manifest, provider, &progress).await?;
    status_for(app_data_dir, &manifest, &running).await
}

#[tauri::command]
pub fn restart_for_provider_updates(
    app: tauri::AppHandle,
    manager: tauri::State<'_, crate::agent_conversation::manager::AgentRuntimeManager>,
) -> Result<(), String> {
    if manager.has_pending_provider_work() {
        return Err("Provider adapters are installed. Finish or stop active conversations before restarting Assembly.".into());
    }
    app.restart();
}

fn ensure_no_downgrade(provider: &ProviderUpdateVersion) -> Result<(), String> {
    if let Some(current) = &provider.current_version {
        if Version::parse(&provider.available_version).map_err(|error| error.to_string())?
            < Version::parse(current).map_err(|error| error.to_string())? {
            return Err(format!(
                "Provider update would downgrade {}; the installed adapter was kept",
                provider.provider
            ));
        }
    }
    Ok(())
}

async fn fetch_signed_manifest() -> Result<(Vec<u8>, RemoteManifest), String> {
    let target = release_target()?;
    let manifest_name = format!("provider-manifest-{target}.json");
    let client = Client::builder()
        .connect_timeout(Duration::from_secs(10))
        .timeout(Duration::from_secs(30))
        .user_agent("Assembly provider updater")
        .build()
        .map_err(|error| format!("Could not create provider update client: {error}"))?;
    let manifest_bytes =
        download_small(&client, &format!("{RELEASE_BASE_URL}/{manifest_name}")).await?;
    let signature_bytes =
        download_small(&client, &format!("{RELEASE_BASE_URL}/{manifest_name}.sig")).await?;
    verify_manifest_signature(&manifest_bytes, &signature_bytes, SIGNING_PUBLIC_KEY)?;
    let manifest: RemoteManifest = serde_json::from_slice(&manifest_bytes)
        .map_err(|error| format!("Provider update manifest is invalid: {error}"))?;
    if manifest.schema_version != 1 || manifest.target != target {
        return Err("Provider update manifest does not match this build".to_string());
    }
    validate_remote_manifest(&manifest)?;
    Ok((manifest_bytes, manifest))
}

fn verify_manifest_signature(manifest_bytes: &[u8], signature_bytes: &[u8], public_key: &str) -> Result<(), String> {
    let public_key = PublicKey::from_base64(public_key)
        .map_err(|error| format!("Provider update public key is invalid: {error}"))?;
    // Tauri signer emits a base64 envelope around the Minisign text.
    let encoded = std::str::from_utf8(signature_bytes)
        .map_err(|_| "Provider update signature is not UTF-8".to_string())?;
    let decoded = base64::engine::general_purpose::STANDARD.decode(encoded.trim())
        .map_err(|error| format!("Provider update signature encoding is invalid: {error}"))?;
    let signature = Signature::decode(
        std::str::from_utf8(&decoded)
            .map_err(|_| "Provider update signature is not UTF-8".to_string())?,
    )
    .map_err(|error| format!("Provider update signature is invalid: {error}"))?;
    public_key
        .verify(&manifest_bytes, &signature, false)
        .map_err(|error| format!("Provider update signature verification failed: {error}"))?;
    Ok(())
}

async fn download_small(client: &Client, url: &str) -> Result<Vec<u8>, String> {
    let mut response = client
        .get(url)
        .send()
        .await
        .map_err(|error| format!("Could not download provider update metadata: {error}"))?
        .error_for_status()
        .map_err(|error| format!("Provider update metadata is unavailable: {error}"))?;
    let mut bytes = Vec::new();
    while let Some(chunk) = response
        .chunk()
        .await
        .map_err(|error| format!("Could not read provider update metadata: {error}"))?
    {
        if bytes.len().saturating_add(chunk.len()) > MAX_MANIFEST_BYTES {
            return Err("Provider update metadata is too large".to_string());
        }
        bytes.extend_from_slice(&chunk);
    }
    Ok(bytes)
}

fn validate_remote_manifest(manifest: &RemoteManifest) -> Result<(), String> {
    if manifest.adapters.len() != 3 {
        return Err("Provider update manifest must contain all three adapters".to_string());
    }
    let expected = [
        ("codex", "codex-acp"),
        ("claude", "claude-agent-acp"),
        ("antigravity", "agy-acp"),
    ];
    let mut providers = HashSet::new();
    for adapter in &manifest.adapters {
        if !expected
            .iter()
            .any(|(provider, id)| adapter.provider == *provider && adapter.id == *id)
            || !providers.insert(adapter.provider.as_str())
            || adapter.files.is_empty()
            || !safe_file_name(&adapter.executable)
            || !adapter
                .files
                .iter()
                .any(|file| file.path == adapter.executable)
        {
            return Err(format!(
                "Provider update for {} is incomplete",
                adapter.provider
            ));
        }
        Version::parse(&adapter.version).map_err(|_| {
            format!(
                "Provider update for {} has an invalid version",
                adapter.provider
            )
        })?;
        for file in &adapter.files {
            if !safe_file_name(&file.path)
                || file.sha256.len() != 64
                || !file.sha256.bytes().all(|byte| byte.is_ascii_hexdigit())
            {
                return Err(format!(
                    "Provider update file for {} is invalid",
                    adapter.provider
                ));
            }
        }
    }
    Ok(())
}

async fn status_for(
    app_data_dir: &Path,
    manifest: &RemoteManifest,
    running: &[(String, String, PathBuf)],
) -> Result<ProviderUpdateStatus, String> {
    // Hashing large adapter files must not block an async runtime worker.
    let directory = app_data_dir.to_path_buf();
    let (current, damaged, has_active, verified) = tokio::task::spawn_blocking(move || {
        let has_active = directory.join("provider-adapters/active.json").is_file();
        let active = discover_active(&directory);
        let mut verified = HashSet::new();
        if let Ok((codex, claude, antigravity)) = &active {
            if codex.is_some() { verified.insert("codex"); }
            if claude.is_some() { verified.insert("claude"); }
            if antigravity.is_some() { verified.insert("antigravity"); }
        }
        (active_versions(&directory).unwrap_or_default(), active.is_err(), has_active, verified)
    }).await.map_err(|error| format!("Could not inspect installed provider adapters: {error}"))?;
    let restart_required = !damaged && current.iter().filter(|(provider, _, _)| verified.contains(provider.as_str())).any(|(provider, version, executable)| {
        running.iter().find(|(name, _, _)| name == provider)
            .map(|(_, active, path)| (active, path)) != Some((version, executable))
    });
    let mut providers = Vec::with_capacity(manifest.adapters.len());
    for adapter in &manifest.adapters {
        let current_version = current
            .iter()
            .find(|(provider, _, _)| provider == &adapter.provider)
            .map(|(_, version, _)| version.as_str())
            .or_else(|| (!has_active).then(|| running.iter().find(|(provider, _, _)| provider == &adapter.provider))
                .flatten()
                .map(|(_, version, _)| version.as_str()));
        let available = Version::parse(&adapter.version).map_err(|error| error.to_string())?;
        let installed = current_version.map(Version::parse).transpose().map_err(|error| error.to_string())?;
        // Assembly can correct a launcher without an upstream adapter version bump.
        // Compare the signed payload on the blocking pool, including bundled installs.
        let changed_payload = if installed.as_ref() == Some(&available) {
            let executable = current.iter().chain(running.iter())
                .find(|(provider, _, _)| provider == &adapter.provider)
                .map(|(_, _, path)| path.clone()).unwrap();
            let files = adapter.files.clone();
            tokio::task::spawn_blocking(move || {
                let Some(directory) = executable.parent() else { return true; };
                files.iter().any(|file| super::file_sha256(&directory.join(&file.path))
                    .map(|hash| hash != file.sha256).unwrap_or(true))
            }).await.map_err(|error| format!("Could not inspect provider package contents: {error}"))?
        } else {
            false
        };
        let damaged_provider = has_active && current_version.is_some() && !verified.contains(adapter.provider.as_str());
        let update_available = current_version.is_none() || damaged || damaged_provider
            || installed.is_some_and(|installed| available > installed) || changed_payload;
        providers.push(ProviderUpdateVersion {
            provider: adapter.provider.clone(),
            current_version: current_version.map(str::to_string),
            available_version: adapter.version.clone(),
            update_available,
        });
    }
    Ok(ProviderUpdateStatus {
        target: manifest.target.clone(),
        update_available: providers.iter().any(|provider| provider.update_available),
        restart_required,
        providers,
    })
}

fn active_versions(app_data_dir: &Path) -> Result<Vec<(String, String, PathBuf)>, String> {
    let root = app_data_dir.join("provider-adapters");
    let pointer_path = root.join("active.json");
    if !pointer_path.is_file() {
        return Ok(Vec::new());
    }
    let pointer: ActiveAdapterDirectory = serde_json::from_slice(
        &std::fs::read(pointer_path)
            .map_err(|error| format!("Could not read active provider adapters: {error}"))?,
    )
    .map_err(|error| format!("Active provider adapter pointer is invalid: {error}"))?;
    if !safe_file_name(&pointer.directory) {
        return Err("Active provider adapter directory is invalid".to_string());
    }
    let manifest: packaged::PackagedManifest = serde_json::from_slice(
        &std::fs::read(root.join(&pointer.directory).join("manifest.json"))
            .map_err(|error| format!("Could not read active provider manifest: {error}"))?,
    )
    .map_err(|error| format!("Active provider manifest is invalid: {error}"))?;
    Ok(manifest
        .adapters
        .into_iter()
        .map(|adapter| (adapter.provider, adapter.version, root.join(&pointer.directory).join(adapter.executable)))
        .collect())
}

// Own only this installation's files. Dropping an interrupted download must
// clean them without touching the directory selected by the active pointer.
struct PendingInstallation(Option<PathBuf>);

impl Drop for PendingInstallation {
    fn drop(&mut self) {
        if let Some(path) = &self.0 {
            let _ = std::fs::remove_dir_all(path);
        }
    }
}

// Keep the lock file: unlinking it could let two processes lock different inodes.
// The OS releases ownership even if the updater is forcibly terminated.
fn prepare_installation(root: &Path) -> Result<std::fs::File, String> {
    std::fs::create_dir_all(root)
        .map_err(|error| format!("Could not create provider update directory: {error}"))?;
    let owner = std::fs::OpenOptions::new().read(true).write(true).create(true)
        .truncate(false).open(root.join("installation.lock"))
        .map_err(|error| format!("Could not lock provider update directory: {error}"))?;
    owner.try_lock()
        .map_err(|error| format!("Provider installation directory is busy: {error}"))?;
    let active = match std::fs::read(root.join("active.json")) {
        Ok(bytes) => serde_json::from_slice::<ActiveAdapterDirectory>(&bytes).ok()
            .map(|pointer| pointer.directory).filter(|name| safe_file_name(name)),
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => None,
        Err(error) => return Err(format!("Could not read active provider adapters: {error}")),
    };
    for entry in std::fs::read_dir(root)
        .map_err(|error| format!("Could not inspect interrupted provider downloads: {error}"))? {
        let entry = entry.map_err(|error| error.to_string())?;
        let name = entry.file_name();
        let Some(name) = name.to_str() else { continue };
        if active.as_deref() == Some(name) { continue; }
        let Some(suffix) = name.strip_prefix("install-") else { continue };
        if uuid::Uuid::parse_str(suffix).is_err() || !entry.file_type()
            .map_err(|error| error.to_string())?.is_dir() { continue; }
        std::fs::remove_dir_all(entry.path())
            .map_err(|error| format!("Could not remove interrupted provider download: {error}"))?;
    }
    Ok(owner)
}

async fn install_manifest(
    app_data_dir: &Path,
    manifest: &RemoteManifest,
    provider: &str,
    progress: &ProgressSink,
) -> Result<(), String> {
    let root = app_data_dir.join("provider-adapters");
    let _directory_owner = prepare_installation(&root)?;
    let selected = manifest.adapters.iter().find(|adapter| adapter.provider == provider)
        .ok_or_else(|| format!("Unknown provider adapter: {provider}"))?;
    // Each verified install owns its directory; never reuse a damaged or
    // interrupted prior installation of the same manifest.
    let directory_name = format!("{}-{}", manifest.target, uuid::Uuid::new_v4());
    let destination = root.join(&directory_name);
    let temporary = root.join(format!("install-{}", uuid::Uuid::new_v4()));
    std::fs::create_dir(&temporary)
        .map_err(|error| format!("Could not stage provider update: {error}"))?;
    let mut pending = PendingInstallation(Some(temporary.clone()));

    let mut installed = Vec::new();
    let active = match std::fs::read(root.join("active.json")) {
        Ok(bytes) => serde_json::from_slice::<ActiveAdapterDirectory>(&bytes).ok()
            .filter(|pointer| safe_file_name(&pointer.directory)),
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => None,
        Err(error) => return Err(format!("Could not read active provider adapters: {error}")),
    };
    if let Some(pointer) = active {
        let source = root.join(pointer.directory);
        let previous = match std::fs::read(source.join("manifest.json")) {
            Ok(bytes) => serde_json::from_slice::<packaged::PackagedManifest>(&bytes).ok(),
            Err(error) if error.kind() == std::io::ErrorKind::NotFound => None,
            Err(error) => return Err(format!("Could not read installed provider manifest: {error}")),
        };
        for adapter in previous.into_iter().flat_map(|manifest| manifest.adapters)
            .filter(|adapter| adapter.provider != provider) {
            if adapter.files.is_empty() || !adapter.files.iter().all(|file| safe_file_name(&file.path)
                && super::file_sha256(&source.join(&file.path))
                    .is_ok_and(|hash| hash.eq_ignore_ascii_case(&file.sha256))) {
                continue;
            }
            for file in &adapter.files {
                std::fs::hard_link(source.join(&file.path), temporary.join(&file.path))
                    .map_err(|error| format!("Could not retain installed {} adapter: {error}", adapter.provider))?;
            }
            installed.push(adapter);
        }
    }
    download_adapter_files(&temporary, &manifest.target, selected, progress).await?;
    installed.push(selected.clone());
    std::fs::write(temporary.join("manifest.json"), serde_json::to_vec(&packaged::PackagedManifest {
        schema_version: 1,
        adapters: installed,
    }).map_err(|error| format!("Could not encode installed provider manifest: {error}"))?)
        .map_err(|error| format!("Could not stage provider manifest: {error}"))?;
    let (codex, claude, antigravity) = packaged::discover_in(&temporary, false)?;
    let selected_verified = match provider {
        "codex" => codex.is_some(),
        "claude" => claude.is_some(),
        "antigravity" => antigravity.is_some(),
        _ => false,
    };
    if !selected_verified {
        return Err(format!("Downloaded {provider} adapter failed verification"));
    }

    // No await between promotion and pointer activation: cancellation cannot
    // race filesystem work and accidentally remove the newly active bundle.
    std::fs::rename(&temporary, &destination)
        .map_err(|error| format!("Could not install provider update: {error}"))?;
    pending.0 = Some(destination);
    let pointer = serde_json::to_vec(&ActiveAdapterDirectory {
        directory: directory_name,
    })
    .map_err(|error| format!("Could not encode provider update pointer: {error}"))?;
    let pointer_temporary = root.join(format!("active-{}.tmp", uuid::Uuid::new_v4()));
    let activation_result = (|| {
        std::fs::write(&pointer_temporary, pointer)
            .map_err(|error| format!("Could not stage provider update pointer: {error}"))?;
        std::fs::rename(&pointer_temporary, root.join("active.json"))
            .map_err(|error| format!("Could not activate provider update: {error}"))?;
        Ok::<(), String>(())
    })();
    if let Err(error) = activation_result {
        let _ = std::fs::remove_file(&pointer_temporary);
        return Err(error);
    }
    pending.0 = None;
    Ok(())
}

async fn download_adapter_files(directory: &Path, target: &str, adapter: &PackagedAdapter, progress: &ProgressSink) -> Result<(), String> {
    let client = Client::builder()
        .connect_timeout(Duration::from_secs(10))
        .timeout(Duration::from_secs(180))
        .user_agent("Assembly provider updater")
        .build()
        .map_err(|error| format!("Could not create provider download client: {error}"))?;
    for (index, file) in adapter.files.iter().enumerate() {
        let asset_name = format!("provider-{target}-{}", file.path);
        download_file(
            &client,
            &format!("{RELEASE_BASE_URL}/{asset_name}"),
            &directory.join(&file.path),
            &file.sha256,
            adapter,
            index,
            progress,
        )
        .await?;
    }
    Ok(())
}

async fn download_file(
    client: &Client,
    url: &str,
    destination: &Path,
    expected_hash: &str,
    adapter: &PackagedAdapter,
    index: usize,
    progress: &ProgressSink,
) -> Result<(), String> {
    let mut response = client
        .get(url)
        .send()
        .await
        .map_err(|error| format!("Could not download provider adapter: {error}"))?
        .error_for_status()
        .map_err(|error| format!("Provider adapter is unavailable: {error}"))?;
    let total = response.content_length();
    let report = |downloaded| progress(ProviderDownloadProgress {
        provider: adapter.provider.clone(),
        file_index: index + 1,
        file_count: adapter.files.len(),
        downloaded,
        total,
        profile_id: None,
    });
    report(0);
    let mut file = tokio::fs::File::create(destination)
        .await
        .map_err(|error| format!("Could not stage provider adapter: {error}"))?;
    let mut hasher = Sha256::new();
    let mut written = 0_u64;
    let mut last_report = std::time::Instant::now();
    while let Some(chunk) = response
        .chunk()
        .await
        .map_err(|error| format!("Could not read provider adapter: {error}"))?
    {
        written = written.saturating_add(chunk.len() as u64);
        if written > MAX_ADAPTER_FILE_BYTES {
            return Err("Provider adapter exceeds the download limit".to_string());
        }
        hasher.update(&chunk);
        file.write_all(&chunk)
            .await
            .map_err(|error| format!("Could not write provider adapter: {error}"))?;
        if last_report.elapsed() >= Duration::from_millis(200) {
            report(written);
            last_report = std::time::Instant::now();
        }
    }
    file.flush()
        .await
        .map_err(|error| format!("Could not finish provider adapter: {error}"))?;
    report(written);
    if format!("{:x}", hasher.finalize()) != expected_hash.to_ascii_lowercase() {
        return Err("Provider adapter failed SHA-256 verification".to_string());
    }
    #[cfg(unix)]
    {
        use std::os::unix::fs::PermissionsExt;
        let mut permissions = tokio::fs::metadata(destination)
            .await
            .map_err(|error| format!("Could not read provider adapter permissions: {error}"))?
            .permissions();
        permissions.set_mode(0o755);
        tokio::fs::set_permissions(destination, permissions)
            .await
            .map_err(|error| format!("Could not make provider adapter executable: {error}"))?;
    }
    Ok(())
}

fn running_versions(registry: &ProviderRegistry) -> Result<Vec<(String, String, PathBuf)>, String> {
    Ok(super::descriptors::all().iter().filter_map(|descriptor| {
        registry.manifest(descriptor.provider()).ok().map(|manifest| (descriptor.id.clone(), manifest.version, manifest.executable))
    }).collect())
}

fn safe_file_name(value: &str) -> bool {
    !value.is_empty()
        && Path::new(value)
            .components()
            .all(|component| matches!(component, Component::Normal(_)))
        && Path::new(value).components().count() == 1
}

fn release_target() -> Result<String, String> {
    let architecture = match std::env::consts::ARCH {
        "aarch64" => "aarch64",
        "x86_64" => "x86_64",
        other => return Err(format!("Provider updates do not support {other}")),
    };
    let platform = match std::env::consts::OS {
        "macos" => "apple-darwin",
        "linux" => "unknown-linux-gnu",
        other => return Err(format!("Provider updates do not support {other}")),
    };
    Ok(format!("{architecture}-{platform}"))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[tokio::test]
    #[ignore = "requires the published GitHub provider release"]
    async fn published_provider_manifest_verifies_with_production_key() {
        let (_, manifest) = fetch_signed_manifest().await.unwrap();
        assert_eq!(manifest.adapters.len(), 3);
        // Linux ships Google’s largest payload; verify our shared download limit admits it.
        let linux = Client::new().head(format!("{RELEASE_BASE_URL}/provider-x86_64-unknown-linux-gnu-agy_acp_server.par"))
            .timeout(Duration::from_secs(30)).send().await.unwrap().error_for_status().unwrap();
        let size: u64 = linux.headers().get(reqwest::header::CONTENT_LENGTH).unwrap().to_str().unwrap().parse().unwrap();
        assert!(size > 0 && size <= MAX_ADAPTER_FILE_BYTES, "Published Linux adapter is {size} bytes");
        for adapter in manifest.adapters {
            println!("Published {} {}", adapter.provider, adapter.version);
        }
    }

    #[test]
    fn provider_feed_is_independent_of_app_and_backend_releases() {
        assert!(RELEASE_BASE_URL.ends_with("/releases/download/assembly-providers-stable"));
    }

    #[test]
    fn selected_adapter_cannot_downgrade() {
        let mut status = ProviderUpdateVersion {
                provider: "claude".into(),
                current_version: Some("0.81.2".into()),
                available_version: "0.76.0".into(),
                update_available: false,
        };
        assert!(ensure_no_downgrade(&status).is_err());
        status.available_version = "0.81.2".into();
        assert!(ensure_no_downgrade(&status).is_ok());
        status.available_version = "0.82.0".into();
        assert!(ensure_no_downgrade(&status).is_ok());
    }

    #[test]
    fn active_pointer_cannot_escape_provider_directory() {
        assert!(safe_file_name("aarch64-apple-darwin-1234"));
        assert!(!safe_file_name("../outside"));
        assert!(!safe_file_name("nested/value"));
    }

    #[test]
    fn invalid_active_pointer_does_not_block_staging() {
        let root = std::env::temp_dir().join(format!("mcb-provider-pointer-repair-{}", uuid::Uuid::new_v4()));
        std::fs::create_dir_all(&root).unwrap();
        std::fs::write(root.join("active.json"), br#"{"directory":"../outside"}"#).unwrap();
        let owner = prepare_installation(&root).unwrap();
        assert!(root.join("active.json").exists());
        drop(owner);
        std::fs::remove_dir_all(root).unwrap();
    }

    #[tokio::test]
    async fn missing_provider_is_offered_when_another_is_installed() {
        let directory = std::env::temp_dir().join(format!("mcb-selective-adapters-{}", uuid::Uuid::new_v4()));
        let installed = directory.join("provider-adapters/installed");
        std::fs::create_dir_all(&installed).unwrap();
        std::fs::write(directory.join("provider-adapters/active.json"), r#"{"directory":"installed"}"#).unwrap();
        let hash = format!("{:x}", Sha256::digest(b"claude"));
        let claude = PackagedAdapter {
            provider: "claude".into(), id: "claude-agent-acp".into(), version: "1.0.0".into(),
            executable: "claude-agent-acp".into(),
            files: vec![packaged::PackagedFile { path: "claude-agent-acp".into(), sha256: hash }],
        };
        std::fs::write(installed.join("claude-agent-acp"), b"claude").unwrap();
        std::fs::write(installed.join("manifest.json"), serde_json::to_vec(&packaged::PackagedManifest {
            schema_version: 1, adapters: vec![claude.clone()],
        }).unwrap()).unwrap();
        let feed = RemoteManifest {
            schema_version: 1, target: release_target().unwrap(),
            adapters: vec![claude, PackagedAdapter {
                provider: "codex".into(), id: "codex-acp".into(), version: "1.0.0".into(),
                executable: "codex-acp".into(),
                files: vec![packaged::PackagedFile { path: "codex-acp".into(), sha256: "0".repeat(64) }],
            }],
        };
        let status = status_for(&directory, &feed, &[]).await.unwrap();
        assert!(!status.providers[0].update_available);
        assert_eq!(status.providers[0].current_version.as_deref(), Some("1.0.0"));
        assert!(status.providers[1].update_available);
        assert_eq!(status.providers[1].current_version, None);
        std::fs::remove_dir_all(directory).unwrap();
    }

    #[tokio::test]
    async fn damaged_provider_does_not_hide_healthy_provider() {
        let directory = std::env::temp_dir().join(format!("mcb-damaged-one-adapter-{}", uuid::Uuid::new_v4()));
        let installed = directory.join("provider-adapters/installed");
        std::fs::create_dir_all(&installed).unwrap();
        std::fs::write(directory.join("provider-adapters/active.json"), r#"{"directory":"installed"}"#).unwrap();
        let adapters = [("codex", "codex-acp"), ("claude", "claude-agent-acp")].map(|(provider, id)| {
            std::fs::write(installed.join(id), provider.as_bytes()).unwrap();
            PackagedAdapter {
                provider: provider.into(), id: id.into(), version: "1.0.0".into(), executable: id.into(),
                files: vec![packaged::PackagedFile { path: id.into(), sha256: format!("{:x}", Sha256::digest(provider.as_bytes())) }],
            }
        });
        std::fs::write(installed.join("manifest.json"), serde_json::to_vec(&packaged::PackagedManifest {
            schema_version: 1, adapters: adapters.to_vec(),
        }).unwrap()).unwrap();
        std::fs::write(installed.join("codex-acp"), b"damaged").unwrap();
        let registry = ProviderRegistry::bundled_from_environment_at(Some(&directory)).unwrap();
        assert!(registry.manifest(crate::agent_conversation::protocol::AgentConversationProvider::Codex).is_err());
        assert!(registry.manifest(crate::agent_conversation::protocol::AgentConversationProvider::Claude).is_ok());
        let feed = RemoteManifest { schema_version: 1, target: release_target().unwrap(), adapters: adapters.to_vec() };
        let running = vec![("claude".into(), "1.0.0".into(), installed.join("claude-agent-acp"))];
        let status = status_for(&directory, &feed, &running).await.unwrap();
        assert!(status.providers[0].update_available);
        assert!(!status.providers[1].update_available);
        assert!(!status.restart_required);
        std::fs::remove_dir_all(directory).unwrap();
    }

    #[tokio::test]
    async fn update_status_only_accepts_newer_versions() {
        let manifest = RemoteManifest {
            schema_version: 1,
            target: release_target().unwrap(),
            adapters: vec![PackagedAdapter {
                provider: "codex".into(),
                id: "codex-acp".into(),
                version: "99.0.0".into(),
                executable: "codex-acp".into(),
                files: vec![packaged::PackagedFile {
                    path: "codex-acp".into(),
                    sha256: "0".repeat(64),
                }],
            }],
        };
        let directory = std::env::temp_dir().join(format!(
            "mcb-provider-update-status-{}",
            uuid::Uuid::new_v4()
        ));
        std::fs::create_dir_all(&directory).unwrap();
        let running = vec![("codex".into(), "1.0.0".into(), PathBuf::from("/bundled/codex-acp"))];
        let status = status_for(&directory, &manifest, &running).await.unwrap();
        assert!(status.update_available);
        std::fs::remove_dir_all(directory).unwrap();
    }

    #[tokio::test]
    async fn installed_update_keeps_restart_required_until_runtime_uses_it() {
        let directory = std::env::temp_dir().join(format!("mcb-provider-restart-{}", uuid::Uuid::new_v4()));
        let root = directory.join("provider-adapters");
        std::fs::create_dir_all(root.join("installed")).unwrap();
        std::fs::write(root.join("active.json"), r#"{"directory":"installed"}"#).unwrap();
        let bytes = serde_json::json!({"schemaVersion":1,"target":release_target().unwrap(),"adapters":[{
            "provider":"codex","id":"codex-acp","version":"2.0.0","executable":"codex-acp","files":[{"path":"codex-acp","sha256":format!("{:x}", Sha256::digest(b"adapter"))}]
        }]}).to_string();
        std::fs::write(root.join("installed/manifest.json"), &bytes).unwrap();
        std::fs::write(root.join("installed/codex-acp"), b"adapter").unwrap();
        let manifest = serde_json::from_str(&bytes).unwrap();
        let before = status_for(&directory, &manifest, &[("codex".into(), "1.0.0".into(), PathBuf::from("/bundled/codex-acp"))]).await.unwrap();
        assert!(before.restart_required);
        assert!(!before.update_available);
        assert_eq!(before.providers[0].current_version.as_deref(), Some("2.0.0"));
        let after = status_for(&directory, &manifest, &[("codex".into(), "2.0.0".into(), root.join("installed/codex-acp"))]).await.unwrap();
        assert!(!after.restart_required);
        assert!(!after.update_available);
        let mut revised: RemoteManifest = serde_json::from_str(&bytes).unwrap();
        revised.adapters[0].files[0].sha256 = format!("{:x}", Sha256::digest(b"corrected launcher"));
        let running = vec![("codex".into(), "2.0.0".into(), root.join("installed/codex-acp"))];
        let correction = status_for(&directory, &revised, &running).await.unwrap();
        assert!(correction.update_available);
        assert!(!correction.restart_required);
        // A bundled install without an active pointer must receive the correction too.
        std::fs::remove_file(root.join("active.json")).unwrap();
        assert!(status_for(&directory, &revised, &running).await.unwrap().update_available);
        assert!(!status_for(&directory, &manifest, &running).await.unwrap().update_available);
        std::fs::remove_dir_all(directory).unwrap();
    }

    #[tokio::test]
    async fn damaged_current_bundle_is_offered_again_without_changing_the_pointer() {
        let directory = std::env::temp_dir().join(format!("mcb-provider-repair-{}", uuid::Uuid::new_v4()));
        let root = directory.join("provider-adapters");
        let installed = root.join("installed");
        std::fs::create_dir_all(&installed).unwrap();
        let pointer = br#"{"directory":"installed"}"#;
        std::fs::write(root.join("active.json"), pointer).unwrap();
        let payload = b"verified adapter";
        let bytes = serde_json::json!({"schemaVersion":1,"target":release_target().unwrap(),"adapters":[{
            "provider":"codex","id":"codex-acp","version":"2.0.0","executable":"codex-acp",
            "files":[{"path":"codex-acp","sha256":format!("{:x}", Sha256::digest(payload))}]
        }]}).to_string();
        std::fs::write(installed.join("manifest.json"), &bytes).unwrap();
        let manifest = serde_json::from_str(&bytes).unwrap();
        let running = vec![("codex".into(), "2.0.0".into(), installed.join("codex-acp"))];
        for contents in [None, Some(b"damaged".as_slice()), Some(payload.as_slice())] {
            if let Some(contents) = contents {
                std::fs::write(installed.join("codex-acp"), contents).unwrap();
            }
            let status = status_for(&directory, &manifest, &running).await.unwrap();
            assert_eq!(status.update_available, contents != Some(payload.as_slice()));
            assert!(!status.restart_required);
            assert_eq!(std::fs::read(root.join("active.json")).unwrap(), pointer);
        }
        // Repair installs a new directory even when the version is unchanged.
        let old_runtime = vec![("codex".into(), "2.0.0".into(), root.join("previous/codex-acp"))];
        let repaired = status_for(&directory, &manifest, &old_runtime).await.unwrap();
        assert!(repaired.restart_required);
        assert!(!repaired.update_available);
        std::fs::remove_dir_all(directory).unwrap();
    }

    #[test]
    fn tauri_signer_output_verifies_and_tampering_is_rejected() {
        // Public fixture generated by the same Tauri signer command used in CI.
        // Its disposable private key was discarded; this is not a release key.
        let public_key = "RWQaQV+jk36Ok4Nq0PBBqubOdKFz09ZM8mQggZWxaJSGmy1tlCTi7Ts3";
        let payload = "{\"fixture\":\"TSK-936 signed provider metadata\"}\n".as_bytes();
        let signature = "dW50cnVzdGVkIGNvbW1lbnQ6IHNpZ25hdHVyZSBmcm9tIHRhdXJpIHNlY3JldCBrZXkKUlVRYVFWK2prMzZPazRkbnF3VW9LQTlwVUw3ZmZnQzNZU3hQekFrT1dpYjRQR096WXZ6ZWpzVnFLVmMreE1naFlhNFEzM3EyYjB0UDFTa3ZnWWtXMENWY2VmZGliV0g5NEFNPQp0cnVzdGVkIGNvbW1lbnQ6IHRpbWVzdGFtcDoxNzkwMzEzMDI0CWZpbGU6bWFuaWZlc3QuanNvbgowMHFXWWQ1QXBPMGhId0ZHMThEVHNXWjdDVmhpQzZxcDVFTnFYNWd5THA3SWY1aDJxSW13WXVIYUpoeU5TTjNXRFdTZXRKclc1SUhrRXJuTEt5MEhEdz09Cg==".as_bytes();
        verify_manifest_signature(payload, signature, public_key).unwrap();
        assert!(verify_manifest_signature(b"tampered manifest", signature, public_key).is_err());
        assert!(verify_manifest_signature(payload, b"invalid signature", public_key).is_err());
        assert!(verify_manifest_signature(payload, signature, SIGNING_PUBLIC_KEY).is_err());
    }

    #[test]
    fn provider_manifest_public_key_is_valid() {
        PublicKey::from_base64(SIGNING_PUBLIC_KEY).unwrap();
    }

    #[test]
    fn provider_manifest_requires_each_expected_executable() {
        let adapters = [
            ("codex", "codex-acp"),
            ("claude", "claude-agent-acp"),
            ("antigravity", "agy-acp"),
        ]
        .map(|(provider, id)| PackagedAdapter {
            provider: provider.into(),
            id: id.into(),
            version: "1.0.0".into(),
            executable: id.into(),
            files: vec![packaged::PackagedFile {
                path: id.into(),
                sha256: "0".repeat(64),
            }],
        });
        let valid = RemoteManifest {
            schema_version: 1,
            target: release_target().unwrap(),
            adapters: adapters.into_iter().collect(),
        };
        assert!(validate_remote_manifest(&valid).is_ok());

        let mut missing_executable = valid;
        missing_executable.adapters[0].files[0].path = "different-file".into();
        assert!(validate_remote_manifest(&missing_executable).is_err());
    }

    #[tokio::test]
    async fn overlapping_native_install_is_rejected_before_network_or_disk_work() {
        let _owner = INSTALL_LOCK.lock().await;
        let directory = std::env::temp_dir().join(format!("mcb-provider-overlap-{}", uuid::Uuid::new_v4()));
        let registry = ProviderRegistry::new([]).unwrap();
        let result = install_at(&directory, &registry, "codex", Arc::new(|_| {})).await;
        assert!(result.unwrap_err().contains("already running"));
        assert!(!directory.exists());
    }

    #[test]
    fn abandoned_download_cleanup_requires_exclusive_directory_ownership() {
        let root = std::env::temp_dir().join(format!("mcb-provider-crash-{}", uuid::Uuid::new_v4()));
        std::fs::create_dir_all(&root).unwrap();
        let active_name = format!("install-{}", uuid::Uuid::new_v4());
        let active = root.join(&active_name);
        std::fs::create_dir(&active).unwrap();
        std::fs::write(active.join("adapter"), b"keep active").unwrap();
        std::fs::write(root.join("active.json"), serde_json::to_vec(&ActiveAdapterDirectory {
            directory: active_name,
        }).unwrap()).unwrap();
        let unrelated = root.join("install-owner-notes");
        std::fs::create_dir(&unrelated).unwrap();
        let owner = prepare_installation(&root).unwrap();
        let abandoned = root.join(format!("install-{}", uuid::Uuid::new_v4()));
        std::fs::create_dir(&abandoned).unwrap();
        std::fs::write(abandoned.join("partial"), b"incomplete").unwrap();
        assert!(prepare_installation(&root).unwrap_err().contains("busy"));
        assert!(abandoned.exists());
        drop(owner);
        let recovered_owner = prepare_installation(&root).unwrap();
        assert!(!abandoned.exists());
        assert_eq!(std::fs::read(active.join("adapter")).unwrap(), b"keep active");
        assert!(unrelated.exists());
        drop(recovered_owner);
        std::fs::remove_dir_all(root).unwrap();
    }

    #[tokio::test]
    async fn cancelled_installation_removes_only_its_owned_files() {
        let directory = std::env::temp_dir().join(format!("mcb-provider-abort-{}", uuid::Uuid::new_v4()));
        let active = directory.join("installed");
        let staging = directory.join("install-interrupted");
        std::fs::create_dir_all(&active).unwrap();
        std::fs::create_dir(&staging).unwrap();
        std::fs::write(active.join("adapter"), b"usable installation").unwrap();
        std::fs::write(directory.join("active.json"), b"original pointer").unwrap();
        let owned = staging.clone();
        let (ready, started) = tokio::sync::oneshot::channel();
        let task = tokio::spawn(async move {
            let _pending = PendingInstallation(Some(owned.clone()));
            std::fs::write(owned.join("partial-download"), b"partial").unwrap();
            ready.send(()).unwrap();
            std::future::pending::<()>().await;
        });
        started.await.unwrap();
        task.abort();
        assert!(task.await.unwrap_err().is_cancelled());
        assert!(!staging.exists());
        assert_eq!(std::fs::read(active.join("adapter")).unwrap(), b"usable installation");
        assert_eq!(std::fs::read(directory.join("active.json")).unwrap(), b"original pointer");
        std::fs::remove_dir_all(directory).unwrap();
    }

    #[tokio::test]
    async fn payload_download_accepts_valid_bytes_and_rejects_corruption_or_truncation() {
        use tokio::io::AsyncReadExt;
        let directory = std::env::temp_dir().join(format!("mcb-provider-http-{}", uuid::Uuid::new_v4()));
        std::fs::create_dir_all(&directory).unwrap();
        let payload = b"verified provider payload";
        let hash = format!("{:x}", Sha256::digest(payload));
        let client = Client::builder().no_proxy().timeout(Duration::from_secs(3)).build().unwrap();
        let adapter = PackagedAdapter {
            provider: "claude".into(), id: "claude-agent-acp".into(), version: "1.0.0".into(),
            executable: "adapter".into(), files: vec![packaged::PackagedFile { path: "adapter".into(), sha256: hash.clone() }],
        };
        let observed = Arc::new(std::sync::Mutex::new(Vec::<ProviderDownloadProgress>::new()));
        let progress: ProgressSink = {
            let observed = observed.clone();
            Arc::new(move |update| observed.lock().unwrap().push(update))
        };
        for (name, expected_hash, extra_length) in [
            ("valid", hash.clone(), 0),
            ("corrupt", "0".repeat(64), 0),
            ("truncated", hash.clone(), 10),
        ] {
            let listener = tokio::net::TcpListener::bind("127.0.0.1:0").await.unwrap();
            let address = listener.local_addr().unwrap();
            let server = tokio::spawn(async move {
                let (mut socket, _) = listener.accept().await.unwrap();
                let mut request = [0_u8; 2048];
                socket.read(&mut request).await.unwrap();
                let header = format!("HTTP/1.1 200 OK\r\nContent-Length: {}\r\nConnection: close\r\n\r\n", payload.len() + extra_length);
                socket.write_all(header.as_bytes()).await.unwrap();
                socket.write_all(payload).await.unwrap();
                socket.shutdown().await.unwrap();
            });
            let result = download_file(&client, &format!("http://{address}/adapter"), &directory.join(name), &expected_hash, &adapter, 0, &progress).await;
            server.await.unwrap();
            if name == "valid" {
                result.unwrap();
                assert_eq!(std::fs::read(directory.join(name)).unwrap(), payload);
                let final_progress = observed.lock().unwrap().last().unwrap().clone();
                assert_eq!(final_progress.downloaded, payload.len() as u64);
                assert_eq!(final_progress.total, Some(payload.len() as u64));
                assert_eq!(final_progress.provider, "claude");
            } else if name == "corrupt" {
                assert!(result.unwrap_err().contains("SHA-256"));
            } else {
                assert!(result.unwrap_err().contains("Could not read provider adapter"));
            }
        }
        std::fs::remove_dir_all(directory).unwrap();
    }

    #[tokio::test]
    async fn failed_install_removes_staging_directory() {
        let directory = std::env::temp_dir().join(format!(
            "mcb-provider-update-cleanup-{}",
            uuid::Uuid::new_v4()
        ));
        std::fs::create_dir_all(&directory).unwrap();
        let manifest = RemoteManifest {
            schema_version: 1,
            target: release_target().unwrap(),
            adapters: Vec::new(),
        };
        let progress: ProgressSink = Arc::new(|_| {});

        assert!(install_manifest(&directory, &manifest, "codex", &progress)
            .await
            .is_err());
        let provider_root = directory.join("provider-adapters");
        assert!(std::fs::read_dir(provider_root).unwrap().all(|entry| !entry
            .unwrap()
            .file_name()
            .to_string_lossy()
            .starts_with("install-")));

        std::fs::remove_dir_all(directory).unwrap();
    }
}
