use std::collections::{BTreeSet, HashMap};
use std::process::{Command, Stdio};
use std::sync::{LazyLock, Mutex};
use std::time::{SystemTime, UNIX_EPOCH};

use base64::Engine;
use futures_util::future::{AbortHandle, Abortable};

use mcb_core::session_store::{NotionTaskProjection, NotionTaskProjectionPage};
use serde::{Deserialize, Serialize};
use serde_json::{json, Value};

use crate::agent_conversation::manager::AgentRuntimeManager;

const SETTINGS_KEY: &str = "notion.tasks";
const KEYCHAIN_SERVICE: &str = "Assembly Notion";
const KEYCHAIN_ACCOUNT: &str = "integration-token";
const SECURITY_ITEM_NOT_FOUND: i32 = 44;
const NOTION_VERSION: &str = "2026-03-11";
const NOTION_PAGE_SIZE: usize = 100;
const MAX_NOTION_TASKS: usize = 500;
static DETAIL_REQUESTS: LazyLock<Mutex<HashMap<u64, AbortHandle>>> =
    LazyLock::new(|| Mutex::new(HashMap::new()));

#[derive(Clone, Debug, Default, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
struct StoredSettings {
    data_source_id: String,
}

#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct NotionTaskSettings {
    data_source_id: String,
    has_token: bool,
}

#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct NotionTaskRow {
    source_task_id: String,
    title: String,
    project: String,
    status: String,
    priority: Option<String>,
    assignee: Option<String>,
    due_date: Option<String>,
    source_url: String,
    fetched_at_ms: i64,
}

#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct NotionTaskRefreshReceipt {
    count: usize,
    fetched_at_ms: i64,
}

#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct NotionTaskPage {
    tasks: Vec<NotionTaskRow>,
    projects: Vec<String>,
    statuses: Vec<String>,
    priorities: Vec<String>,
    has_more: bool,
}

#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct NotionTaskProperty {
    name: String,
    kind: String,
    values: Vec<String>,
    links: Vec<Option<String>>,
}

#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct NotionBlock {
    id: String,
    kind: String,
    markdown: String,
    checked: Option<bool>,
    icon: Option<String>,
    children: Vec<NotionBlock>,
}

#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct NotionTaskComment {
    author: String,
    created: String,
    text: String,
}

#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct NotionTaskDetail {
    properties: Vec<NotionTaskProperty>,
    body: Vec<NotionBlock>,
    comments: Vec<NotionTaskComment>,
}

struct PendingNotionTask {
    source_task_id: String,
    title: String,
    project_ids: Vec<String>,
    status: String,
    priority: Option<String>,
    assignee: Option<String>,
    due_date: Option<String>,
    source_url: String,
    fetched_at_ms: i64,
}

impl From<NotionTaskProjection> for NotionTaskRow {
    fn from(task: NotionTaskProjection) -> Self {
        Self {
            source_task_id: task.source_task_id,
            title: task.title,
            project: task.project,
            status: task.status,
            priority: task.priority,
            assignee: task.assignee,
            due_date: task.due_date,
            source_url: task.source_url,
            fetched_at_ms: task.fetched_at_ms,
        }
    }
}

impl From<NotionTaskProjectionPage> for NotionTaskPage {
    fn from(page: NotionTaskProjectionPage) -> Self {
        Self {
            tasks: page.tasks.into_iter().map(NotionTaskRow::from).collect(),
            projects: page.projects,
            statuses: page.statuses,
            priorities: page.priorities,
            has_more: page.has_more,
        }
    }
}

fn read_stored_settings(manager: &AgentRuntimeManager) -> Result<StoredSettings, String> {
    let Some(value) = manager.read_app_setting(SETTINGS_KEY)? else {
        return Ok(StoredSettings::default());
    };
    serde_json::from_str(&value).map_err(|_| "Notion task settings are invalid".to_string())
}

fn read_keychain_token() -> Result<Option<String>, String> {
    let output = Command::new("/usr/bin/security")
        .args([
            "find-generic-password",
            "-s",
            KEYCHAIN_SERVICE,
            "-a",
            KEYCHAIN_ACCOUNT,
            "-w",
        ])
        .stdin(Stdio::null())
        .output()
        .map_err(|error| format!("Could not read the Notion token: {error}"))?;
    if output.status.code() == Some(SECURITY_ITEM_NOT_FOUND) {
        return Ok(None);
    }
    if !output.status.success() {
        return Err("The Notion token could not be read from Keychain".to_string());
    }
    let token = String::from_utf8_lossy(&output.stdout).trim().to_string();
    Ok((!token.is_empty()).then_some(token))
}

fn write_keychain_token(token: &str) -> Result<(), String> {
    let output = Command::new("/usr/bin/security")
        .args([
            "add-generic-password",
            "-U",
            "-s",
            KEYCHAIN_SERVICE,
            "-a",
            KEYCHAIN_ACCOUNT,
            "-w",
            token,
        ])
        .stdin(Stdio::null())
        .output()
        .map_err(|error| format!("Could not store the Notion token: {error}"))?;
    if output.status.success() {
        Ok(())
    } else {
        Err("The Notion token could not be stored in Keychain".to_string())
    }
}

fn delete_keychain_token() -> Result<(), String> {
    let output = Command::new("/usr/bin/security")
        .args([
            "delete-generic-password",
            "-s",
            KEYCHAIN_SERVICE,
            "-a",
            KEYCHAIN_ACCOUNT,
        ])
        .stdin(Stdio::null())
        .output()
        .map_err(|error| format!("Could not remove the Notion token: {error}"))?;
    if output.status.success() || output.status.code() == Some(SECURITY_ITEM_NOT_FOUND) {
        Ok(())
    } else {
        Err("The Notion token could not be removed from Keychain".to_string())
    }
}

#[tauri::command]
pub async fn read_notion_task_settings(
    manager: tauri::State<'_, AgentRuntimeManager>,
) -> Result<NotionTaskSettings, String> {
    let stored = read_stored_settings(&manager)?;
    let has_token =
        tauri::async_runtime::spawn_blocking(|| read_keychain_token().map(|v| v.is_some()))
            .await
            .map_err(|error| format!("Notion Keychain task failed: {error}"))??;
    Ok(NotionTaskSettings {
        data_source_id: stored.data_source_id,
        has_token,
    })
}

#[tauri::command]
pub async fn save_notion_task_settings(
    manager: tauri::State<'_, AgentRuntimeManager>,
    data_source_id: String,
    token: String,
) -> Result<NotionTaskSettings, String> {
    let token = token.trim().to_string();
    let active_token = if token.is_empty() {
        tauri::async_runtime::spawn_blocking(read_keychain_token)
            .await
            .map_err(|error| format!("Notion Keychain task failed: {error}"))??
            .ok_or_else(|| "Enter a Notion personal access token".to_string())?
    } else {
        token.clone()
    };
    let data_source_id = if data_source_id.trim().is_empty() {
        discover_task_data_source(&reqwest::Client::new(), &active_token).await?
    } else {
        data_source_id.trim().to_string()
    };
    if !token.is_empty() {
        tauri::async_runtime::spawn_blocking(move || write_keychain_token(&token))
            .await
            .map_err(|error| format!("Notion Keychain task failed: {error}"))??;
    }
    let stored = StoredSettings {
        data_source_id: data_source_id.clone(),
    };
    manager.write_app_setting(
        SETTINGS_KEY,
        &serde_json::to_string(&stored).map_err(|error| error.to_string())?,
    )?;
    Ok(NotionTaskSettings {
        data_source_id,
        has_token: true,
    })
}

pub(crate) async fn save_oauth_connection(
    manager: &AgentRuntimeManager,
    token: String,
) -> Result<NotionTaskSettings, String> {
    let data_source_id = discover_task_data_source(&reqwest::Client::new(), &token).await?;
    let token_to_store = token.clone();
    tauri::async_runtime::spawn_blocking(move || write_keychain_token(&token_to_store))
        .await
        .map_err(|error| format!("Notion Keychain task failed: {error}"))??;
    let stored = StoredSettings {
        data_source_id: data_source_id.clone(),
    };
    manager.write_app_setting(
        SETTINGS_KEY,
        &serde_json::to_string(&stored).map_err(|error| error.to_string())?,
    )?;
    Ok(NotionTaskSettings {
        data_source_id,
        has_token: true,
    })
}

