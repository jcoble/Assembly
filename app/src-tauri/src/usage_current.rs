use crate::usage_sources::read_latest_local_quota;
use crate::{claude_quota, usage_remote};
use serde::{Deserialize, Serialize};

#[derive(Debug, Clone, Copy, Deserialize, Serialize, PartialEq, Eq)]
#[serde(rename_all = "lowercase")]
pub enum ProviderUsageState {
    Available,
    Unavailable,
    Error,
}

#[derive(Debug, Clone, Deserialize, Serialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct ProviderUsageWindow {
    pub label: String,
    pub used_percent: f64,
    pub resets_at: Option<String>,
    pub window_minutes: Option<u64>,
}

#[derive(Debug, Clone, Deserialize, Serialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct ProviderUsageSnapshot {
    pub provider: String,
    pub account: Option<String>,
    pub instance_id: String,
    pub state: ProviderUsageState,
    pub windows: Vec<ProviderUsageWindow>,
    pub captured_at: u128,
    pub source: Option<String>,
    pub source_version: Option<String>,
    pub unavailable_reason: Option<String>,
}

pub trait ProviderUsageReader: Send + Sync {
    fn read(&self, provider: &str, instance_id: &str) -> ProviderUsageSnapshot;
}

#[tauri::command]
pub async fn read_current_provider_usage(
    provider: Option<String>,
    instance_id: Option<String>,
) -> Result<ProviderUsageSnapshot, String> {
    let provider = provider.unwrap_or_else(|| "unknown".to_string());
    let instance_id = instance_id.unwrap_or_else(|| "unknown".to_string());
    // Claude quota is authenticated /api/oauth/usage, not transcript tokens; see usage_remote::tests::usage_remote_401_refresh_persists_rotation_and_retries_once.
    if provider.eq_ignore_ascii_case("claude") {
        let snapshot = match usage_remote::read_claude_quota().await {
            Ok(quota) => ProviderUsageSnapshot {
                provider: "claude".to_string(),
                account: quota.account,
                instance_id,
                state: ProviderUsageState::Available,
                windows: quota
                    .windows
                    .into_iter()
                    .map(|window| ProviderUsageWindow {
                        label: window.label,
                        used_percent: window.used_percent,
                        resets_at: window.resets_at,
                        window_minutes: window.window_minutes,
                    })
                    .collect(),
                captured_at: quota.captured_at,
                source: Some(claude_quota::USAGE_SOURCE.to_string()),
                source_version: Some(claude_quota::USAGE_SOURCE_VERSION.to_string()),
                unavailable_reason: None,
            },
            Err(error) if error.is_unavailable() => {
                unavailable_snapshot(&provider, &instance_id, &error.reason())
            }
            Err(error) => error_snapshot(&provider, &instance_id, &error.reason()),
        };
        return Ok(normalize_provider_usage(snapshot));
    }
    let snapshot = read_latest_local_quota(&provider)
        .map(|quota| ProviderUsageSnapshot {
            provider: quota.provider,
            account: quota.account,
            instance_id: instance_id.clone(),
            state: ProviderUsageState::Available,
            windows: quota
                .windows
                .into_iter()
                .map(|window| ProviderUsageWindow {
                    label: window.name,
                    used_percent: window.percent_consumed,
                    resets_at: window.reset_at,
                    window_minutes: window
                        .semantics
                        .split_whitespace()
                        .find_map(|part| part.strip_suffix("-minute"))
                        .and_then(|minutes| minutes.parse::<u64>().ok()),
                })
                .collect(),
            captured_at: quota.captured_at,
            source: Some("local provider rate-limit records".to_string()),
            source_version: Some("local-v1".to_string()),
            unavailable_reason: None,
        })
        .unwrap_or_else(|| UnavailableProviderUsageReader.read(&provider, &instance_id));
    Ok(normalize_provider_usage(snapshot))
}

fn error_snapshot(provider: &str, instance_id: &str, reason: &str) -> ProviderUsageSnapshot {
    ProviderUsageSnapshot {
        provider: provider.trim().to_string(),
        account: None,
        instance_id: instance_id.trim().to_string(),
        state: ProviderUsageState::Error,
        windows: Vec::new(),
        captured_at: now_ms(),
        source: Some(claude_quota::USAGE_SOURCE.to_string()),
        source_version: Some(claude_quota::USAGE_SOURCE_VERSION.to_string()),
        unavailable_reason: Some(reason.to_string()),
    }
}

