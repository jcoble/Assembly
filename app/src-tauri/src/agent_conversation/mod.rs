//! Synchronous Tauri commands run on the UI thread, so storage-backed commands are async to keep the interface responsive.

pub mod agui; pub mod agui_projection; mod attachments;
mod broker_status;
pub mod capabilities;
pub mod handoff;
pub mod manager;
pub mod messaging;
pub(crate) mod normalization;
mod phone;
pub mod prompt_content;
pub mod protocol;
pub mod providers;
pub mod reaper;
pub mod remote;
pub mod safe_markdown;
pub mod terminal_projection;
pub mod history;
use manager::AgentRuntimeManager;
use mcb_core::session_store::AnnotationRow;
use prompt_content::prompt_from_blocks;
use protocol::{
    AgentCapabilities, AgentConversationConfigState, AgentConversationConnection,
    AgentConversationSendReceipt,
    AgentConversationProvider, ExecutionEnvironment,
    AgentConversationEvent, AgentConversationEventPage, AgentConversationSessionRecord,
    AgentConversationItemPage, AgentConversationSelectionSnapshot,
    ChangeAgentConversationCheckoutRequest, CommandResult,
    EnsureAgentConversationRequest, RespondAgentConversationApprovalRequest,
    RespondAgentConversationInputRequest, RespondAgentConversationPermissionRequest,
    SendAgentConversationMessageRequest, SetAgentConversationConfigRequest,
    StopAgentConversationTurnRequest, UpdateAgentConversationSessionMetaRequest,
};
use remote::RemoteConnectionManager;
use std::collections::HashMap;
use std::sync::{Mutex, OnceLock};

static ATTACHMENT_DOWNLOADS: tokio::sync::Semaphore = tokio::sync::Semaphore::const_new(3);
static ORIGINAL_READS: OnceLock<Mutex<HashMap<String, bool>>> = OnceLock::new();

fn original_reads() -> &'static Mutex<HashMap<String, bool>> {
    ORIGINAL_READS.get_or_init(|| Mutex::new(HashMap::new()))
}

#[derive(serde::Serialize)]
#[serde(rename_all = "camelCase")]
pub struct AgentConversationSessionAnnotation {
    id: i64,
    owned_id: String,
    url: String,
    rect_json: String,
    note: String,
    created_at_ms: i64,
}

impl From<AnnotationRow> for AgentConversationSessionAnnotation {
    fn from(row: AnnotationRow) -> Self {
        Self {
            id: row.id,
            owned_id: row.owned_id,
            url: row.url,
            rect_json: row.rect_json,
            note: row.note,
            created_at_ms: row.created_at_ms,
        }
    }
}

#[tauri::command]
/// Adds one annotation and converts storage failures at the native boundary.
pub async fn agent_conversation_add_session_annotation(
    manager: tauri::State<'_, AgentRuntimeManager>,
    owned_id: String,
    url: String,
    rect_json: String,
    note: String,
) -> CommandResult<AgentConversationSessionAnnotation> {
    command_result(
        manager
            .add_session_annotation(&owned_id, &url, &rect_json, &note)
            .map(Into::into),
    )
}

#[tauri::command]
/// Lists annotations for one conversation with the shared command error shape.
pub async fn agent_conversation_list_session_annotations(
    manager: tauri::State<'_, AgentRuntimeManager>,
    owned_id: String,
) -> CommandResult<Vec<AgentConversationSessionAnnotation>> {
    command_result(
        manager
            .list_session_annotations(&owned_id)
            .map(|annotations| annotations.into_iter().map(Into::into).collect()),
    )
}

#[tauri::command]
/// Deletes one annotation and converts storage failures at the native boundary.
pub async fn agent_conversation_delete_session_annotation(
    manager: tauri::State<'_, AgentRuntimeManager>,
    id: i64,
) -> CommandResult<()> {
    command_result(manager.delete_session_annotation(id))
}

#[tauri::command]
/// Ensures a conversation runtime and returns typed failures to the frontend.
pub async fn ensure_agent_conversation(
    manager: tauri::State<'_, AgentRuntimeManager>,
    remote: tauri::State<'_, RemoteConnectionManager>,
    request: EnsureAgentConversationRequest,
) -> CommandResult<AgentConversationConnection> {
    let owned_id = request.owned_id.clone();
    if request.execution_environment == protocol::ExecutionEnvironment::Remote {
        return command_result(remote.ensure(request).await);
    }
    let ensured = log_command_error(
        "ensure_agent_conversation",
        &owned_id,
        manager.ensure_async(request).await,
    )?;

    // Ensuring does not start an adapter, which is why a resumed conversation
    // opens on "Agent settings unavailable": the model list, the effort levels
    // and the command roster all come from a handshake, and the adapter is
    // otherwise started by a turn.
    //
    // Starting one here was tried and taken out again. Suspending afterwards
    // does not stop the process — the pool detaches the session and keeps the
    // adapter warm for the next turn — and the config it fetched did not
    // survive into the next ensure, so the "have we got models yet" test stayed
    // false and every open started another one. Several adapters were left
    // running, hundreds of megabytes each.
    //
    // Whatever replaces it has to keep the config it fetched, and has to stop
    // the process rather than suspend it.
    Ok(ensured)
}

#[tauri::command]
/// Sends one structured message and returns typed failures without logging its content.
pub async fn send_agent_conversation_message(
    manager: tauri::State<'_, AgentRuntimeManager>,
    remote: tauri::State<'_, RemoteConnectionManager>,
    request: SendAgentConversationMessageRequest,
) -> CommandResult<AgentConversationSendReceipt> {
    let owned_id = request.owned_id.clone();
    if remote.owns(&owned_id) {
        return command_result(remote.send(request).await);
    }
    let result = async {
        let mut prompt = prompt_from_blocks(request.text.trim(), request.content)?;
        // The ids come from the composer, not from the image bytes, so they are
        // attached here rather than inside the content block conversion.
        prompt.attachment_ids = request.attachment_ids;
        manager
            .send_message(
                &request.owned_id,
                request.generation,
                prompt,
                request.model,
                request.approval_policy,
            )
            .await
    }
    .await;
    log_command_error("send_agent_conversation_message", &owned_id, result)
}

#[tauri::command]
/// Persists one draft and converts storage failures at the native boundary.
pub async fn agent_conversation_set_session_draft(
    manager: tauri::State<'_, AgentRuntimeManager>,
    remote: tauri::State<'_, RemoteConnectionManager>,
    owned_id: String,
    text: String,
) -> CommandResult<()> {
    if remote.owns(&owned_id) {
        return command_result(remote.set_draft(owned_id, text).await);
    }
    command_result(manager.set_session_draft(&owned_id, &text))
}

