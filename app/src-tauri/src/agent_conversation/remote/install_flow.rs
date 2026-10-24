//! The "Install and connect" command, and the one install failure a development app keeps the machine for.

use super::super::manager::AgentRuntimeManager;
use super::super::protocol::CommandError;
use super::{RemoteAssemblyProfile, RemoteConnectionManager, RemoteConnectionResult, REMOTE_ASSEMBLY_PROFILES_SETTING_KEY};

/// A development build may speak a newer protocol than the latest published backend. Its machine is
/// still saved so "Deploy from this checkout" can reach it. This matches the error `install::validate_record`
/// returns for that case; its test checks the real message, so a wording change there fails here.
pub(super) fn keep_dev_install_profile(error: &str, development: bool) -> bool {
    development
        && error.starts_with("The latest published backend (")
        && error.contains(" uses protocol ")
        && error.contains(", but this Assembly requires ")
}

#[tauri::command]
pub async fn install_remote_assembly(
    manager: tauri::State<'_, AgentRuntimeManager>, remote: tauri::State<'_, RemoteConnectionManager>,
    profile: RemoteAssemblyProfile, operation_id: String, status: tauri::ipc::Channel<String>,
) -> Result<RemoteConnectionResult, CommandError> {
    let result = remote.install_and_connect_profile(profile, operation_id, status).await;
    if result.as_ref().is_err_and(|error| !keep_dev_install_profile(error, cfg!(debug_assertions))) {
        return result.map_err(CommandError::from);
    }
    let profiles_json = serde_json::to_string(&remote.environment().profiles)
        .map_err(|error| CommandError::from(error.to_string()))?;
    manager.write_app_setting(REMOTE_ASSEMBLY_PROFILES_SETTING_KEY, &profiles_json)
        .map_err(CommandError::from)?;
    result.map_err(CommandError::from)
}

#[cfg(test)]
mod tests {
    use super::keep_dev_install_profile;

    #[test]
    fn dev_install_keeps_only_older_published_protocol_failures() {
        let older = "The latest published backend (v0.1.6) uses protocol 20, but this Assembly requires 21. A matching backend release has not been published yet.";
        assert!(keep_dev_install_profile(older, true));
        assert!(!keep_dev_install_profile(older, false));
        for error in [
            "SSH authentication failed",
            "Installation cancelled",
            "The latest backend (v0.1.6) requires protocol 21, but this Assembly uses 20.",
        ] {
            assert!(!keep_dev_install_profile(error, true), "{error}");
        }
    }
}
