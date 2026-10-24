//! Signed release discovery and one-shot installation for a remote Assembly backend.

use std::io::Read;
use std::path::{Path, PathBuf};
use std::process::Stdio;

use base64::Engine;
use minisign_verify::{PublicKey, Signature};
use serde::{Deserialize, Serialize};
use tauri::ipc::Channel;
use tokio::io::{AsyncBufReadExt, BufReader};

const RELEASES_ENDPOINT: &str = "https://api.github.com/repos/jcoble/mac-command-bar/releases?per_page=30";
const RELEASE_TAG_PREFIX: &str = "assembly-backend-v";
const TARGET: &str = "x86_64-unknown-linux-gnu";
const SIGNING_PUBLIC_KEY: &str = "RWQZvQJuc5RPnp9xO8+V9ppE3cCiodEFHPYqJpIMMVRhAnaxo0udp8xh";
const MAX_ARCHIVE_BYTES: u64 = 512 * 1024 * 1024;
const MAX_SIGNATURE_BYTES: u64 = 16 * 1024;
const REMOTE_DOWNLOAD: &str = r#"
import hashlib, json, os, sys, tarfile, time, urllib.request
url, archive_path, total_text, trusted_hash, expected_version = sys.argv[1:]
total = int(total_text)
expected_files = {'manifest.json', 'SHA256SUMS', 'install.sh', 'payload/assembly-remote-server'}
request = urllib.request.Request(url, headers={'User-Agent': 'Assembly-backend-installer'})
with urllib.request.urlopen(request, timeout=30) as response:
    if not response.geturl().startswith('https://'):
        raise ValueError('Backend release redirected outside HTTPS')
    declared = response.headers.get('Content-Length')
    if declared and int(declared) != total:
        raise ValueError('Backend release size differs from signed record')
    digest = hashlib.sha256()
    written, last_report = 0, time.monotonic()
    with open(archive_path, 'xb') as output:
        os.chmod(archive_path, 0o600)
        while chunk := response.read(65536):
            written += len(chunk)
            if written > total:
                raise ValueError('Backend release exceeds signed size')
            digest.update(chunk)
            output.write(chunk)
            now = time.monotonic()
            if now - last_report >= .25:
                print(f'P\t{written}', flush=True)
                last_report = now
        output.flush()
    print(f'P\t{written}', flush=True)
if written != total or digest.hexdigest() != trusted_hash:
    raise ValueError('Backend release is truncated or differs from signed digest')
stage = os.path.dirname(archive_path)
found = set()
with tarfile.open(archive_path, 'r:gz') as archive:
    for member in archive:
        name = member.name.removeprefix('./').rstrip('/')
        if member.isdir() and name in ('', '.', 'payload', 'payload/adapters'):
            continue
        if not member.isfile() or name not in expected_files or name in found:
            raise ValueError(f'Unexpected backend archive entry: {name}')
        found.add(name)
        destination = os.path.join(stage, name)
        os.makedirs(os.path.dirname(destination), exist_ok=True)
        source = archive.extractfile(member)
        if source is None:
            raise ValueError(f'Cannot read backend archive entry: {name}')
        with open(destination, 'xb') as output:
            while chunk := source.read(65536):
                output.write(chunk)
    if found != expected_files:
        raise ValueError('Backend archive is missing required entries')
with open(os.path.join(stage, 'manifest.json'), encoding='utf-8') as source:
    manifest = json.load(source)
if (manifest.get('schemaVersion') != 1 or manifest.get('packageType') != 'assembly-remote-backend'
        or manifest.get('target') != 'x86_64-unknown-linux-gnu'
        or manifest.get('version') != expected_version):
    raise ValueError('Backend manifest does not match signed release')
files = manifest.get('files')
if not isinstance(files, list) or len(files) != len(expected_files) - 2:
    raise ValueError('Backend manifest file list is invalid')
listed = set()
for item in files:
    if not isinstance(item, dict) or item.get('path') not in expected_files - {'manifest.json', 'SHA256SUMS'}:
        raise ValueError('Backend manifest file list is invalid')
    path, expected = item['path'], item.get('sha256')
    if path in listed or not isinstance(expected, str) or len(expected) != 64:
        raise ValueError('Backend manifest checksum is invalid')
    listed.add(path)
    actual = hashlib.sha256()
    with open(os.path.join(stage, path), 'rb') as source:
        while chunk := source.read(65536):
            actual.update(chunk)
    if actual.hexdigest() != expected:
        raise ValueError(f'Backend payload checksum failed: {path}')