#[tauri::command]
/// Reads one persisted draft with the shared command error shape.
pub async fn agent_conversation_get_session_draft(
    manager: tauri::State<'_, AgentRuntimeManager>,
    remote: tauri::State<'_, RemoteConnectionManager>,
    owned_id: String,
) -> CommandResult<Option<String>> {
    if remote.owns(&owned_id) {
        return command_result(remote.get_draft(owned_id).await);
    }
    command_result(manager.get_session_draft(&owned_id))
}

#[tauri::command]
/// Clears one persisted draft and converts storage failures at the native boundary.
pub async fn agent_conversation_clear_session_draft(
    manager: tauri::State<'_, AgentRuntimeManager>,
    remote: tauri::State<'_, RemoteConnectionManager>,
    owned_id: String,
) -> CommandResult<()> {
    if remote.owns(&owned_id) {
        return command_result(remote.clear_draft(owned_id).await);
    }
    command_result(manager.clear_session_draft(&owned_id))
}

#[tauri::command]
pub async fn write_agent_conversation_workspace(
    manager: tauri::State<'_, AgentRuntimeManager>,
    remote: tauri::State<'_, RemoteConnectionManager>,
    owned_id: String,
    snapshot_json: String,
) -> CommandResult<()> {
    if remote.owns(&owned_id) {
        return command_result(remote.write_workspace(owned_id, snapshot_json).await);
    }
    command_result(manager.write_workspace(&owned_id, &snapshot_json))
}

#[tauri::command]
pub async fn read_agent_conversation_workspace(
    manager: tauri::State<'_, AgentRuntimeManager>,
    remote: tauri::State<'_, RemoteConnectionManager>,
    owned_id: String,
) -> CommandResult<Option<String>> {
    if remote.owns(&owned_id) {
        return command_result(remote.read_workspace(owned_id).await);
    }
    command_result(manager.read_workspace(&owned_id))
}

#[tauri::command]
pub async fn read_agent_conversation_workspace_expanded_paths(
    manager: tauri::State<'_, AgentRuntimeManager>,
    remote: tauri::State<'_, RemoteConnectionManager>,
    owned_id: String,
    root: String,
    request_id: u64,
) -> CommandResult<Vec<String>> {
    if remote.owns(&owned_id) {
        return command_result(remote.read_expanded_paths(owned_id, root, request_id).await);
    }
    command_result(manager.read_workspace_expanded_paths(&owned_id, &root))
}

#[tauri::command]
pub async fn write_agent_conversation_workspace_expanded_paths(
    manager: tauri::State<'_, AgentRuntimeManager>,
    remote: tauri::State<'_, RemoteConnectionManager>,
    owned_id: String,
    root: String,
    paths: Vec<String>,
) -> CommandResult<()> {
    if remote.owns(&owned_id) {
        return command_result(remote.write_expanded_paths(owned_id, root, paths).await);
    }
    command_result(manager.write_workspace_expanded_paths(&owned_id, &root, &paths))
}

#[tauri::command]
pub async fn delete_agent_conversation_workspace(
    manager: tauri::State<'_, AgentRuntimeManager>,
    remote: tauri::State<'_, RemoteConnectionManager>,
    owned_id: String,
) -> CommandResult<()> {
    if remote.owns(&owned_id) {
        return command_result(remote.delete_workspace(owned_id).await);
    }
    command_result(manager.delete_workspace(&owned_id))
}

#[tauri::command]
pub async fn clear_agent_conversation_workspace_editors(
    manager: tauri::State<'_, AgentRuntimeManager>,
) -> CommandResult<()> {
    command_result(manager.clear_workspace_editors())
}

#[tauri::command]
pub async fn clear_agent_conversation_workspace_tabs(
    manager: tauri::State<'_, AgentRuntimeManager>,
) -> CommandResult<()> {
    command_result(manager.clear_workspace_tabs())
}

#[tauri::command]
pub async fn write_assembly_setting(
    manager: tauri::State<'_, AgentRuntimeManager>,
    setting_key: String,
    value_json: String,
) -> CommandResult<()> {
    command_result(manager.write_app_setting(&setting_key, &value_json))
}

fn project_record(row: mcb_core::session_store::ProjectRow) -> protocol::ProjectRecord {
    protocol::ProjectRecord {
        id: row.id,
        machine: row.machine,
        root_path: row.root_path,
        title: row.title,
        repo_key: row.repo_key,
        created_at_ms: row.created_at_ms,
        group_key: row.group_key,
        pinned_at_ms: row.pinned_at_ms,
        last_used_ms: row.last_used_ms,
        already_existed: false,
    }
}

/// Applies saved desktop assignments and groups in two batched SQL reads.
/// Remote records join this desktop's projects for their machine.
pub(crate) fn attach_project_groups(
    store: &mcb_core::session_store::SessionStore,
    sessions: &mut [AgentConversationSessionRecord],
) -> Result<(), String> {
    manager::session_project::apply_saved_projects(store, sessions)?;
    let pairs: Vec<(&str, Option<&str>)> = sessions
        .iter()
        .map(|session| (session.owned_id.as_str(), session.project_id.as_deref()))
        .collect();
    let pairs_json = serde_json::to_string(&pairs).map_err(|error| error.to_string())?;
    let groups = store.project_groups(&pairs_json).map_err(|error| error.to_string())?;
    // One row per pair, in input order.
    for (session, (_, key, label)) in sessions.iter_mut().zip(groups) {
        session.project_group_key = key;
        session.project_group_label = label;
    }
    Ok(())
}

#[tauri::command]
pub async fn list_projects(
    manager: tauri::State<'_, AgentRuntimeManager>,
) -> Result<Vec<protocol::ProjectRecord>, String> {
    Ok(manager.list_projects()?.into_iter().map(project_record).collect())
}

/// Inspects `path` on the machine that owns it: here, or through the remote's
/// `inspect_project_folder` operation.
async fn inspect_folder_on(
    remote: &RemoteConnectionManager,
    machine: &str,
    path: String,
) -> Result<crate::project_folders::ProjectFolderInspection, String> {
    if machine == "local" {
        return tauri::async_runtime::spawn_blocking(move || crate::project_folders::inspect_project_folder_sync(&path))
            .await
            .map_err(|error| error.to_string())?;
    }
    let value = remote
        .workspace_operation(machine, "inspect_project_folder".into(), serde_json::json!({ "path": path }), None)
        .await?;
    serde_json::from_value(value).map_err(|error| error.to_string())
}