async fn discover_task_data_source(
    client: &reqwest::Client,
    token: &str,
) -> Result<String, String> {
    let response = notion_request(client.post("https://api.notion.com/v1/search"), token)
        .json(&json!({
            "page_size": NOTION_PAGE_SIZE,
            "filter": { "property": "object", "value": "data_source" }
        }))
        .send()
        .await
        .map_err(|error| format!("Notion could not be reached: {error}"))?;
    task_data_source_id(&response_payload(response).await?)
}

fn task_data_source_id(payload: &Value) -> Result<String, String> {
    let results = payload
        .get("results")
        .and_then(Value::as_array)
        .ok_or_else(|| "Notion returned no data sources".to_string())?;
    let candidates = results
        .iter()
        .filter(|source| source.get("object").and_then(Value::as_str) == Some("data_source"))
        .filter(|source| {
            let Some(properties) = source.get("properties").and_then(Value::as_object) else {
                return false;
            };
            properties.contains_key("Status")
                && properties
                    .values()
                    .any(|property| property.get("type").and_then(Value::as_str) == Some("title"))
        })
        .filter_map(|source| {
            Some((
                source.get("id")?.as_str()?.to_string(),
                rich_text(source.get("title")?).unwrap_or_default(),
            ))
        })
        .collect::<Vec<_>>();
    let named_tasks = candidates
        .iter()
        .filter(|(_, title)| title.eq_ignore_ascii_case("tasks"))
        .collect::<Vec<_>>();
    if let [candidate] = named_tasks.as_slice() {
        return Ok(candidate.0.clone());
    }
    if let [candidate] = candidates.as_slice() {
        return Ok(candidate.0.clone());
    }
    if candidates.is_empty() {
        Err("No Notion task database was found. Make sure the token can read a database with a title and Status fields.".to_string())
    } else {
        Err("More than one Notion task database was found. Open Advanced and paste the data source ID to choose one.".to_string())
    }
}

#[tauri::command]
pub async fn clear_notion_task_settings(
    manager: tauri::State<'_, AgentRuntimeManager>,
) -> Result<(), String> {
    tauri::async_runtime::spawn_blocking(delete_keychain_token)
        .await
        .map_err(|error| format!("Notion Keychain task failed: {error}"))??;
    manager.write_app_setting(
        SETTINGS_KEY,
        &serde_json::to_string(&StoredSettings::default()).map_err(|error| error.to_string())?,
    )
}

#[tauri::command]
pub async fn list_notion_tasks(
    manager: tauri::State<'_, AgentRuntimeManager>,
    offset: u32,
    limit: u32,
    search: String,
    project: String,
    statuses: Vec<String>,
    priorities: Vec<String>,
    sort_by: String,
    sort_direction: String,
) -> Result<NotionTaskPage, String> {
    let store = manager.store_handle();
    tauri::async_runtime::spawn_blocking(move || {
        store
            .query_notion_task_projections(
                offset,
                limit.min(100),
                &search,
                &project,
                &statuses,
                &priorities,
                &sort_by,
                &sort_direction,
            )
            .map(NotionTaskPage::from)
            .map_err(|error| error.to_string())
    })
    .await
    .map_err(|error| format!("Notion cache task failed: {error}"))?
}

/// A cached task by its Notion page id, or none when the cache does not hold it.
#[tauri::command]
pub async fn read_cached_notion_task(
    manager: tauri::State<'_, AgentRuntimeManager>,
    source_task_id: String,
) -> Result<Option<NotionTaskRow>, String> {
    let store = manager.store_handle();
    tauri::async_runtime::spawn_blocking(move || {
        store
            .notion_task_projection_by_id(&source_task_id)
            .map(|task| task.map(NotionTaskRow::from))
            .map_err(|error| error.to_string())
    })
    .await
    .map_err(|error| format!("Notion cache task failed: {error}"))?
}

#[tauri::command]
pub async fn read_notion_task_detail(
    source_task_id: String,
    request_id: u64,
) -> Result<NotionTaskDetail, String> {
    validate_task_id(&source_task_id)?;
    let (handle, registration) = AbortHandle::new_pair();
    DETAIL_REQUESTS
        .lock()
        .unwrap_or_else(std::sync::PoisonError::into_inner)
        .insert(request_id, handle);
    let result = Abortable::new(
        async {
            let token = tauri::async_runtime::spawn_blocking(read_keychain_token)
                .await
                .map_err(|error| format!("Notion Keychain task failed: {error}"))??
                .ok_or_else(|| "Connect Notion first".to_string())?;
            read_task_detail(&reqwest::Client::new(), &token, &source_task_id).await
        },
        registration,
    )
    .await;
    DETAIL_REQUESTS
        .lock()
        .unwrap_or_else(std::sync::PoisonError::into_inner)
        .remove(&request_id);
    result.map_err(|_| "Notion task request was cancelled".to_string())?
}

#[tauri::command]
pub fn cancel_notion_task_detail(request_id: u64) {
    if let Some(handle) = DETAIL_REQUESTS
        .lock()
        .unwrap_or_else(std::sync::PoisonError::into_inner)
        .remove(&request_id)
    {
        handle.abort();
    }
}

#[tauri::command]
pub async fn update_notion_task_status(
    manager: tauri::State<'_, AgentRuntimeManager>,
    source_task_id: String,
    status: String,
) -> Result<NotionTaskRefreshReceipt, String> {
    validate_task_id(&source_task_id)?;
    let status = status.trim();
    if status.is_empty() || status.len() > 100 {
        return Err("Choose a valid Notion task status".to_string());
    }
    let token = tauri::async_runtime::spawn_blocking(read_keychain_token)
        .await
        .map_err(|error| format!("Notion Keychain task failed: {error}"))??
        .ok_or_else(|| "Connect Notion first".to_string())?;
    let client = reqwest::Client::new();
    let response = notion_request(
        client.patch(format!("https://api.notion.com/v1/pages/{source_task_id}")),
        &token,
    )
    .json(&status_update_payload(status))
    .send()
    .await
    .map_err(|error| format!("Notion could not update this task: {error}"))?;
    if response.status() == reqwest::StatusCode::FORBIDDEN {
        return Err(
            "Assembly's Notion connection needs the Update content capability. Enable it in Notion Developer tools, then reconnect Notion in Assembly."
                .to_string(),
        );
    }
    response_payload(response).await?;
    refresh_notion_tasks(manager).await
}

/// Edit the task's title or priority, then refresh the list so it shows the change.
#[tauri::command]
pub async fn update_notion_task_property(
    manager: tauri::State<'_, AgentRuntimeManager>,
    source_task_id: String,
    property: String,
    value: String,
) -> Result<NotionTaskRefreshReceipt, String> {
    validate_task_id(&source_task_id)?;
    let payload = property_update_payload(&property, value.trim())?;
    let token = notion_token().await?;
    notion_write(
        reqwest::Client::new().patch(format!("https://api.notion.com/v1/pages/{source_task_id}")),
        &token,
        &payload,
    )
    .await?;
    refresh_notion_tasks(manager).await
}

/// Replace one block's text (written as Markdown) or tick a to-do.
#[tauri::command]
pub async fn update_notion_block(
    block_id: String,
    kind: String,
    markdown: Option<String>,
    checked: Option<bool>,
) -> Result<(), String> {
    validate_task_id(&block_id)?;
    let payload = block_update_payload(&kind, markdown.as_deref(), checked)?;
    let token = notion_token().await?;
    notion_write(
        reqwest::Client::new().patch(format!("https://api.notion.com/v1/blocks/{block_id}")),
        &token,
        &payload,
    )
    .await
}