if listed != expected_files - {'manifest.json', 'SHA256SUMS'}:
    raise ValueError('Backend manifest is missing payloads')
commit = manifest.get('commit', '')
if not isinstance(commit, str) or len(commit) < 12 or len(commit) > 40 or any(c not in '0123456789abcdef' for c in commit):
    raise ValueError('Backend manifest commit is invalid')
server_hash = next(item['sha256'] for item in files if item['path'] == 'payload/assembly-remote-server')
print(f'V\t{expected_version}\t{commit}\t{server_hash}', flush=True)
"#;

#[derive(Debug, Deserialize)]
struct GithubRelease {
    tag_name: String,
    draft: bool,
    prerelease: bool,
    assets: Vec<GithubAsset>,
}

#[derive(Debug, Deserialize, Clone)]
struct GithubAsset {
    name: String,
    size: u64,
    browser_download_url: String,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
struct DownloadRecord {
    schema_version: u32,
    version: String,
    protocol_version: u16,
    target: String,
    archive_file: String,
    bytes: u64,
    sha256: String,
}

pub struct InstallReceipt {
    pub version: String,
    pub commit: String,
}

#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct BackendStatus {
    pub installed: bool,
    pub installed_version: Option<String>,
    pub backend_build_kind: BackendBuildKind,
    pub backend_commit: Option<String>,
    pub latest_version: String,
    pub update_available: Option<bool>,
}

#[derive(Clone, Copy, Debug, Serialize, PartialEq, Eq)]
#[serde(rename_all = "lowercase")]
pub enum BackendBuildKind {
    Release,
    Development,
}

struct LocalStage(PathBuf);

impl LocalStage {
    fn create() -> Result<Self, String> {
        let path =
            std::env::temp_dir().join(format!("assembly-remote-install-{}", uuid::Uuid::new_v4()));
        std::fs::create_dir(&path)
            .map_err(|error| format!("Could not create local install staging: {error}"))?;
        Ok(Self(path))
    }
}

impl Drop for LocalStage {
    fn drop(&mut self) {
        let _ = std::fs::remove_dir_all(&self.0);
    }
}

struct RemoteStage {
    target: String,
    path: String,
    armed: bool,
}

impl RemoteStage {
    fn new(target: &str, path: String) -> Self {
        Self {
            target: target.to_string(),
            path,
            armed: true,
        }
    }

    async fn cleanup(&mut self) -> Result<(), String> {
        ssh_output(&self.target, &remote_cleanup_command(&self.path))
        .await?;
        self.armed = false;
        Ok(())
    }
}

impl Drop for RemoteStage {
    fn drop(&mut self) {
        if !self.armed {
            return;
        }
        let target = self.target.clone();
        let command = remote_cleanup_command(&self.path);
        std::thread::spawn(move || {
            let _ = std::process::Command::new("ssh")
                .args([
                    "-o",
                    "BatchMode=yes",
                    "-o",
                    "ConnectTimeout=10",
                    "-o",
                    "ControlMaster=no",
                    "-o",
                    "ControlPath=none",
                    "--",
                    &target,
                    &command,
                ])
                .stdin(Stdio::null())
                .stdout(Stdio::null())
                .stderr(Stdio::null())
                .status();
        });
    }
}

fn remote_cleanup_command(path: &str) -> String {
    let stage = shell_quote(path);
    let archive = shell_quote(&format!("{path}/package.tar.gz"));
    format!("stage={stage}; if test -r \"$stage/download.pid\"; then pid=$(cat \"$stage/download.pid\"); case \"$pid\" in ''|*[!0-9]*) ;; *) if test -r \"/proc/$pid/cmdline\" && grep -aFq -- {archive} \"/proc/$pid/cmdline\"; then kill -KILL \"$pid\" 2>/dev/null || true; fi;; esac; fi; find \"$stage\" -depth -delete 2>/dev/null || true")
}