/// How a folder made by `add_project` starts: `"git"` or `{ "clone": "<url or path>" }`.
#[derive(serde::Deserialize)]
#[serde(rename_all = "camelCase")]
pub enum ProjectStart {
    Git,
    Clone(String),
}

#[tauri::command]
/// Inspects the folder on the machine that owns it, then registers it. Adding
/// the same folder again returns the project already registered, marked
/// `alreadyExisted`. With `create`
/// it first makes the folder (one level) on that machine, after
/// checking the folder it goes in so a refusal leaves nothing behind. `start`
/// then runs `git init` in it, or has `git clone` make it instead.
pub async fn add_project(
    manager: tauri::State<'_, AgentRuntimeManager>,
    remote: tauri::State<'_, RemoteConnectionManager>,
    machine: String,
    path: String,
    create: bool,
    start: Option<ProjectStart>,
) -> Result<protocol::ProjectRecord, String> {
    if start.is_some() && !create {
        return Err("Only a new folder can start with git or a copy of a repository.".into());
    }
    if create {
        let parent = std::path::Path::new(&path)
            .parent()
            .filter(|_| path.starts_with('/'))
            .ok_or_else(|| "Folder path must be absolute".to_string())?;
        let parent = inspect_folder_on(&remote, &machine, parent.to_string_lossy().into_owned()).await?;
        crate::project_folders::refuse_repository_parent(&parent)?;
        let local = machine == "local";
        let folder = std::path::PathBuf::from(&path);
        if let Some(ProjectStart::Clone(source)) = start {
            if local {
                tauri::async_runtime::spawn_blocking(move || crate::project_folders::git_clone(&source, &folder))
                    .await
                    .map_err(|error| error.to_string())??;
            } else {
                let args = serde_json::json!({ "url": source, "path": path });
                remote.workspace_operation(&machine, "workspace_git_clone".into(), args, None).await?;
            }
        } else {
            let made = if local {
                std::fs::create_dir(&path).map_err(|error| error.to_string())
            } else {
                remote
                    .workspace_operation(&machine, "workspace_mkdir".into(), serde_json::json!({ "path": path }), None)
                    .await
                    .map(|_| ())
            };
            made.map_err(|error| format!("The folder could not be created: {error}"))?;
            if start.is_some() && local {
                tauri::async_runtime::spawn_blocking(move || crate::project_folders::git_init(&folder))
                    .await
                    .map_err(|error| error.to_string())??;
            } else if start.is_some() {
                remote.workspace_operation(&machine, "workspace_git_init".into(), serde_json::json!({ "path": path }), None).await?;
            }
        }
    }
    let inspection = inspect_folder_on(&remote, &machine, path).await?;
    let row = crate::project_folders::new_project_row(machine, inspection);
    let (project, already_existed) = manager.add_project(row)?;
    if project.machine == "local" {
        // A new local project files the older sessions in its folders now, so
        // they regroup without a restart (amendment A1: the project set changed).
        let store = manager.store_handle();
        tauri::async_runtime::spawn_blocking(move || crate::project_folders::backfill_local_session_projects(&store))
            .await
            .map_err(|error| error.to_string())??;
    }
    Ok(protocol::ProjectRecord { already_existed, ..project_record(project) })
}

const PROJECT_GONE: &str = "That project no longer exists.";

#[tauri::command]
/// Changes the project's title only.
pub async fn rename_project(manager: tauri::State<'_, AgentRuntimeManager>, id: String, name: String) -> Result<(), String> {
    let name = name.trim();
    if name.is_empty() {
        return Err("A project needs a name.".into());
    }
    match manager.store().rename_project(&id, name).map_err(|error| error.to_string())? {
        true => Ok(()),
        false => Err(PROJECT_GONE.into()),
    }
}

#[tauri::command]
/// Moves the project to another folder on the same machine, after the same
/// checks as adding one. Its sessions keep the folders they were started in.
pub async fn set_project_root(
    manager: tauri::State<'_, AgentRuntimeManager>,
    remote: tauri::State<'_, RemoteConnectionManager>,
    id: String,
    root: String,
) -> Result<(), String> {
    let project = manager.store().get_project(&id).map_err(|error| error.to_string())?.ok_or(PROJECT_GONE)?;
    let inspection = inspect_folder_on(&remote, &project.machine, root).await?;
    match manager.store().set_project_root(&id, &inspection.root_path, &inspection.repo_key).map_err(|error| error.to_string())? {
        true => Ok(()),
        false => Err("Another project already uses that folder.".into()),
    }
}

#[tauri::command]
pub async fn set_project_pinned(manager: tauri::State<'_, AgentRuntimeManager>, id: String, pinned: bool) -> Result<(), String> {
    match manager.store().set_project_pinned(&id, pinned).map_err(|error| error.to_string())? {
        true => Ok(()),
        false => Err(PROJECT_GONE.into()),
    }
}

#[tauri::command]
/// Takes the project off the list, keeping or deleting its sessions. Files stay.
/// Remote sessions are deleted on their machine first; the manager deletes the rest.
pub async fn remove_project(
    manager: tauri::State<'_, AgentRuntimeManager>,
    remote: tauri::State<'_, RemoteConnectionManager>,
    id: String,
    delete_sessions: bool,
) -> Result<(), String> {
    remove_project_on(&manager, &remote, &id, delete_sessions).await
}

async fn remove_project_on(
    manager: &AgentRuntimeManager,
    remote: &RemoteConnectionManager,
    id: &str,
    delete_sessions: bool,
) -> Result<(), String> {
    if delete_sessions {
        for owned_id in manager.store().project_session_ids(id, true).map_err(|error| error.to_string())? {
            if remote.owns(&owned_id) {
                remote.delete(owned_id).await?;
            }
        }
    }
    manager.remove_project(id, delete_sessions).await
}

#[tauri::command]
pub async fn read_assembly_setting(
    manager: tauri::State<'_, AgentRuntimeManager>,
    setting_key: String,
) -> CommandResult<Option<String>> {
    command_result(manager.read_app_setting(&setting_key))
}

#[tauri::command]
/// Records a legacy approval decision after validating its request identity.
pub async fn respond_agent_conversation_approval(
    manager: tauri::State<'_, AgentRuntimeManager>,
    remote: tauri::State<'_, RemoteConnectionManager>,
    request: RespondAgentConversationApprovalRequest,
) -> CommandResult<()> {
    let request_id = required_id(&request.request_id, "Approval request id")?;
    if remote.owns(&request.owned_id) {
        return command_result(remote.respond_approval(request).await);
    }
    command_result(
        manager
            .respond_legacy_approval(
                &request.owned_id,
                request.generation,
                request_id,
                request.decision,
            )
            .await,
    )
}

