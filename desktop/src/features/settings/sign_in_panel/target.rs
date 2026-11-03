use crate::features::session::composer::agent_config::AgentTarget;

/// One captured advertised local provider on its stable owning Engine connection.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct SignInTarget {
    pub machine: String,
    pub provider: String,
    pub account_context: String,
    pub provider_target_id: String,
    pub configuration_target_id: String,
    pub machine_label: String,
    pub provider_name: String,
}
impl SignInTarget {
    pub fn local(machine: &str, machine_label: &str, agent: &AgentTarget) -> Option<Self> {
        let account_context = agent.account_context.as_ref()?;
        let provider_target_id = agent.provider_target_id.as_ref()?;
        let configuration_target_id = agent.target_id.as_ref()?;
        if machine.is_empty()
            || agent.id.is_empty()
            || configuration_target_id.is_empty()
            || *provider_target_id != format!("{}/{}", agent.id, account_context)
        {
            return None;
        }
        Some(Self {
            machine: machine.into(),
            provider: agent.id.clone(),
            account_context: account_context.clone(),
            provider_target_id: provider_target_id.clone(),
            configuration_target_id: configuration_target_id.clone(),
            machine_label: machine_label.into(),
            provider_name: agent.name.clone(),
        })
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::features::session::composer::agent_config::AgentTarget;
    use serde_json::json;
    fn agent(value: serde_json::Value) -> AgentTarget {
        serde_json::from_value(value).unwrap()
    }

    #[test]
    fn local_sign_in_keeps_engine_provider_and_configuration_targets_distinct() {
        let agent = agent(
            json!({"id":"codex","name":"Codex","targetId":"opaque-env-hash","accountContext":"","providerTargetId":"codex/"}),
        );
        let target = SignInTarget::local("stable-engine", "This Mac", &agent).unwrap();
        assert_eq!(target.machine, "stable-engine");
        assert_eq!(target.provider, "codex");
        assert_eq!(target.provider_target_id, "codex/");
        assert_eq!(target.configuration_target_id, "opaque-env-hash");
        assert_eq!(target.account_context, "");
    }

    #[test]
    fn missing_or_mismatched_discovery_identity_cannot_start_auth() {
        for value in [
            json!({"id":"codex","name":"Codex","targetId":"hash"}),
            json!({"id":"codex","name":"Codex","targetId":"hash","accountContext":"","providerTargetId":"claude/"}),
            json!({"id":"codex","name":"Codex","targetId":"","accountContext":"","providerTargetId":"codex/"}),
        ] {
            assert!(SignInTarget::local("stable-engine", "This Mac", &agent(value)).is_none());
        }
        let agent = agent(
            json!({"id":"codex","name":"Codex","targetId":"hash","accountContext":"work","providerTargetId":"codex/work"}),
        );
        assert!(SignInTarget::local("", "This Mac", &agent).is_none());
    }
}
