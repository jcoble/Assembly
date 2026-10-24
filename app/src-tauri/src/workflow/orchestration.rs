use std::collections::HashMap;
use std::path::{Path, PathBuf};
use std::sync::{Mutex, OnceLock};

use crate::RuntimeContextProject;
use mcb_core::session_store::{OrchestrationEventRow, SessionStore};

const ORCHESTRATION_SCHEMA_VERSION: u16 = 1;
const ASSEMBLY_ORCHESTRATION_EVENTS_ENV: &str = "ASSEMBLY_ORCHESTRATION_EVENTS";
const LEGACY_ORCHESTRATION_EVENTS_ENV: &str = "MAC_COMMAND_BAR_ORCHESTRATION_EVENTS";
const ORCHESTRATION_EVENT_STORE_FILE: &str = "orchestration-events.jsonl";
/// Set this to 1 to see the made-up sample runs on a machine that has never recorded one.
const DEMO_ORCHESTRATION_RUNS_ENV: &str = "MCB_DEMO_RUNS";

#[derive(Debug, Clone, Default, serde::Deserialize, serde::Serialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub(crate) struct OrchestrationEvent {
    #[serde(rename = "schemaVersion", alias = "schema_version", default)]
    schema_version: u16,
    #[serde(default)]
    id: String,
    #[serde(rename = "runId", alias = "runID", alias = "run_id", default)]
    run_id: String,
    #[serde(default)]
    timestamp: String,
    #[serde(default)]
    kind: String,
    #[serde(default)]
    status: String,
    title: Option<String>,
    message: Option<String>,
    #[serde(rename = "projectID", alias = "projectId", alias = "project_id")]
    project_id: Option<String>,
    #[serde(alias = "project_name")]
    project_name: Option<String>,
    #[serde(alias = "project_path")]
    project_path: Option<String>,
    #[serde(alias = "root_label")]
    root_label: Option<String>,
    #[serde(rename = "taskID", alias = "taskId", alias = "task_id")]
    task_id: Option<String>,
    #[serde(alias = "agent_id")]
    agent_id: Option<String>,
    #[serde(alias = "agent_provider")]
    agent_provider: Option<String>,
    #[serde(alias = "agent_role", alias = "agent_type")]
    agent_role: Option<String>,
    #[serde(alias = "step_id")]
    step_id: Option<String>,
    #[serde(alias = "step_kind")]
    step_kind: Option<String>,
    #[serde(alias = "artifact_id")]
    artifact_id: Option<String>,
    #[serde(alias = "artifact_kind")]
    artifact_kind: Option<String>,
    #[serde(alias = "artifact_path")]
    artifact_path: Option<String>,
    #[serde(alias = "artifact_url", alias = "artifactURL")]
    artifact_url: Option<String>,
    #[serde(alias = "link_kind")]
    link_kind: Option<String>,
    #[serde(alias = "link_label")]
    link_label: Option<String>,
    #[serde(alias = "link_url", alias = "linkURL")]
    link_url: Option<String>,
    scenario: Option<String>,
    #[serde(rename = "issueID", alias = "issueId", alias = "issue_id")]
    issue_id: Option<String>,
    #[serde(alias = "retry_attempt")]
    retry_attempt: Option<u32>,
    #[serde(alias = "approval_subject")]
    approval_subject: Option<String>,
    #[serde(alias = "blocker_reason")]
    blocker_reason: Option<String>,
    #[serde(alias = "decision_prompt")]
    decision_prompt: Option<String>,
    #[serde(alias = "scenario_count")]
    scenario_count: Option<u32>,
    #[serde(alias = "issue_count")]
    issue_count: Option<u32>,
    #[serde(alias = "test_count")]
    test_count: Option<u32>,
    #[serde(alias = "retest_count")]
    retest_count: Option<u32>,
    #[serde(alias = "fix_count")]
    fix_count: Option<u32>,
    #[serde(alias = "resolved_count", alias = "fixed_count")]
    resolved_count: Option<u32>,
    #[serde(alias = "verified_count")]
    verified_count: Option<u32>,
    #[serde(alias = "delegated_count", alias = "delegation_count")]
    delegated_count: Option<u32>,
    #[serde(alias = "decision_count")]
    decision_count: Option<u32>,
    #[serde(alias = "approval_count", alias = "signoff_count")]
    approval_count: Option<u32>,
    #[serde(alias = "failed_count")]
    failed_count: Option<u32>,
    // WorkflowEngine fields are additive so schema-v1 rows and the existing presentation
    // reducer keep their exact meaning. The authoritative workflow read model is rebuilt from
    // these same rows; there is deliberately no workflow-side event file.
    #[serde(alias = "workflow_id", default)]
    workflow_id: Option<String>,
    #[serde(alias = "workflow_version", default)]
    workflow_version: Option<u16>,
    #[serde(alias = "node_id", default)]
    node_id: Option<String>,
    #[serde(alias = "node_run_id", default)]
    node_run_id: Option<String>,
    #[serde(alias = "parent_node_run_id", default)]
    parent_node_run_id: Option<String>,
    #[serde(rename = "ownedId", alias = "owned_id", default)]
    owned_id: Option<String>,
    #[serde(default)]
    attempt: Option<u32>,
    #[serde(default)]
    depth: Option<u32>,
    #[serde(alias = "input_hash", default)]
    input_hash: Option<String>,
    #[serde(alias = "output_contract", default)]
    output_contract: Option<String>,
    #[serde(alias = "workflow_artifacts", default)]
    workflow_artifacts: Option<serde_json::Value>,
    #[serde(alias = "gate_id", default)]
    gate_id: Option<String>,
    #[serde(alias = "lease_id", default)]
    lease_id: Option<String>,
    #[serde(alias = "provider_instance_id", default)]
    provider_instance_id: Option<String>,
    #[serde(default)]
    provenance: Option<String>,
    #[serde(default)]
    sequence: Option<u64>,
    #[serde(alias = "idempotency_key", default)]
    idempotency_key: Option<String>,
    #[serde(alias = "workflow_payload", default)]
    workflow_payload: Option<serde_json::Value>,
}

#[derive(Debug, Clone, serde::Deserialize, serde::Serialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub(crate) struct OrchestrationRun {
    id: String,
    title: String,
    status: String,
    phase: String,
    progress: u8,
    #[serde(rename = "projectID")]
    project_id: Option<String>,
    project_name: String,
    project_path: String,
    root_label: String,
    #[serde(rename = "taskID")]
    task_id: Option<String>,
    started_at: Option<String>,
    updated_at: Option<String>,
    summary: String,
    agents: Vec<OrchestrationAgent>,
    steps: Vec<OrchestrationStep>,
    artifacts: Vec<OrchestrationArtifact>,
    links: Vec<OrchestrationLink>,
    events: Vec<OrchestrationEvent>,
}