pub async fn install_latest(
    ssh_target: &str,
    status: Channel<String>,
) -> Result<InstallReceipt, String> {
    super::validate_ssh_target(ssh_target)?;
    let _ = status.send("Finding the latest signed backend…".into());
    let (archive_asset, record) = verified_latest_release().await?;

    let _ = status.send("Checking the remote operating system and download tools…".into());
    let platform = ssh_output(ssh_target, "printf '%s %s' \"$(uname -s)\" \"$(uname -m)\"").await?;
    if platform != "Linux x86_64" {
        return Err(format!("Unsupported remote platform: {platform}"));
    }
    ssh_output(ssh_target, "set -eu; for tool in python3 sha256sum systemctl openssl install ss awk grep; do command -v \"$tool\" >/dev/null || { echo \"Missing remote install tool: $tool\" >&2; exit 1; }; done; python3 -c 'import ssl, tarfile, urllib.request, sys; sys.exit(0 if sys.version_info >= (3, 9) else 1)' || { echo 'Remote install requires Python 3.9 or newer with SSL support' >&2; exit 1; }").await?;
    let home = remote_home(ssh_target).await?;
    let staging_path = format!("{home}/.cache/.assembly-install-{}", uuid::Uuid::new_v4());
    let mut remote_stage = RemoteStage::new(ssh_target, staging_path.clone());
    let quoted_stage = shell_quote(&staging_path);
    ssh_output(ssh_target, &format!("install -d -m 700 {quoted_stage}")).await?;
    let result: Result<InspectedPackage, String> = async {
        let _ = status.send("Downloading backend on the remote machine…".into());
        let inspected = receive_remote_package(ssh_target, &staging_path, &archive_asset, &record, &status).await?;
        let _ = status.send("Installing and starting the backend…".into());
        ssh_output(ssh_target, &format!("set -eu; cd {quoted_stage}; sh install.sh")).await?;
        let _ = status.send("Verifying the service, database, and loopback listener…".into());
        let receipt = ssh_output(ssh_target, "set -eu; systemctl --user is-active --quiet assembly-remote.service; test -f \"$HOME/.local/share/assembly/sessions.db\"; listeners=$(ss -ltnH 'sport = :7777'); test -n \"$listeners\"; printf '%s\\n' \"$listeners\" | awk '$4 != \"127.0.0.1:7777\" { exit 1 } END { if (NR == 0) exit 1 }'; sha256sum \"$HOME/.local/bin/assembly-remote-server\" | awk '{print $1}'").await?;
        if receipt != inspected.server_sha256 {
            return Err("Installed backend checksum does not match the signed package".into());
        }
        Ok(inspected)
    }.await;
    let cleanup = remote_stage.cleanup().await;
    let inspected = result?;
    cleanup?;
    Ok(InstallReceipt { version: inspected.version, commit: inspected.commit })
}

pub async fn latest_version() -> Result<String, String> {
    let (_, record) = verified_latest_release().await?;
    Ok(record.version)
}

pub async fn read_status(
    ssh_target: &str,
    latest_version: &str,
) -> Result<BackendStatus, String> {
    super::validate_ssh_target(ssh_target)?;
    let home = remote_home(ssh_target).await?;
    let binary = shell_quote(&format!("{home}/.local/bin/assembly-remote-server"));
    let manifest = shell_quote(&format!(
        "{home}/.local/share/assembly/backend-manifest.json"
    ));
    let output = ssh_output(
        ssh_target,
        &format!(
            "if test -x {binary}; then printf 'installed\\n'; if test -f {manifest}; then sed -n 's/.*\"version\":[[:space:]]*\"\\([^\"]*\\)\".*/version=\\1/p' {manifest} | head -n 1; sed -n 's/.*\"buildKind\":[[:space:]]*\"\\([^\"]*\\)\".*/buildKind=\\1/p' {manifest} | head -n 1; sed -n 's/.*\"commit\":[[:space:]]*\"\\([^\"]*\\)\".*/commit=\\1/p' {manifest} | head -n 1; fi; else printf 'not-installed\\n'; fi"
        ),
    )
    .await?;
    parse_status_output(&output, latest_version)
}

