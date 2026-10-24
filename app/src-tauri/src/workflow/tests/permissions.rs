use super::*;

#[tokio::test(flavor = "multi_thread")]
async fn non_claude_worker_applies_the_effort_advertised_at_session_creation() {
    for (fixture, model, wanted, effort_id) in [
        ("config_options", None, "high", "effort"),
        ("codex_catalog", Some("gpt-b"), "medium", "reasoning_effort"),
        ("config_options", Some("sonnet"), "xhigh", "effort"),
    ] {
        let root =
            std::env::temp_dir().join(format!("tsk1508-worker-effort-{}", std::process::id()));
        std::fs::create_dir_all(&root).unwrap();
        let log = root.join("adapter.jsonl");
        let mut manifest =
            crate::agent_conversation::providers::acp_client::tests::fixture_manifest_named(
                &log, fixture,
            );
        // Keep the existing adapter fixture, but let its effort setter acknowledge the requested value.
        // Switching models still resets effort to high, including when the initial value was xhigh.
        if fixture == "config_options" {
            let script = &mut manifest.args[1];
            let at = script
                .find("*'\"method\":\"session/set_config_option\"'*)")
                .unwrap();
            let (before, after) = script.split_at(at);
            let after = after.replacen(
            "elif [ \"$fixture\" = \"config_options\" ]; then",
            r#"elif [ "$fixture" = "config_options" ]; then
        reported_effort=high
        case "$line" in *'"configId":"effort"'*) reported_effort=$(printf '%s\n' "$line" | sed -n 's/.*"value":"\([^" ]*\)".*/\1/p') ;; esac"#, 1,
        ).replacen(r#""currentValue":"high""#, r#""currentValue":"'"$reported_effort"'""#, 1);
            *script = format!("{before}{after}");
        }
        let providers = crate::agent_conversation::providers::ProviderRegistry::new([(
            AgentConversationProvider::Antigravity,
            manifest,
        )])
        .unwrap();
        let manager = AgentRuntimeManager::new(providers);
        let result = manager
            .dispatch(AgentDispatchRequest {
                owned_id: "effort-worker".into(),
                provider: "antigravity".into(),
                cwd: root.to_string_lossy().into(),
                title: "Effort fixture".into(),
                prompt: "Review it".into(),
                role_id: "review".into(),
                model: model.map(str::to_string),
                effort: Some(wanted.into()),
                permission: None,
                starter: None,
                policy: Default::default(),
            })
            .await;
        let config = manager.conversation_config("effort-worker").unwrap();
        let frames: Vec<serde_json::Value> = std::fs::read_to_string(&log)
            .unwrap()
            .lines()
            .filter_map(|line| serde_json::from_str(line).ok())
            .collect();
        manager.shutdown().await;
        std::fs::remove_dir_all(root).unwrap();
        assert!(
            result.is_ok(),
            "an advertised effort must work for any provider: {result:?}"
        );
        assert_eq!(config.reasoning_effort.as_deref(), Some(wanted));
        let effort = frames.iter().position(|frame| {
            frame["method"] == "session/set_config_option"
                && frame["params"]["configId"] == effort_id
                && frame["params"]["value"] == wanted
        });
        assert!(
            effort.is_some(),
            "the advertised effort control was not used: {frames:?}"
        );
        let prompt = frames
            .iter()
            .position(|frame| frame["method"] == "session/prompt");
        assert!(
            prompt.is_none() || effort < prompt,
            "effort was applied after the prompt"
        );
        if let Some(model) = model {
            assert_eq!(config.model.as_deref(), Some(model));
            let selected_model = frames.iter().position(|frame| {
                frame["method"] == "session/set_config_option"
                    && frame["params"]["configId"] == "model"
                    && frame["params"]["value"] == model
            });
            assert!(
                selected_model.is_some() && selected_model < effort,
                "the requested model must be applied before its effort: {frames:?}"
            );
        }
    }
}

#[test]
fn a_worker_with_no_step_permission_starts_in_auto_under_a_starter_in_default() {
    #[allow(non_snake_case)] let (Claude, Codex) = (AgentConversationProvider::Claude, AgentConversationProvider::Codex);
    assert_eq!(capped_permission(None, Some(Some("default")), Claude).as_deref(), Some("auto"));
    assert_eq!(capped_permission(None, Some(Some("read-only")), Codex).as_deref(), Some("agent"));
    // A step asking for full access under a lower starter falls back to Auto, never below it.
    assert_eq!(capped_permission(Some("bypassPermissions".into()), Some(Some("default")), Claude).as_deref(), Some("auto"));
}

#[test]
fn yaml_agent_permission_reaches_the_role() {
    let (root, _) = yaml_fixture("agent-permission");
    let path = Path::new(".assembly/workflows/demo.yml");
    std::fs::write(root.join(path), "name: Permissions\nsteps:\n  explicit:\n    agent: { provider: claude, permission: bypassPermissions }\n    prompt: prompts/build.md\n  omitted:\n    needs: [explicit]\n    agent: { provider: claude }\n    prompt: prompts/build.md\n").unwrap();
    let definition = WorkflowEngine::validate_file(&root, path).unwrap();
    let role = |id: &str| definition.roles.iter().find(|role| role.id == id).unwrap();
    assert_eq!(role("explicit").permission_policy.value.as_deref(), Some("bypassPermissions"));
    assert_eq!(role("omitted").permission_policy.value, None);
    std::fs::remove_dir_all(root).unwrap();
}

#[test]
fn yaml_policy_block_reaches_the_definition() {
    let (root, _) = yaml_fixture("guard-policy");
    let path = Path::new(".assembly/workflows/demo.yml");
    std::fs::write(root.join(path), "name: Policy\npolicy:\n  commands: { allow: [pnpm test], deny: [\"curl *\"] }\n  default: deny\nsteps:\n  build:\n    agent: { provider: claude }\n    prompt: prompts/build.md\n").unwrap();
    let definition = WorkflowEngine::validate_file(&root, path).unwrap();
    assert_eq!(definition.policy.commands.allow, vec!["pnpm test".to_string()]);
    assert_eq!(definition.policy.commands.deny, vec!["curl *".to_string()]);
    assert_eq!(definition.policy.default, policy::Decision::Deny);
    std::fs::write(root.join(path), "name: Policy\npolicy: { commands: { alow: [ls] } }\nsteps:\n  build:\n    agent: { provider: claude }\n    prompt: prompts/build.md\n").unwrap();
    assert!(WorkflowEngine::validate_file(&root, path).is_err(), "a misspelt rule list must not be ignored");
    std::fs::remove_dir_all(root).unwrap();
}