#[derive(Debug, Clone, serde::Deserialize, serde::Serialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
struct OrchestrationAgent {
    id: String,
    provider: String,
    role: String,
    status: String,
    title: String,
    last_activity: Option<String>,
}

#[derive(Debug, Clone, serde::Deserialize, serde::Serialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
struct OrchestrationStep {
    id: String,
    kind: String,
    title: String,
    status: String,
    summary: String,
    agent_id: Option<String>,
    started_at: Option<String>,
    finished_at: Option<String>,
}

#[derive(Debug, Clone, serde::Deserialize, serde::Serialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
struct OrchestrationArtifact {
    id: String,
    kind: String,
    title: String,
    path: Option<String>,
    url: Option<String>,
    status: String,
}

#[derive(Debug, Clone, serde::Deserialize, serde::Serialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
struct OrchestrationLink {
    kind: String,
    label: String,
    url: String,
}

#[derive(Debug, Clone)]
struct OrchestrationRunDraft {
    run: OrchestrationRun,
    agent_index: HashMap<String, usize>,
    step_index: HashMap<String, usize>,
    artifact_index: HashMap<String, usize>,
    link_index: HashMap<String, usize>,
}

pub(crate) fn list_orchestration_runs_sync(
    store: &SessionStore,
    projects: Vec<RuntimeContextProject>,
) -> Result<Vec<OrchestrationRun>, String> {
    let events = read_orchestration_events(store)?;
    let recorded = reduce_orchestration_events(events);
    let mut runs = orchestration_runs_or_samples(
        recorded,
        projects,
        demo_orchestration_runs_requested(
            std::env::var(DEMO_ORCHESTRATION_RUNS_ENV).ok().as_deref(),
        ),
    );

    sort_orchestration_runs(&mut runs);
    Ok(runs)
}

/// What the runs list shows. Real recorded runs always win.
///
/// When nothing has been recorded, the answer is an empty list — a machine that has never
/// run anything should say so. The made-up sample runs are only produced for someone who
/// explicitly asked to see the card populated, because runs that look real but never
/// happened are worse than an empty card.
fn orchestration_runs_or_samples(
    recorded: Vec<OrchestrationRun>,
    projects: Vec<RuntimeContextProject>,
    samples_requested: bool,
) -> Vec<OrchestrationRun> {
    if !recorded.is_empty() || !samples_requested {
        return recorded;
    }

    demo_orchestration_runs(projects)
}

/// The sample runs are switched on by setting MCB_DEMO_RUNS to exactly 1. Anything else,
/// including the variable being unset or empty, leaves them off.
fn demo_orchestration_runs_requested(value: Option<&str>) -> bool {
    value.map(str::trim) == Some("1")
}

pub(crate) fn record_orchestration_event_sync(
    store: &SessionStore,
    mut event: OrchestrationEvent,
) -> Result<OrchestrationRun, String> {
    if event.workflow_id.is_some() || event.kind.starts_with("workflow.") {
        return Err("Workflow transitions may only be appended by WorkflowEngine".to_string());
    }
    let _writer = orchestration_writer_lock()
        .lock()
        .map_err(|_| "Orchestration event writer is unavailable".to_string())?;
    normalize_orchestration_event(&mut event)?;
    append_orchestration_event(store, &event)?;

    let runs = reduce_orchestration_events(read_orchestration_events(store)?);
    runs.into_iter()
        .find(|run| run.id == event.run_id)
        .ok_or_else(|| "Recorded orchestration event, but could not rebuild its run".to_string())
}

fn orchestration_writer_lock() -> &'static Mutex<()> {
    static LOCK: OnceLock<Mutex<()>> = OnceLock::new();
    LOCK.get_or_init(|| Mutex::new(()))
}

/// Append a WorkflowEngine transition to the one orchestration ledger. Sequence allocation and
/// idempotency are guarded by the same writer lock so a single app process has one total order.
pub(crate) fn append_workflow_event_value(
    store: &SessionStore,
    value: serde_json::Value,
) -> Result<serde_json::Value, String> {
    let _writer = orchestration_writer_lock()
        .lock()
        .map_err(|_| "Orchestration event writer is unavailable".to_string())?;
    let mut event = serde_json::from_value::<OrchestrationEvent>(value)
        .map_err(|error| format!("Invalid workflow event: {error}"))?;
    if event
        .workflow_id
        .as_deref()
        .unwrap_or_default()
        .trim()
        .is_empty()
        || !event.kind.starts_with("workflow.")
    {
        return Err("Workflow event requires workflowId and a workflow.* kind".to_string());
    }
    normalize_orchestration_event(&mut event)?;
    if let Some(key) = event.idempotency_key.as_deref() {
        if let Some(prior) = store
            .find_orchestration_event_by_idempotency_key(&event.run_id, key)
            .map_err(|error| error.to_string())?
        {
            return serde_json::from_str(&prior.payload_json)
                .map_err(|error| format!("Could not serialize workflow event: {error}"));
        }
    }
    let current_sequence = store
        .latest_orchestration_sequence(&event.run_id)
        .map_err(|error| error.to_string())? as u64;
    let expected_sequence = event
        .workflow_payload
        .as_ref()
        .and_then(|payload| payload.get("run"))
        .and_then(|run| run.get("lastSequence"))
        .and_then(serde_json::Value::as_u64)
        .unwrap_or_default();
    if expected_sequence != current_sequence {
        return Err(format!(
            "Stale workflow transition: expected sequence {expected_sequence}, current sequence {current_sequence}"
        ));
    }
    event.sequence = Some(current_sequence.saturating_add(1));
    append_orchestration_event(store, &event)?;
    serde_json::to_value(event)
        .map_err(|error| format!("Could not serialize workflow event: {error}"))
}

pub(crate) fn read_workflow_run_values(
    store: &SessionStore,
) -> Result<Vec<serde_json::Value>, String> {
    store
        .list_latest_workflow_events()
        .map_err(|error| error.to_string())?
        .into_iter()
        .map(|row| {
            serde_json::from_str(&row.payload_json)
                .map_err(|error| format!("Could not serialize workflow event: {error}"))
        })
        .collect()
}