fn parse_status_output(output: &str, latest_version: &str) -> Result<BackendStatus, String> {
    let mut lines = output.lines();
    let installed = lines.next() == Some("installed");
    let fields = lines
        .filter_map(|line| line.split_once('='))
        .collect::<std::collections::HashMap<_, _>>();
    let installed_version = fields
        .get("version")
        .map(|value| (*value).to_string())
        .filter(|value| !value.is_empty());
    let backend_build_kind = if fields.get("buildKind") == Some(&"development") {
        BackendBuildKind::Development
    } else {
        BackendBuildKind::Release
    };
    let backend_commit = fields
        .get("commit")
        .map(|value| (*value).to_string())
        .filter(|value| !value.is_empty());
    let latest = semver::Version::parse(latest_version)
        .map_err(|_| format!("Invalid latest backend version: {latest_version}"))?;
    let update_available = installed_version.as_deref().and_then(|version| {
        semver::Version::parse(version)
            .ok()
            .and_then(|installed| (installed <= latest).then_some(installed < latest))
    });
    Ok(BackendStatus {
        installed,
        installed_version,
        backend_build_kind,
        backend_commit,
        latest_version: latest_version.to_string(),
        update_available,
    })
}

pub async fn uninstall(ssh_target: &str, delete_data: bool) -> Result<(), String> {
    super::validate_ssh_target(ssh_target)?;
    let home = remote_home(ssh_target).await?;
    let service = shell_quote(&format!(
        "{home}/.config/systemd/user/assembly-remote.service"
    ));
    let binary = shell_quote(&format!("{home}/.local/bin/assembly-remote-server"));
    let adapters = shell_quote(&format!("{home}/.local/bin/assembly-adapters"));
    let manifest = shell_quote(&format!(
        "{home}/.local/share/assembly/backend-manifest.json"
    ));
    let mut command = format!(
        "set -eu; systemctl --user disable --now assembly-remote.service >/dev/null 2>&1 || true; rm -f {service} {binary} {manifest}; rm -rf {adapters}; systemctl --user daemon-reload"
    );
    if delete_data {
        let config = shell_quote(&format!("{home}/.config/assembly"));
        let data = shell_quote(&format!("{home}/.local/share/assembly"));
        command.push_str(&format!("; rm -rf {config} {data}"));
    }
    command.push_str(&format!(
        "; test ! -e {service}; test ! -e {binary}; test ! -e {adapters}; test -z \"$(ss -ltnH 'sport = :7777')\""
    ));
    tokio::time::timeout(
        std::time::Duration::from_secs(30),
        ssh_output(ssh_target, &command),
    )
    .await
    .map_err(|_| "Remote backend uninstall timed out after 30 seconds".to_string())??;
    Ok(())
}

// Status and installation must accept exactly the same signed, compatible release.
async fn verified_latest_release() -> Result<(GithubAsset, DownloadRecord), String> {
    let (archive_asset, record_asset, signature_asset) = release_assets(latest_release().await?)?;
    let local_stage = LocalStage::create()?;
    let record_path = local_stage.0.join(&record_asset.name);
    let signature_path = local_stage.0.join(&signature_asset.name);
    tokio::fs::write(&record_path, download_small(&record_asset).await?)
        .await.map_err(|error| format!("Could not stage backend record: {error}"))?;
    tokio::fs::write(&signature_path, download_small(&signature_asset).await?)
        .await.map_err(|error| format!("Could not stage backend signature: {error}"))?;
    verify_signature(&record_path, &signature_path)?;
    let record: DownloadRecord = serde_json::from_slice(&std::fs::read(&record_path)
        .map_err(|error| format!("Could not read signed backend record: {error}"))?)
        .map_err(|error| format!("Signed backend record is invalid: {error}"))?;
    validate_record(&record, &archive_asset, &release_version_from_archive(&archive_asset.name)?)?;
    validate_public_url(&archive_asset.browser_download_url)?;

    Ok((archive_asset, record))
}

async fn latest_release() -> Result<GithubRelease, String> {
    let releases = reqwest::Client::new().get(RELEASES_ENDPOINT)
        .header(reqwest::header::USER_AGENT, "Assembly-backend-installer")
        .send().await.map_err(|error| format!("Could not check public backend releases: {error}"))?
        .error_for_status().map_err(|error| format!("Public backend release lookup failed: {error}"))?
        .json::<Vec<GithubRelease>>().await
        .map_err(|error| format!("Backend release metadata is invalid: {error}"))?;
    releases.into_iter()
        .find(|release| !release.draft && !release.prerelease && release.tag_name.starts_with(RELEASE_TAG_PREFIX))
        .ok_or_else(|| "No published Assembly backend release is available".to_string())
}