/// Add a paragraph (written as Markdown) to the end of the task page.
#[tauri::command]
pub async fn append_notion_task_text(source_task_id: String, markdown: String) -> Result<(), String> {
    validate_task_id(&source_task_id)?;
    let block = json!({ "type": "paragraph", "paragraph": { "rich_text": markdown_rich_text(&markdown) } });
    let token = notion_token().await?;
    append_block(&reqwest::Client::new(), &token, &source_task_id, block).await
}

/// Upload an image (a pasted screenshot or a picked file) and add it to the end of the task page.
#[tauri::command]
pub async fn append_notion_task_image(
    source_task_id: String,
    file_name: String,
    content_type: String,
    data_base64: String,
) -> Result<(), String> {
    validate_task_id(&source_task_id)?;
    if !matches!(content_type.as_str(), "image/png" | "image/jpeg" | "image/gif" | "image/webp") {
        return Err("Only PNG, JPEG, GIF and WebP images can be added".to_string());
    }
    let bytes = base64::engine::general_purpose::STANDARD
        .decode(data_base64)
        .map_err(|_| "The image could not be read".to_string())?;
    if bytes.len() > 20 * 1024 * 1024 {
        return Err("Images over 20 MB cannot be added".to_string());
    }
    let file_name = upload_file_name(&file_name);
    let token = notion_token().await?;
    let client = reqwest::Client::new();
    let response = notion_request(client.post("https://api.notion.com/v1/file_uploads"), &token)
        .json(&json!({ "filename": file_name, "content_type": content_type }))
        .send()
        .await
        .map_err(|error| format!("Notion could not start the upload: {error}"))?;
    let upload = response_payload(response).await?;
    let upload_id = upload["id"].as_str().ok_or("Notion did not return an upload id")?;
    let boundary = format!("assembly-{}", now_ms());
    let mut body = format!(
        "--{boundary}\r\nContent-Disposition: form-data; name=\"file\"; filename=\"{file_name}\"\r\nContent-Type: {content_type}\r\n\r\n"
    )
    .into_bytes();
    body.extend_from_slice(&bytes);
    body.extend_from_slice(format!("\r\n--{boundary}--\r\n").as_bytes());
    let response = client
        .post(format!("https://api.notion.com/v1/file_uploads/{upload_id}/send"))
        .bearer_auth(&token)
        .header("Notion-Version", NOTION_VERSION)
        .header(reqwest::header::CONTENT_TYPE, format!("multipart/form-data; boundary={boundary}"))
        .body(body)
        .send()
        .await
        .map_err(|error| format!("Notion could not receive the image: {error}"))?;
    response_payload(response).await?;
    let block = json!({ "type": "image", "image": { "type": "file_upload", "file_upload": { "id": upload_id } } });
    append_block(&client, &token, &source_task_id, block).await
}

async fn notion_token() -> Result<String, String> {
    tauri::async_runtime::spawn_blocking(read_keychain_token)
        .await
        .map_err(|error| format!("Notion Keychain task failed: {error}"))??
        .ok_or_else(|| "Connect Notion first".to_string())
}

/// Send a change to Notion. A 403 means the connection may read but not edit.
async fn notion_write(builder: reqwest::RequestBuilder, token: &str, payload: &Value) -> Result<(), String> {
    let response = notion_request(builder, token)
        .json(payload)
        .send()
        .await
        .map_err(|error| format!("Notion could not save this change: {error}"))?;
    if response.status() == reqwest::StatusCode::FORBIDDEN {
        return Err(
            "Assembly's Notion connection needs the Update content capability. Enable it in Notion Developer tools, then reconnect Notion in Assembly."
                .to_string(),
        );
    }
    response_payload(response).await.map(|_| ())
}

async fn append_block(client: &reqwest::Client, token: &str, page_id: &str, block: Value) -> Result<(), String> {
    notion_write(
        client.patch(format!("https://api.notion.com/v1/blocks/{page_id}/children")),
        token,
        &json!({ "children": [block] }),
    )
    .await
}

#[tauri::command]
pub async fn refresh_notion_tasks(
    manager: tauri::State<'_, AgentRuntimeManager>,
) -> Result<NotionTaskRefreshReceipt, String> {
    let settings = read_stored_settings(&manager)?;
    if settings.data_source_id.trim().is_empty() {
        return Err("Configure a Notion data source first".to_string());
    }
    let token = tauri::async_runtime::spawn_blocking(read_keychain_token)
        .await
        .map_err(|error| format!("Notion Keychain task failed: {error}"))??
        .ok_or_else(|| "Configure a Notion integration token first".to_string())?;
    let client = reqwest::Client::new();
    let fetched_at_ms = now_ms();
    let pending_tasks =
        query_task_pages(&client, &token, &settings.data_source_id, fetched_at_ms).await?;
    let project_names = load_project_names(&client, &token, &pending_tasks).await?;
    let tasks = pending_tasks
        .into_iter()
        .map(|task| finish_task_projection(task, &project_names))
        .collect::<Vec<_>>();
    let count = tasks.len();
    let store = manager.store_handle();
    tauri::async_runtime::spawn_blocking(move || {
        store
            .replace_notion_task_projections(&tasks)
            .map_err(|error| error.to_string())
    })
    .await
    .map_err(|error| format!("Notion cache task failed: {error}"))??;
    Ok(NotionTaskRefreshReceipt {
        count,
        fetched_at_ms,
    })
}

async fn query_task_pages(
    client: &reqwest::Client,
    token: &str,
    data_source_id: &str,
    fetched_at_ms: i64,
) -> Result<Vec<PendingNotionTask>, String> {
    let mut tasks = Vec::new();
    let mut results_seen = 0;
    let mut cursor: Option<String> = None;
    while results_seen < MAX_NOTION_TASKS {
        let mut body = json!({ "page_size": NOTION_PAGE_SIZE });
        if let Some(value) = &cursor {
            body["start_cursor"] = Value::String(value.clone());
        }
        let response = notion_request(
            client.post(format!(
                "https://api.notion.com/v1/data_sources/{data_source_id}/query"
            )),
            token,
        )
        .json(&body)
        .send()
        .await
        .map_err(|error| format!("Notion could not be reached: {error}"))?;
        let payload = response_payload(response).await?;
        let results = payload
            .get("results")
            .and_then(Value::as_array)
            .ok_or_else(|| "Notion returned no task results".to_string())?;
        if results.is_empty() {
            break;
        }
        let result_count = results.len().min(MAX_NOTION_TASKS - results_seen);
        results_seen += result_count;
        tasks.extend(
            results
                .iter()
                .take(result_count)
                .filter_map(|page| pending_task_projection(page, fetched_at_ms)),
        );
        if payload.get("has_more").and_then(Value::as_bool) != Some(true) {
            break;
        }
        cursor = payload
            .get("next_cursor")
            .and_then(Value::as_str)
            .map(str::to_string);
        if cursor.is_none() {
            break;
        }
    }
    Ok(tasks)
}

async fn detail_get(client: &reqwest::Client, token: &str, path: &str) -> Result<Value, String> {
    response_payload(
        notion_request(
            client.get(format!("https://api.notion.com/v1/{path}")),
            token,
        )
        .send()
        .await
        .map_err(|error| format!("Notion could not read this task: {error}"))?,
    )
    .await
}

async fn read_task_detail(
    client: &reqwest::Client,
    token: &str,
    id: &str,
) -> Result<NotionTaskDetail, String> {
    let page = detail_get(client, token, &format!("pages/{id}")).await?;
    let mut related = HashMap::new();
    if let Some(properties) = page["properties"].as_object() {
        for property in properties.values() {
            for relation in property["relation"].as_array().into_iter().flatten() {
                let id = relation["id"].as_str().unwrap_or_default();
                if !related.contains_key(id) {
                    let page = detail_get(client, token, &format!("pages/{id}")).await?;
                    related.insert(
                        id.to_string(),
                        (
                            page_title(&page).unwrap_or_else(|| "Notion page".into()),
                            page["url"]
                                .as_str()
                                .map(str::to_string)
                                .unwrap_or_else(|| notion_page_url(id)),
                        ),
                    );
                }
            }
        }
    }
    let properties = task_properties(&page, &related);
    let blocks = read_child_blocks(client, token, id, 0).await?;
    let body = blocks.iter().map(notion_block).collect();
    let comments = read_comments(client, token, id).await?;
    Ok(NotionTaskDetail {
        properties,
        body,
        comments,
    })
}