#[cfg(test)]
pub(crate) fn read_workflow_event_values(
    store: &SessionStore,
) -> Result<Vec<serde_json::Value>, String> {
    store
        .list_workflow_orchestration_events()
        .map_err(|error| error.to_string())?
        .into_iter()
        .map(|row| {
            serde_json::from_str(&row.payload_json)
                .map_err(|error| format!("Could not serialize workflow event: {error}"))
        })
        .collect()
}

fn normalize_orchestration_event(event: &mut OrchestrationEvent) -> Result<(), String> {
    event.run_id = event.run_id.trim().to_string();
    if event.run_id.is_empty() {
        return Err("Orchestration event runId is required".to_string());
    }

    if event.schema_version == 0 {
        event.schema_version = ORCHESTRATION_SCHEMA_VERSION;
    }
    if event.timestamp.trim().is_empty() {
        event.timestamp = unix_epoch_millis().to_string();
    }
    if event.kind.trim().is_empty() {
        event.kind = "run.updated".to_string();
    }
    if event.status.trim().is_empty() {
        event.status = "running".to_string();
    }
    if event.id.trim().is_empty() {
        event.id = format!(
            "evt-{}",
            stable_orchestration_id(&format!(
                "{}:{}:{}:{}:{}",
                event.run_id,
                event.timestamp,
                event.kind,
                event.title.as_deref().unwrap_or_default(),
                event.message.as_deref().unwrap_or_default()
            ))
        );
    }

    Ok(())
}

fn orchestration_event_store_path() -> Result<PathBuf, String> {
    if let Some(path) = orchestration_event_store_override() {
        return Ok(path);
    }

    let home = std::env::var_os("HOME")
        .map(PathBuf::from)
        .ok_or_else(|| "HOME is not set".to_string())?;
    Ok(home
        .join("Library")
        .join("Application Support")
        .join("MacCommandBar")
        .join(ORCHESTRATION_EVENT_STORE_FILE))
}

fn orchestration_event_store_override() -> Option<PathBuf> {
    [
        ASSEMBLY_ORCHESTRATION_EVENTS_ENV,
        LEGACY_ORCHESTRATION_EVENTS_ENV,
    ]
    .into_iter()
    .filter_map(|name| std::env::var(name).ok())
    .map(|path| path.trim().to_string())
    .find(|path| !path.is_empty())
    .map(PathBuf::from)
}

pub(crate) fn import_legacy_orchestration_events(store: &SessionStore) -> Result<(), String> {
    let path = orchestration_event_store_path()?;
    if !path.exists() {
        return Ok(());
    }

    let contents = std::fs::read_to_string(&path).map_err(|error| {
        format!(
            "Could not read legacy orchestration event store {}: {error}",
            path.display()
        )
    })?;
    let rows = parse_orchestration_events_jsonl(&contents)?
        .iter()
        .map(orchestration_event_row)
        .collect::<Result<Vec<_>, _>>()?;
    store
        .import_orchestration_events(&rows)
        .map_err(|error| error.to_string())?;
    if let Err(error) = std::fs::remove_file(&path) {
        eprintln!(
            "Imported legacy orchestration event store {}, but could not remove it: {error}",
            path.display()
        );
    }
    Ok(())
}

fn read_orchestration_events(store: &SessionStore) -> Result<Vec<OrchestrationEvent>, String> {
    store
        .list_orchestration_events()
        .map_err(|error| error.to_string())?
        .into_iter()
        .map(|row| {
            serde_json::from_str::<OrchestrationEvent>(&row.payload_json)
                .map_err(|error| format!("Could not read orchestration event {}: {error}", row.id))
        })
        .collect()
}

fn parse_orchestration_events_jsonl(input: &str) -> Result<Vec<OrchestrationEvent>, String> {
    let mut events = Vec::new();
    for (line_index, line) in input.lines().enumerate() {
        let trimmed = line.trim();
        if trimmed.is_empty() {
            continue;
        }

        let mut event = serde_json::from_str::<OrchestrationEvent>(trimmed).map_err(|error| {
            format!(
                "Could not parse orchestration event line {}: {error}",
                line_index + 1
            )
        })?;
        normalize_orchestration_event(&mut event)?;
        events.push(event);
    }

    Ok(events)
}

fn append_orchestration_event(
    store: &SessionStore,
    event: &OrchestrationEvent,
) -> Result<(), String> {
    store
        .append_orchestration_event(&orchestration_event_row(event)?)
        .map_err(|error| error.to_string())
}

fn orchestration_event_row(event: &OrchestrationEvent) -> Result<OrchestrationEventRow, String> {
    let payload_json = serde_json::to_string(event)
        .map_err(|error| format!("Could not serialize orchestration event: {error}"))?;
    let sequence = event
        .sequence
        .map(i64::try_from)
        .transpose()
        .map_err(|_| "Orchestration event sequence is too large".to_string())?;
    Ok(OrchestrationEventRow {
        id: event.id.clone(),
        run_id: event.run_id.clone(),
        kind: event.kind.clone(),
        timestamp: event.timestamp.clone(),
        sequence,
        workflow_id: event.workflow_id.clone(),
        idempotency_key: event.idempotency_key.clone(),
        payload_json,
    })
}

fn reduce_orchestration_events(events: Vec<OrchestrationEvent>) -> Vec<OrchestrationRun> {
    let mut drafts: HashMap<String, OrchestrationRunDraft> = HashMap::new();

    for event in events {
        let draft = drafts
            .entry(event.run_id.clone())
            .or_insert_with(|| OrchestrationRunDraft::new(&event));
        draft.apply_event(event);
    }

    let mut runs: Vec<OrchestrationRun> = drafts
        .into_values()
        .map(|mut draft| {
            draft.finalize();
            draft.run
        })
        .collect();
    sort_orchestration_runs(&mut runs);
    runs
}

impl OrchestrationRunDraft {
    fn new(event: &OrchestrationEvent) -> Self {
        let project_path = event.project_path.clone().unwrap_or_default();
        let project_name = event
            .project_name
            .clone()
            .or_else(|| {
                (!project_path.is_empty())
                    .then(|| path_last_segment(Path::new(&project_path)).unwrap_or_default())
            })
            .filter(|value| !value.trim().is_empty())
            .unwrap_or_else(|| "Project".to_string());
        let root_label = event
            .root_label
            .clone()
            .or_else(|| {
                (!project_path.is_empty()).then(|| root_label_for_path(Path::new(&project_path)))
            })
            .unwrap_or_else(|| "unknown".to_string());

        Self {
            run: OrchestrationRun {
                id: event.run_id.clone(),
                title: event
                    .title
                    .clone()
                    .filter(|value| !value.trim().is_empty())
                    .unwrap_or_else(|| format!("{} orchestration", project_name)),
                status: event.status.clone(),
                phase: orchestration_phase_for_event(event),
                progress: 0,
                project_id: event.project_id.clone(),
                project_name,
                project_path,
                root_label,
                task_id: event.task_id.clone(),
                started_at: Some(event.timestamp.clone()),
                updated_at: Some(event.timestamp.clone()),
                summary: event.message.clone().unwrap_or_default(),
                agents: Vec::new(),
                steps: Vec::new(),
                artifacts: Vec::new(),
                links: Vec::new(),
                events: Vec::new(),
            },
            agent_index: HashMap::new(),
            step_index: HashMap::new(),
            artifact_index: HashMap::new(),
            link_index: HashMap::new(),
        }
    }