#[tauri::command]
/// Records a provider permission choice after validating its request identity.
pub async fn respond_agent_conversation_permission(
    manager: tauri::State<'_, AgentRuntimeManager>,
    remote: tauri::State<'_, RemoteConnectionManager>,
    request: RespondAgentConversationPermissionRequest,
) -> CommandResult<()> {
    let request_id = required_id(&request.request_id, "Permission request id")?;
    if remote.owns(&request.owned_id) {
        return command_result(remote.respond_permission(request).await);
    }
    command_result(
        manager
            .respond_permission_option(
                &request.owned_id,
                request.generation,
                request_id,
                request.option_id,
            )
            .await,
    )
}

#[tauri::command]
/// Records structured user input after validating its request identity.
pub async fn respond_agent_conversation_input(
    manager: tauri::State<'_, AgentRuntimeManager>,
    remote: tauri::State<'_, RemoteConnectionManager>,
    request: RespondAgentConversationInputRequest,
) -> CommandResult<()> {
    let request_id = required_id(&request.request_id, "User input request id")?;
    if remote.owns(&request.owned_id) {
        return command_result(remote.respond_input(request).await);
    }
    command_result(
        manager
            .respond_user_input(protocol::AgentUserInputResponse {
                identity: protocol::AgentRequestIdentity {
                    owned_id: request.owned_id,
                    generation: request.generation,
                    request_id,
                    turn_id: None,
                    item_id: None,
                },
                action: request.action,
                content: request.content,
            })
            .await,
    )
}

#[tauri::command]
/// Stops the active turn for the requested conversation generation.
pub async fn stop_agent_conversation_turn(
    manager: tauri::State<'_, AgentRuntimeManager>,
    remote: tauri::State<'_, RemoteConnectionManager>,
    request: StopAgentConversationTurnRequest,
) -> CommandResult<()> {
    if remote.owns(&request.owned_id) {
        return command_result(remote.stop(request).await);
    }
    command_result(
        manager
            .cancel_turn(&request.owned_id, request.generation)
            .await,
    )
}

#[tauri::command]
/// Changes a Codex session's durable checkout while it is quiescent.
pub async fn change_agent_conversation_checkout(
    manager: tauri::State<'_, AgentRuntimeManager>,
    remote: tauri::State<'_, RemoteConnectionManager>,
    request: ChangeAgentConversationCheckoutRequest,
) -> CommandResult<AgentConversationSessionRecord> {
    let owned_id = request.owned_id.clone();
    if remote.owns(&owned_id) {
        return command_result(remote.change_checkout(request).await);
    }
    log_command_error(
        "change_agent_conversation_checkout",
        &owned_id,
        manager.change_checkout(request).await,
    )
}

#[tauri::command]
/// Applies the supported conversation configuration fields for one generation.
pub async fn set_agent_conversation_config(
    manager: tauri::State<'_, AgentRuntimeManager>,
    remote: tauri::State<'_, RemoteConnectionManager>,
    request: SetAgentConversationConfigRequest,
) -> CommandResult<AgentConversationConfigState> {
    if remote.owns(&request.owned_id) {
        return command_result(remote.set_config(request).await);
    }
    command_result(manager.set_conversation_config(request).await)
}

#[derive(Clone, Debug, serde::Deserialize, serde::Serialize)]
#[serde(rename_all = "camelCase")]
pub struct SetAgentConversationConfigOptionRequest {
    owned_id: String,
    generation: u64,
    option_id: String,
    value: providers::AgentConfigValue,
}

#[derive(serde::Serialize)]
#[serde(rename_all = "camelCase")]
pub struct SetAgentConversationConfigOptionResponse {
    config_options: Vec<protocol::AgentConfigOption>,
}

#[tauri::command]
/// Applies one advertised provider option and returns the refreshed choices.
pub async fn set_agent_conversation_config_option(
    manager: tauri::State<'_, AgentRuntimeManager>,
    remote: tauri::State<'_, RemoteConnectionManager>,
    request: SetAgentConversationConfigOptionRequest,
) -> CommandResult<SetAgentConversationConfigOptionResponse> {
    if remote.owns(&request.owned_id) {
        return command_result(
            remote
                .set_config_option(request)
                .await
                .map(|config_options| SetAgentConversationConfigOptionResponse { config_options }),
        );
    }
    command_result(
        manager
            .set_config(
                &request.owned_id,
                request.generation,
                &request.option_id,
                request.value,
            )
            .await
            .map(|config_options| SetAgentConversationConfigOptionResponse { config_options }),
    )
}

#[tauri::command]
/// Asks a conversation's adapter what it offers, once, and stops it again.
///
/// For a conversation resumed from a past transcript, which has never run and so
/// has never been told what it offers. What comes back is stored, and every
/// later read goes to the database the same way an existing session's does.
pub async fn warm_agent_conversation_config(
    manager: tauri::State<'_, AgentRuntimeManager>,
    remote: tauri::State<'_, RemoteConnectionManager>,
    owned_id: String,
    generation: u64,
) -> CommandResult<AgentConversationConfigState> {
    if remote.owns(&owned_id) {
        return command_result(remote.warm_config(owned_id, generation).await);
    }
    command_result(
        manager
            .warm_conversation_config(&owned_id, generation)
            .await,
    )
}

#[tauri::command]
/// Reads one machine's provider choices without saving a conversation.
pub async fn probe_agent_provider_config(
    manager: tauri::State<'_, AgentRuntimeManager>,
    remote: tauri::State<'_, RemoteConnectionManager>,
    provider: AgentConversationProvider,
    execution_environment: ExecutionEnvironment,
    remote_profile_id: Option<String>,
    cwd: String,
    request_id: u64,
) -> CommandResult<AgentConversationConfigState> {
    if manager.provider_probe_cancelled(request_id) {
        return command_result(Err("Provider catalog request was cancelled".into()));
    }
    if execution_environment == ExecutionEnvironment::Remote {
        let Some(profile_id) = remote_profile_id.filter(|id| !id.is_empty()) else {
            return command_result(Err("Choose a connected remote machine first".into()));
        };
        let result = tokio::select! {
            biased;
            _ = manager.wait_for_probe_cancellation(request_id) => {
                remote.cancel_request(request_id).await;
                Err("Provider catalog request was cancelled".into())
            }
            result = remote.probe_provider_config(&profile_id, provider, cwd, request_id) => result,
        };
        return command_result(result);
    }
    command_result(manager.probe_provider_config_for_request(provider, &cwd, request_id).await)
}

#[tauri::command]
pub async fn cancel_agent_provider_probe(
    manager: tauri::State<'_, AgentRuntimeManager>,
    remote: tauri::State<'_, RemoteConnectionManager>,
    request_id: u64,
) -> CommandResult<()> {
    manager.cancel_provider_probe(request_id);
    remote.cancel_request(request_id).await;
    Ok(())
}