fn release_version(tag_name: &str) -> Result<String, String> {
    let version = tag_name.strip_prefix(RELEASE_TAG_PREFIX)
        .ok_or_else(|| format!("Invalid Assembly backend release tag: {tag_name}"))?;
    semver::Version::parse(version)
        .map_err(|_| format!("Invalid Assembly backend release tag: {tag_name}"))?;
    Ok(version.to_string())
}

fn release_version_from_archive(name: &str) -> Result<String, String> {
    let version = name.strip_prefix("assembly-remote-backend-")
        .and_then(|value| value.strip_suffix(&format!("-{TARGET}.tar.gz")))
        .ok_or_else(|| "Backend archive name is invalid".to_string())?;
    semver::Version::parse(version).map_err(|_| "Backend archive version is invalid".to_string())?;
    Ok(version.to_string())
}

fn release_assets(release: GithubRelease) -> Result<(GithubAsset, GithubAsset, GithubAsset), String> {
    let version = release_version(&release.tag_name)?;
    let archive_name = format!("assembly-remote-backend-{version}-{TARGET}.tar.gz");
    let find = |name: &str| release.assets.iter().find(|asset| asset.name == name).cloned();
    let archive = find(&archive_name)
        .ok_or_else(|| format!("{} has no Linux x86-64 backend package", release.tag_name))?;
    let record = find(&format!("{archive_name}.download.json"))
        .ok_or_else(|| format!("{} lacks the signed download record; publish a compatible backend release", release.tag_name))?;
    let signature = find(&format!("{archive_name}.download.json.sig"))
        .ok_or_else(|| format!("{} lacks the signed download record signature", release.tag_name))?;
    Ok((archive, record, signature))
}

fn validate_public_url(value: &str) -> Result<(), String> {
    let url = reqwest::Url::parse(value).map_err(|_| "Backend asset URL is invalid".to_string())?;
    if url.scheme() != "https" || url.host_str() != Some("github.com")
        || !url.path().starts_with("/jcoble/mac-command-bar/releases/download/") {
        return Err("Backend asset URL is not a public HTTPS release URL".into());
    }
    Ok(())
}

async fn download_small(asset: &GithubAsset) -> Result<Vec<u8>, String> {
    if asset.size == 0 || asset.size > MAX_SIGNATURE_BYTES {
        return Err(format!("Backend release record asset has an invalid size: {}", asset.name));
    }
    validate_public_url(&asset.browser_download_url)?;
    let mut response = reqwest::Client::new().get(&asset.browser_download_url)
        .header(reqwest::header::USER_AGENT, "Assembly-backend-installer")
        .send().await.map_err(|error| format!("Could not download {}: {error}", asset.name))?
        .error_for_status().map_err(|error| format!("Backend release asset is unavailable: {error}"))?;
    let mut bytes = Vec::new();
    while let Some(chunk) = response.chunk().await
        .map_err(|error| format!("Could not read {}: {error}", asset.name))? {
        if bytes.len() + chunk.len() > MAX_SIGNATURE_BYTES as usize {
            return Err(format!("Backend release asset exceeded size limit: {}", asset.name));
        }
        bytes.extend_from_slice(&chunk);
    }
    if bytes.len() as u64 != asset.size {
        return Err(format!("Backend release asset was incomplete: {}", asset.name));
    }
    Ok(bytes)
}

fn validate_record(record: &DownloadRecord, archive: &GithubAsset, version: &str) -> Result<(), String> {
    if record.schema_version != 1 || record.version != version || record.target != TARGET
        || record.archive_file != archive.name || record.bytes != archive.size
        || record.bytes == 0 || record.bytes > MAX_ARCHIVE_BYTES
        || record.sha256.len() != 64
        || !record.sha256.bytes().all(|byte| byte.is_ascii_digit() || (b'a'..=b'f').contains(&byte)) {
        return Err("Signed backend download record does not match this release".into());
    }
    let client_protocol = super::PROTOCOL_VERSION;
    if record.protocol_version < client_protocol {
        return Err(format!("The latest published backend ({}) uses protocol {}, but this Assembly requires {}. A matching backend release has not been published yet. Check for backend updates after the release build finishes; reinstalling the current release will not help.", record.version, record.protocol_version, client_protocol));
    }
    if record.protocol_version > client_protocol {
        return Err(format!("The latest backend ({}) requires protocol {}, but this Assembly uses {}. Update or rebuild Assembly from latest main before installing this backend.", record.version, record.protocol_version, client_protocol));
    }
    Ok(())
}