    fn apply_event(&mut self, event: OrchestrationEvent) {
        if let Some(title) = event
            .title
            .as_ref()
            .filter(|value| !value.trim().is_empty())
        {
            if event.kind.starts_with("run.") || self.run.title.ends_with(" orchestration") {
                self.run.title = title.clone();
            }
        }

        if let Some(message) = event
            .message
            .as_ref()
            .filter(|value| !value.trim().is_empty())
        {
            self.run.summary = message.clone();
        }

        let next_phase = orchestration_phase_for_event(&event);
        if next_phase != "link" && next_phase != "artifact" {
            self.run.phase = next_phase;
        }
        self.run.status = merge_orchestration_status(&self.run.status, &event.status);
        self.run.task_id = self.run.task_id.clone().or(event.task_id.clone());
        self.run.project_id = self.run.project_id.clone().or(event.project_id.clone());
        self.run.project_name = event
            .project_name
            .clone()
            .filter(|value| !value.trim().is_empty())
            .unwrap_or_else(|| self.run.project_name.clone());
        self.run.project_path = event
            .project_path
            .clone()
            .filter(|value| !value.trim().is_empty())
            .unwrap_or_else(|| self.run.project_path.clone());
        self.run.root_label = event
            .root_label
            .clone()
            .filter(|value| !value.trim().is_empty())
            .unwrap_or_else(|| self.run.root_label.clone());
        self.run.started_at =
            oldest_timestamp(self.run.started_at.take(), Some(event.timestamp.clone()));
        self.run.updated_at =
            latest_timestamp(self.run.updated_at.take(), Some(event.timestamp.clone()));

        self.upsert_agent(&event);
        self.upsert_step(&event);
        self.upsert_artifact(&event);
        self.upsert_link(&event);
        self.run.events.push(event);
    }

    fn upsert_agent(&mut self, event: &OrchestrationEvent) {
        let Some(id) = event.agent_id.clone().or_else(|| {
            event
                .agent_provider
                .as_ref()
                .map(|provider| format!("{}:{}", provider, event.run_id))
        }) else {
            return;
        };

        let provider = event
            .agent_provider
            .clone()
            .unwrap_or_else(|| "agent".to_string());
        let role = event
            .agent_role
            .clone()
            .unwrap_or_else(|| "worker".to_string());
        let title = event
            .title
            .clone()
            .unwrap_or_else(|| format!("{provider} {role}"));

        if let Some(index) = self.agent_index.get(&id).copied() {
            let agent = &mut self.run.agents[index];
            agent.status = merge_orchestration_status(&agent.status, &event.status);
            agent.title = title;
            agent.last_activity =
                latest_timestamp(agent.last_activity.take(), Some(event.timestamp.clone()));
            return;
        }

        self.agent_index.insert(id.clone(), self.run.agents.len());
        self.run.agents.push(OrchestrationAgent {
            id,
            provider,
            role,
            status: event.status.clone(),
            title,
            last_activity: Some(event.timestamp.clone()),
        });
    }

    fn upsert_step(&mut self, event: &OrchestrationEvent) {
        if event.kind.starts_with("run.")
            || event.kind.starts_with("agent.")
            || event.kind.starts_with("artifact.")
            || event.kind.starts_with("link.")
        {
            return;
        }

        let id = event.step_id.clone().unwrap_or_else(|| {
            stable_orchestration_id(&format!(
                "{}:{}:{}",
                event.run_id,
                event.kind,
                event.title.as_deref().unwrap_or_default()
            ))
        });
        let kind = event
            .step_kind
            .clone()
            .unwrap_or_else(|| event.kind.split('.').next().unwrap_or("step").to_string());
        let title = event
            .title
            .clone()
            .filter(|value| !value.trim().is_empty())
            .unwrap_or_else(|| humanize_orchestration_kind(&event.kind));
        let summary = event
            .message
            .clone()
            .filter(|value| !value.trim().is_empty())
            .unwrap_or_else(|| event.kind.clone());

        if let Some(index) = self.step_index.get(&id).copied() {
            let step = &mut self.run.steps[index];
            step.status = merge_orchestration_status(&step.status, &event.status);
            step.title = title;
            step.summary = summary;
            step.finished_at = if orchestration_status_is_terminal(&event.status) {
                latest_timestamp(step.finished_at.take(), Some(event.timestamp.clone()))
            } else {
                step.finished_at.take()
            };
            return;
        }

        self.step_index.insert(id.clone(), self.run.steps.len());
        self.run.steps.push(OrchestrationStep {
            id,
            kind,
            title,
            status: event.status.clone(),
            summary,
            agent_id: event.agent_id.clone(),
            started_at: Some(event.timestamp.clone()),
            finished_at: orchestration_status_is_terminal(&event.status)
                .then(|| event.timestamp.clone()),
        });
    }

    fn upsert_artifact(&mut self, event: &OrchestrationEvent) {
        if event.artifact_id.is_none()
            && event.artifact_path.is_none()
            && event.artifact_url.is_none()
        {
            return;
        }

        let id = event.artifact_id.clone().unwrap_or_else(|| {
            stable_orchestration_id(&format!(
                "{}:{}:{}",
                event.run_id,
                event.artifact_path.as_deref().unwrap_or_default(),
                event.artifact_url.as_deref().unwrap_or_default()
            ))
        });
        let kind = event
            .artifact_kind
            .clone()
            .unwrap_or_else(|| "artifact".to_string());
        let title = event
            .title
            .clone()
            .or_else(|| event.artifact_path.clone())
            .or_else(|| event.artifact_url.clone())
            .unwrap_or_else(|| "Artifact".to_string());

        if let Some(index) = self.artifact_index.get(&id).copied() {
            let artifact = &mut self.run.artifacts[index];
            artifact.status = merge_orchestration_status(&artifact.status, &event.status);
            artifact.title = title;
            artifact.path = artifact.path.clone().or(event.artifact_path.clone());
            artifact.url = artifact.url.clone().or(event.artifact_url.clone());
            return;
        }

        self.artifact_index
            .insert(id.clone(), self.run.artifacts.len());
        self.run.artifacts.push(OrchestrationArtifact {
            id,
            kind,
            title,
            path: event.artifact_path.clone(),
            url: event.artifact_url.clone(),
            status: event.status.clone(),
        });
    }