async fn read_child_blocks(
    client: &reqwest::Client,
    token: &str,
    id: &str,
    depth: usize,
) -> Result<Vec<Value>, String> {
    let mut blocks = Vec::new();
    let mut cursor = None;
    loop {
        let path = paged_path(
            &format!("blocks/{id}/children?page_size={NOTION_PAGE_SIZE}"),
            cursor.as_deref(),
        );
        let payload = detail_get(client, token, &path).await?;
        for mut block in response_results(&payload)?.iter().cloned() {
            if depth < 3 && block["has_children"].as_bool() == Some(true) {
                let id = block["id"].as_str().unwrap_or_default();
                let children = Box::pin(read_child_blocks(client, token, id, depth + 1)).await?;
                block["children"] = Value::Array(children);
            }
            blocks.push(block);
        }
        cursor = next_cursor(&payload).map(str::to_string);
        if cursor.is_none() {
            break;
        }
    }
    Ok(blocks)
}

fn response_results(payload: &Value) -> Result<&[Value], String> {
    payload
        .get("results")
        .and_then(Value::as_array)
        .map(Vec::as_slice)
        .ok_or_else(|| "Notion returned invalid results: expected an array".to_string())
}

fn paged_path(path: &str, cursor: Option<&str>) -> String {
    match cursor {
        Some(cursor) => format!("{path}&start_cursor={cursor}"),
        None => path.to_string(),
    }
}

fn next_cursor(payload: &Value) -> Option<&str> {
    (payload["has_more"].as_bool() == Some(true))
        .then(|| payload["next_cursor"].as_str())
        .flatten()
}

fn notion_page_url(id: &str) -> String {
    format!("https://www.notion.so/{}", id.replace('-', ""))
}

fn task_properties(
    page: &Value,
    related: &HashMap<String, (String, String)>,
) -> Vec<NotionTaskProperty> {
    let mut rows = Vec::new();
    for (name, property) in page["properties"].as_object().into_iter().flatten() {
        let raw_kind = property["type"].as_str().unwrap_or("other");
        if raw_kind == "title" {
            continue;
        }
        let value = &property[raw_kind];
        let mut values = Vec::new();
        let mut links = Vec::new();
        let kind = match raw_kind {
            "status" | "select" => {
                if let Some(name) = value["name"].as_str() {
                    values.push(name.into());
                }
                raw_kind
            }
            "multi_select" | "people" | "files" => {
                for item in value.as_array().into_iter().flatten() {
                    values.push(item["name"].as_str().unwrap_or("Notion user").into());
                    links.push(if raw_kind == "files" {
                        item[item["type"].as_str().unwrap_or("external")]["url"]
                            .as_str()
                            .map(str::to_string)
                    } else {
                        None
                    });
                }
                raw_kind
            }
            "date" => {
                if let Some(start) = value["start"].as_str() {
                    values.push(start.into());
                    if let Some(end) = value["end"].as_str() {
                        values.push(end.into());
                    }
                }
                raw_kind
            }
            "relation" => {
                for item in value.as_array().into_iter().flatten() {
                    let id = item["id"].as_str().unwrap_or_default();
                    let (title, url) = related
                        .get(id)
                        .cloned()
                        .unwrap_or_else(|| ("Notion page".into(), notion_page_url(id)));
                    values.push(title);
                    links.push(Some(url));
                }
                raw_kind
            }
            "rich_text" => {
                if let Some(text) = rich_text(value) {
                    values.push(text);
                }
                "text"
            }
            "url" | "created_time" | "last_edited_time" => {
                if let Some(text) = value.as_str() {
                    values.push(text.into());
                    if raw_kind == "url" {
                        links.push(Some(text.into()));
                    }
                }
                raw_kind
            }
            "checkbox" | "number" => {
                if !value.is_null() {
                    values.push(value.to_string());
                }
                raw_kind
            }
            "unique_id" => {
                if let Some(number) = value["number"].as_i64() {
                    let prefix = value["prefix"].as_str().unwrap_or_default();
                    values.push(if prefix.is_empty() {
                        number.to_string()
                    } else {
                        format!("{prefix}-{number}")
                    });
                }
                raw_kind
            }
            _ => {
                if !value.is_null() {
                    values.push(value.to_string());
                }
                "other"
            }
        };
        links.resize(values.len(), None);
        rows.push(NotionTaskProperty {
            name: name.clone(),
            kind: kind.into(),
            values,
            links,
        });
    }
    rows.sort_by_key(|row| {
        (
            matches!(row.kind.as_str(), "created_time" | "last_edited_time"),
            row.name.clone(),
        )
    });
    rows
}

fn code_language(language: &str) -> &str {
    match language {
        "c#" => "csharp",
        "f#" => "plaintext",
        "c++" => "cpp",
        "java/c/c++/c#" => "cpp",
        "objective-c" | "plain text" => "plaintext",
        "shell" => "sh",
        "visual basic" => "plaintext",
        other => other,
    }
}

fn notion_block(block: &Value) -> NotionBlock {
    let kind = block["type"].as_str().unwrap_or_default();
    let value = &block[kind];
    let children = block["children"]
        .as_array()
        .map(Vec::as_slice)
        .unwrap_or_default();
    let markdown = match kind {
        "divider" => String::new(),
        "code" => {
            let code = rich_text(&value["rich_text"]).unwrap_or_default();
            let fence = "`"
                .repeat((code.split(|ch| ch != '`').map(str::len).max().unwrap_or(0) + 1).max(3));
            format!(
                "{fence}{}\n{code}\n{fence}",
                code_language(value["language"].as_str().unwrap_or_default())
            )
        }
        "image" => {
            let url = value[value["type"].as_str().unwrap_or_default()]["url"]
                .as_str()
                .unwrap_or_default();
            if !url.starts_with("https://") {
                String::new()
            } else {
                format!(
                    "![{}](<{}>)",
                    rich_text(&value["caption"])
                        .unwrap_or_default()
                        .replace(']', "\\]"),
                    url.replace('\\', "\\\\")
                        .replace('<', "\\<")
                        .replace('>', "\\>")
                )
            }
        }
        "table" => {
            let mut rows = Vec::new();
            for (index, row) in children.iter().enumerate() {
                let cells = row["table_row"]["cells"]
                    .as_array()
                    .into_iter()
                    .flatten()
                    .map(|cell| {
                        let text = rich_text_markdown(cell).replace('\n', " ");
                        let mut cell = String::new();
                        let mut escaped = false;
                        for ch in text.chars() {
                            if ch == '|' && !escaped {
                                cell.push('\\');
                            }
                            cell.push(ch);
                            escaped = ch == '\\' && !escaped;
                        }
                        cell
                    })
                    .collect::<Vec<_>>();
                rows.push(format!("| {} |", cells.join(" | ")));
                if index == 0 {
                    rows.push(format!("| {} |", vec!["---"; cells.len()].join(" | ")));
                }
            }
            rows.join("\n")
        }
        _ => rich_text_markdown(&value["rich_text"]),
    };
    NotionBlock {
        id: block["id"].as_str().unwrap_or_default().to_string(),
        kind: kind.to_string(),
        markdown,
        checked: (kind == "to_do").then(|| value["checked"].as_bool().unwrap_or(false)),
        icon: if kind == "callout" {
            value["icon"]["emoji"].as_str().map(str::to_string)
        } else {
            None
        },
        children: children.iter().map(notion_block).collect(),
    }
}

fn forbidden_comments(status: reqwest::StatusCode) -> Option<Vec<NotionTaskComment>> {
    (status == reqwest::StatusCode::FORBIDDEN).then(Vec::new)
}