#[tauri::command]
/// Reads the current provider configuration for one conversation.
pub async fn read_agent_conversation_config(
    manager: tauri::State<'_, AgentRuntimeManager>,
    remote: tauri::State<'_, RemoteConnectionManager>,
    owned_id: String,
    request_id: u64,
) -> CommandResult<AgentConversationConfigState> {
    if remote.owns(&owned_id) {
        return command_result(remote.config(owned_id, request_id).await);
    }
    command_result(manager.conversation_config(&owned_id))
}

#[tauri::command]
/// Reads the current provider capabilities for one conversation.
pub async fn read_agent_conversation_capabilities(
    manager: tauri::State<'_, AgentRuntimeManager>,
    remote: tauri::State<'_, RemoteConnectionManager>,
    owned_id: String,
    request_id: u64,
) -> CommandResult<AgentCapabilities> {
    if remote.owns(&owned_id) {
        return command_result(remote.capabilities(owned_id, request_id).await);
    }
    command_result(manager.capabilities_for_owned_id(&owned_id))
}

#[tauri::command]
/// Closes one exact conversation generation so stale views cannot close a replacement.
pub async fn close_agent_conversation(
    manager: tauri::State<'_, AgentRuntimeManager>,
    remote: tauri::State<'_, RemoteConnectionManager>,
    owned_id: String,
    generation: u64,
) -> CommandResult<bool> {
    if remote.owns(&owned_id) {
        return command_result(remote.close(owned_id, generation).await);
    }
    command_result(manager.close(&owned_id, generation).await)
}

#[tauri::command]
/// Takes one conversation out of the store for good, stopping it first if it
/// is running. Answers whether there was anything to delete.
pub async fn delete_agent_conversation_session(
    manager: tauri::State<'_, AgentRuntimeManager>,
    remote: tauri::State<'_, RemoteConnectionManager>,
    terminal_registry: tauri::State<'_, crate::terminal::TerminalRegistry>,
    owned_id: String,
) -> CommandResult<bool> {
    if remote.owns(&owned_id) {
        return command_result(remote.delete(owned_id).await);
    }
    let result = async {
        if manager
            .store()
            .get_session(&owned_id)
            .map_err(|error| error.to_string())?
            .is_none()
        {
            return Ok(false);
        }
        // Keep the durable row reachable when a PTY refuses to die, so the
        // user can retry the deletion instead of orphaning its shell.
        terminal_registry.close_user_terminals_for_owned(&owned_id)?;
        manager.delete(&owned_id).await
    }
    .await;
    log_command_error("delete_agent_conversation_session", &owned_id, result)
}

#[tauri::command]
pub fn read_agent_conversation_history_download(
    remote: tauri::State<'_, RemoteConnectionManager>, owned_id: String,
) -> CommandResult<Option<remote::HistoryDownloadProgress>> {
    command_result(remote.history_download_progress(&owned_id))
}

#[tauri::command]
/// Reads one complete-item selection from the local journal.
pub async fn read_agent_conversation_selection(
    manager: tauri::State<'_, AgentRuntimeManager>,
    remote: tauri::State<'_, RemoteConnectionManager>,
    owned_id: String,
    request_id: u64,
    max_bytes: u32,
    minimum_generation: Option<u64>,
) -> CommandResult<Option<AgentConversationSelectionSnapshot>> {
    match manager.store().private_remote_child_source(&owned_id) {
        Ok(Some(source)) => {
            return command_result(
                remote
                    .child_selection_snapshot(
                        source.remote_profile_id,
                        source.source_owned_id,
                        request_id,
                        max_bytes,
                    )
                    .await,
            );
        }
        Ok(None) => {}
        Err(error) => return command_result(Err(error.to_string())),
    }
    if remote.owns(&owned_id) {
        return command_result(
            remote
                .selection_snapshot(owned_id, request_id, max_bytes, minimum_generation)
                .await,
        );
    }
    command_result(manager.latest_selection_snapshot(&owned_id, request_id, max_bytes))
}

#[tauri::command]
/// Cancels a frontend-owned conversation request whose owner has been released.
pub async fn cancel_agent_conversation_request(
    manager: tauri::State<'_, AgentRuntimeManager>,
    remote: tauri::State<'_, RemoteConnectionManager>,
    request_id: u64,
) -> CommandResult<()> {
    manager.cancel_snapshot(request_id);
    manager.cancel_child_history_request(request_id);
    remote.cancel_request(request_id).await;
    Ok(())
}

#[tauri::command]
/// Marks a session as the owner's main orchestrator on its machine and clears the
/// mark everywhere else; with no session, clears it everywhere.
pub async fn set_main_orchestrator(
    remote: tauri::State<'_, RemoteConnectionManager>,
    owned_id: Option<String>,
    remote_profile_id: Option<String>,
) -> CommandResult<()> {
    let machine = remote_profile_id.unwrap_or_else(|| remote::LOCAL_MACHINE.to_string());
    command_result(remote.set_main_orchestrator(&machine, owned_id.as_deref()).await)
}

#[tauri::command]
/// One ordered page of agent-to-agent messages from one machine or all reachable ones.
pub async fn list_agent_messages(
    remote: tauri::State<'_, RemoteConnectionManager>,
    machine: Option<String>,
    run_id: Option<String>,
    session: Option<String>,
    status: Option<String>,
    search: Option<String>,
    sort: Option<String>,
    before_key: Option<String>,
    before_ms: Option<i64>,
    before_id: Option<String>,
    limit: i64,
    request_id: u64,
) -> CommandResult<Vec<serde_json::Value>> {
    let args = serde_json::json!({"run_id": run_id, "session": session, "before_ms": before_ms, "before_id": before_id, "limit": limit, "status": status, "search": search, "sort": sort, "before_key": before_key});
    Ok(remote.unless_cancelled(request_id, remote.list_messages(machine.as_deref(), args, request_id)).await.unwrap_or_default())
}

#[tauri::command]
pub async fn count_agent_messages(
    remote: tauri::State<'_, RemoteConnectionManager>,
    session: String,
    after_ms: i64,
    request_id: u64,
) -> CommandResult<i64> {
    Ok(remote.unless_cancelled(request_id, remote.count_messages(&session, after_ms, request_id)).await.unwrap_or_default())
}