    fn upsert_link(&mut self, event: &OrchestrationEvent) {
        let Some(url) = event.link_url.clone() else {
            return;
        };

        let kind = event
            .link_kind
            .clone()
            .unwrap_or_else(|| "link".to_string());
        let key = format!("{kind}:{url}");
        if self.link_index.contains_key(&key) {
            return;
        }

        self.link_index.insert(key, self.run.links.len());
        self.run.links.push(OrchestrationLink {
            kind,
            label: event
                .link_label
                .clone()
                .or_else(|| event.title.clone())
                .unwrap_or_else(|| "Open link".to_string()),
            url,
        });
    }

    fn finalize(&mut self) {
        self.run.events.sort_by(|left, right| {
            left.timestamp
                .cmp(&right.timestamp)
                .then(left.id.cmp(&right.id))
        });
        self.run.steps.sort_by(|left, right| {
            left.started_at
                .cmp(&right.started_at)
                .then(left.title.cmp(&right.title))
        });
        self.run.agents.sort_by(|left, right| {
            right
                .last_activity
                .cmp(&left.last_activity)
                .then(left.provider.cmp(&right.provider))
        });
        self.run
            .artifacts
            .sort_by(|left, right| left.title.cmp(&right.title));
        self.run.progress = orchestration_progress(&self.run);
        if self.run.summary.trim().is_empty() {
            self.run.summary = format!(
                "{} agents · {} steps · {} artifacts",
                self.run.agents.len(),
                self.run.steps.len(),
                self.run.artifacts.len()
            );
        }
    }
}

fn demo_orchestration_runs(projects: Vec<RuntimeContextProject>) -> Vec<OrchestrationRun> {
    projects
        .into_iter()
        .map(|project| {
            let task_id = (project.id == "mac-command-bar").then(|| "TSK-127".to_string());
            let run_id = task_id
                .clone()
                .map(|task| format!("run-{}", task.to_lowercase()))
                .unwrap_or_else(|| format!("run-{}", stable_orchestration_id(&project.path)));
            let project_path = project.path.clone();
            let root_label = root_label_for_path(Path::new(&project_path));
            let mut events = vec![
                OrchestrationEvent {
                    schema_version: ORCHESTRATION_SCHEMA_VERSION,
                    id: format!("{run_id}-created"),
                    run_id: run_id.clone(),
                    timestamp: "preview-001".to_string(),
                    kind: "run.created".to_string(),
                    status: "running".to_string(),
                    title: Some(match &task_id {
                        Some(task) => format!("{task} orchestration model"),
                        None => format!("{} project monitor", project.name),
                    }),
                    message: Some(
                        "Preview run seeded until real orchestration events exist".to_string(),
                    ),
                    project_id: Some(project.id.clone()),
                    project_name: Some(project.name.clone()),
                    project_path: Some(project_path.clone()),
                    root_label: Some(root_label.clone()),
                    task_id: task_id.clone(),
                    agent_id: None,
                    agent_provider: None,
                    agent_role: None,
                    step_id: None,
                    step_kind: None,
                    artifact_id: None,
                    artifact_kind: None,
                    artifact_path: None,
                    artifact_url: None,
                    link_kind: None,
                    link_label: None,
                    link_url: None,
                    scenario: None,
                    issue_id: None,
                    retry_attempt: None,
                    approval_subject: None,
                    blocker_reason: None,
                    decision_prompt: None,
                    scenario_count: None,
                    issue_count: None,
                    test_count: None,
                    retest_count: None,
                    fix_count: None,
                    resolved_count: None,
                    verified_count: None,
                    delegated_count: None,
                    decision_count: None,
                    approval_count: None,
                    failed_count: None,
                    ..Default::default()
                },
                OrchestrationEvent {
                    schema_version: ORCHESTRATION_SCHEMA_VERSION,
                    id: format!("{run_id}-model"),
                    run_id: run_id.clone(),
                    timestamp: "preview-002".to_string(),
                    kind: "model.designed".to_string(),
                    status: "succeeded".to_string(),
                    title: Some("Event schema".to_string()),
                    message: Some("Run, agent, step, artifact, and link records".to_string()),
                    project_id: Some(project.id.clone()),
                    project_name: Some(project.name.clone()),
                    project_path: Some(project_path.clone()),
                    root_label: Some(root_label.clone()),
                    task_id: task_id.clone(),
                    agent_id: Some("controller".to_string()),
                    agent_provider: Some("codex".to_string()),
                    agent_role: Some("orchestrator".to_string()),
                    step_id: Some("event-schema".to_string()),
                    step_kind: Some("model".to_string()),
                    artifact_id: None,
                    artifact_kind: None,
                    artifact_path: None,
                    artifact_url: None,
                    link_kind: None,
                    link_label: None,
                    link_url: None,
                    scenario: None,
                    issue_id: None,
                    retry_attempt: None,
                    approval_subject: None,
                    blocker_reason: None,
                    decision_prompt: None,
                    scenario_count: None,
                    issue_count: None,
                    test_count: None,
                    retest_count: None,
                    fix_count: None,
                    resolved_count: None,
                    verified_count: None,
                    delegated_count: None,
                    decision_count: None,
                    approval_count: None,
                    failed_count: None,
                    ..Default::default()
                },
                OrchestrationEvent {
                    schema_version: ORCHESTRATION_SCHEMA_VERSION,
                    id: format!("{run_id}-store"),
                    run_id: run_id.clone(),
                    timestamp: "preview-003".to_string(),
                    kind: "store.pending".to_string(),
                    status: "running".to_string(),
                    title: Some("JSONL event store".to_string()),
                    message: Some("Append-only run events for skills and adapters".to_string()),
                    project_id: Some(project.id.clone()),
                    project_name: Some(project.name.clone()),
                    project_path: Some(project_path.clone()),
                    root_label: Some(root_label.clone()),
                    task_id: task_id.clone(),
                    agent_id: Some("controller".to_string()),
                    agent_provider: Some("codex".to_string()),
                    agent_role: Some("orchestrator".to_string()),
                    step_id: Some("event-store".to_string()),
                    step_kind: Some("store".to_string()),
                    artifact_id: None,
                    artifact_kind: None,
                    artifact_path: None,
                    artifact_url: None,
                    link_kind: None,
                    link_label: None,
                    link_url: None,
                    scenario: None,
                    issue_id: None,
                    retry_attempt: None,
                    approval_subject: None,
                    blocker_reason: None,
                    decision_prompt: None,
                    scenario_count: None,
                    issue_count: None,
                    test_count: None,
                    retest_count: None,
                    fix_count: None,
                    resolved_count: None,
                    verified_count: None,
                    delegated_count: None,
                    decision_count: None,
                    approval_count: None,
                    failed_count: None,
                    ..Default::default()
                },
            ];

            if let Some(task_id) = &task_id {
                events.push(OrchestrationEvent {
                    schema_version: ORCHESTRATION_SCHEMA_VERSION,
                    id: format!("{run_id}-task"),
                    run_id: run_id.clone(),
                    timestamp: "preview-004".to_string(),
                    kind: "link.task".to_string(),
                    status: "succeeded".to_string(),
                    title: Some(task_id.clone()),
                    message: Some("Command Center task link".to_string()),
                    project_id: Some(project.id),
                    project_name: Some(project.name),
                    project_path: Some(project_path),
                    root_label: Some(root_label),
                    task_id: Some(task_id.clone()),
                    agent_id: None,
                    agent_provider: None,
                    agent_role: None,
                    step_id: None,
                    step_kind: None,
                    artifact_id: None,
                    artifact_kind: None,
                    artifact_path: None,
                    artifact_url: None,
                    link_kind: Some("task".to_string()),
                    link_label: Some(task_id.clone()),
                    link_url: Some("https://app.notion.com/p/TSK-127-Create-a-native-MAC-OS-app-for-doing-diff-things-in-menu-bar-379394b0689d8053af76fd44c7ffdba4".to_string()),
                    scenario: None,
                    issue_id: None,
                    retry_attempt: None,
                    approval_subject: None,
                    blocker_reason: None,
                    decision_prompt: None,
                    scenario_count: None,
                    issue_count: None,
                    test_count: None,
                    retest_count: None,
                    fix_count: None,
                    resolved_count: None,
                    verified_count: None,
                    delegated_count: None,
                    decision_count: None,
                    approval_count: None,
                    failed_count: None,
                    ..Default::default()
                });
            }

            reduce_orchestration_events(events)
                .into_iter()
                .next()
                .expect("demo events should produce one run")
        })
        .collect()
}