fn comment_author(user: Option<&Value>) -> String {
    user.and_then(|user| user["name"].as_str())
        .filter(|name| !name.is_empty())
        .unwrap_or("Notion user")
        .into()
}

async fn read_comments(
    client: &reqwest::Client,
    token: &str,
    id: &str,
) -> Result<Vec<NotionTaskComment>, String> {
    let mut comments = Vec::new();
    let mut users = HashMap::new();
    let mut cursor = None;
    loop {
        let path = paged_path(
            &format!("comments?block_id={id}&page_size={NOTION_PAGE_SIZE}"),
            cursor.as_deref(),
        );
        let response = notion_request(
            client.get(format!("https://api.notion.com/v1/{path}")),
            token,
        )
        .send()
        .await
        .map_err(|error| format!("Notion could not read comments: {error}"))?;
        if let Some(empty) = forbidden_comments(response.status()) {
            return Ok(empty);
        }
        let payload = response_payload(response).await?;
        for comment in response_results(&payload)? {
            let user_id = comment["created_by"]["id"].as_str().unwrap_or_default();
            if !users.contains_key(user_id) {
                let user = detail_get(client, token, &format!("users/{user_id}"))
                    .await
                    .ok();
                users.insert(user_id.to_string(), comment_author(user.as_ref()));
            }
            comments.push(NotionTaskComment {
                author: users[user_id].clone(),
                created: comment["created_time"].as_str().unwrap_or_default().into(),
                text: rich_text_markdown(&comment["rich_text"]),
            });
        }
        cursor = next_cursor(&payload).map(str::to_string);
        if cursor.is_none() {
            break;
        }
    }
    Ok(comments)
}

async fn load_project_names(
    client: &reqwest::Client,
    token: &str,
    tasks: &[PendingNotionTask],
) -> Result<HashMap<String, String>, String> {
    let ids = tasks
        .iter()
        .flat_map(|task| task.project_ids.iter().cloned())
        .collect::<BTreeSet<_>>();
    let mut names = HashMap::new();
    for id in ids.into_iter().take(50) {
        let response = notion_request(
            client.get(format!("https://api.notion.com/v1/pages/{id}")),
            token,
        )
        .send()
        .await
        .map_err(|error| format!("Notion could not read project {id}: {error}"))?;
        let payload = response_payload(response).await?;
        names.insert(
            id,
            page_title(&payload).unwrap_or_else(|| "Project".to_string()),
        );
    }
    Ok(names)
}

fn notion_request(builder: reqwest::RequestBuilder, token: &str) -> reqwest::RequestBuilder {
    builder
        .bearer_auth(token)
        .header("Notion-Version", NOTION_VERSION)
        .header(reqwest::header::CONTENT_TYPE, "application/json")
}

async fn response_payload(response: reqwest::Response) -> Result<Value, String> {
    let status = response.status();
    let payload = response
        .json::<Value>()
        .await
        .map_err(|error| format!("Notion returned invalid JSON: {error}"))?;
    if status.is_success() {
        Ok(payload)
    } else {
        Err(payload
            .get("message")
            .and_then(Value::as_str)
            .unwrap_or("Notion rejected the request")
            .to_string())
    }
}

fn pending_task_projection(page: &Value, fetched_at_ms: i64) -> Option<PendingNotionTask> {
    let properties = page.get("properties")?;
    Some(PendingNotionTask {
        source_task_id: page.get("id")?.as_str()?.to_string(),
        title: property_text(properties, "Name", "title")
            .or_else(|| page_title(page))
            .unwrap_or_else(|| "Untitled task".to_string()),
        project_ids: project_relation_ids(page),
        status: property_choice(properties, "Status").unwrap_or_else(|| "Unspecified".to_string()),
        priority: property_choice(properties, "Priority"),
        assignee: property_people(properties, "Assignee"),
        due_date: property_date(properties, "Due"),
        source_url: page
            .get("url")
            .and_then(Value::as_str)
            .unwrap_or_default()
            .to_string(),
        fetched_at_ms,
    })
}

fn finish_task_projection(
    task: PendingNotionTask,
    project_names: &HashMap<String, String>,
) -> NotionTaskProjection {
    let project = task
        .project_ids
        .iter()
        .filter_map(|id| project_names.get(id))
        .cloned()
        .collect::<Vec<_>>()
        .join(", ");
    NotionTaskProjection {
        source_task_id: task.source_task_id,
        title: task.title,
        project,
        status: task.status,
        priority: task.priority,
        assignee: task.assignee,
        due_date: task.due_date,
        source_url: task.source_url,
        fetched_at_ms: task.fetched_at_ms,
    }
}

fn project_relation_ids(page: &Value) -> Vec<String> {
    page.get("properties")
        .and_then(|properties| properties.get("Project"))
        .and_then(|property| property.get("relation"))
        .and_then(Value::as_array)
        .into_iter()
        .flatten()
        .filter_map(|entry| entry.get("id").and_then(Value::as_str).map(str::to_string))
        .collect()
}

fn page_title(page: &Value) -> Option<String> {
    let properties = page.get("properties")?.as_object()?;
    properties
        .values()
        .find_map(|property| rich_text(property.get("title")?))
}

fn property_text(properties: &Value, name: &str, kind: &str) -> Option<String> {
    rich_text(properties.get(name)?.get(kind)?)
}

fn rich_text(value: &Value) -> Option<String> {
    let text = value
        .as_array()?
        .iter()
        .filter_map(|part| part.get("plain_text").and_then(Value::as_str))
        .collect::<String>();
    (!text.is_empty()).then_some(text)
}

fn rich_text_markdown(value: &Value) -> String {
    let mut runs: Vec<(String, &Value, Option<String>)> = Vec::new();
    for part in value.as_array().into_iter().flatten() {
        let mut text = part["plain_text"]
            .as_str()
            .or_else(|| part["text"]["content"].as_str())
            .unwrap_or_default()
            .to_string();
        let mention = &part["mention"];
        let mut href = part["href"]
            .as_str()
            .or_else(|| part["text"]["link"]["url"].as_str())
            .map(str::to_string);
        match mention["type"].as_str() {
            Some("page") => {
                href = mention["page"]["id"].as_str().map(notion_page_url);
                if text.is_empty() {
                    text = "Notion page".into();
                }
            }
            Some("user") => {
                text = format!(
                    "@{}",
                    mention["user"]["name"]
                        .as_str()
                        .unwrap_or(text.trim_start_matches('@'))
                );
            }
            Some("date") if text.is_empty() => {
                text = mention["date"]["start"].as_str().unwrap_or_default().into();
                if let Some(end) = mention["date"]["end"].as_str() {
                    text.push_str(&format!(" → {end}"));
                }
            }
            _ => {}
        }
        let annotations = &part["annotations"];
        if let Some((previous, previous_annotations, previous_href)) = runs.last_mut() {
            if ["bold", "italic", "strikethrough", "code"]
                .iter()
                .all(|flag| {
                    (previous_annotations[*flag].as_bool() == Some(true))
                        == (annotations[*flag].as_bool() == Some(true))
                })
                && *previous_href == href
            {
                previous.push_str(&text);
                continue;
            }
        }
        runs.push((text, annotations, href));
    }
    runs.into_iter()
        .map(|(mut text, annotations, href)| {
            if annotations["code"].as_bool() == Some(true) {
                let fence =
                    "`".repeat(text.split(|ch| ch != '`').map(str::len).max().unwrap_or(0) + 1);
                text = format!("{fence} {text} {fence}");
            } else {
                text = text
                    .replace('\\', "\\\\")
                    .replace('*', "\\*")
                    .replace('_', "\\_")
                    .replace('[', "\\[")
                    .replace(']', "\\]")
                    .replace('`', "\\`")
                    .replace('~', "\\~")
                    .replace('|', "\\|")
                    .replace('<', "\\<");
                text = text
                    .split('\n')
                    .map(|line| {
                        let content = line.trim_start_matches([' ', '\t']);
                        let padding = line.len() - content.len();
                        let digits = content.bytes().take_while(u8::is_ascii_digit).count();
                        let marker = if content.starts_with(['#', '>', '-', '+', '=']) {
                            Some(padding)
                        } else if digits > 0 && content[digits..].starts_with(['.', ')']) {
                            Some(padding + digits)
                        } else {
                            None
                        };
                        match marker {
                            Some(offset) => format!("{}\\{}", &line[..offset], &line[offset..]),
                            None => line.to_string(),
                        }
                    })
                    .collect::<Vec<_>>()
                    .join("\n");
            }
            for (annotation, marker) in [("bold", "**"), ("italic", "*"), ("strikethrough", "~~")] {
                if annotations[annotation].as_bool() == Some(true) {
                    let content = text.trim();
                    if !content.is_empty() {
                        text = format!(
                            "{}{marker}{content}{marker}{}",
                            &text[..text.len() - text.trim_start().len()],
                            &text[text.trim_end().len()..]
                        );
                    }
                }
            }
            if let Some(url) = href {
                text = format!(
                    "[{text}](<{}>)",
                    url.replace('\\', "\\\\")
                        .replace('<', "\\<")
                        .replace('>', "\\>")
                );
            }
            text
        })
        .collect()
}