async fn receive_remote_package(
    target: &str,
    stage: &str,
    archive: &GithubAsset,
    record: &DownloadRecord,
    status: &Channel<String>,
) -> Result<InspectedPackage, String> {
    let archive_path = format!("{stage}/package.tar.gz");
    let command = format!(
        "set -eu; cd {}; python3 -u -c {} {} {} {} {} {} & child=$!; printf '%s\\n' \"$child\" > download.pid; wait \"$child\"; rm -f download.pid",
        shell_quote(stage), shell_quote(REMOTE_DOWNLOAD), shell_quote(&archive.browser_download_url),
        shell_quote(&archive_path), record.bytes, shell_quote(&record.sha256), shell_quote(&record.version)
    );
    let mut child = tokio::process::Command::new("ssh")
        .args(["-o", "BatchMode=yes", "-o", "ConnectTimeout=10", "-o", "ControlMaster=no", "-o", "ControlPath=none", "--", target, &command])
        .stdin(Stdio::null()).stdout(Stdio::piped()).stderr(Stdio::piped())
        .kill_on_drop(true).spawn()
        .map_err(|error| format!("Remote backend download could not start: {error}"))?;
    let stdout = child.stdout.take().ok_or("Remote backend download has no output")?;
    let mut lines = BufReader::new(stdout).lines();
    let start = std::time::Instant::now();
    let mut inspected = None;
    while let Some(line) = lines.next_line().await
        .map_err(|error| format!("Remote backend download output failed: {error}"))? {
        if let Some(bytes) = line.strip_prefix("P\t") {
            let bytes = bytes.parse::<u64>().map_err(|_| "Invalid backend download progress")?;
            if bytes > record.bytes { return Err("Backend download progress exceeds signed size".into()); }
            let elapsed = start.elapsed().as_secs_f64();
            let rate = if elapsed >= 1.0 && bytes > 0 { Some(bytes as f64 / elapsed) } else { None };
            let eta = rate.map(|value| ((record.bytes - bytes) as f64 / value).ceil() as u64);
            let _ = status.send(serde_json::json!({
                "kind": "transfer", "stage": "download", "bytes": bytes, "total": record.bytes,
                "bytesPerSecond": rate, "etaSeconds": eta
            }).to_string());
        } else if let Some(value) = line.strip_prefix("V\t") {
            let parts = value.split('\t').collect::<Vec<_>>();
            if parts.len() != 3 || parts[0] != record.version || parts[1].len() < 12
                || parts[2].len() != 64 || !parts[2].bytes().all(|byte| byte.is_ascii_hexdigit()) {
                return Err("Remote backend verification receipt is invalid".into());
            }
            inspected = Some(InspectedPackage {
                version: parts[0].to_string(), commit: parts[1].to_string(), server_sha256: parts[2].to_string()
            });
        } else {
            return Err(format!("Unexpected remote backend download output: {line}"));
        }
    }
    let output = child.wait_with_output().await
        .map_err(|error| format!("Remote backend download failed: {error}"))?;
    if !output.status.success() {
        let error = String::from_utf8_lossy(&output.stderr);
        return Err(format!("Remote backend download or verification failed: {}", error.trim()));
    }
    inspected.ok_or_else(|| "Remote backend download finished without verification".into())
}

#[derive(Debug)]
struct InspectedPackage {
    version: String,
    commit: String,
    server_sha256: String,
}

