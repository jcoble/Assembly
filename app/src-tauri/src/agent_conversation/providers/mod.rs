pub mod acp;
pub mod acp_client;
pub(crate) mod authentication;
mod packaged;
pub mod process;
pub mod updates;
pub mod cli_updates;
pub mod descriptors;

pub use acp::{AcpRuntimeAdapter, StructuredRuntimeHandle};
pub use acp_client::AcpClient;

use std::path::{Path, PathBuf};
use std::sync::Arc;

#[cfg(unix)]
use std::os::unix::fs::PermissionsExt;

use serde::Serialize;
use serde_json::Value;
use sha2::{Digest, Sha256};

use super::capabilities::{
    validate_manifest, AGY_ACP_VERSION, CLAUDE_AGENT_ACP_VERSION, CODEX_ACP_VERSION,
};
use super::protocol::{
    AgentApprovalResponse, AgentCapabilities, AgentCommandDescriptor, AgentConfigOption,
    AgentConversationConfigState, AgentConversationProvider, AgentProviderManifest, ProviderSource,
    ProviderTransport,
};

#[derive(Clone, Debug)]
pub struct InitializeAgentInput {
    pub provider: AgentConversationProvider,
}

#[derive(Clone, Debug)]
pub struct NewAgentSession {
    pub cwd: PathBuf,
    pub mcp_servers: Vec<agent_client_protocol::schema::v1::McpServer>,
}

#[derive(Clone, Debug)]
pub struct LoadAgentSession {
    pub cwd: PathBuf,
    pub native_session_id: String,
    pub mcp_servers: Vec<agent_client_protocol::schema::v1::McpServer>,
}