fn property_choice(properties: &Value, name: &str) -> Option<String> {
    let property = properties.get(name)?;
    property
        .get("status")
        .or_else(|| property.get("select"))?
        .get("name")?
        .as_str()
        .map(str::to_string)
}

fn property_people(properties: &Value, name: &str) -> Option<String> {
    let names = properties
        .get(name)?
        .get("people")?
        .as_array()?
        .iter()
        .filter_map(|person| person.get("name").and_then(Value::as_str))
        .collect::<Vec<_>>()
        .join(", ");
    (!names.is_empty()).then_some(names)
}

fn property_date(properties: &Value, name: &str) -> Option<String> {
    properties
        .get(name)?
        .get("date")?
        .get("start")?
        .as_str()
        .map(str::to_string)
}

fn validate_task_id(source_task_id: &str) -> Result<(), String> {
    if source_task_id.len() > 64
        || !source_task_id
            .chars()
            .all(|character| character.is_ascii_hexdigit() || character == '-')
    {
        return Err("The Notion task ID is invalid".to_string());
    }
    Ok(())
}

fn status_update_payload(status: &str) -> Value {
    json!({ "properties": { "Status": { "select": { "name": status } } } })
}

fn property_update_payload(property: &str, value: &str) -> Result<Value, String> {
    if value.is_empty() || value.chars().count() > 2000 {
        return Err("Enter a value for this property".to_string());
    }
    match property {
        // Notion gives every database's title property the id "title", whatever it is named.
        "title" => {
            let mut runs = Vec::new();
            push_rich_text(&mut runs, value, [false; 4], None);
            Ok(json!({ "properties": { "title": { "title": runs } } }))
        }
        "Priority" => Ok(json!({ "properties": { "Priority": { "select": { "name": value } } } })),
        _ => Err("This property cannot be edited here".to_string()),
    }
}

/// Block types whose text the task page can edit.
const EDITABLE_TEXT_BLOCKS: [&str; 10] = [
    "paragraph",
    "heading_1",
    "heading_2",
    "heading_3",
    "bulleted_list_item",
    "numbered_list_item",
    "to_do",
    "quote",
    "callout",
    "toggle",
];

fn block_update_payload(kind: &str, markdown: Option<&str>, checked: Option<bool>) -> Result<Value, String> {
    match (markdown, checked) {
        (Some(markdown), None) if EDITABLE_TEXT_BLOCKS.contains(&kind) => {
            Ok(json!({ kind: { "rich_text": markdown_rich_text(markdown) } }))
        }
        (None, Some(checked)) if kind == "to_do" => Ok(json!({ "to_do": { "checked": checked } })),
        _ => Err("This block cannot be edited here".to_string()),
    }
}

/// Markdown written on the task page back into Notion rich text: bold, italic,
/// strikethrough, inline code and links. Mentions come back as plain links or text.
fn markdown_rich_text(markdown: &str) -> Vec<Value> {
    let arena = comrak::Arena::new();
    let mut options = comrak::Options::default();
    options.extension.strikethrough = true;
    let root = comrak::parse_document(&arena, markdown, &options);
    let mut runs = Vec::new();
    for (index, block) in root.children().enumerate() {
        if index > 0 {
            push_rich_text(&mut runs, "\n", [false; 4], None);
        }
        collect_rich_text(block, [false; 4], None, &mut runs);
    }
    runs
}

/// `style` is bold, italic, strikethrough, code.
fn collect_rich_text<'a>(
    node: &'a comrak::nodes::AstNode<'a>,
    mut style: [bool; 4],
    mut link: Option<String>,
    runs: &mut Vec<Value>,
) {
    use comrak::nodes::NodeValue;
    match &node.data.borrow().value {
        NodeValue::Text(text) => return push_rich_text(runs, text, style, link.as_deref()),
        NodeValue::Code(code) => {
            style[3] = true;
            return push_rich_text(runs, &code.literal, style, link.as_deref());
        }
        NodeValue::SoftBreak | NodeValue::LineBreak => return push_rich_text(runs, "\n", style, None),
        NodeValue::Strong => style[0] = true,
        NodeValue::Emph => style[1] = true,
        NodeValue::Strikethrough => style[2] = true,
        NodeValue::Link(target) => link = Some(target.url.replace(' ', "%20")),
        _ => {}
    }
    for child in node.children() {
        collect_rich_text(child, style, link.clone(), runs);
    }
}

/// Notion caps one rich-text run at 2000 characters, so longer text is split.
fn push_rich_text(runs: &mut Vec<Value>, text: &str, style: [bool; 4], link: Option<&str>) {
    let characters = text.chars().collect::<Vec<_>>();
    for chunk in characters.chunks(2000) {
        let content = chunk.iter().collect::<String>();
        runs.push(json!({
            "type": "text",
            "text": { "content": content, "link": link.map(|url| json!({ "url": url })) },
            "annotations": { "bold": style[0], "italic": style[1], "strikethrough": style[2], "code": style[3] }
        }));
    }
}

/// A file name Notion will accept: letters, digits, dot, dash and underscore.
fn upload_file_name(name: &str) -> String {
    let clean = name
        .chars()
        .filter(|character| character.is_ascii_alphanumeric() || matches!(character, '.' | '-' | '_'))
        .take(100)
        .collect::<String>();
    if clean.trim_matches('.').is_empty() { "screenshot.png".to_string() } else { clean }
}

fn now_ms() -> i64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|duration| duration.as_millis().min(i64::MAX as u128) as i64)
        .unwrap_or_default()
}

#[cfg(test)]
mod tests {
    #[test]
    fn markdown_rich_text_round_trips_the_task_page_formatting() {
        let runs = super::markdown_rich_text(
            "Plain **bold** *italic* ~~gone~~ `code` [link](<https://example.com/a b>) \\*star\\*",
        );
        let pieces = runs
            .iter()
            .map(|run| {
                let flags = ["bold", "italic", "strikethrough", "code"]
                    .iter()
                    .filter(|flag| run["annotations"][**flag] == true)
                    .copied()
                    .collect::<Vec<_>>()
                    .join("+");
                (run["text"]["content"].as_str().unwrap().to_string(), flags, run["text"]["link"]["url"].as_str().map(str::to_string))
            })
            .collect::<Vec<_>>();
        assert!(pieces.contains(&("bold".into(), "bold".into(), None)));
        assert!(pieces.contains(&("italic".into(), "italic".into(), None)));
        assert!(pieces.contains(&("gone".into(), "strikethrough".into(), None)));
        assert!(pieces.contains(&("code".into(), "code".into(), None)));
        assert!(pieces.contains(&("link".into(), String::new(), Some("https://example.com/a%20b".into()))));
        let text = pieces.iter().map(|piece| piece.0.as_str()).collect::<String>();
        assert!(text.ends_with(" *star*"), "{text}");
    }