fn verify_signature(record_path: &Path, signature_path: &Path) -> Result<(), String> {
    let encoded = std::fs::read_to_string(signature_path)
        .map_err(|error| format!("Could not read backend signature: {error}"))?;
    let decoded = base64::engine::general_purpose::STANDARD
        .decode(encoded.trim())
        .map_err(|error| format!("Backend signature encoding is invalid: {error}"))?;
    let signature = Signature::decode(
        std::str::from_utf8(&decoded).map_err(|_| "Backend signature is not UTF-8".to_string())?,
    )
    .map_err(|error| format!("Backend signature is invalid: {error}"))?;
    let key = PublicKey::from_base64(SIGNING_PUBLIC_KEY)
        .map_err(|error| format!("Backend signing key is invalid: {error}"))?;
    let mut verifier = key
        .verify_stream(&signature)
        .map_err(|error| format!("Could not verify backend signature: {error}"))?;
    let mut file = std::fs::File::open(record_path)
        .map_err(|error| format!("Could not open backend download record: {error}"))?;
    let mut buffer = [0_u8; 64 * 1024];
    loop {
        let read = file
            .read(&mut buffer)
            .map_err(|error| format!("Could not read backend download record: {error}"))?;
        if read == 0 {
            break;
        }
        verifier.update(&buffer[..read]);
    }
    verifier
        .finalize()
        .map_err(|error| format!("Backend download record signature verification failed: {error}"))
}

async fn remote_home(target: &str) -> Result<String, String> {
    let home = ssh_output(target, "printf '%s' \"$HOME\"").await?;
    let path = Path::new(&home);
    if !path.is_absolute() || home == "/" || home.contains('\n') || home.contains('\r') {
        return Err("Remote home directory is invalid".to_string());
    }
    Ok(home)
}

fn shell_quote(value: &str) -> String {
    format!("'{}'", value.replace('\'', "'\\''"))
}

pub(crate) async fn ssh_output(target: &str, remote_command: &str) -> Result<String, String> {
    command_output(
        tokio::process::Command::new("ssh").args([
            "-o",
            "BatchMode=yes",
            "-o",
            "ConnectTimeout=10",
            "-o",
            "ControlMaster=no",
            "-o",
            "ControlPath=none",
            "--",
            target,
            remote_command,
        ]),
        "Remote backend command",
    )
    .await
}

async fn command_output(
    command: &mut tokio::process::Command,
    label: &str,
) -> Result<String, String> {
    let output = command
        .stdin(Stdio::null())
        .kill_on_drop(true)
        .output()
        .await
        .map_err(|error| format!("{label} could not start: {error}"))?;
    if !output.status.success() {
        let message = [String::from_utf8_lossy(&output.stdout), String::from_utf8_lossy(&output.stderr)]
            .iter().map(|part| part.trim()).filter(|part| !part.is_empty()).collect::<Vec<_>>().join("\n");
        return Err(if message.is_empty() {
            format!("{label} failed")
        } else {
            format!("{label} failed: {message}")
        });
    }
    Ok(String::from_utf8_lossy(&output.stdout).trim().to_string())
}

#[cfg(test)]
mod tests {
    #[tokio::test]
    async fn failed_remote_command_retains_stdout_and_stderr() {
        let error = super::command_output(
            tokio::process::Command::new("sh").args(["-c", "printf 'upgrade failed'; printf 'check permissions' >&2; exit 1"]),
            "Remote backend command",
        ).await.unwrap_err();
        assert_eq!(error, "Remote backend command failed: upgrade failed\ncheck permissions");
    }
    use super::*;

    #[test]
    fn ssh_targets_accept_hosts_but_reject_options_and_shell_syntax() {
        assert!(super::super::validate_ssh_target("agent-workbox").is_ok());
        assert!(super::super::validate_ssh_target("person@server.example").is_ok());
        for target in [
            "",
            "-oProxyCommand=false",
            "host name",
            "host;false",
            "$(false)",
        ] {
            assert!(super::super::validate_ssh_target(target).is_err());
        }
    }

    #[test]
    fn backend_manifest_status_defaults_releases_and_identifies_development_builds() {
        let release =
            parse_status_output("installed\nversion=0.1.18\ncommit=abc123", "0.1.19").unwrap();
        assert_eq!(release.backend_build_kind, BackendBuildKind::Release);
        assert_eq!(release.backend_commit.as_deref(), Some("abc123"));
        let development = parse_status_output(
            "installed\nversion=0.1.19\nbuildKind=development\ncommit=15add6b2-dirty",
            "0.1.19",
        )
        .unwrap();
        assert_eq!(
            development.backend_build_kind,
            BackendBuildKind::Development
        );
        assert_eq!(
            development.backend_commit.as_deref(),
            Some("15add6b2-dirty")
        );
    }