fn sort_orchestration_runs(runs: &mut [OrchestrationRun]) {
    runs.sort_by(|left, right| {
        right
            .updated_at
            .cmp(&left.updated_at)
            .then(left.project_name.cmp(&right.project_name))
            .then(left.title.cmp(&right.title))
    });
}

fn orchestration_phase_for_event(event: &OrchestrationEvent) -> String {
    event
        .step_kind
        .clone()
        .unwrap_or_else(|| event.kind.split('.').next().unwrap_or("run").to_string())
}

fn orchestration_progress(run: &OrchestrationRun) -> u8 {
    if run.status == "succeeded" {
        return 100;
    }
    if run.status == "failed" || run.status == "cancelled" {
        return 100;
    }
    if run.steps.is_empty() {
        return match run.status.as_str() {
            "queued" => 5,
            "running" => 35,
            _ => 0,
        };
    }

    let completed = run
        .steps
        .iter()
        .filter(|step| orchestration_status_is_terminal(&step.status))
        .count();
    let total = run.steps.len().max(1);
    let weighted = ((completed * 100) / total).clamp(10, 95);
    u8::try_from(weighted).unwrap_or(95)
}

fn merge_orchestration_status(current: &str, next: &str) -> String {
    let current_rank = orchestration_status_rank(current);
    let next_rank = orchestration_status_rank(next);
    if next_rank >= current_rank {
        next.to_string()
    } else {
        current.to_string()
    }
}

fn orchestration_status_rank(status: &str) -> u8 {
    let normalized = status.trim().to_lowercase();
    if normalized.is_empty() {
        return 0;
    }
    if normalized.contains("fail") || normalized.contains("error") || normalized.contains("cancel")
    {
        return 6;
    }
    if normalized.contains("block")
        || normalized.contains("wait")
        || normalized.contains("approval")
        || normalized.contains("decision")
        || normalized.contains("review")
        || normalized.contains("needs")
    {
        return 5;
    }
    if normalized.contains("run")
        || normalized.contains("active")
        || normalized.contains("work")
        || normalized.contains("test")
        || normalized.contains("fix")
        || normalized.contains("progress")
    {
        return 4;
    }
    if normalized == "queued" || normalized.contains("queue") {
        return 2;
    }
    if normalized.contains("success")
        || normalized.contains("succeed")
        || normalized.contains("complete")
        || normalized.contains("pass")
        || normalized.contains("done")
        || normalized.contains("skip")
    {
        return 1;
    }
    0
}

fn orchestration_status_is_terminal(status: &str) -> bool {
    let normalized = status.trim().to_lowercase();
    normalized.contains("success")
        || normalized.contains("succeed")
        || normalized.contains("complete")
        || normalized.contains("pass")
        || normalized.contains("done")
        || normalized.contains("skip")
        || normalized.contains("fail")
        || normalized.contains("error")
        || normalized.contains("cancel")
}

fn latest_timestamp(left: Option<String>, right: Option<String>) -> Option<String> {
    match (left, right) {
        (Some(left), Some(right)) => Some(left.max(right)),
        (Some(left), None) => Some(left),
        (None, Some(right)) => Some(right),
        (None, None) => None,
    }
}

fn oldest_timestamp(left: Option<String>, right: Option<String>) -> Option<String> {
    match (left, right) {
        (Some(left), Some(right)) => Some(left.min(right)),
        (Some(left), None) => Some(left),
        (None, Some(right)) => Some(right),
        (None, None) => None,
    }
}

fn humanize_orchestration_kind(kind: &str) -> String {
    let value = kind
        .replace(['.', '_', '-'], " ")
        .split_whitespace()
        .map(|word| {
            let mut chars = word.chars();
            match chars.next() {
                Some(first) => format!("{}{}", first.to_uppercase(), chars.as_str()),
                None => String::new(),
            }
        })
        .collect::<Vec<_>>()
        .join(" ");
    if value.is_empty() {
        "Step".to_string()
    } else {
        value
    }
}

