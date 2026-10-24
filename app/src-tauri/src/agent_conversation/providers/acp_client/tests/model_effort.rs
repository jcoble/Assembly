use super::*;

#[tokio::test(flavor = "current_thread")]
async fn claude_model_and_effort_catalogues_contain_only_advertised_choices() {
    let root = fixture_root();
    let log = root.join("catalogue.jsonl");
    let mut client = AcpClient::spawn(
        &fixture_manifest_named(&log, "config_options"),
        &root,
        "catalogue",
    )
    .unwrap();
    client
        .initialize(AgentConversationProvider::Claude)
        .await
        .unwrap();
    let started = client.new_session(&root, vec![]).await.unwrap();
    let configured = client
        .set_conversation_config_on(
            &started.native_session_id,
            &AgentConversationConfigUpdate {
                model: Some("sonnet".into()),
                ..Default::default()
            },
        )
        .await
        .unwrap();
    client.close().await.unwrap();
    for config in [started.config, configured] {
        assert_eq!(
            config.available_models,
            [
                "default",
                "opus[1m]",
                "claude-fable-5[1m]",
                "sonnet",
                "haiku"
            ]
        );
        assert_eq!(
            config.available_efforts,
            ["default", "low", "medium", "high", "xhigh", "max"]
        );
    }
    std::fs::remove_dir_all(root).unwrap();
}