pub type ResumeAgentSession = LoadAgentSession;

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct AgentPromptImage {
    pub data: String,
    pub mime_type: String,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct AgentPrompt {
    pub text: String,
    pub images: Vec<AgentPromptImage>,
    /// Ids of the saved screenshots the composer sent with this prompt.
    pub attachment_ids: Vec<String>,
}

pub type AgentConfigValue = Value;
pub type PermissionResponse = AgentApprovalResponse;

#[derive(Clone, Debug, PartialEq)]
pub struct StartedAgentSession {
    pub native_session_id: String,
    pub config: AgentConversationConfigState,
    pub commands: Vec<AgentCommandDescriptor>,
}

#[derive(Clone, Debug, Default, Eq, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct AgentConversationConfigUpdate {
    #[serde(skip_serializing_if = "Option::is_none")]
    pub model: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub reasoning_effort: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub approval_policy: Option<String>,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct GeneratedText {
    pub turn_id: Option<String>,
    pub text: String,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct AgentRuntimeError {
    pub code: &'static str,
    pub message: String,
}

impl AgentRuntimeError {
    pub fn new(code: &'static str, message: impl Into<String>) -> Self {
        Self {
            code,
            message: message.into(),
        }
    }
}

impl std::fmt::Display for AgentRuntimeError {
    fn fmt(&self, formatter: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(formatter, "{}: {}", self.code, self.message)
    }
}

impl std::error::Error for AgentRuntimeError {}

#[allow(async_fn_in_trait)]
pub trait AgentRuntimeAdapter: Send + Sync {
    async fn initialize(
        &mut self,
        input: InitializeAgentInput,
    ) -> Result<AgentCapabilities, AgentRuntimeError>;
    async fn new_session(
        &mut self,
        input: NewAgentSession,
    ) -> Result<StartedAgentSession, AgentRuntimeError>;
    async fn load_session(
        &mut self,
        input: LoadAgentSession,
    ) -> Result<StartedAgentSession, AgentRuntimeError>;
    async fn resume_session(
        &mut self,
        input: ResumeAgentSession,
    ) -> Result<StartedAgentSession, AgentRuntimeError>;
    /// Stop the transport without sending the destructive session/close
    /// request. The native session id remains valid for a later resume.
    async fn detach_session(&mut self) -> Result<(), AgentRuntimeError>;
    async fn close_session(&mut self) -> Result<(), AgentRuntimeError>;
}

#[derive(Clone, Default)]
pub struct ProviderRegistry {
    manifests: Arc<Vec<(AgentConversationProvider, AgentProviderManifest)>>,
}

impl ProviderRegistry {
    pub fn bundled_from_environment_at(app_data_dir: Option<&Path>) -> Result<Self, String> {
        let configured_pair = |path_name: &str, hash_name: &str| match (
            std::env::var_os(path_name),
            std::env::var(hash_name).ok(),
        ) {
            (None, None) => Ok(None),
            (Some(path), Some(hash)) => {
                let executable = PathBuf::from(path);
                let manifest_path = executable.parent().unwrap_or(Path::new(".")).join("manifest.json");
                let manifest: packaged::PackagedManifest = serde_json::from_slice(
                    &std::fs::read(&manifest_path).map_err(|error| format!("Could not read configured adapter manifest: {error}"))?
                ).map_err(|error| format!("Configured adapter manifest is invalid: {error}"))?;
                let adapter = manifest.adapters.iter().find(|adapter| {
                    executable.file_name().and_then(|name| name.to_str()) == Some(adapter.executable.as_str())
                        && adapter.files.iter().any(|file| file.path == adapter.executable && file.sha256.eq_ignore_ascii_case(&hash))
                }).ok_or_else(|| "Configured adapter does not match its manifest".to_string())?;
                Ok(Some(packaged::AdapterPackage {
                    executable,
                    content_hash: hash,
                    version: adapter.version.clone(),
                }))
            },
            _ => Err(
                "Packaged ACP adapter paths and SHA-256 values must be configured together"
                    .to_string(),
            ),
        };
        // An explicitly installed update wins even when a dev relaunch inherits
        // the bootstrap wrappers in its environment.
        let (mut codex, mut claude, mut antigravity) = (None, None, None);
        let mut installed = false;
        if let Some(app_data_dir) = app_data_dir {
            installed = app_data_dir.join("provider-adapters/active.json").is_file();
            match updates::discover_active(app_data_dir) {
                Ok(active) => (codex, claude, antigravity) = active,
                Err(error) => crate::debug_log::stderr_log!(
                    "Invalid active provider adapters; no installed adapters were registered: {error}"
                ),
            }
        }
        if !installed && codex.is_none() && claude.is_none() && antigravity.is_none() {
            codex = configured_pair("MCB_CODEX_ACP_PATH", "MCB_CODEX_ACP_SHA256")?;
            claude = configured_pair("MCB_CLAUDE_AGENT_ACP_PATH", "MCB_CLAUDE_AGENT_ACP_SHA256")?;
            antigravity = configured_pair("MCB_AGY_ACP_PATH", "MCB_AGY_ACP_SHA256")?;
        }
        if !installed && codex.is_none() && claude.is_none() && antigravity.is_none() {
            (codex, claude, antigravity) = packaged::discover()?;
        }
        if !installed && (codex.is_none() || claude.is_none() || antigravity.is_none()) {
            if let Some(home) = std::env::var_os("HOME").map(PathBuf::from) {
                if codex.is_none() {
                    codex =
                        discover_home_wrapper(&home, &["codex-acp-bridge.sh", "codex-acp-dev.sh"])?;
                }
                if claude.is_none() {
                    claude = discover_home_wrapper(&home, &["claude-acp-wrapper.sh"])?;
                }
            }
        }
        Self::initial_manifests(codex, claude, antigravity)
    }

    pub fn new(
        manifests: impl IntoIterator<Item = (AgentConversationProvider, AgentProviderManifest)>,
    ) -> Result<Self, String> {
        let mut values = Vec::new();
        for (provider, manifest) in manifests {
            validate_manifest(&manifest)?;
            if values.iter().any(|(current, _)| *current == provider) {
                return Err("Provider manifests must have one entry per provider".to_string());
            }
            values.push((provider, manifest));
        }
        Ok(Self {
            manifests: Arc::new(values),
        })
    }

    fn initial_manifests(
        codex: Option<packaged::AdapterPackage>,
        claude: Option<packaged::AdapterPackage>,
        antigravity: Option<packaged::AdapterPackage>,
    ) -> Result<Self, String> {
        let packages = [
            (AgentConversationProvider::Codex, codex, CODEX_ACP_VERSION),
            (AgentConversationProvider::Claude, claude, CLAUDE_AGENT_ACP_VERSION),
            (AgentConversationProvider::Antigravity, antigravity, AGY_ACP_VERSION),
        ];
        let manifests = packages.into_iter().filter_map(|(provider, package, bundled_version)| {
            let (package, descriptor) = (package?, provider.descriptor());
            Some((provider, AgentProviderManifest {
                id: descriptor.adapter_id.clone(),
                display_name: descriptor.display_name.clone(),
                transport: ProviderTransport::AcpStdio,
                executable: package.executable,
                args: Vec::new(),
                version: package_version(&package.version, bundled_version),
                content_hash: package.content_hash,
                trusted_source: ProviderSource::Bundled,
            }))
        });
        Self::new(manifests)
    }

    pub fn manifest(
        &self,
        provider: AgentConversationProvider,
    ) -> Result<AgentProviderManifest, String> {
        self.manifests
            .iter()
            .find(|(current, _)| *current == provider)
            .map(|(_, manifest)| manifest.clone())
            .ok_or_else(|| "No trusted adapter is registered for this provider".to_string())
    }
}

#[cfg(test)]
fn discover_home_wrappers(
    home: &Path,
) -> Result<
    (
        Option<packaged::AdapterPackage>,
        Option<packaged::AdapterPackage>,
        Option<packaged::AdapterPackage>,
    ),
    String,
> {
    let codex = discover_home_wrapper(home, &["codex-acp-bridge.sh", "codex-acp-dev.sh"])?;
    let claude = discover_home_wrapper(home, &["claude-acp-wrapper.sh"])?;
    let antigravity = None;
    Ok((codex, claude, antigravity))
}

fn package_version(version: &str, fallback: &str) -> String {
    if version.is_empty() {
        fallback.to_string()
    } else {
        version.to_string()
    }
}

fn discover_home_wrapper(
    home: &Path,
    names: &[&str],
) -> Result<Option<packaged::AdapterPackage>, String> {
    let wrapper_dir = home.join(".mac-command-bar");
    executable_wrapper(&wrapper_dir, names)
        .map(|path| {
            file_sha256(&path).map(|content_hash| packaged::AdapterPackage {
                executable: path,
                content_hash,
                version: String::new(),
            })
        })
        .transpose()
}

fn executable_wrapper(directory: &Path, names: &[&str]) -> Option<PathBuf> {
    names.iter().map(|name| directory.join(name)).find(|path| {
        path.is_file()
            && path
                .metadata()
                .map(|metadata| {
                    #[cfg(unix)]
                    {
                        metadata.permissions().mode() & 0o111 != 0
                    }
                    #[cfg(not(unix))]
                    {
                        true
                    }
                })
                .unwrap_or(false)
    })
}

fn file_sha256(path: &Path) -> Result<String, String> {
    use std::io::Read;
    let mut file = std::fs::File::open(path)
        .map_err(|error| format!("Could not read ACP adapter {}: {error}", path.display()))?;
    let mut hash = Sha256::new();
    let mut buffer = [0_u8; 64 * 1024];
    loop {
        let read = file.read(&mut buffer).map_err(|error| error.to_string())?;
        if read == 0 { break; }
        hash.update(&buffer[..read]);
    }
    Ok(format!("{:x}", hash.finalize()))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[cfg(unix)]
    fn write_executable(path: &Path, contents: &[u8], mode: u32) {
        std::fs::write(path, contents).unwrap();
        let mut permissions = std::fs::metadata(path).unwrap().permissions();
        permissions.set_mode(mode);
        std::fs::set_permissions(path, permissions).unwrap();
    }

    #[test]
    fn initial_provider_manifests_pin_both_official_adapters() {
        let package = |version: &str| packaged::AdapterPackage {
            executable: PathBuf::from("/bin/sh"),
            content_hash: "0000000000000000000000000000000000000000000000000000000000000000".into(),
            version: version.into(),
        };
        let registry = ProviderRegistry::initial_manifests(
            Some(package(CODEX_ACP_VERSION)),
            Some(package(CLAUDE_AGENT_ACP_VERSION)),
            None,
        )
        .unwrap();
        let codex = registry.manifest(AgentConversationProvider::Codex).unwrap();
        let claude = registry
            .manifest(AgentConversationProvider::Claude)
            .unwrap();
        assert_eq!(
            (codex.id.as_str(), codex.version.as_str()),
            ("codex-acp", CODEX_ACP_VERSION)
        );
        assert_eq!(
            (claude.id.as_str(), claude.version.as_str()),
            ("claude-agent-acp", CLAUDE_AGENT_ACP_VERSION)
        );
        assert!(registry
            .manifests
            .iter()
            .all(|(_, manifest)| !manifest.args.iter().any(|arg| arg.contains("@latest"))));
    }

    #[cfg(unix)]
    #[test]
    fn installed_bundle_is_selected_with_its_actual_versions() {
        let app_data = std::env::temp_dir().join(format!("mcb-installed-adapters-{}", uuid::Uuid::new_v4()));
        let root = app_data.join("provider-adapters");
        let bundle = root.join("verified-test-bundle");
        std::fs::create_dir_all(&bundle).unwrap();
        let mut adapters = Vec::new();
        for (provider, id) in [("codex", "codex-acp"), ("claude", "claude-agent-acp"), ("antigravity", "agy-acp")] {
            let executable = bundle.join(id);
            write_executable(&executable, b"#!/bin/sh\nexit 0\n", 0o755);
            adapters.push(serde_json::json!({
                "provider": provider, "id": id, "version": "99.0.0", "executable": id,
                "files": [{"path": id, "sha256": file_sha256(&executable).unwrap()}]
            }));
        }
        std::fs::write(bundle.join("manifest.json"), serde_json::to_vec(&serde_json::json!({
            "schemaVersion": 1, "adapters": adapters
        })).unwrap()).unwrap();
        std::fs::write(root.join("active.json"), br#"{"directory":"verified-test-bundle"}"#).unwrap();
        let registry = ProviderRegistry::bundled_from_environment_at(Some(&app_data)).unwrap();
        for (_, manifest) in registry.manifests.iter() {
            assert_eq!(manifest.version, "99.0.0");
        }
        assert_eq!(registry.manifests.len(), 3);
        std::fs::remove_dir_all(app_data).unwrap();
    }

    #[test]
    fn invalid_active_adapter_pointer_does_not_block_registry_startup() {
        let app_data = std::env::temp_dir().join(format!(
            "mcb-provider-active-fallback-{}",
            uuid::Uuid::new_v4()
        ));
        let adapter_root = app_data.join("provider-adapters");
        std::fs::create_dir_all(&adapter_root).unwrap();
        std::fs::write(
            adapter_root.join("active.json"),
            br#"{"directory":"../outside"}"#,
        )
        .unwrap();

        assert!(ProviderRegistry::bundled_from_environment_at(Some(&app_data)).is_ok());

        std::fs::remove_dir_all(app_data).unwrap();
    }

    #[cfg(unix)]
    #[test]
    fn home_wrapper_resolver_prefers_codex_bridge_and_hashes_executable_files() {
        let home =
            std::env::temp_dir().join(format!("mcb-provider-wrappers-{}", uuid::Uuid::new_v4()));
        let wrapper_dir = home.join(".mac-command-bar");
        std::fs::create_dir_all(&wrapper_dir).unwrap();

        let bridge = wrapper_dir.join("codex-acp-bridge.sh");
        let legacy = wrapper_dir.join("codex-acp-dev.sh");
        let claude = wrapper_dir.join("claude-acp-wrapper.sh");
        let antigravity = wrapper_dir.join("agy-acp-wrapper.sh");
        write_executable(&bridge, b"bridge", 0o755);
        write_executable(&legacy, b"legacy", 0o755);
        write_executable(&claude, b"claude", 0o755);
        write_executable(&antigravity, b"agy", 0o644);

        let (codex, resolved_claude, resolved_antigravity) = discover_home_wrappers(&home).unwrap();
        let codex = codex.unwrap();
        assert_eq!(codex.executable, bridge);
        assert_eq!(
            codex.content_hash,
            "17f29b073143d8cd97b5bbe492bdeffec1c5fee55cc1fe2112c8b9335f8b6121"
        );
        assert_eq!(resolved_claude.unwrap().executable, claude);
        assert!(resolved_antigravity.is_none());

        write_executable(&bridge, b"bridge", 0o644);
        let (codex_fallback, _, _) = discover_home_wrappers(&home).unwrap();
        assert_eq!(codex_fallback.unwrap().executable, legacy);

        std::fs::remove_dir_all(home).unwrap();
    }
}
