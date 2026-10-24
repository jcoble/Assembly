use serde::Serialize;
use std::collections::HashSet;
use std::process::Stdio;
use std::sync::{Mutex, OnceLock};
use std::time::Duration;

use crate::agent_conversation::remote::{install::ssh_output, RemoteConnectionManager};

static PENDING_CLI_UPDATES: OnceLock<Mutex<HashSet<(String, String)>>> = OnceLock::new();

#[derive(Debug)]
struct PendingCliUpdate {
    key: (String, String),
}

impl PendingCliUpdate {
    fn claim(machine: &str, provider: &str) -> Result<Self, String> {
        let key = (machine.to_string(), provider.to_string());
        let mut pending = PENDING_CLI_UPDATES
            .get_or_init(|| Mutex::new(HashSet::new()))
            .lock()
            .map_err(|_| "Provider CLI update lock failed".to_string())?;
        if !pending.insert(key.clone()) {
            return Err(format!(
                "An update for {provider} on {machine} is already running."
            ));
        }
        Ok(Self { key })
    }
}

impl Drop for PendingCliUpdate {
    fn drop(&mut self) {
        if let Ok(mut pending) = PENDING_CLI_UPDATES.get().unwrap().lock() {
            pending.remove(&self.key);
        }
    }
}

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct CliStatus {
    provider: String,
    version: Option<String>,
    error: Option<String>,
}

// Antigravity ships agy_acp_server.par + localharness_external with its adapter, not a separate CLI (scripts/prepareReleaseAdapters.ts:119–121,170–179).
fn cli_command(provider: &str, update: bool) -> Result<String, String> {
    match provider {
        "codex" | "claude" => Ok(format!(
            "{provider} {}",
            if update { "update" } else { "--version" }
        )),
        _ => Err("Only Codex and Claude have separately installed provider CLIs".into()),
    }
}

fn saved_target(
    remote: &RemoteConnectionManager,
    profile_id: Option<String>,
) -> Result<Option<String>, String> {
    profile_id
        .map(|id| {
            remote
                .environment()
                .profiles
                .into_iter()
                .find(|profile| profile.id == id)
                .map(|profile| profile.ssh_target)
                .ok_or_else(|| "This saved remote machine no longer exists".to_string())
        })
        .transpose()
}

async fn run_cli(target: Option<&str>, provider: &str, update: bool) -> Result<String, String> {
    let command = cli_command(provider, update)?;
    let operation = async {
        if let Some(target) = target {
            // Only the fixed allowlisted command is passed to the login shell.
            return ssh_output(target, &format!("bash -lic '{command}'")).await;
        }
        let shell = if cfg!(target_os = "macos") {
            "/bin/zsh"
        } else {
            "/bin/bash"
        };
        let output = tokio::process::Command::new(shell)
            .args(["-lic", &command])
            .stdin(Stdio::null())
            .kill_on_drop(true)
            .output()
            .await
            .map_err(|error| format!("Could not start {provider}: {error}"))?;
        if !output.status.success() {
            return Err(format!(
                "{provider} command failed: {}{}",
                String::from_utf8_lossy(&output.stdout),
                String::from_utf8_lossy(&output.stderr)
            ));
        }
        Ok(String::from_utf8_lossy(&output.stdout).trim().to_string())
    };
    tokio::time::timeout(
        Duration::from_secs(if update { 600 } else { 20 }),
        operation,
    )
    .await
    .map_err(|_| {
        format!(
            "{provider} {} timed out",
            if update { "update" } else { "version check" }
        )
    })?
}

fn checked_version(value: String) -> Result<String, String> {
    if value.trim().is_empty() {
        Err("CLI did not report its installed version".into())
    } else {
        Ok(value)
    }
}

async fn cli_status(target: Option<&str>, provider: &str) -> CliStatus {
    let result = run_cli(target, provider, false)
        .await
        .and_then(checked_version);
    match result {
        Ok(version) => CliStatus {
            provider: provider.into(),
            version: Some(version),
            error: None,
        },
        Err(error) => CliStatus {
            provider: provider.into(),
            version: None,
            error: Some(error),
        },
    }
}

#[tauri::command]
pub async fn check_provider_clis(
    remote: tauri::State<'_, RemoteConnectionManager>,
    profile_id: Option<String>,
) -> Result<Vec<CliStatus>, String> {
    let target = saved_target(&remote, profile_id)?;
    // Same separately installed CLI contract as cli_command; Antigravity updates with its bundle.
    let (codex, claude) = tokio::join!(
        cli_status(target.as_deref(), "codex"),
        cli_status(target.as_deref(), "claude")
    );
    Ok(vec![codex, claude])
}

#[tauri::command]
pub async fn update_provider_cli(
    remote: tauri::State<'_, RemoteConnectionManager>,
    profile_id: Option<String>,
    provider: String,
) -> Result<CliStatus, String> {
    let target = saved_target(&remote, profile_id)?;
    cli_command(&provider, true)?;
    let machine = target.as_deref().unwrap_or("This Mac");
    let _pending = PendingCliUpdate::claim(machine, &provider)?;
    run_cli(target.as_deref(), &provider, true).await?;
    let status = cli_status(target.as_deref(), &provider).await;
    if let Some(error) = &status.error {
        return Err(format!(
            "Updater finished, but version could not be verified: {error}"
        ));
    }
    Ok(status)
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn commands_are_fixed_and_reject_other_providers() {
        assert_eq!(cli_command("codex", true).unwrap(), "codex update");
        assert_eq!(cli_command("claude", false).unwrap(), "claude --version");
        for invalid in ["antigravity", "codex; touch /tmp/injected", "", "CODEx"] {
            assert!(cli_command(invalid, true).is_err());
        }
    }
    #[test]
    fn empty_output_is_not_a_verified_version() {
        assert!(checked_version("  ".into()).is_err());
        assert_eq!(
            checked_version("codex-cli 0.161.0".into()).unwrap(),
            "codex-cli 0.161.0"
        );
    }
    #[test]
    fn duplicate_update_for_the_same_machine_and_provider_is_rejected() {
        let _first = PendingCliUpdate::claim("agent-workbox", "codex").unwrap();
        assert_eq!(
            PendingCliUpdate::claim("agent-workbox", "codex").unwrap_err(),
            "An update for codex on agent-workbox is already running."
        );
    }
    // Protects ecd21d542: a finished (or failed) update frees its slot, and an
    // update on one machine never blocks the same provider on another.
    #[test]
    fn finished_update_frees_its_slot_and_other_machines_are_independent() {
        let first = PendingCliUpdate::claim("pin-test-machine-a", "claude").unwrap();
        let other_machine = PendingCliUpdate::claim("pin-test-machine-b", "claude").unwrap();
        drop(first);
        let _again = PendingCliUpdate::claim("pin-test-machine-a", "claude").unwrap();
        assert!(PendingCliUpdate::claim("pin-test-machine-b", "claude").is_err());
        drop(other_machine);
    }
    #[tokio::test]
    async fn invalid_update_never_starts_a_shell() {
        assert!(run_cli(None, "claude && false", true)
            .await
            .unwrap_err()
            .contains("Only Codex"));
    }
}