#[tauri::command]
/// Lists local sessions plus the compact cached references for remote sessions.
/// Remote transcripts and events remain exclusively on their remote backend.
pub async fn list_agent_conversation_sessions(
    manager: tauri::State<'_, AgentRuntimeManager>,
    remote: tauri::State<'_, RemoteConnectionManager>,
) -> CommandResult<Vec<AgentConversationSessionRecord>> {
    let mut sessions = manager
        .list_sessions()
        .map_err(protocol::CommandError::from)?;
    let cached_remote = remote.cached_sessions();
    let cached_ids = cached_remote
        .iter()
        .map(|session| session.owned_id.as_str())
        .collect::<std::collections::HashSet<_>>();
    sessions.retain(|session| !cached_ids.contains(session.owned_id.as_str()));
    sessions.extend(cached_remote);
    sessions.sort_by(|left, right| {
        right
            .last_activity_at_ms
            .cmp(&left.last_activity_at_ms)
            .then_with(|| left.owned_id.cmp(&right.owned_id))
    });
    attach_project_groups(manager.store(), &mut sessions).map_err(protocol::CommandError::from)?;
    Ok(sessions)
}

#[tauri::command]
/// Lists remote sessions separately so an unavailable machine cannot hold the local rail open.
pub async fn list_remote_agent_conversation_sessions(
    manager: tauri::State<'_, AgentRuntimeManager>,
    remote: tauri::State<'_, RemoteConnectionManager>,
    request_id: u64,
) -> CommandResult<Vec<AgentConversationSessionRecord>> {
    if !remote.is_configured() {
        return Ok(Vec::new());
    }
    let mut sessions = command_result(remote.list_sessions(request_id).await)?;
    attach_project_groups(manager.store(), &mut sessions).map_err(protocol::CommandError::from)?;
    Ok(sessions)
}

#[tauri::command]
/// Lists durable events after the requested sequence for one conversation.
pub async fn list_agent_conversation_events(
    manager: tauri::State<'_, AgentRuntimeManager>,
    owned_id: String,
    from_sequence: Option<i64>,
) -> CommandResult<Vec<AgentConversationEvent>> {
    command_result(manager.list_events(&owned_id, from_sequence.unwrap_or(0)))
}

#[tauri::command]
/// Lists the page of durable events just older than the requested sequence.
pub async fn list_agent_conversation_events_before(
    manager: tauri::State<'_, AgentRuntimeManager>,
    remote: tauri::State<'_, RemoteConnectionManager>,
    owned_id: String,
    before_sequence: i64,
    max_bytes: u32,
) -> CommandResult<AgentConversationEventPage> {
    if remote.owns(&owned_id) {
        return command_result(remote.events_before(owned_id, before_sequence, max_bytes).await);
    }
    command_result(manager.list_events_before(&owned_id, before_sequence, max_bytes))
}

#[tauri::command]
pub async fn list_agent_conversation_items_before(
    manager: tauri::State<'_, AgentRuntimeManager>,
    remote: tauri::State<'_, RemoteConnectionManager>,
    owned_id: String,
    before_sequence: i64,
    max_bytes: u32,
    request_id: u64,
) -> CommandResult<AgentConversationItemPage> {
    match manager.store().private_remote_child_source(&owned_id) {
        Ok(Some(source)) => {
            return command_result(
                remote
                    .child_item_page(
                        source.remote_profile_id,
                        source.source_owned_id,
                        before_sequence,
                        max_bytes,
                        true,
                        request_id,
                    )
                    .await,
            );
        }
        Ok(None) => {}
        Err(error) => return command_result(Err(error.to_string())),
    }
    if remote.owns(&owned_id) {
        return command_result(remote.item_page(owned_id, before_sequence, max_bytes, true, request_id).await);
    }
    command_result(manager.list_items_before(&owned_id, before_sequence, max_bytes))
}

#[tauri::command]
pub async fn list_agent_conversation_items_after(
    manager: tauri::State<'_, AgentRuntimeManager>,
    remote: tauri::State<'_, RemoteConnectionManager>,
    owned_id: String,
    after_sequence: i64,
    max_bytes: u32,
    request_id: u64,
) -> CommandResult<AgentConversationItemPage> {
    match manager.store().private_remote_child_source(&owned_id) {
        Ok(Some(source)) => {
            return command_result(
                remote
                    .child_item_page(
                        source.remote_profile_id,
                        source.source_owned_id,
                        after_sequence,
                        max_bytes,
                        false,
                        request_id,
                    )
                    .await,
            );
        }
        Ok(None) => {}
        Err(error) => return command_result(Err(error.to_string())),
    }
    if remote.owns(&owned_id) {
        return command_result(remote.item_page(owned_id, after_sequence, max_bytes, false, request_id).await);
    }
    command_result(manager.list_items_after(&owned_id, after_sequence, max_bytes))
}

#[tauri::command]
/// Updates owner-scoped conversation metadata and returns the stored record.
pub async fn update_agent_conversation_session_meta(
    manager: tauri::State<'_, AgentRuntimeManager>,
    remote: tauri::State<'_, RemoteConnectionManager>,
    request: UpdateAgentConversationSessionMetaRequest,
) -> CommandResult<AgentConversationSessionRecord> {
    if remote.owns(&request.owned_id) {
        return command_result(remote.update_meta(request).await);
    }
    command_result(manager.update_session_meta(request))
}

#[tauri::command]
/// Selects one durable child history and returns its bounded SQLite page.
pub async fn read_agent_conversation_child_history(
    manager: tauri::State<'_, AgentRuntimeManager>,
    remote: tauri::State<'_, RemoteConnectionManager>,
    parent_owned_id: String,
    child_session_id: String,
    request_id: u64,
    max_bytes: u32,
) -> CommandResult<protocol::AgentConversationChildHistorySelection> {
    if remote.owns(&parent_owned_id) {
        return command_result(
            remote
                .select_child_history(
                    parent_owned_id,
                    child_session_id,
                    request_id,
                    max_bytes,
                )
                .await,
        );
    }
    command_result(manager.select_child_history(
        &parent_owned_id,
        &child_session_id,
        request_id,
        max_bytes,
        IMPORT_MAX_BYTES,
        IMPORT_MAX_RECORDS,
    ))
}

#[tauri::command]
pub async fn stop_agent_conversation_child_history(
    manager: tauri::State<'_, AgentRuntimeManager>,
    remote: tauri::State<'_, RemoteConnectionManager>,
    parent_owned_id: String,
    request_id: u64,
) -> CommandResult<bool> {
    if remote.owns(&parent_owned_id) {
        return command_result(
            remote
                .stop_child_history(parent_owned_id, request_id)
                .await,
        );
    }
    command_result(Ok(manager.stop_child_history(&parent_owned_id, request_id)))
}

