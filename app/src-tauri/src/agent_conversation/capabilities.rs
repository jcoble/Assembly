use std::collections::HashSet;
use std::path::Path;

use serde_json::Value;

use super::protocol::{
    AgentCapabilities, AgentConfigOption, AgentProviderManifest, ProviderTransport,
};

pub const CODEX_ACP_VERSION: &str = "1.13.1";
pub const CLAUDE_AGENT_ACP_VERSION: &str = "0.88.0";
pub const AGY_ACP_VERSION: &str = "1.2.1";

pub fn validate_manifest(manifest: &AgentProviderManifest) -> Result<(), String> {
    required(&manifest.id, "Provider id")?;
    required(&manifest.display_name, "Provider display name")?;
    required(&manifest.version, "Provider version")?;
    required(&manifest.content_hash, "Provider content hash")?;
    if manifest.content_hash.len() != 64
        || !manifest
            .content_hash
            .bytes()
            .all(|byte| byte.is_ascii_hexdigit())
    {
        return Err("Provider content hash must be a SHA-256 hex digest".to_string());
    }
    if manifest.version.eq_ignore_ascii_case("latest")
        || manifest.args.iter().any(|arg| arg.contains("@latest"))
        || manifest.executable.to_string_lossy().contains("npx")
    {
        return Err("Provider manifests must use a pinned packaged adapter".to_string());
    }
    if manifest.transport != ProviderTransport::BuiltIn {
        validate_executable(&manifest.executable)?;
    }
    let _ = manifest.trusted_source;
    Ok(())
}

pub fn validate_capabilities(capabilities: &AgentCapabilities) -> Result<(), String> {
    if capabilities.revision == 0 {
        return Err("Capability revision must be positive".to_string());
    }
    required(
        &capabilities.implementation.name,
        "Provider implementation name",
    )?;
    required(
        &capabilities.implementation.version,
        "Provider implementation version",
    )?;
    validate_config_options(&capabilities.config_options)
}

// Checkout changes detach and resume the same native ID with a new cwd.
// Claude ACP 0.90.0: dist/acp-agent.js:7625 recreates with cwd +
// resume ID; dist/resumed-session.js:37 searches transcripts across projects.
// Official Antigravity ACP 1.4.1: acp_server/server.py:3781 restores
// the ID's trajectory and supplies the requested cwd; session_store.py:52 keys
// trajectories by ID, not cwd. All three adapters therefore use negotiated resume.
pub fn provider_supports_changing_checkout(capabilities: &AgentCapabilities) -> bool {
    capabilities.session.resume
}

pub fn validate_config_options(options: &[AgentConfigOption]) -> Result<(), String> {
    let mut ids = HashSet::new();
    for option in options {
        let id = required(&option.id, "Config option id")?;
        required(&option.label, "Config option label")?;
        required(&option.category, "Config option category")?;
        if !ids.insert(id) {
            return Err("Config option ids must be unique".to_string());
        }
        if let Some(choices) = &option.choices {
            if choices.is_empty() {
                return Err(format!("Config option {} has no choices", option.id));
            }
            if !choices.iter().any(|choice| choice.value == option.value) {
                return Err(format!(
                    "Config option {} has an invalid selected value",
                    option.id
                ));
            }
        }
    }
    Ok(())
}

pub fn replace_config_options(
    capabilities: &mut AgentCapabilities,
    replacement: Vec<AgentConfigOption>,
) -> Result<(), String> {
    validate_config_options(&replacement)?;
    capabilities.config_options = replacement;
    capabilities.revision = capabilities.revision.saturating_add(1);
    Ok(())
}

pub fn validate_config_value(option: &AgentConfigOption, value: &Value) -> Result<(), String> {
    if let Some(choices) = &option.choices {
        if !choices.iter().any(|choice| &choice.value == value) {
            return Err(format!("Unsupported value for config option {}", option.id));
        }
    }
    Ok(())
}

fn validate_executable(path: &Path) -> Result<(), String> {
    if path.as_os_str().is_empty() {
        return Err("Provider executable is required".to_string());
    }
    if !path.is_absolute() {
        return Err("Provider executable must use an absolute packaged path".to_string());
    }
    if !path.is_file() {
        return Err("Provider executable does not exist".to_string());
    }
    Ok(())
}