fn stable_orchestration_id(value: &str) -> String {
    let mut hash = 5381_u32;
    for byte in value.bytes() {
        hash = hash.wrapping_mul(33) ^ u32::from(byte);
    }
    format!("orch-{hash:x}")
}

fn root_label_for_path(path: &Path) -> String {
    if let Some((_, slug)) = worktree_repo_and_slug(path) {
        return format!("worktree:{slug}");
    }

    let segments = path_segments(path);
    if segments
        .len()
        .checked_sub(2)
        .and_then(|index| segments.get(index))
        .is_some_and(|segment| segment == "work")
    {
        return "main checkout".to_string();
    }

    path_last_segment(path).unwrap_or_else(|| "unknown".to_string())
}

fn worktree_repo_and_slug(path: &Path) -> Option<(String, String)> {
    let segments = path_segments(path);
    let worktrees_index = segments.iter().position(|segment| segment == "worktrees")?;
    let repo = segments.get(worktrees_index + 1)?.clone();
    let slug = segments.get(worktrees_index + 2)?.clone();
    Some((repo, slug))
}

fn path_segments(path: &Path) -> Vec<String> {
    path.components()
        .filter_map(|component| component.as_os_str().to_str())
        .filter(|segment| !segment.is_empty() && *segment != "/")
        .map(ToString::to_string)
        .collect()
}

fn path_last_segment(path: &Path) -> Option<String> {
    path.file_name()
        .and_then(|segment| segment.to_str())
        .filter(|segment| !segment.is_empty())
        .map(ToString::to_string)
}