    #[test]
    fn release_assets_require_a_signed_download_record() {
        let archive_name = format!("assembly-remote-backend-1.2.3-{TARGET}.tar.gz");
        let asset = |name: String| GithubAsset {
            browser_download_url: format!("https://github.com/jcoble/mac-command-bar/releases/download/assembly-backend-v1.2.3/{name}"),
            name,
            size: 1,
        };
        let release = GithubRelease {
            tag_name: "assembly-backend-v1.2.3".into(), draft: false, prerelease: false,
            assets: vec![asset(archive_name.clone()), asset(format!("{archive_name}.download.json")),
                asset(format!("{archive_name}.download.json.sig"))],
        };
        let (archive, record, signature) = release_assets(release).unwrap();
        assert_eq!(archive.name, archive_name);
        assert!(record.name.ends_with(".download.json"));
        assert!(signature.name.ends_with(".download.json.sig"));
    }

    #[test]
    fn release_version_requires_the_backend_tag_prefix_and_semver() {
        assert_eq!(release_version("assembly-backend-v1.2.3").unwrap(), "1.2.3");
        assert!(release_version("assembly-v1.2.3").is_err());
        assert!(release_version("assembly-backend-vnext").is_err());
    }

    #[test]
    fn signed_record_rejects_protocol_mismatch_before_transfer() {
        let name = format!("assembly-remote-backend-1.2.3-{TARGET}.tar.gz");
        let archive = GithubAsset { name: name.clone(), size: 12,
            browser_download_url: format!("https://github.com/jcoble/mac-command-bar/releases/download/assembly-backend-v1.2.3/{name}") };
        let mut record = DownloadRecord { schema_version: 1, version: "1.2.3".into(),
            protocol_version: super::super::PROTOCOL_VERSION, target: TARGET.into(),
            archive_file: name, bytes: 12, sha256: "0".repeat(64) };
        assert!(validate_record(&record, &archive, "1.2.3").is_ok());
        record.protocol_version += 1;
        let newer = validate_record(&record, &archive, "1.2.3").unwrap_err(); assert!(newer.contains("Update or rebuild Assembly") && !super::super::install_flow::keep_dev_install_profile(&newer, true));
        record.protocol_version -= 2;
        let older = validate_record(&record, &archive, "1.2.3").unwrap_err(); assert!(older.contains("has not been published yet") && super::super::install_flow::keep_dev_install_profile(&older, true));
        record.protocol_version += 2;
        record.protocol_version -= 1;
        record.bytes -= 1;
        assert!(validate_record(&record, &archive, "1.2.3").is_err());
    }

    #[cfg(target_os = "linux")]
    #[test]
    fn cleanup_kills_only_the_download_from_its_unique_stage() {
        let stage = LocalStage::create().unwrap();
        let unrelated = LocalStage::create().unwrap();
        let archive = stage.0.join("package.tar.gz");
        let mut owned = std::process::Command::new("python3").args(["-c", "import time; time.sleep(30)"])
            .arg(&archive).spawn().unwrap();
        let mut other = std::process::Command::new("python3").args(["-c", "import time; time.sleep(30)"])
            .arg(unrelated.0.join("package.tar.gz")).spawn().unwrap();
        std::fs::write(stage.0.join("download.pid"), owned.id().to_string()).unwrap();
        let result = std::process::Command::new("sh").arg("-c")
            .arg(remote_cleanup_command(stage.0.to_str().unwrap())).status().unwrap();
        assert!(result.success());
        assert!(!stage.0.exists());
        assert!(!owned.wait().unwrap().success());
        assert!(other.try_wait().unwrap().is_none());
        other.kill().unwrap();
        other.wait().unwrap();
    }

    #[test]
    fn shell_quote_keeps_remote_paths_as_one_argument() {
        assert_eq!(shell_quote("/home/person/app"), "'/home/person/app'");
        assert_eq!(shell_quote("/home/o'neil/app"), "'/home/o'\\''neil/app'");
    }

    #[test]
    fn signature_verification_rejects_invalid_artifacts() {
        let stage = LocalStage::create().unwrap();
        let archive_path = stage.0.join("package.download.json");
        let signature_path = stage.0.join("package.download.json.sig");
        std::fs::write(&archive_path, b"package").unwrap();
        std::fs::write(&signature_path, b"not-base64").unwrap();

        assert!(verify_signature(&archive_path, &signature_path)
            .unwrap_err()
            .contains("signature encoding is invalid"));
    }
}