fn required(value: &str, label: &str) -> Result<String, String> {
    let value = value.trim();
    if value.is_empty() {
        Err(format!("{label} is required"))
    } else {
        Ok(value.to_string())
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::agent_conversation::protocol::{
        AgentConversationProvider, AgentImplementation, AgentInteractionCapabilities,
        AgentPromptCapabilities, AgentSessionCapabilities, ProviderSource, ProviderTransport,
    };

    fn capabilities() -> AgentCapabilities {
        AgentCapabilities {
            revision: 1,
            provider: AgentConversationProvider::Codex,
            implementation: AgentImplementation {
                name: "fake".into(),
                version: "1".into(),
            },
            session: AgentSessionCapabilities {
                multi_session: false,
                list: true,
                load: true,
                resume: true,
                close: true,
                steering: true,
                fork: false,
            },
            prompt: AgentPromptCapabilities {
                text: true,
                image: true,
                embedded_context: false,
                resource_links: false,
            },
            interaction: AgentInteractionCapabilities {
                permissions: true,
                structured_user_input: true,
                tool_terminals: true,
                plans: false,
                tasks: false,
                subagents: false,
            },
            config_options: Vec::new(),
            commands: Vec::new(),
        }
    }

    #[test]
    fn config_replacement_is_full_state_and_increments_revision() {
        let mut value = capabilities();
        value.config_options.push(AgentConfigOption {
            id: "old".into(),
            label: "Old".into(),
            category: "mode".into(),
            description: None,
            value: Value::String("a".into()),
            choices: None,
            provider_metadata: None,
        });
        replace_config_options(
            &mut value,
            vec![AgentConfigOption {
                id: "new".into(),
                label: "New".into(),
                category: "model".into(),
                description: None,
                value: Value::String("b".into()),
                choices: None,
                provider_metadata: None,
            }],
        )
        .unwrap();
        assert_eq!(value.revision, 2);
        assert_eq!(value.config_options.len(), 1);
        assert_eq!(value.config_options[0].id, "new");
    }

    #[test]
    fn manifest_validation_rejects_latest_and_unrecorded_hashes() {
        let manifest = AgentProviderManifest {
            id: "codex-acp".into(),
            display_name: "Codex".into(),
            transport: ProviderTransport::AcpStdio,
            executable: "/bin/sh".into(),
            args: vec!["npx @agentclientprotocol/codex-acp@latest".into()],
            version: "latest".into(),
            content_hash: "0000000000000000000000000000000000000000000000000000000000000000".into(),
            trusted_source: ProviderSource::Bundled,
        };
        assert!(validate_manifest(&manifest).unwrap_err().contains("pinned"));
        let mut missing_hash = manifest;
        missing_hash.args.clear();
        missing_hash.version = CODEX_ACP_VERSION.into();
        missing_hash.content_hash = "unverified".into();
        assert!(validate_manifest(&missing_hash)
            .unwrap_err()
            .contains("SHA-256"));
    }

    fn effort_option(value: &str, choices: &[&str]) -> AgentConfigOption {
        AgentConfigOption {
            id: "effort".into(),
            label: "Effort".into(),
            category: "thought_level".into(),
            description: None,
            value: Value::String(value.into()),
            choices: Some(choices.iter().map(|choice| crate::agent_conversation::protocol::AgentConfigOptionChoice {
                value: Value::String((*choice).into()),
                label: (*choice).into(),
                description: None,
            }).collect()),
            provider_metadata: None,
        }
    }

    // Protects 325d7805c: an adapter's model/effort catalogue is accepted only when
    // it is coherent, and a bad one never replaces the controls the composer shows.
    #[test]
    fn incoherent_config_catalogue_is_rejected_and_keeps_the_previous_controls() {
        let mut value = capabilities();
        replace_config_options(&mut value, vec![effort_option("high", &["low", "high"])]).unwrap();
        assert_eq!(value.revision, 2);
        for (case, replacement) in [
            ("selected value is not offered", vec![effort_option("max", &["low", "high"])]),
            ("no choices at all", vec![effort_option("high", &[])]),
            ("the same id twice", vec![effort_option("low", &["low"]), effort_option("high", &["high"])]),
        ] {
            assert!(replace_config_options(&mut value, replacement).is_err(), "{case}");
            assert_eq!(value.revision, 2, "{case}: revision is unchanged");
            assert_eq!(value.config_options, vec![effort_option("high", &["low", "high"])], "{case}");
        }
        let mut no_revision = capabilities();
        no_revision.revision = 0;
        assert!(validate_capabilities(&no_revision).is_err());
    }

    // Protects 325d7805c: a requested model or effort must be one the adapter offered.
    #[test]
    fn config_change_must_name_an_advertised_choice() {
        let option = effort_option("high", &["low", "high"]);
        assert!(validate_config_value(&option, &Value::String("low".into())).is_ok());
        assert!(validate_config_value(&option, &Value::String("extreme".into())).unwrap_err().contains("effort"));
        let free_text = AgentConfigOption { choices: None, ..option };
        assert!(validate_config_value(&free_text, &Value::String("anything".into())).is_ok());
    }

    #[test]
    fn checkout_support_follows_negotiated_resume_for_each_provider() {
        for provider in [AgentConversationProvider::Codex, AgentConversationProvider::Claude, AgentConversationProvider::Antigravity] {
            let mut capabilities = capabilities();
            capabilities.provider = provider;
            capabilities.session.resume = false;
            assert!(!provider_supports_changing_checkout(&capabilities), "load alone: {provider:?}");
            capabilities.session.resume = true;
            assert!(provider_supports_changing_checkout(&capabilities), "{provider:?}");
        }
    }
}
