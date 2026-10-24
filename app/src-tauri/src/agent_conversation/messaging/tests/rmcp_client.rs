use super::*;
use rmcp::{model::{CallToolRequestParams, ProtocolVersion}, transport::{streamable_http_client::StreamableHttpClientTransportConfig, StreamableHttpClientTransport}, ClientLifecycleMode, ClientServiceExt};

#[tokio::test]
async fn an_mcp_sdk_client_initializes_lists_the_tools_and_sends_a_message() {
    let endpoint = MessagingEndpoint::start().unwrap();
    let store = store();
    store.set_main_orchestrator(Some("owned-a")).unwrap();
    let a = endpoint.issue("owned-a");
    endpoint.bind_store(&a.token, &store, None);
    let _ = rustls::crypto::ring::default_provider().install_default();
    let config = StreamableHttpClientTransportConfig::with_uri(a.url.as_str()).auth_header(a.token.clone());
    // The 2026-07-28 lifecycle: server/discover, then self-contained requests.
    let lifecycle = ClientLifecycleMode::Discover { preferred_versions: vec![ProtocolVersion::LATEST] };
    let client = ().serve_with_lifecycle(StreamableHttpClientTransport::from_config(config), lifecycle).await.unwrap();
    let server = client.peer_info().unwrap();
    assert_eq!(server.protocol_version, ProtocolVersion::LATEST);
    assert_eq!(server.server_info.as_ref().unwrap().name, "assembly");
    let tools = client.list_all_tools().await.unwrap();
    assert_eq!(tools.len(), 13);
    let send = tools.iter().find(|tool| tool.name == "send_message").unwrap();
    assert_eq!(send.input_schema["required"], json!(["to", "text"]));
    // An optional argument is left out; a null is read as a value and refused or misread.
    for tool in &tools {
        for (name, property) in tool.input_schema["properties"].as_object().unwrap() {
            let allows_null = |key: &str| property.get(key).is_some_and(|value| value.to_string().contains("null"));
            assert!(!allows_null("type") && !allows_null("enum"), "{} {name}: {property}", tool.name);
        }
    }
    let arguments = json!({"to":"b","text":"hello"}).as_object().unwrap().clone();
    let result = client.call_tool(CallToolRequestParams::new("send_message").with_arguments(arguments)).await.unwrap();
    let receipt: Value = serde_json::from_str(&result.content[0].as_text().unwrap().text).unwrap();
    let message = store.pending_for("owned-b").unwrap().unwrap();
    assert_eq!(receipt, json!({"id":message.id,"status":"queued"}));
    assert_eq!(message.from_agent, "owned-a");
    client.cancel().await.unwrap();
}
