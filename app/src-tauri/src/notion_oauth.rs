use sha2::{Digest, Sha256};

use serde::{Deserialize, Serialize};
use serde_json::json;
use uuid::Uuid;

use crate::agent_conversation::manager::AgentRuntimeManager;
use crate::notion_tasks::{save_oauth_connection, NotionTaskSettings};

const OAUTH_SERVICE_URL: &str = "https://auth.coblesolutions.com";

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
struct StartResponse {
    authorization_url: String,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
struct ClaimResponse {
    access_token: Option<String>,
}

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct NotionOAuthAttempt {
    authorization_url: String,
    state: String,
    verifier: String,
}

fn random_identifier() -> String {
    format!("{}{}", Uuid::new_v4().simple(), Uuid::new_v4().simple())
}

async fn error_message(response: reqwest::Response, fallback: &str) -> String {
    response
        .json::<serde_json::Value>()
        .await
        .ok()
        .and_then(|payload| {
            payload
                .get("error")
                .and_then(|value| value.as_str())
                .map(str::to_string)
        })
        .unwrap_or_else(|| fallback.to_string())
}

#[tauri::command]
pub async fn begin_notion_oauth() -> Result<NotionOAuthAttempt, String> {
    let state = random_identifier();
    let verifier = random_identifier();
    let verifier_hash = format!("{:x}", Sha256::digest(verifier.as_bytes()));
    let response = reqwest::Client::new()
        .post(format!("{OAUTH_SERVICE_URL}/notion/start"))
        .json(&json!({ "state": state, "verifierHash": verifier_hash }))
        .send()
        .await
        .map_err(|error| format!("Could not start Notion sign-in: {error}"))?;
    if !response.status().is_success() {
        return Err(error_message(response, "Could not start Notion sign-in").await);
    }
    let payload = response
        .json::<StartResponse>()
        .await
        .map_err(|error| format!("Notion sign-in returned invalid data: {error}"))?;
    Ok(NotionOAuthAttempt {
        authorization_url: payload.authorization_url,
        state,
        verifier,
    })
}

#[tauri::command]
pub async fn claim_notion_oauth(
    manager: tauri::State<'_, AgentRuntimeManager>,
    state: String,
    verifier: String,
    claim_code: String,
) -> Result<NotionTaskSettings, String> {
    // The claim code reaches the app only through the deep link the Notion
    // callback page opens, so a flow started elsewhere cannot be claimed.
    let response = reqwest::Client::new()
        .post(format!("{OAUTH_SERVICE_URL}/notion/claim"))
        .json(&json!({ "state": state, "verifier": verifier, "claimCode": claim_code }))
        .send()
        .await
        .map_err(|error| format!("Could not finish Notion sign-in: {error}"))?;
    if !response.status().is_success() {
        return Err(error_message(response, "Could not finish Notion sign-in").await);
    }
    let payload = response
        .json::<ClaimResponse>()
        .await
        .map_err(|error| format!("Notion sign-in returned invalid data: {error}"))?;
    let token = payload
        .access_token
        .filter(|token| !token.trim().is_empty())
        .ok_or_else(|| "Notion sign-in returned no access token".to_string())?;
    save_oauth_connection(&manager, token).await
}