/// One import page reads at most this many transcript bytes.
/// How much of a past transcript one read pulls in — the initial import and
/// each Load More alike. Bytes bound this rather than records because a single
/// turn carrying a large tool dump can outweigh many ordinary ones.
const IMPORT_MAX_BYTES: u64 = 2 * 1024 * 1024;
/// One import page keeps at most this many transcript events.
const IMPORT_MAX_RECORDS: usize = 2_000;

#[tauri::command]
/// Names a past provider transcript as an owned conversation and returns its owned id, reading no records.
/// A remote transcript is named, and its newest page read, there. Also answers whether a session already here was read again.
pub async fn begin_agent_conversation_import(
    manager: tauri::State<'_, AgentRuntimeManager>,
    remote: tauri::State<'_, RemoteConnectionManager>,
    provider: protocol::AgentConversationProvider,
    native_session_id: String,
    transcript_path: String,
    cwd: String,
    title: Option<String>,
    remote_profile_id: Option<String>,
) -> CommandResult<(String, bool)> {
    if let Some(profile) = remote_profile_id {
        return command_result(remote.begin_import(profile, provider, native_session_id, transcript_path, cwd, title).await);
    }
    command_result(manager.begin_import_transcript_session(provider, &native_session_id, std::path::Path::new(&transcript_path), &cwd, title, None).await)
}

#[tauri::command]
/// Reads the newest page of a named import and returns how many events it gained.
pub async fn finish_agent_conversation_import(
    manager: tauri::State<'_, AgentRuntimeManager>,
    owned_id: String,
) -> CommandResult<usize> {
    command_result(manager.finish_import_transcript_session(
        &owned_id,
        IMPORT_MAX_BYTES,
        IMPORT_MAX_RECORDS,
    ))
}

#[tauri::command]
/// Adds one older page to an imported conversation and returns how many events it gained.
pub async fn extend_agent_conversation_import(
    manager: tauri::State<'_, AgentRuntimeManager>,
    remote: tauri::State<'_, RemoteConnectionManager>,
    owned_id: String,
) -> CommandResult<history::ExtendedImport> {
    match manager.store().private_remote_child_source(&owned_id) {
        Ok(Some(source)) => {
            return command_result(
                remote
                    .extend_child_import(source.remote_profile_id, source.source_owned_id)
                    .await,
            );
        }
        Ok(None) => {}
        Err(error) => return command_result(Err(error.to_string())),
    }
    if remote.owns(&owned_id) { return command_result(remote.extend_import(owned_id).await); }
    command_result(manager.extend_imported_session(&owned_id, IMPORT_MAX_BYTES, IMPORT_MAX_RECORDS))
}

#[tauri::command]
/// Starts transcript projection for one native conversation session.
pub async fn start_agent_conversation_terminal_projection(
    manager: tauri::State<'_, AgentRuntimeManager>,
    registry: tauri::State<'_, terminal_projection::TerminalProjectionRegistry>,
    request: terminal_projection::StartTerminalProjectionRequest,
) -> CommandResult<terminal_projection::TerminalProjectionRegistration> {
    command_result(registry.start(manager.inner().clone(), request))
}

#[tauri::command]
/// Stops transcript projection for one owner-scoped conversation.
pub async fn stop_agent_conversation_terminal_projection(
    registry: tauri::State<'_, terminal_projection::TerminalProjectionRegistry>,
    owned_id: String,
) -> CommandResult<bool> {
    command_result(registry.stop(&owned_id))
}

#[tauri::command]
/// Saves one validated image in the conversation attachment vault.
pub async fn save_agent_conversation_attachment(
    app: tauri::AppHandle,
    manager: tauri::State<'_, AgentRuntimeManager>,
    remote: tauri::State<'_, RemoteConnectionManager>,
    owned_id: String,
    mime_type: String,
    bytes: String,
) -> CommandResult<attachments::SavedConversationAttachment> {
    let bytes = match tokio::task::spawn_blocking(move || {
        use base64::Engine as _;
        base64::engine::general_purpose::STANDARD.decode(bytes).map_err(|error| error.to_string())
    }).await {
        Ok(result) => result.map_err(protocol::CommandError::from)?,
        Err(error) => return command_result(Err(error.to_string())),
    };
    if remote.owns(&owned_id) {
        return command_result(remote.save_attachment(owned_id, mime_type, bytes).await);
    }
    let store = manager.store_handle();
    command_result(tokio::task::spawn_blocking(move ||
        attachments::save(&app, &store, &owned_id, &mime_type, &bytes)
    ).await.map_err(|error| error.to_string())?)
}

#[tauri::command]
/// Lists validated images stored for one conversation owner.
pub async fn read_agent_conversation_attachments(
    app: tauri::AppHandle,
    manager: tauri::State<'_, AgentRuntimeManager>,
    remote: tauri::State<'_, RemoteConnectionManager>,
    owned_id: String,
) -> CommandResult<Vec<attachments::SavedConversationAttachment>> {
    if remote.owns(&owned_id) {
        return command_result(remote.read_attachments(owned_id).await);
    }
    let store = manager.store_handle();
    command_result(tokio::task::spawn_blocking(move ||
        attachments::read(&app, &store, &owned_id)
    ).await.map_err(|error| error.to_string())?)
}

#[tauri::command]
/// Lists only attachment metadata referenced by the selected item page.
pub async fn read_agent_conversation_selected_attachments(
    app: tauri::AppHandle,
    manager: tauri::State<'_, AgentRuntimeManager>,
    remote: tauri::State<'_, RemoteConnectionManager>,
    owned_id: String,
    attachment_ids: Vec<String>,
) -> CommandResult<Vec<attachments::SavedConversationAttachment>> {
    if remote.owns(&owned_id) {
        return command_result(
            remote
                .read_selected_attachments(owned_id, attachment_ids)
                .await,
        );
    }
    let store = manager.store_handle();
    command_result(
        tokio::task::spawn_blocking(move || {
            attachments::read_selected(&app, &store, &owned_id, &attachment_ids)
        })
        .await
        .map_err(|error| error.to_string())?,
    )
}