#[derive(Debug, Default, Clone, Copy)]
pub struct UnavailableProviderUsageReader;

impl ProviderUsageReader for UnavailableProviderUsageReader {
    fn read(&self, provider: &str, instance_id: &str) -> ProviderUsageSnapshot {
        unavailable_snapshot(
            provider,
            instance_id,
            "The provider has not advertised quota data",
        )
    }
}

pub fn unavailable_snapshot(
    provider: &str,
    instance_id: &str,
    reason: &str,
) -> ProviderUsageSnapshot {
    ProviderUsageSnapshot {
        provider: provider.trim().to_string(),
        account: None,
        instance_id: instance_id.trim().to_string(),
        state: ProviderUsageState::Unavailable,
        windows: Vec::new(),
        captured_at: now_ms(),
        source: None,
        source_version: None,
        unavailable_reason: Some(reason.to_string()),
    }
}

/// Accept only a provider-authored quota snapshot. Transcript token counts are
/// deliberately not an input to this function.
pub fn normalize_provider_usage(mut snapshot: ProviderUsageSnapshot) -> ProviderUsageSnapshot {
    if snapshot.provider.trim().is_empty() || snapshot.instance_id.trim().is_empty() {
        snapshot.state = ProviderUsageState::Unavailable;
        snapshot.windows.clear();
        snapshot.unavailable_reason = Some("Provider identity is incomplete".to_string());
        return snapshot;
    }
    if snapshot.state == ProviderUsageState::Available && snapshot.windows.is_empty() {
        snapshot.state = ProviderUsageState::Unavailable;
        snapshot.unavailable_reason = Some("The provider returned no quota windows".to_string());
    }
    for window in &mut snapshot.windows {
        window.used_percent = window.used_percent.clamp(0.0, 100.0);
        if window.label.trim().is_empty() {
            window.label = "Provider window".to_string();
        }
    }
    snapshot
}

fn now_ms() -> u128 {
    std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map(|duration| duration.as_millis())
        .unwrap_or_default()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn usage_current_never_infers_quota_from_tokens() {
        let snapshot = normalize_provider_usage(ProviderUsageSnapshot {
            provider: "provider-a".into(),
            account: None,
            instance_id: "instance-a".into(),
            state: ProviderUsageState::Unavailable,
            windows: Vec::new(),
            captured_at: 1,
            source: None,
            source_version: None,
            unavailable_reason: Some("No quota data".into()),
        });
        assert_eq!(snapshot.state, ProviderUsageState::Unavailable);
        assert!(snapshot.windows.is_empty());
    }

    fn window(label: &str, used_percent: f64) -> ProviderUsageWindow {
        ProviderUsageWindow { label: label.into(), used_percent, resets_at: None, window_minutes: Some(300) }
    }

    fn available(provider: &str, instance_id: &str, windows: Vec<ProviderUsageWindow>) -> ProviderUsageSnapshot {
        ProviderUsageSnapshot {
            provider: provider.into(),
            account: None,
            instance_id: instance_id.into(),
            state: ProviderUsageState::Available,
            windows,
            captured_at: 1,
            source: Some("provider".into()),
            source_version: None,
            unavailable_reason: None,
        }
    }

    // Protects e794abc4c: the quota meter shows 0-100% with a name for every
    // window, and never claims "available" without a window or a provider.
    #[test]
    fn provider_quota_is_clamped_named_and_never_available_without_windows() {
        let snapshot = normalize_provider_usage(available(
            "provider-a",
            "instance-a",
            vec![window("5h", 140.0), window("  ", -3.0)],
        ));
        assert_eq!(snapshot.state, ProviderUsageState::Available);
        assert_eq!(snapshot.windows, vec![window("5h", 100.0), window("Provider window", 0.0)]);

        let empty = normalize_provider_usage(available("provider-a", "instance-a", Vec::new()));
        assert_eq!(empty.state, ProviderUsageState::Unavailable);
        assert_eq!(empty.unavailable_reason.as_deref(), Some("The provider returned no quota windows"));

        let anonymous = normalize_provider_usage(available(" ", "instance-a", vec![window("5h", 10.0)]));
        assert_eq!(anonymous.state, ProviderUsageState::Unavailable);
        assert!(anonymous.windows.is_empty());
    }
}