    #[test]
    fn markdown_rich_text_splits_long_runs_and_keeps_paragraph_breaks() {
        let runs = super::markdown_rich_text(&format!("{}\n\nSecond", "a".repeat(2500)));
        let contents = runs.iter().map(|run| run["text"]["content"].as_str().unwrap().chars().count()).collect::<Vec<_>>();
        assert_eq!(contents, vec![2000, 500, 1, 6]);
    }

    #[test]
    fn property_update_payload_edits_title_and_priority_only() {
        assert_eq!(
            super::property_update_payload("title", "New title").unwrap()["properties"]["title"]["title"][0]["text"]["content"],
            "New title"
        );
        assert_eq!(
            super::property_update_payload("Priority", "High").unwrap(),
            serde_json::json!({ "properties": { "Priority": { "select": { "name": "High" } } } })
        );
        assert!(super::property_update_payload("Status", "Done").is_err());
        assert!(super::property_update_payload("title", "").is_err());
    }

    #[test]
    fn block_update_payload_edits_text_blocks_and_to_do_checks() {
        assert_eq!(
            super::block_update_payload("heading_2", Some("Plan"), None).unwrap()["heading_2"]["rich_text"][0]["text"]["content"],
            "Plan"
        );
        assert_eq!(
            super::block_update_payload("to_do", None, Some(true)).unwrap(),
            serde_json::json!({ "to_do": { "checked": true } })
        );
        assert!(super::block_update_payload("code", Some("x"), None).is_err());
        assert!(super::block_update_payload("paragraph", None, Some(true)).is_err());
    }

    #[test]
    fn upload_file_name_keeps_safe_characters_only() {
        assert_eq!(super::upload_file_name("Screen Shot 2026-10-11 at 1.png"), "ScreenShot2026-10-11at1.png");
        assert_eq!(super::upload_file_name("\"/../"), "screenshot.png");
    }

    use super::*;
    include!("notion_tasks/tests.rs");
    #[test]
    fn discovers_the_named_tasks_data_source() {
        let payload = json!({
            "results": [
                {
                    "object": "data_source",
                    "id": "notes-id",
                    "title": [{ "plain_text": "Notes" }],
                    "properties": { "Name": { "type": "title" } }
                },
                {
                    "object": "data_source",
                    "id": "tasks-id",
                    "title": [{ "plain_text": "Tasks" }],
                    "properties": {
                        "Name": { "type": "title" },
                        "Status": { "type": "status" }
                    }
                }
            ]
        });
        assert_eq!(task_data_source_id(&payload).unwrap(), "tasks-id");
    }

    #[test]
    fn requires_a_choice_when_multiple_task_sources_match() {
        let results = ["Alpha", "Beta"]
            .into_iter()
            .map(|title| {
                json!({
                "object": "data_source",
                "id": format!("{title}-id"),
                "title": [{ "plain_text": title }],
                "properties": {
                    "Name": { "type": "title" },
                    "Status": { "type": "status" }
                }
                })
            })
            .collect::<Vec<_>>();
        let payload = json!({ "results": results });
        assert!(task_data_source_id(&payload)
            .unwrap_err()
            .contains("More than one"));
    }

    #[test]
    fn task_page_projects_only_the_fields_the_panel_uses() {
        let page = json!({
            "id": "task-1",
            "url": "https://www.notion.so/task-1",
            "properties": {
                "Name": { "title": [{ "plain_text": "[TSK-808] Workbench" }] },
                "Project": { "relation": [{ "id": "project-1" }] },
                "Status": { "select": { "name": "Doing" } },
                "Priority": { "select": { "name": "High" } },
                "Assignee": { "people": [{ "name": "Codex" }] },
                "Due": { "date": { "start": "2026-08-25" } }
            }
        });
        let task = finish_task_projection(
            pending_task_projection(&page, 42).unwrap(),
            &HashMap::from([("project-1".to_string(), "MacCommandBar".to_string())]),
        );

        assert_eq!(task.title, "[TSK-808] Workbench");
        assert_eq!(task.project, "MacCommandBar");
        assert_eq!(task.status, "Doing");
        assert_eq!(task.assignee.as_deref(), Some("Codex"));
        assert_eq!(task.fetched_at_ms, 42);
    }

    #[test]
    fn task_without_project_does_not_invent_an_assignment() {
        let page = json!({
            "id": "task-1",
            "url": "https://www.notion.so/task-1",
            "properties": {
                "Name": { "title": [{ "plain_text": "[TSK-808] Workbench" }] },
                "Status": { "select": { "name": "Doing" } }
            }
        });
        let task =
            finish_task_projection(pending_task_projection(&page, 42).unwrap(), &HashMap::new());

        assert!(task.project.is_empty());
    }

    #[test]
    fn detail_rich_text_keeps_annotations_and_mentions() {
        let value = json!([
            { "plain_text": "Bold", "annotations": { "bold": true } },
            { "plain_text": "code", "annotations": { "code": true } },
            { "plain_text": "Link", "href": "https://example.com" },
            { "plain_text": "Page", "mention": { "type": "page", "page": { "id": "ab-cd" } } }
        ]);
        assert_eq!(
            rich_text_markdown(&value),
            "**Bold**` code `[Link](<https://example.com>)[Page](<https://www.notion.so/abcd>)"
        );
        assert_eq!(rich_text(&value).as_deref(), Some("BoldcodeLinkPage"));
    }

    #[test]
    fn detail_literal_markdown_syntax_stays_text() {
        let text = "`code` ~~strike~~\n# heading\n- item\n+ item\n1. item\n2) item\n> quote\n---\n===\n| cell |";
        assert_eq!(rich_text_markdown(&json!([{ "plain_text": text }])),
            "\\`code\\` \\~\\~strike\\~\\~\n\\# heading\n\\- item\n\\+ item\n1\\. item\n2\\) item\n\\> quote\n\\---\n\\===\n\\| cell \\|");
    }

    #[test]
    fn detail_adjacent_matching_annotations_use_continuous_delimiters() {
        for (annotation, expected) in [
            ("bold", "**onetwo**"),
            ("italic", "*onetwo*"),
            ("strikethrough", "~~onetwo~~"),
            ("code", "` onetwo `"),
        ] {
            let spans = json!([
                { "plain_text": "one", "annotations": { annotation: true, "color": "red" } },
                { "plain_text": "two", "annotations": { annotation: true, "color": "blue" } }
            ]);
            assert_eq!(rich_text_markdown(&spans), expected);
        }
    }

    #[test]
    fn detail_link_and_image_destinations_preserve_sensitive_characters() {
        let url = "https://example.com/a b)c<d>.png";
        let destination = "<https://example.com/a b)c\\<d\\>.png>";
        assert_eq!(
            rich_text_markdown(&json!([{ "plain_text": "Link", "href": url }])),
            format!("[Link]({destination})")
        );
        let image = json!({ "id": "image", "type": "image", "image": {
            "type": "external", "external": { "url": url }, "caption": [{ "plain_text": "Caption" }]
        } });
        assert_eq!(
            notion_block(&image).markdown,
            format!("![Caption]({destination})")
        );
    }

    #[test]
    fn detail_table_cell_newlines_use_plain_spaces() {
        let table = json!({ "type": "table", "children": [
            { "type": "table_row", "table_row": { "cells": [[{ "plain_text": "First\nSecond | Third" }]] } }
        ] });
        assert_eq!(
            notion_block(&table).markdown,
            "| First Second \\| Third |\n| --- |"
        );
    }

    #[test]
    fn detail_body_and_comment_results_must_be_arrays() {
        for payload in [
            json!({}),
            json!({ "results": null }),
            json!({ "results": {} }),
            json!({ "results": "invalid" }),
        ] {
            assert_eq!(
                response_results(&payload).unwrap_err(),
                "Notion returned invalid results: expected an array"
            );
        }
        assert!(response_results(&json!({ "results": [] }))
            .unwrap()
            .is_empty());
    }

    fn text_block(kind: &str, text: &str) -> Value {
        json!({ "id": format!("{kind}-{text}"), "type": kind, kind: { "rich_text": [{ "plain_text": text }] } })
    }