#[tauri::command]
/// Fetches a remote thumbnail once, or a clicked original for one lightbox open.
pub async fn read_agent_conversation_attachment_file(
    app: tauri::AppHandle,
    remote: tauri::State<'_, RemoteConnectionManager>,
    owned_id: String,
    attachment_id: String,
    thumbnail: bool,
    byte_length: usize,
    mime_type: String,
    transfer_id: String,
) -> CommandResult<String> {
    let owned = command_result(attachments::safe_segment(&owned_id, "Owned session id").map(str::to_owned))?;
    let id = command_result(uuid::Uuid::parse_str(&attachment_id).map_err(|_| "Attachment id is invalid".to_string()))?;
    let transfer = command_result(uuid::Uuid::parse_str(&transfer_id).map_err(|_| "Transfer id is invalid".to_string()))?;
    if byte_length == 0 || byte_length > attachments::MAX_ATTACHMENT_BYTES {
        return command_result(Err("Attachment read is outside the 20 MB limit".to_string()));
    }
    let extension = if thumbnail { "webp" } else {
        match mime_type.as_str() {
            "image/png" => "png", "image/jpeg" => "jpg", "image/gif" => "gif", "image/webp" => "webp",
            _ => return command_result(Err("Unsupported image type".to_string())),
        }
    };
    let root = command_result(attachments::vault_root(&app))?.join(owned);
    let name = if thumbnail { format!("remote-{id}-thumb.{extension}") }
        else { format!("remote-{id}-{transfer}.{extension}") };
    let path = root.join(name);
    if thumbnail && tokio::task::spawn_blocking({ let path = path.clone(); move || path.is_file() })
        .await.map_err(|error| error.to_string()).map_err(protocol::CommandError::from)? {
        return command_result(Ok(path.display().to_string()));
    }
    if !thumbnail {
        original_reads().lock().unwrap_or_else(std::sync::PoisonError::into_inner)
            .insert(transfer_id.clone(), false);
    }
    let result = async {
        let _permit = ATTACHMENT_DOWNLOADS.acquire().await.map_err(|error| error.to_string())?;
        let mut bytes = Vec::with_capacity(byte_length);
        while bytes.len() < byte_length {
            if !thumbnail && original_reads().lock().unwrap_or_else(std::sync::PoisonError::into_inner)
                .get(&transfer_id).copied() != Some(false) { return Err("Attachment read cancelled".into()); }
            let chunk = remote.read_attachment_chunk(owned_id.clone(), attachment_id.clone(), thumbnail, bytes.len() as u64).await?;
            if chunk.is_empty() || chunk.len() > 128 * 1024 || bytes.len() + chunk.len() > byte_length {
                return Err("Remote attachment read is incomplete".into());
            }
            bytes.extend_from_slice(&chunk);
        }
        let transfer_for_write = transfer_id.clone();
        tokio::task::spawn_blocking(move || {
            if thumbnail {
                std::fs::create_dir_all(&root).map_err(|error| error.to_string())?;
                let pending = root.join(format!(".remote-{id}-{transfer_for_write}.tmp"));
                std::fs::write(&pending, bytes).map_err(|error| error.to_string())?;
                if let Err(error) = std::fs::rename(&pending, &path) {
                    let _ = std::fs::remove_file(&pending);
                    return Err(error.to_string());
                }
            } else {
                let reads = original_reads();
                let active = reads.lock().unwrap_or_else(std::sync::PoisonError::into_inner);
                if active.get(&transfer_for_write).copied() != Some(false) { return Err("Attachment read cancelled".into()); }
                std::fs::create_dir_all(&root).map_err(|error| error.to_string())?;
                std::fs::write(&path, bytes).map_err(|error| error.to_string())?;
            }
            Ok::<_, String>(path.display().to_string())
        }).await.map_err(|error| error.to_string())?
    }.await;
    if !thumbnail { original_reads().lock().unwrap_or_else(std::sync::PoisonError::into_inner).remove(&transfer_id); }
    command_result(result)
}

#[tauri::command]
pub async fn discard_agent_conversation_original(
    app: tauri::AppHandle,
    owned_id: String,
    attachment_id: String,
    mime_type: String,
    transfer_id: String,
) -> CommandResult<()> {
    let owned = command_result(attachments::safe_segment(&owned_id, "Owned session id").map(str::to_owned))?;
    let id = command_result(uuid::Uuid::parse_str(&attachment_id).map_err(|_| "Attachment id is invalid".to_string()))?;
    let transfer = command_result(uuid::Uuid::parse_str(&transfer_id).map_err(|_| "Transfer id is invalid".to_string()))?;
    let extension = match mime_type.as_str() {
        "image/png" => "png", "image/jpeg" => "jpg", "image/gif" => "gif", "image/webp" => "webp",
        _ => return command_result(Err("Unsupported image type".to_string())),
    };
    let path = command_result(attachments::vault_root(&app))?.join(owned).join(format!("remote-{id}-{transfer}.{extension}"));
    command_result(tokio::task::spawn_blocking(move || {
        let mut reads = original_reads().lock().unwrap_or_else(std::sync::PoisonError::into_inner);
        if let Some(active) = reads.get_mut(&transfer_id) { *active = true; }
        match std::fs::remove_file(path) {
            Ok(()) => Ok(()),
            Err(error) if error.kind() == std::io::ErrorKind::NotFound => Ok(()),
            Err(error) => Err(error.to_string()),
        }
    }).await.map_err(|error| error.to_string())?)
}

#[tauri::command]
/// Deletes one validated image from the conversation attachment vault.
pub async fn delete_agent_conversation_attachment(
    app: tauri::AppHandle,
    manager: tauri::State<'_, AgentRuntimeManager>,
    remote: tauri::State<'_, RemoteConnectionManager>,
    request: attachments::DeleteConversationAttachmentRequest,
) -> CommandResult<()> {
    if remote.owns(&request.owned_id) {
        return command_result(remote.delete_attachment(request).await);
    }
    let store = manager.store_handle();
    command_result(tokio::task::spawn_blocking(move ||
        attachments::delete(&app, &store, request)
    ).await.map_err(|error| error.to_string())?)
}

/// Rejects blank request identifiers before manager work begins.
fn required_id(value: &str, label: &str) -> Result<String, String> {
    let value = value.trim();
    if value.is_empty() {
        Err(format!("{label} is required"))
    } else {
        Ok(value.to_string())
    }
}

/// Converts an internal string failure only when it crosses the native command boundary.
fn command_result<T>(result: Result<T, String>) -> CommandResult<T> {
    result.map_err(Into::into)
}

/// Logs a sanitized command failure and returns it in the shared boundary shape.
fn log_command_error<T>(
    command: &str,
    owned_id: &str,
    result: Result<T, String>,
) -> CommandResult<T> {
    if let Err(error) = &result {
        crate::debug_log::stderr_log!("{command} [{owned_id}]: {error}");
    }
    command_result(result)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn command_rejection_serializes_as_a_typed_error() {
        let rejection = command_result(required_id("", "Request id"));
        let serialized = serde_json::to_value(rejection.unwrap_err()).unwrap();

        assert_eq!(
            serialized,
            serde_json::json!({
                "code": "agent-conversation-command-failed",
                "message": "Request id is required",
                "recoverable": false
            })
        );
    }
}