fn unix_epoch_millis() -> u128 {
    std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map(|duration| duration.as_millis())
        .unwrap_or_default()
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::ffi::OsString;

    fn identity_env_test_lock() -> std::sync::MutexGuard<'static, ()> {
        static LOCK: OnceLock<Mutex<()>> = OnceLock::new();
        LOCK.get_or_init(|| Mutex::new(())).lock().unwrap()
    }

    fn restore_env(name: &str, value: Option<OsString>) {
        match value {
            Some(value) => std::env::set_var(name, value),
            None => std::env::remove_var(name),
        }
    }

    #[test]
    fn product_identity_orchestration_prefers_assembly_env() {
        let _lock = identity_env_test_lock();
        let assembly = std::env::var_os(ASSEMBLY_ORCHESTRATION_EVENTS_ENV);
        let legacy = std::env::var_os(LEGACY_ORCHESTRATION_EVENTS_ENV);
        std::env::set_var(ASSEMBLY_ORCHESTRATION_EVENTS_ENV, "/tmp/assembly-events");
        std::env::set_var(LEGACY_ORCHESTRATION_EVENTS_ENV, "/tmp/legacy-events");

        assert_eq!(
            orchestration_event_store_override(),
            Some(PathBuf::from("/tmp/assembly-events"))
        );

        restore_env(ASSEMBLY_ORCHESTRATION_EVENTS_ENV, assembly);
        restore_env(LEGACY_ORCHESTRATION_EVENTS_ENV, legacy);
    }

    #[test]
    fn product_identity_orchestration_falls_back_to_legacy_env() {
        let _lock = identity_env_test_lock();
        let assembly = std::env::var_os(ASSEMBLY_ORCHESTRATION_EVENTS_ENV);
        let legacy = std::env::var_os(LEGACY_ORCHESTRATION_EVENTS_ENV);
        std::env::set_var(ASSEMBLY_ORCHESTRATION_EVENTS_ENV, " ");
        std::env::set_var(LEGACY_ORCHESTRATION_EVENTS_ENV, "/tmp/legacy-events");

        assert_eq!(
            orchestration_event_store_override(),
            Some(PathBuf::from("/tmp/legacy-events"))
        );

        restore_env(ASSEMBLY_ORCHESTRATION_EVENTS_ENV, assembly);
        restore_env(LEGACY_ORCHESTRATION_EVENTS_ENV, legacy);
    }

    #[test]
    fn product_identity_orchestration_preserves_legacy_support_path() {
        let home = PathBuf::from("/Users/example");
        assert_eq!(
            home.join("Library")
                .join("Application Support")
                .join("MacCommandBar")
                .join(ORCHESTRATION_EVENT_STORE_FILE),
            PathBuf::from(
                "/Users/example/Library/Application Support/MacCommandBar/orchestration-events.jsonl"
            )
        );
    }

    fn demo_projects() -> Vec<RuntimeContextProject> {
        vec![RuntimeContextProject {
            id: "mac-command-bar".to_string(),
            name: "MacCommandBar".to_string(),
            path: "/repo".to_string(),
        }]
    }

    #[test]
    fn no_recorded_runs_means_an_empty_list_not_made_up_runs() {
        let runs = orchestration_runs_or_samples(Vec::new(), demo_projects(), false);

        assert!(
            runs.is_empty(),
            "a machine that has never recorded a run shows nothing, not invented runs"
        );
    }

    #[test]
    fn sample_runs_appear_only_when_they_were_explicitly_asked_for() {
        let runs = orchestration_runs_or_samples(Vec::new(), demo_projects(), true);

        assert!(!runs.is_empty());
        assert!(runs
            .iter()
            .all(|run| run.project_name == "MacCommandBar" || run.project_name.is_empty()));
    }

    #[test]
    fn real_runs_are_never_replaced_by_samples() {
        let real = reduce_orchestration_events(
            parse_orchestration_events_jsonl(
                r#"{"schemaVersion":1,"id":"evt-1","runId":"run-real","timestamp":"2026-06-10T10:00:00Z","kind":"run.created","status":"running","title":"A real run","projectID":"mac-command-bar","projectName":"MacCommandBar","projectPath":"/repo","rootLabel":"main checkout"}"#,
            )
            .unwrap(),
        );

        let runs = orchestration_runs_or_samples(real, demo_projects(), true);

        assert_eq!(runs.len(), 1);
        assert_eq!(runs[0].id, "run-real");
    }

    #[test]
    fn sample_runs_are_requested_by_setting_the_switch_to_one() {
        assert!(demo_orchestration_runs_requested(Some("1")));
        assert!(demo_orchestration_runs_requested(Some(" 1 ")));
        assert!(!demo_orchestration_runs_requested(None));
        assert!(!demo_orchestration_runs_requested(Some("")));
        assert!(!demo_orchestration_runs_requested(Some("0")));
        assert!(!demo_orchestration_runs_requested(Some("true")));
    }

    #[test]
    fn orchestration_events_reduce_to_run_timeline() {
        let input = [
            r#"{"schemaVersion":1,"id":"evt-1","runId":"run-tsk-127","timestamp":"2026-06-10T10:00:00Z","kind":"run.created","status":"running","title":"TSK-127 run","message":"Started","projectID":"mac-command-bar","projectName":"MacCommandBar","projectPath":"/repo","rootLabel":"main checkout","taskID":"TSK-127","agentId":null,"agentProvider":null,"agentRole":null,"stepId":null,"stepKind":null,"artifactId":null,"artifactKind":null,"artifactPath":null,"artifactUrl":null,"linkKind":null,"linkLabel":null,"linkUrl":null}"#,
            r#"{"schemaVersion":1,"id":"evt-2","runId":"run-tsk-127","timestamp":"2026-06-10T10:01:00Z","kind":"test.failed","status":"failed","title":"Playwright checkout","message":"Button mismatch","projectID":"mac-command-bar","projectName":"MacCommandBar","projectPath":"/repo","rootLabel":"main checkout","taskID":"TSK-127","agentId":"agent-a","agentProvider":"codex","agentRole":"tester","stepId":"e2e","stepKind":"test","artifactId":"trace-1","artifactKind":"trace","artifactPath":"/tmp/trace.zip","artifactUrl":null,"linkKind":null,"linkLabel":null,"linkUrl":null,"scenario":"Google auth callback","issueID":"AUTH-7","retryAttempt":1,"approvalSubject":"Review auth redirect fix","blockerReason":"Needs user sign-off","decisionPrompt":"Merge this fix batch?","issueCount":2,"failedCount":1}"#,
            r#"{"schemaVersion":1,"id":"evt-3","runId":"run-tsk-127","timestamp":"2026-06-10T10:02:00Z","kind":"link.task","status":"succeeded","title":"TSK-127","message":"Task link","projectID":"mac-command-bar","projectName":"MacCommandBar","projectPath":"/repo","rootLabel":"main checkout","taskID":"TSK-127","agentId":null,"agentProvider":null,"agentRole":null,"stepId":null,"stepKind":null,"artifactId":null,"artifactKind":null,"artifactPath":null,"artifactUrl":null,"linkKind":"task","linkLabel":"TSK-127","linkUrl":"https://example.test/task"}"#,
        ]
        .join("\n");

        let events = parse_orchestration_events_jsonl(&input).unwrap();
        assert_eq!(events[1].issue_count, Some(2));
        assert_eq!(events[1].failed_count, Some(1));
        assert_eq!(events[1].scenario.as_deref(), Some("Google auth callback"));
        assert_eq!(events[1].issue_id.as_deref(), Some("AUTH-7"));
        assert_eq!(events[1].retry_attempt, Some(1));
        assert_eq!(
            events[1].approval_subject.as_deref(),
            Some("Review auth redirect fix")
        );
        assert_eq!(
            events[1].blocker_reason.as_deref(),
            Some("Needs user sign-off")
        );
        assert_eq!(
            events[1].decision_prompt.as_deref(),
            Some("Merge this fix batch?")
        );
        let runs = reduce_orchestration_events(events);

        assert_eq!(runs.len(), 1);
        let run = &runs[0];
        assert_eq!(run.id, "run-tsk-127");
        assert_eq!(run.status, "failed");
        assert_eq!(run.phase, "test");
        assert_eq!(run.task_id.as_deref(), Some("TSK-127"));
        assert_eq!(run.agents.len(), 1);
        assert_eq!(run.agents[0].provider, "codex");
        assert_eq!(run.steps.len(), 1);
        assert_eq!(run.steps[0].id, "e2e");
        assert_eq!(run.steps[0].status, "failed");
        assert_eq!(run.artifacts.len(), 1);
        assert_eq!(run.artifacts[0].path.as_deref(), Some("/tmp/trace.zip"));
        assert_eq!(run.links.len(), 1);
        assert_eq!(run.links[0].url, "https://example.test/task");
        assert_eq!(run.progress, 100);
    }

    #[test]
    fn partial_orchestration_events_keep_waiting_decisions_visible() {
        let input = [
            r#"{"run_id":"run-loop","timestamp":"2026-06-10T10:00:00Z","kind":"run.started","status":"running","project_id":"mac-command-bar","project_name":"MacCommandBar","project_path":"/repo","task_id":"TSK-127"}"#,
            r#"{"run_id":"run-loop","timestamp":"2026-06-10T10:02:00Z","kind":"handoff.available","status":"succeeded","artifact_kind":"handoff","artifact_path":"/repo/.codex-artifacts/handoff.md"}"#,
            r#"{"run_id":"run-loop","timestamp":"2026-06-10T10:03:00Z","kind":"approval.required","status":"waiting-for-approval","title":"Approval required","message":"Awaiting owner sign-off","agent_id":"controller","agent_provider":"codex","agent_role":"orchestrator","step_kind":"approval","decision_prompt":"Approve merge?","approval_count":1,"decision_count":1}"#,
        ]
        .join("\n");

        let first_parse = parse_orchestration_events_jsonl(&input).unwrap();
        let second_parse = parse_orchestration_events_jsonl(&input).unwrap();
        assert_eq!(first_parse[0].schema_version, ORCHESTRATION_SCHEMA_VERSION);
        assert_eq!(first_parse[0].id, second_parse[0].id);
        assert_eq!(first_parse[2].run_id, "run-loop");
        assert_eq!(
            first_parse[2].decision_prompt.as_deref(),
            Some("Approve merge?")
        );
        assert_eq!(first_parse[2].approval_count, Some(1));
        assert_eq!(first_parse[2].decision_count, Some(1));

        let runs = reduce_orchestration_events(first_parse);
        assert_eq!(runs.len(), 1);
        let run = &runs[0];
        assert_eq!(run.id, "run-loop");
        assert_eq!(run.status, "waiting-for-approval");
        assert_eq!(run.phase, "approval");
        assert_eq!(run.task_id.as_deref(), Some("TSK-127"));
        assert_eq!(run.progress, 50);
        assert_eq!(run.agents.len(), 1);
        assert_eq!(run.agents[0].status, "waiting-for-approval");
        assert_eq!(run.steps.len(), 2);
        let approval_step = run
            .steps
            .iter()
            .find(|step| step.kind == "approval")
            .expect("approval step should be retained");
        assert_eq!(approval_step.status, "waiting-for-approval");
        assert_eq!(run.artifacts.len(), 1);
        assert_eq!(
            run.artifacts[0].path.as_deref(),
            Some("/repo/.codex-artifacts/handoff.md")
        );
    }
}