    #[test]
    fn detail_paragraph_keeps_id_kind_and_own_markdown() {
        let mut paragraph = text_block("paragraph", "Text");
        paragraph["paragraph"]["rich_text"][0]["annotations"] = json!({ "bold": true });
        assert_eq!(
            serde_json::to_value(notion_block(&paragraph)).unwrap(),
            json!({
                "id": "paragraph-Text", "kind": "paragraph", "markdown": "**Text**",
                "checked": null, "icon": null, "children": []
            })
        );
    }

    #[test]
    fn detail_heading_1_has_no_marker() {
        assert_eq!(
            notion_block(&text_block("heading_1", "Title")).markdown,
            "Title"
        );
    }

    #[test]
    fn detail_heading_2_has_no_marker() {
        assert_eq!(
            notion_block(&text_block("heading_2", "Title")).markdown,
            "Title"
        );
    }

    #[test]
    fn detail_heading_3_has_no_marker() {
        assert_eq!(
            notion_block(&text_block("heading_3", "Title")).markdown,
            "Title"
        );
    }

    #[test]
    fn detail_bulleted_list_item_has_no_marker() {
        assert_eq!(
            notion_block(&text_block("bulleted_list_item", "Item")).markdown,
            "Item"
        );
    }

    #[test]
    fn detail_numbered_list_item_has_no_marker() {
        assert_eq!(
            notion_block(&text_block("numbered_list_item", "Item")).markdown,
            "Item"
        );
    }

    #[test]
    fn detail_to_do_keeps_check_state_without_marker() {
        let mut block = text_block("to_do", "Verify");
        assert_eq!(notion_block(&block).checked, Some(false));
        block["to_do"]["checked"] = json!(true);
        let todo = notion_block(&block);
        assert_eq!(todo.checked, Some(true));
        assert_eq!(todo.markdown, "Verify");
    }

    #[test]
    fn detail_quote_has_no_marker() {
        assert_eq!(
            notion_block(&text_block("quote", "First\nSecond")).markdown,
            "First\nSecond"
        );
    }

    #[test]
    fn detail_divider_has_empty_markdown() {
        let divider = notion_block(&text_block("divider", ""));
        assert_eq!(divider.kind, "divider");
        assert!(divider.markdown.is_empty());
    }

    #[test]
    fn detail_code_keeps_fence_and_maps_language() {
        let mut code = text_block("code", "int x;");
        code["code"]["language"] = json!("c#");
        assert_eq!(notion_block(&code).markdown, "```csharp\nint x;\n```");
        assert_eq!(code_language("c++"), "cpp");
    }

    #[test]
    fn detail_image_keeps_caption_and_url() {
        let image = json!({ "id": "image-id", "type": "image", "image": {
            "type": "file", "file": { "url": "https://files.example/image.png" },
            "caption": [{ "plain_text": "Caption" }]
        } });
        let block = notion_block(&image);
        assert_eq!(block.id, "image-id");
        assert_eq!(
            block.markdown,
            "![Caption](<https://files.example/image.png>)"
        );
    }

    #[test]
    fn detail_table_keeps_markdown_and_row_ids() {
        let table = json!({ "id": "table-id", "type": "table", "table": {}, "children": [
            { "id": "row-1", "type": "table_row", "table_row": { "cells": [[{ "plain_text": "Name" }], [{ "plain_text": "Value" }]] } },
            { "id": "row-2", "type": "table_row", "table_row": { "cells": [[{ "plain_text": "A" }], [{ "plain_text": "B" }]] } }
        ] });
        let block = notion_block(&table);
        assert_eq!(block.markdown, "| Name | Value |\n| --- | --- |\n| A | B |");
        assert_eq!(
            block
                .children
                .iter()
                .map(|child| child.id.as_str())
                .collect::<Vec<_>>(),
            ["row-1", "row-2"]
        );
    }

    #[test]
    fn detail_toggle_keeps_own_text_and_child() {
        let mut toggle = text_block("toggle", "Summary");
        toggle["children"] = json!([text_block("paragraph", "Inside")]);
        let block = notion_block(&toggle);
        assert_eq!(block.markdown, "Summary");
        assert_eq!(block.children.len(), 1);
        assert_eq!(block.children[0].markdown, "Inside");
    }

    #[test]
    fn detail_callout_keeps_own_text_icon_and_child() {
        let mut callout = text_block("callout", "First");
        assert_eq!(notion_block(&callout).icon, None);
        callout["callout"]["icon"] = json!({ "emoji": "!" });
        callout["children"] = json!([text_block("paragraph", "Second")]);
        let block = notion_block(&callout);
        assert_eq!(block.icon.as_deref(), Some("!"));
        assert_eq!(block.markdown, "First");
        assert_eq!(block.children.len(), 1);
        assert_eq!(block.children[0].markdown, "Second");
    }

    #[test]
    fn detail_nested_children_and_adjacent_blocks_stay_separate() {
        let mut list = text_block("bulleted_list_item", "Parent");
        let mut child = text_block("numbered_list_item", "Child");
        child["children"] = json!([text_block("paragraph", "Grandchild")]);
        list["children"] = json!([child]);
        let blocks = [list, text_block("paragraph", "After")]
            .iter()
            .map(notion_block)
            .collect::<Vec<_>>();
        assert_eq!(blocks.len(), 2);
        assert_eq!(blocks[0].id, "bulleted_list_item-Parent");
        assert_eq!(blocks[0].markdown, "Parent");
        assert_eq!(blocks[0].children[0].id, "numbered_list_item-Child");
        assert_eq!(blocks[0].children[0].children[0].id, "paragraph-Grandchild");
        assert_eq!(blocks[1].markdown, "After");
    }

    #[test]
    fn detail_other_blocks_keep_raw_kind() {
        let block = notion_block(&text_block("unsupported", "Text"));
        assert_eq!(block.kind, "unsupported");
        assert_eq!(block.markdown, "Text");
    }

    #[test]
    fn detail_properties_format_status_date_relation_and_unique_id() {
        let page = json!({ "properties": {
            "Title": { "type": "title", "title": [] },
            "Status": { "type": "status", "status": { "name": "Doing" } },
            "Due": { "type": "date", "date": { "start": "2026-10-11", "end": "2026-10-12" } },
            "Project": { "type": "relation", "relation": [{ "id": "project" }] },
            "ID": { "type": "unique_id", "unique_id": { "prefix": "TSK", "number": 1400 } },
            "Created": { "type": "created_time", "created_time": "2026-10-11T00:00:00Z" }
        } });
        let rows = task_properties(
            &page,
            &HashMap::from([(
                "project".into(),
                ("Assembly".into(), "https://www.notion.so/project".into()),
            )]),
        );
        assert_eq!(
            rows.iter().map(|row| row.name.as_str()).collect::<Vec<_>>(),
            ["Due", "ID", "Project", "Status", "Created"]
        );
        assert_eq!(rows[0].values, ["2026-10-11", "2026-10-12"]);
        assert_eq!(rows[1].values, ["TSK-1400"]);
        assert_eq!(rows[2].values, ["Assembly"]);
        assert_eq!(
            rows[2].links,
            [Some("https://www.notion.so/project".into())]
        );
        assert_eq!(rows[3].kind, "status");
        assert_eq!(rows[3].values, ["Doing"]);
    }

    #[test]
    fn comments_access_denied_is_optional_and_failed_user_is_named() {
        assert!(forbidden_comments(reqwest::StatusCode::FORBIDDEN)
            .unwrap()
            .is_empty());
        assert!(forbidden_comments(reqwest::StatusCode::OK).is_none());
        assert_eq!(comment_author(None), "Notion user");
        assert_eq!(comment_author(Some(&json!({}))), "Notion user");
        assert_eq!(comment_author(Some(&json!({ "name": "Sam" }))), "Sam");
    }

    #[test]
    fn task_status_update_changes_only_the_status_property() {
        assert_eq!(
            status_update_payload("Doing"),
            json!({ "properties": { "Status": { "select": { "name": "Doing" } } } })
        );
    }
}
