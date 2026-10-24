//! Resource commands that scan or mutate system state are async so they do not block Tauri's UI thread.

use mcb_core::scanners::disk::{
    disk_protection_allows_cleanup, scan_disk_roots, stable_entry_id, DiskProtection,
    DiskScanOptions, DiskScanReport, DiskScanRoot, WorkspaceDiskKind,
};
use mcb_core::scanners::resources::{
    scan_resource_snapshot, validate_resource_action, ProcessOwner, ResourceActionError,
    ResourceOwnerHint, ResourceSnapshot,
};
use serde::{Deserialize, Serialize};
use std::collections::{BTreeMap, HashMap, HashSet, VecDeque};
use std::fs;
use std::path::{Path, PathBuf};
use std::process::Command;
use std::sync::atomic::{AtomicU64, AtomicUsize, Ordering};
use std::sync::{Arc, Mutex, OnceLock};
use std::time::{SystemTime, UNIX_EPOCH};
use sysinfo::{Pid, ProcessesToUpdate, System};
use tauri::State;

use crate::debug_log::stderr_log;

/// How long a stopped process is given to exit on its own before the harder
/// signal follows.
const RESOURCE_STOP_GRACE_SECONDS: u64 = 5;

static ACTIVE_SOURCE_DIRECTORY_READS: AtomicUsize = AtomicUsize::new(0);

pub(crate) struct SourceDirectoryReadGuard;

impl Drop for SourceDirectoryReadGuard {
    fn drop(&mut self) {
        ACTIVE_SOURCE_DIRECTORY_READS.fetch_sub(1, Ordering::Relaxed);
    }
}

pub(crate) fn begin_source_directory_read() -> SourceDirectoryReadGuard {
    ACTIVE_SOURCE_DIRECTORY_READS.fetch_add(1, Ordering::Relaxed);
    SourceDirectoryReadGuard
}

pub struct ResourceRegistry {
    generation: AtomicU64,
    disk_scan_generation: Arc<AtomicU64>,
    active_source_root: Arc<Mutex<Option<PathBuf>>>,
    sample_system: Arc<Mutex<System>>,
    sampled_process_ids: Arc<Mutex<Vec<u32>>>,
}

impl Default for ResourceRegistry {
    fn default() -> Self {
        Self {
            generation: AtomicU64::default(),
            disk_scan_generation: Arc::new(AtomicU64::default()),
            active_source_root: Arc::new(Mutex::new(None)),
            sample_system: Arc::new(Mutex::new(System::new())),
            sampled_process_ids: Arc::new(Mutex::new(Vec::new())),
        }
    }
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ResourceSample {
    pub generated_at_ms: u128,
    pub totals: ResourceSampleTotals,
    pub diagnostics: ResourceDiagnostics,
    pub app: ResourceSampleApp,
    pub process_categories: Vec<ResourceSampleCategory>,
    pub groups: Vec<ResourceSampleGroup>,
}

#[derive(Debug, Clone, Default, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ResourceDiagnostics {
    pub conversations: crate::agent_conversation::manager::AgentRuntimeDiagnostics,
    pub database: DatabaseResourceDiagnostics,
    pub terminals: TerminalResourceDiagnostics,
    pub streams: crate::projection_streams::ProjectionStreamDiagnostics,
    pub language_servers: LanguageServerResourceDiagnostics,
    pub browser: BrowserResourceDiagnostics,
}

#[derive(Debug, Clone, Default, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct DatabaseResourceDiagnostics {
    pub session_store_open_handles: usize,
    pub session_store_active_reads: usize,
    pub session_store_active_writes: usize,
}

#[derive(Debug, Clone, Default, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct TerminalResourceDiagnostics {
    pub live_sessions: usize,
    pub transcript_projections: usize,
    pub user_ptys: usize,
    pub agent_tool_ptys: usize,
    pub run_configurations: usize,
    pub browser_automations: usize,
    pub exited_sessions_retained: usize,
}

#[derive(Debug, Clone, Default, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct LanguageServerResourceDiagnostics {
    pub running_processes: usize,
}

#[derive(Debug, Clone, Default, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct BrowserResourceDiagnostics {
    pub native_views: usize,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ResourceSampleTotals {
    pub cpu_percent: f32,
    pub physical_footprint_bytes: u64,
    /// Resident set size, kept separate from Activity Monitor's footprint.
    pub rss_bytes: u64,
    pub process_count: usize,
    pub active_source_directory_reads: usize,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ResourceSampleApp {
    pub parts: Vec<ResourceSampleAppPart>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ResourceSampleAppPart {
    pub label: String,
    pub pid: u32,
    pub cpu_percent: f32,
    pub physical_footprint_bytes: u64,
    pub rss_bytes: u64,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ResourceSampleCategory {
    pub label: &'static str,
    pub cpu_percent: f32,
    pub physical_footprint_bytes: u64,
    pub rss_bytes: u64,
    pub process_count: usize,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ResourceSampleGroup {
    pub workspace: String,
    pub sessions: Vec<ResourceSampleSession>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ResourceSampleSession {
    #[serde(skip_serializing_if = "Option::is_none")]
    pub owned_id: Option<String>,
    pub label: String,
    pub kind: ResourceSampleSessionKind,
    /// The process the app itself started for this session. Every stop request
    /// names this pid so the backend can prove the tree is one the app owns.
    pub root_pid: u32,
    pub processes: Vec<ResourceSampleProcess>,
}

#[derive(Debug, Clone, Copy, Eq, PartialEq, Serialize)]
#[serde(rename_all = "lowercase")]
pub enum ResourceSampleSessionKind {
    Terminal,
    Conversation,
    Other,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ResourceSampleProcess {
    pub pid: u32,
    pub name: String,
    pub cpu_percent: f32,
    pub physical_footprint_bytes: u64,
    pub rss_bytes: u64,
}

#[derive(Debug, Clone)]
struct ObservedProcess {
    pid: u32,
    parent_pid: Option<u32>,
    name: String,
    cpu_percent: f32,
    physical_footprint_bytes: u64,
    rss_bytes: u64,
}

#[derive(Debug, Clone)]
struct ResourceSampleOwner {
    root_pid: u32,
    owned_id: Option<String>,
    label: String,
    kind: ResourceSampleSessionKind,
    workspace: String,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ResourceDiskRootRequest {
    pub repository_id: String,
    pub workspace_id: String,
    pub path: String,
    pub kind: WorkspaceDiskKind,
    pub protection: DiskProtection,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ResourceStopRequest {
    pub pid: u32,
    pub expected_pgid: Option<u32>,
    pub expected_generation: u64,
    pub owner_id: Option<String>,
    pub expected_root: Option<String>,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ResourceRootRequest {
    pub root: String,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ResourceCommandReceipt {
    pub action: String,
    pub pid: u32,
    pub pgid: u32,
    pub owner: ProcessOwner,
    pub owner_id: Option<String>,
    pub registry_generation: u64,
    pub signal: String,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ResourceUnavailable {
    pub available: bool,
    pub reason: String,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ResourceCleanupRequest {
    pub repository_id: String,
    pub workspace_id: String,
    pub repository_root: Option<String>,
    pub path: String,
    pub kind: WorkspaceDiskKind,
    pub protection: DiskProtection,
    pub expected_id: String,
    pub expected_bytes: u64,
    pub max_depth: Option<usize>,
    pub max_entries: Option<usize>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ResourceCleanupReceipt {
    pub action: String,
    pub entry_id: String,
    pub path: String,
    pub protection: DiskProtection,
    pub before_bytes: u64,
    pub after_bytes: u64,
    pub reclaimed_bytes: u64,
    pub message: String,
}

pub fn next_generation(registry: &ResourceRegistry) -> u64 {
    registry.generation.fetch_add(1, Ordering::Relaxed) + 1
}

#[tauri::command]
pub async fn read_resource_snapshot(
    registry: State<'_, ResourceRegistry>,
    terminal_registry: State<'_, crate::terminal::TerminalRegistry>,
    agent_runtime: State<'_, crate::agent_conversation::manager::AgentRuntimeManager>,
) -> Result<ResourceSnapshot, String> {
    let generation = next_generation(&registry);
    let terminal_registry = terminal_registry.inner().clone();
    let agent_runtime = agent_runtime.inner().clone();
    let snapshot = tauri::async_runtime::spawn_blocking(move || {
        read_resource_snapshot_at_generation(generation, &terminal_registry, &agent_runtime)
    })
    .await
    .map_err(|error| format!("Resource inventory task failed: {error}"))??;
    if registry.generation.load(Ordering::Relaxed) != generation {
        return Err("Resource inventory cancelled".to_string());
    }
    Ok(snapshot)
}

#[tauri::command]
pub fn cancel_resource_snapshot(registry: State<'_, ResourceRegistry>) {
    registry.generation.fetch_add(1, Ordering::Relaxed);
}

fn read_resource_snapshot_at_generation(
    generation: u64,
    terminal_registry: &crate::terminal::TerminalRegistry,
    agent_runtime: &crate::agent_conversation::manager::AgentRuntimeManager,
) -> Result<ResourceSnapshot, String> {
    let sessions = crate::terminal::list_terminal_sessions(terminal_registry)?;
    let mut hints = sessions
        .into_iter()
        .filter_map(|session| {
            let pid = session.pid?;
            let session_name = resource_terminal_name(&session);
            let cwd = session.cwd.clone();
            let owner_id = session
                .owned_id
                .clone()
                .or_else(|| {
                    session
                        .tool_terminal_identity
                        .as_ref()
                        .map(|value| value.owned_id.clone())
                })
                .or_else(|| Some(session.session_id.clone()));
            let (project_id, workspace_id) = resource_path_labels(&cwd);
            Some(ResourceOwnerHint {
                pid,
                pgid: None,
                owner: ProcessOwner::OwnedSession,
                owner_id,
                root: Some(cwd),
                project_id,
                workspace_id,
                session_name: Some(session_name),
                registry_generation: generation,
                can_stop: !session.exited,
            })
        })
        .collect::<Vec<_>>();

    // Structured agents do not own a PTY, but their ACP transport has the same
    // app-owned root process boundary. Include those roots beside terminal
    // sessions so the scanner can walk both kinds of trees.
    hints.extend(agent_runtime.resource_roots().into_iter().map(|agent| {
        let (project_id, workspace_id) = resource_path_labels(&agent.cwd);
        ResourceOwnerHint {
            pid: agent.pid,
            pgid: Some(agent.pid),
            owner: ProcessOwner::OwnedSession,
            owner_id: Some(agent.owned_id.clone()),
            root: Some(agent.cwd.clone()),
            project_id,
            workspace_id,
            session_name: Some(format!("{} agent", resource_provider_label(agent.provider))),
            registry_generation: generation,
            can_stop: true,
        }
    }));

    scan_resource_snapshot(&hints, generation)
}

/// Read the live footprint without blocking Tauri's command thread. The
/// `System` value is intentionally reused: process CPU usage is a delta, so a
/// fresh instance on every call would report zeros instead of a live sample.
#[tauri::command]
pub async fn read_resource_sample(
    registry: State<'_, ResourceRegistry>,
    terminal_registry: State<'_, crate::terminal::TerminalRegistry>,
    terminal_projection_registry: State<
        '_,
        crate::agent_conversation::terminal_projection::TerminalProjectionRegistry,
    >,
    projection_streams: State<'_, crate::projection_streams::ProjectionStreams>,
    agent_runtime: State<'_, crate::agent_conversation::manager::AgentRuntimeManager>,
    lsp_registry: State<'_, crate::files::lsp::SourceLspRegistry>,
    browser_registry: State<'_, crate::browser::BrowserRegistry>,
) -> Result<ResourceSample, String> {
    let system = Arc::clone(&registry.sample_system);
    let sampled_process_ids = Arc::clone(&registry.sampled_process_ids);
    let terminal_registry = terminal_registry.inner().clone();
    let agent_runtime = agent_runtime.inner().clone();
    let lsp_registry = lsp_registry.inner().clone();
    let projection_streams = projection_streams.inner().clone();
    let native_browser_views = browser_registry.live_view_count()?;
    let transcript_projections = terminal_projection_registry.live_watcher_count()?;

    tauri::async_runtime::spawn_blocking(move || {
        let owners = resource_sample_owners(&terminal_registry, &agent_runtime, &lsp_registry)?;
        let diagnostics = resource_diagnostics(
            &terminal_registry,
            &agent_runtime,
            &lsp_registry,
            native_browser_views,
            transcript_projections,
            projection_streams.clone(),
        )?;
        let mut system = system
            .lock()
            .map_err(|_| "Resource sampler is unavailable".to_string())?;
        let processes = observe_processes(&mut system, std::process::id());
        let mut sample = build_resource_sample(
            resource_sample_timestamp_millis(),
            std::process::id(),
            &processes,
            &owners,
        );
        let mut process_ids = sample
            .app
            .parts
            .iter()
            .map(|part| part.pid)
            .chain(sample.groups.iter().flat_map(|group| {
                group
                    .sessions
                    .iter()
                    .flat_map(|session| session.processes.iter().map(|process| process.pid))
            }))
            .collect::<Vec<_>>();
        process_ids.sort_unstable();
        process_ids.dedup();
        *sampled_process_ids
            .lock()
            .map_err(|_| "Resource sampler is unavailable".to_string())? = process_ids;
        sample.diagnostics = diagnostics;
        Ok(sample)
    })
    .await
    .map_err(|error| format!("Resource sampling task failed: {error}"))?
}

/// Refresh only the process ids already attributed to Assembly. A missing or
/// stale cache is rediscovered so the gutter is accurate before the Resource
/// Manager has opened and after an attributed helper restarts.
#[tauri::command]
pub async fn read_resource_totals(
    registry: State<'_, ResourceRegistry>,
    terminal_registry: State<'_, crate::terminal::TerminalRegistry>,
    agent_runtime: State<'_, crate::agent_conversation::manager::AgentRuntimeManager>,
    lsp_registry: State<'_, crate::files::lsp::SourceLspRegistry>,
) -> Result<ResourceSampleTotals, String> {
    let system = Arc::clone(&registry.sample_system);
    let sampled_process_ids = Arc::clone(&registry.sampled_process_ids);
    let terminal_registry = terminal_registry.inner().clone();
    let agent_runtime = agent_runtime.inner().clone();
    let lsp_registry = lsp_registry.inner().clone();

    tauri::async_runtime::spawn_blocking(move || {
        let owners = resource_sample_owners(&terminal_registry, &agent_runtime, &lsp_registry)?;
        let cached_process_ids = sampled_process_ids
            .lock()
            .map_err(|_| "Resource sampler is unavailable".to_string())?
            .clone();
        let mut process_ids = cached_process_ids.clone();
        process_ids.push(std::process::id());
        process_ids.extend(owners.iter().map(|owner| owner.root_pid));
        process_ids.sort_unstable();
        process_ids.dedup();

        let mut system = system
            .lock()
            .map_err(|_| "Resource sampler is unavailable".to_string())?;
        let sysinfo_pids = process_ids
            .iter()
            .copied()
            .map(Pid::from_u32)
            .collect::<Vec<_>>();
        system.refresh_processes(ProcessesToUpdate::Some(&sysinfo_pids), true);

        if cached_process_ids.is_empty()
            || cached_process_ids
                .iter()
                .any(|pid| system.process(Pid::from_u32(*pid)).is_none())
        {
            let processes = observe_processes(&mut system, std::process::id());
            let sample = build_resource_sample(
                resource_sample_timestamp_millis(),
                std::process::id(),
                &processes,
                &owners,
            );
            process_ids = sample
                .app
                .parts
                .iter()
                .map(|part| part.pid)
                .chain(sample.groups.iter().flat_map(|group| {
                    group
                        .sessions
                        .iter()
                        .flat_map(|session| session.processes.iter().map(|process| process.pid))
                }))
                .collect();
            process_ids.sort_unstable();
            process_ids.dedup();
            *sampled_process_ids
                .lock()
                .map_err(|_| "Resource sampler is unavailable".to_string())? = process_ids;
            return Ok(sample.totals);
        }

        let mut totals = ResourceSampleTotals {
            cpu_percent: 0.0,
            physical_footprint_bytes: 0,
            rss_bytes: 0,
            process_count: 0,
            active_source_directory_reads: ACTIVE_SOURCE_DIRECTORY_READS.load(Ordering::Relaxed),
        };
        let mut alive_process_ids = Vec::with_capacity(process_ids.len());
        for pid in process_ids {
            let Some(process) = system.process(Pid::from_u32(pid)) else {
                continue;
            };
            let rss_bytes = process.memory();
            totals.cpu_percent += process.cpu_usage();
            totals.physical_footprint_bytes +=
                mcb_core::scanners::resources::process_footprint_bytes(pid, &process.name().to_string_lossy(), rss_bytes);
            totals.rss_bytes += rss_bytes;
            totals.process_count += 1;
            alive_process_ids.push(pid);
        }
        *sampled_process_ids
            .lock()
            .map_err(|_| "Resource sampler is unavailable".to_string())? = alive_process_ids;
        Ok(totals)
    })
    .await
    .map_err(|error| format!("Resource totals task failed: {error}"))?
}

fn resource_sample_owners(
    terminal_registry: &crate::terminal::TerminalRegistry,
    agent_runtime: &crate::agent_conversation::manager::AgentRuntimeManager,
    lsp_registry: &crate::files::lsp::SourceLspRegistry,
) -> Result<Vec<ResourceSampleOwner>, String> {
    let mut sessions = crate::terminal::list_terminal_sessions(terminal_registry)?;
    sessions.sort_by_key(|session| session.started_at);
    let mut terminal_number = 0usize;
    let mut run_number = 0usize;
    let mut browser_number = 0usize;
    let mut tool_number = 0usize;
    let mut owners = Vec::new();

    for session in sessions {
        if session.exited {
            continue;
        }
        let Some(pid) = session.pid else {
            continue;
        };
        let (label, kind) = match session.kind {
            crate::terminal::TerminalKind::UserPty => {
                terminal_number += 1;
                (
                    format!("Terminal {terminal_number}"),
                    ResourceSampleSessionKind::Terminal,
                )
            }
            crate::terminal::TerminalKind::AgentTool => {
                tool_number += 1;
                (
                    format!("Agent tool {tool_number}"),
                    ResourceSampleSessionKind::Other,
                )
            }
            crate::terminal::TerminalKind::RunConfiguration => {
                run_number += 1;
                (
                    format!("Run {run_number}"),
                    ResourceSampleSessionKind::Other,
                )
            }
            crate::terminal::TerminalKind::BrowserAutomation => {
                browser_number += 1;
                (
                    format!("Browser automation {browser_number}"),
                    ResourceSampleSessionKind::Other,
                )
            }
        };
        let owned_id = session
            .owned_id
            .clone()
            .or_else(|| {
                session
                    .tool_terminal_identity
                    .as_ref()
                    .map(|identity| identity.owned_id.clone())
            })
            .or_else(|| Some(session.session_id.clone()));
        owners.push(ResourceSampleOwner {
            root_pid: pid,
            owned_id,
            label,
            kind,
            workspace: resource_workspace_label(&session.cwd),
        });
    }

    let mut conversations = agent_runtime.resource_roots();
    conversations.sort_by(|left, right| left.owned_id.cmp(&right.owned_id));
    owners.extend(
        conversations
            .into_iter()
            .map(|conversation| ResourceSampleOwner {
                root_pid: conversation.pid,
                owned_id: Some(conversation.owned_id),
                label: format!(
                    "{} conversation",
                    resource_provider_label(conversation.provider)
                ),
                kind: ResourceSampleSessionKind::Conversation,
                workspace: resource_workspace_label(&conversation.cwd),
            }),
    );

    // A language server is the most expensive thing the editor starts, and it
    // belongs to a project rather than to a session. Naming its process here is
    // what puts that cost under the project's own heading instead of leaving it
    // as an unnamed app helper — which is the whole reason the editor's switch
    // is worth having.
    owners.extend(
        lsp_registry
            .running_language_server_processes()
            .into_iter()
            .map(|server| ResourceSampleOwner {
                root_pid: server.pid,
                owned_id: None,
                label: format!("{} language server", server.server_name),
                kind: ResourceSampleSessionKind::Other,
                workspace: resource_workspace_label(&server.root),
            }),
    );
    Ok(owners)
}

fn resource_diagnostics(
    terminal_registry: &crate::terminal::TerminalRegistry,
    agent_runtime: &crate::agent_conversation::manager::AgentRuntimeManager,
    lsp_registry: &crate::files::lsp::SourceLspRegistry,
    native_browser_views: usize,
    transcript_projections: usize,
    projection_streams: crate::projection_streams::ProjectionStreams,
) -> Result<ResourceDiagnostics, String> {
    let terminal_sessions = crate::terminal::list_terminal_sessions(terminal_registry)?;
    let live_terminal_sessions = terminal_sessions
        .iter()
        .filter(|session| !session.exited)
        .collect::<Vec<_>>();
    Ok(ResourceDiagnostics {
        conversations: agent_runtime.resource_diagnostics()?,
        database: DatabaseResourceDiagnostics {
            session_store_open_handles: mcb_core::session_store::session_store_open_handles(),
            session_store_active_reads: mcb_core::session_store::session_store_active_reads(),
            session_store_active_writes: mcb_core::session_store::session_store_active_writes(),
        },
        terminals: TerminalResourceDiagnostics {
            live_sessions: live_terminal_sessions.len(),
            transcript_projections,
            user_ptys: live_terminal_sessions
                .iter()
                .filter(|session| session.kind == crate::terminal::TerminalKind::UserPty)
                .count(),
            agent_tool_ptys: live_terminal_sessions
                .iter()
                .filter(|session| session.kind == crate::terminal::TerminalKind::AgentTool)
                .count(),
            run_configurations: live_terminal_sessions
                .iter()
                .filter(|session| session.kind == crate::terminal::TerminalKind::RunConfiguration)
                .count(),
            browser_automations: live_terminal_sessions
                .iter()
                .filter(|session| session.kind == crate::terminal::TerminalKind::BrowserAutomation)
                .count(),
            exited_sessions_retained: terminal_sessions
                .iter()
                .filter(|session| session.exited)
                .count(),
        },
        streams: projection_streams.diagnostics(),
        language_servers: LanguageServerResourceDiagnostics {
            running_processes: lsp_registry.running_language_server_processes().len(),
        },
        browser: BrowserResourceDiagnostics {
            native_views: native_browser_views,
        },
    })
}

fn resource_workspace_label(cwd: &str) -> String {
    let (project, workspace) = resource_path_labels(cwd);
    match (project, workspace) {
        (Some(project), Some(workspace)) if project != workspace => {
            format!("{project} / {workspace}")
        }
        (Some(project), _) => project,
        (_, Some(workspace)) => workspace,
        _ => cwd.to_string(),
    }
}

/// A workspace folder the disk section can measure, named the way the rest of
/// the panel names it.
#[derive(Debug, Clone, PartialEq, Eq)]
pub(crate) struct ResourceWorkspaceRoot {
    pub label: String,
    pub path: PathBuf,
}

/// The workspace folders behind the sessions that are running right now, one
/// entry per folder. A checkout is preferred over a subfolder inside it, so
/// three terminals in the same repository measure that repository once.
pub(crate) fn resource_workspace_roots(
    terminal_registry: &crate::terminal::TerminalRegistry,
    agent_runtime: &crate::agent_conversation::manager::AgentRuntimeManager,
) -> Result<Vec<ResourceWorkspaceRoot>, String> {
    let mut working_directories = crate::terminal::list_terminal_sessions(terminal_registry)?
        .into_iter()
        .filter(|session| !session.exited)
        .map(|session| session.cwd)
        .collect::<Vec<_>>();
    working_directories.extend(
        agent_runtime
            .resource_roots()
            .into_iter()
            .map(|agent| agent.cwd),
    );

    let mut roots = Vec::<ResourceWorkspaceRoot>::new();
    for cwd in working_directories {
        let path = resource_workspace_root_path(&cwd);
        if roots.iter().any(|root| root.path == path) {
            continue;
        }
        roots.push(ResourceWorkspaceRoot {
            label: resource_workspace_label(&cwd),
            path,
        });
    }
    roots.sort_by(|left, right| left.label.cmp(&right.label));
    Ok(roots)
}

/// The checkout a working directory belongs to, or the folder itself when it is
/// not in a repository.
fn resource_workspace_root_path(cwd: &str) -> PathBuf {
    let path = PathBuf::from(cwd);
    let canonical = path.canonicalize().unwrap_or_else(|_| path.clone());
    git_rev_parse_path(&canonical, "--show-toplevel")
        .and_then(|root| root.canonicalize().ok())
        .unwrap_or(canonical)
}

fn resource_sample_timestamp_millis() -> u128 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .unwrap_or_default()
        .as_millis()
}

/// Every process on the machine, measured the way Activity Monitor measures
/// it and with the app's own helpers hung under the app.
///
/// The memory number is the physical footprint — Activity Monitor's Memory
/// column — where the kernel will say, and the resident size where it will
/// not (another user's process). The web view's helpers are launched by the
/// system on the app's behalf, so their parent is launchd and a walk down
/// from the app never meets them; the kernel still knows whose they are, and
/// they are given the app as their parent here so the walk finds them.
fn observe_processes(system: &mut System, app_pid: u32) -> Vec<ObservedProcess> {
    system.refresh_processes(ProcessesToUpdate::All, true);
    let app_started_at = system
        .process(Pid::from_u32(app_pid))
        .map(|process| process.start_time())
        .unwrap_or(0);
    let app_responsible = responsible_pid(app_pid);
    system
        .processes()
        .values()
        .filter(|process| process.thread_kind().is_none())
        .map(|process| {
            let pid = process.pid().as_u32();
            let name = process.name().to_string_lossy().into_owned();
            let mut parent_pid = process.parent().map(|pid| pid.as_u32());
            if webkit_helper_name(&name)
                && webkit_helper_of_app(
                    &name,
                    responsible_pid(pid),
                    process.start_time(),
                    app_pid,
                    app_responsible,
                    app_started_at,
                )
            {
                parent_pid = Some(app_pid);
            }
            let rss_bytes = process.memory();
            let physical_footprint_bytes = mcb_core::scanners::resources::process_footprint_bytes(pid, &name, rss_bytes);
            ObservedProcess {
                pid,
                parent_pid,
                name,
                cpu_percent: process.cpu_usage(),
                physical_footprint_bytes,
                rss_bytes,
            }
        })
        .collect()
}

fn webkit_helper_name(name: &str) -> bool {
    name.starts_with("com.apple.WebKit.")
}

/// Whether a WebKit helper was started for this app.
///
/// The kernel names the process each one runs on behalf of. An app opened
/// from the Finder answers for itself and its helpers name it directly; an
/// app started from a terminal is answered for by that terminal, and so are
/// its helpers, so the two are compared through what answers for them. A
/// helper older than the app was started for something else.
fn webkit_helper_of_app(
    name: &str,
    responsible: Option<u32>,
    started_at: u64,
    app_pid: u32,
    app_responsible: Option<u32>,
    app_started_at: u64,
) -> bool {
    webkit_helper_name(name)
        && started_at >= app_started_at
        && responsible.is_some_and(|responsible| {
            responsible == app_pid || Some(responsible) == app_responsible
        })
}

/// The process the kernel holds responsible for `pid` — the app for a helper
/// it started, the terminal for a program run from one — or `None` when it
/// will not say.
#[cfg(target_os = "macos")]
fn responsible_pid(pid: u32) -> Option<u32> {
    extern "C" {
        fn responsibility_get_pid_responsible_for_pid(pid: libc::pid_t) -> libc::pid_t;
    }
    // SAFETY: the call takes a pid by value and answers with one; it touches
    // no memory of ours.
    let answer = unsafe { responsibility_get_pid_responsible_for_pid(pid as libc::pid_t) };
    (answer > 0).then_some(answer as u32)
}

#[cfg(not(target_os = "macos"))]
fn responsible_pid(_pid: u32) -> Option<u32> {
    None
}

fn descendant_distances(root_pid: u32, processes: &[ObservedProcess]) -> Vec<(u32, usize)> {
    let process_ids = processes
        .iter()
        .map(|process| process.pid)
        .collect::<HashSet<_>>();
    if !process_ids.contains(&root_pid) {
        return Vec::new();
    }

    let mut children = HashMap::<u32, Vec<u32>>::new();
    for process in processes {
        if let Some(parent_pid) = process.parent_pid {
            children.entry(parent_pid).or_default().push(process.pid);
        }
    }
    for child_pids in children.values_mut() {
        child_pids.sort_unstable();
    }

    let mut queue = VecDeque::from([(root_pid, 0usize)]);
    let mut visited = HashSet::new();
    let mut descendants = Vec::new();
    while let Some((pid, distance)) = queue.pop_front() {
        if !visited.insert(pid) {
            continue;
        }
        descendants.push((pid, distance));
        if let Some(child_pids) = children.get(&pid) {
            queue.extend(
                child_pids
                    .iter()
                    .map(|child_pid| (*child_pid, distance + 1)),
            );
        }
    }
    descendants
}

fn descendant_process_ids(root_pid: u32, processes: &[ObservedProcess]) -> Vec<u32> {
    descendant_distances(root_pid, processes)
        .into_iter()
        .map(|(pid, _)| pid)
        .collect()
}

fn sample_process(process: &ObservedProcess) -> ResourceSampleProcess {
    ResourceSampleProcess {
        pid: process.pid,
        name: if process.name.trim().is_empty() {
            format!("Process {}", process.pid)
        } else {
            process.name.clone()
        },
        cpu_percent: process.cpu_percent,
        physical_footprint_bytes: process.physical_footprint_bytes,
        rss_bytes: process.rss_bytes,
    }
}

fn app_part_label(process: &ObservedProcess, app_pid: u32) -> String {
    if process.pid == app_pid {
        return "Main process".to_string();
    }
    // The web view's helpers, named as Activity Monitor names them.
    match process.name.as_str() {
        "com.apple.WebKit.WebContent" => return "Web content".to_string(),
        "com.apple.WebKit.Networking" => return "Networking".to_string(),
        "com.apple.WebKit.GPU" => return "Graphics and Media".to_string(),
        _ => {}
    }
    let name = process.name.to_ascii_lowercase();
    if name.contains("webview") || name.contains("webkit") || name.contains("renderer") {
        "Webview / renderer".to_string()
    } else if name.contains("node") || name.contains("acp") || name.contains("adapter") {
        "ACP bridge / node".to_string()
    } else if process.name.trim().is_empty() {
        "App helper".to_string()
    } else {
        format!("App helper / {}", process.name)
    }
}

const RESOURCE_PROCESS_CATEGORIES: [&str; 9] = [
    "Main app",
    "WebContent",
    "Networking",
    "Graphics and Media/GPU",
    "AutoFill",
    "ACP adapters/sidecars",
    "PTYs",
    "LSPs",
    "Unknown / unattributed",
];

fn resource_owner_category(owner: &ResourceSampleOwner) -> &'static str {
    if owner.kind == ResourceSampleSessionKind::Conversation {
        "ACP adapters/sidecars"
    } else if owner.kind == ResourceSampleSessionKind::Terminal
        || owner.label.starts_with("Agent tool ")
    {
        "PTYs"
    } else if owner.label.ends_with(" language server") {
        "LSPs"
    } else {
        "Unknown / unattributed"
    }
}

fn resource_app_process_category(process: &ObservedProcess, app_pid: u32) -> &'static str {
    if process.pid == app_pid {
        return "Main app";
    }
    match process.name.as_str() {
        "com.apple.WebKit.WebContent" => "WebContent",
        "com.apple.WebKit.Networking" => "Networking",
        "com.apple.WebKit.GPU" => "Graphics and Media/GPU",
        "AutoFillPanelService" | "com.apple.AutoFillPanel" => "AutoFill",
        _ => "Unknown / unattributed",
    }
}

fn resource_process_categories(
    app_pid: u32,
    processes: &[ObservedProcess],
    claims: &HashMap<u32, (usize, usize)>,
    app_process_ids: &HashSet<u32>,
    owners: &[ResourceSampleOwner],
) -> Vec<ResourceSampleCategory> {
    let mut totals = RESOURCE_PROCESS_CATEGORIES
        .into_iter()
        .map(|label| (label, (0.0f32, 0u64, 0u64, 0usize)))
        .collect::<BTreeMap<_, _>>();
    for process in processes {
        let Some(category) = claims
            .get(&process.pid)
            .map(|(owner_index, _)| resource_owner_category(&owners[*owner_index]))
            .or_else(|| {
                app_process_ids
                    .contains(&process.pid)
                    .then(|| resource_app_process_category(process, app_pid))
            })
        else {
            continue;
        };
        let entry = totals
            .get_mut(category)
            .expect("every process category has a serialized row");
        entry.0 += process.cpu_percent;
        entry.1 += process.physical_footprint_bytes;
        entry.2 += process.rss_bytes;
        entry.3 += 1;
    }
    RESOURCE_PROCESS_CATEGORIES
        .into_iter()
        .map(|label| {
            let (cpu_percent, physical_footprint_bytes, rss_bytes, process_count) = totals
                .get(label)
                .copied()
                .expect("every process category has a serialized row");
            ResourceSampleCategory {
                label,
                cpu_percent,
                physical_footprint_bytes,
                rss_bytes,
                process_count,
            }
        })
        .collect()
}

fn build_resource_sample(
    generated_at_ms: u128,
    app_pid: u32,
    processes: &[ObservedProcess],
    owners: &[ResourceSampleOwner],
) -> ResourceSample {
    // A process can sit beneath two roots during a handoff. Attribute it to
    // the closest registered root so nested conversations never count twice.
    let mut claims = HashMap::<u32, (usize, usize)>::new();
    for (owner_index, owner) in owners.iter().enumerate() {
        for (pid, distance) in descendant_distances(owner.root_pid, processes) {
            match claims.get_mut(&pid) {
                Some((claimed_owner, claimed_distance)) if distance < *claimed_distance => {
                    *claimed_owner = owner_index;
                    *claimed_distance = distance;
                }
                None => {
                    claims.insert(pid, (owner_index, distance));
                }
                _ => {}
            }
        }
    }

    let process_by_pid = processes
        .iter()
        .map(|process| (process.pid, process))
        .collect::<HashMap<_, _>>();
    let mut sessions_by_workspace = BTreeMap::<String, Vec<ResourceSampleSession>>::new();
    for (owner_index, owner) in owners.iter().enumerate() {
        let mut owned_processes = claims
            .iter()
            .filter_map(|(pid, (claimed_owner, _))| {
                (*claimed_owner == owner_index)
                    .then(|| process_by_pid.get(pid).copied())
                    .flatten()
            })
            .map(sample_process)
            .collect::<Vec<_>>();
        owned_processes.sort_by(|left, right| {
            right
                .physical_footprint_bytes
                .cmp(&left.physical_footprint_bytes)
                .then_with(|| left.pid.cmp(&right.pid))
        });
        if owned_processes.is_empty() {
            continue;
        }
        sessions_by_workspace
            .entry(owner.workspace.clone())
            .or_default()
            .push(ResourceSampleSession {
                owned_id: owner.owned_id.clone(),
                label: owner.label.clone(),
                kind: owner.kind,
                root_pid: owner.root_pid,
                processes: owned_processes,
            });
    }
    let groups = sessions_by_workspace
        .into_iter()
        .map(|(workspace, sessions)| ResourceSampleGroup {
            workspace,
            sessions,
        })
        .collect::<Vec<_>>();

    let app_process_ids = descendant_process_ids(app_pid, processes)
        .into_iter()
        .filter(|pid| !claims.contains_key(pid))
        .collect::<HashSet<_>>();
    let mut app_parts = processes
        .iter()
        .filter(|process| app_process_ids.contains(&process.pid))
        .map(|process| ResourceSampleAppPart {
            label: app_part_label(process, app_pid),
            pid: process.pid,
            cpu_percent: process.cpu_percent,
            physical_footprint_bytes: process.physical_footprint_bytes,
            rss_bytes: process.rss_bytes,
        })
        .collect::<Vec<_>>();
    app_parts.sort_by(|left, right| {
        (left.pid != app_pid)
            .cmp(&(right.pid != app_pid))
            .then_with(|| {
                right
                    .physical_footprint_bytes
                    .cmp(&left.physical_footprint_bytes)
            })
            .then_with(|| left.pid.cmp(&right.pid))
    });

    let included = processes
        .iter()
        .filter(|process| {
            claims.contains_key(&process.pid) || app_process_ids.contains(&process.pid)
        })
        .collect::<Vec<_>>();
    let process_categories =
        resource_process_categories(app_pid, processes, &claims, &app_process_ids, owners);
    ResourceSample {
        generated_at_ms,
        totals: ResourceSampleTotals {
            cpu_percent: included.iter().map(|process| process.cpu_percent).sum(),
            physical_footprint_bytes: included
                .iter()
                .map(|process| process.physical_footprint_bytes)
                .sum(),
            rss_bytes: included.iter().map(|process| process.rss_bytes).sum(),
            process_count: included.len(),
            active_source_directory_reads: ACTIVE_SOURCE_DIRECTORY_READS.load(Ordering::Relaxed),
        },
        diagnostics: ResourceDiagnostics::default(),
        app: ResourceSampleApp { parts: app_parts },
        process_categories,
        groups,
    }
}

fn resource_terminal_name(session: &crate::terminal::TerminalSessionInfo) -> String {
    match session.kind {
        crate::terminal::TerminalKind::AgentTool => session
            .tool_terminal_identity
            .as_ref()
            .map(|identity| format!("Agent tool {}", identity.tool_call_id))
            .unwrap_or_else(|| "Agent tool".to_string()),
        crate::terminal::TerminalKind::RunConfiguration => "Run configuration".to_string(),
        crate::terminal::TerminalKind::BrowserAutomation => "Browser automation".to_string(),
        crate::terminal::TerminalKind::UserPty => "Terminal".to_string(),
    }
}

fn resource_provider_label(
    provider: crate::agent_conversation::protocol::AgentConversationProvider,
) -> &'static str {
    &provider.descriptor().display_name
}

type ResourceIdentity = (Option<String>, Option<String>);

fn resource_path_labels(cwd: &str) -> ResourceIdentity {
    static CACHE: OnceLock<Mutex<HashMap<PathBuf, ResourceIdentity>>> = OnceLock::new();
    let path = PathBuf::from(cwd);
    let cache_key = path.canonicalize().unwrap_or_else(|_| path.clone());
    let cache = CACHE.get_or_init(|| Mutex::new(HashMap::new()));
    if let Ok(entries) = cache.lock() {
        if let Some(identity) = entries.get(&cache_key) {
            return identity.clone();
        }
    }

    let identity = git_resource_path_labels(&cache_key)
        .unwrap_or_else(|| heuristic_resource_path_labels(&cache_key));
    if let Ok(mut entries) = cache.lock() {
        entries.insert(cache_key, identity.clone());
    }
    identity
}

fn git_resource_path_labels(cwd: &Path) -> Option<ResourceIdentity> {
    let git_root = git_rev_parse_path(cwd, "--show-toplevel")?;
    let git_common_dir = git_rev_parse_path(cwd, "--git-common-dir")?;
    let project = git_common_dir.parent().and_then(path_leaf).or_else(|| {
        git_root
            .file_name()
            .map(|value| value.to_string_lossy().to_string())
    });
    let workspace = git_root
        .file_name()
        .map(|value| value.to_string_lossy().to_string());
    Some((project, workspace))
}

fn git_rev_parse_path(cwd: &Path, argument: &str) -> Option<PathBuf> {
    let absolute_output = Command::new("git")
        .current_dir(cwd)
        .args(["rev-parse", "--path-format=absolute", argument])
        .output()
        .ok();
    let output = absolute_output
        .filter(|output| output.status.success())
        .or_else(|| {
            Command::new("git")
                .current_dir(cwd)
                .args(["rev-parse", argument])
                .output()
                .ok()
                .filter(|output| output.status.success())
        })?;
    let value = String::from_utf8_lossy(&output.stdout).trim().to_string();
    if value.is_empty() {
        None
    } else {
        let path = PathBuf::from(value);
        Some(if path.is_absolute() {
            path
        } else {
            cwd.join(path)
        })
    }
}

fn path_leaf(path: &Path) -> Option<String> {
    path.file_name()
        .map(|value| value.to_string_lossy().to_string())
}

fn heuristic_resource_path_labels(path: &Path) -> ResourceIdentity {
    let workspace = path_leaf(path);
    let project = path.parent().and_then(path_leaf);
    (project, workspace)
}

#[tauri::command]
pub async fn read_resource_disk_scan(
    registry: State<'_, ResourceRegistry>,
    roots: Vec<ResourceDiskRootRequest>,
    max_depth: Option<usize>,
    max_entries: Option<usize>,
) -> Result<DiskScanReport, String> {
    let roots = roots
        .into_iter()
        .map(|root| {
            let path = PathBuf::from(root.path);
            validate_disk_scan_root(&path)?;
            Ok(DiskScanRoot {
                repository_id: root.repository_id,
                workspace_id: root.workspace_id,
                path,
                kind: root.kind,
                protection: root.protection,
            })
        })
        .collect::<Result<Vec<_>, String>>()?;
    let generation = registry
        .disk_scan_generation
        .fetch_add(1, Ordering::Relaxed)
        + 1;
    let active_generation = Arc::clone(&registry.disk_scan_generation);
    tauri::async_runtime::spawn_blocking(move || {
        scan_disk_roots(
            &roots,
            DiskScanOptions {
                max_depth: max_depth.unwrap_or(3),
                max_entries: max_entries.unwrap_or(2_000),
            },
            || active_generation.load(Ordering::Relaxed) == generation,
        )
    })
    .await
    .map_err(|error| format!("Resource disk scan task failed: {error}"))?
}

#[tauri::command]
pub fn cancel_resource_disk_scan(registry: State<'_, ResourceRegistry>) {
    registry
        .disk_scan_generation
        .fetch_add(1, Ordering::Relaxed);
}

#[tauri::command]
pub async fn cleanup_workspace_disk_entry(
    request: ResourceCleanupRequest,
) -> Result<ResourceCleanupReceipt, String> {
    if !disk_protection_allows_cleanup(request.protection) {
        return Err("Only SafeCandidate disk entries can be cleaned".to_string());
    }
    if !matches!(
        request.kind,
        WorkspaceDiskKind::Worktree
            | WorkspaceDiskKind::BuildOutput
            | WorkspaceDiskKind::DependencyCache
            | WorkspaceDiskKind::AgentData
    ) {
        return Err("This disk entry kind is not eligible for cleanup".to_string());
    }

    let path = PathBuf::from(&request.path);
    validate_cleanup_path(&path)?;
    let canonical = path
        .canonicalize()
        .map_err(|error| format!("Cleanup entry is unavailable: {error}"))?;
    let entry_id = stable_entry_id(&request.repository_id, &request.workspace_id, &canonical);
    if entry_id != request.expected_id {
        return Err("The disk entry identity changed; scan again before cleaning".to_string());
    }
    let before = scan_disk_entry(&request, &canonical)?;
    if before != request.expected_bytes {
        return Err("The disk entry size changed; scan again before cleaning".to_string());
    }

    let message = if request.kind == WorkspaceDiskKind::Worktree {
        let repository_root = request
            .repository_root
            .as_deref()
            .map(PathBuf::from)
            .or_else(|| derive_repository_root(&canonical))
            .ok_or_else(|| "A worktree cleanup needs a discoverable repository root".to_string())?;
        let _result =
            crate::remove_project_worktree_sync(repository_root, canonical.clone(), false)?;
        "Removed the clean worktree through the existing Git safety route".to_string()
    } else {
        if fs::symlink_metadata(&path)
            .map_err(|error| format!("Cleanup entry is unavailable: {error}"))?
            .file_type()
            .is_symlink()
        {
            return Err("Symlink cleanup is refused".to_string());
        }
        fs::remove_dir_all(&canonical)
            .map_err(|error| format!("Could not remove the safe cleanup entry: {error}"))?;
        "Removed the explicit safe cleanup entry after revalidation".to_string()
    };

    let after = if canonical.exists() {
        scan_disk_entry(&request, &canonical)?
    } else {
        0
    };

    Ok(ResourceCleanupReceipt {
        action: "cleanup-workspace-disk-entry".to_string(),
        entry_id: request.expected_id,
        path: canonical.display().to_string(),
        protection: request.protection,
        before_bytes: before,
        after_bytes: after,
        reclaimed_bytes: before.saturating_sub(after),
        message,
    })
}

fn derive_repository_root(worktree: &PathBuf) -> Option<PathBuf> {
    let output = std::process::Command::new("git")
        .args([
            "-C",
            worktree.to_str()?,
            "rev-parse",
            "--path-format=absolute",
            "--git-common-dir",
        ])
        .output()
        .ok()?;
    if !output.status.success() {
        return None;
    }
    let common_dir = PathBuf::from(String::from_utf8_lossy(&output.stdout).trim());
    common_dir.parent().map(PathBuf::from)
}

#[tauri::command]
pub async fn stop_owned_resource(
    request: ResourceStopRequest,
    registry: State<'_, ResourceRegistry>,
    terminal_registry: State<'_, crate::terminal::TerminalRegistry>,
    agent_runtime: State<'_, crate::agent_conversation::manager::AgentRuntimeManager>,
) -> Result<ResourceCommandReceipt, String> {
    if registry.generation.load(Ordering::Acquire) != request.expected_generation {
        return Err(resource_action_error_message(
            ResourceActionError::StaleSnapshot,
        ));
    }
    let snapshot = read_resource_snapshot_at_generation(
        request.expected_generation,
        &terminal_registry,
        &agent_runtime,
    )?;
    if registry.generation.load(Ordering::Acquire) != request.expected_generation {
        return Err(resource_action_error_message(
            ResourceActionError::StaleSnapshot,
        ));
    }
    let process = snapshot
        .processes
        .iter()
        .find(|process| process.pid == request.pid)
        .ok_or_else(|| "The process is no longer in the resource snapshot".to_string())?;
    validate_resource_action(process, request.expected_generation, request.expected_pgid)
        .map_err(resource_action_error_message)?;
    if request.owner_id.as_deref() != process.owner_id.as_deref() {
        return Err("The resource owner changed; refresh before stopping it".to_string());
    }
    if request.expected_root.as_deref() != process.root.as_deref() {
        return Err("The resource root changed; refresh before stopping it".to_string());
    }
    if process.pgid == 0 {
        return Err("The resource has no safe process group".to_string());
    }

    // Stop the proven process itself. The process group is still rechecked and
    // recorded, but a group-wide signal could include an unowned sibling.
    let result = unsafe { libc::kill(process.pid as i32, libc::SIGTERM) };
    if result != 0 {
        return Err(format!(
            "Could not stop owned resource: {}",
            std::io::Error::last_os_error()
        ));
    }
    Ok(ResourceCommandReceipt {
        action: "stop-owned-resource".to_string(),
        pid: process.pid,
        pgid: process.pgid,
        owner: process.owner,
        owner_id: process.owner_id.clone(),
        registry_generation: snapshot.generation,
        signal: "SIGTERM".to_string(),
    })
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ResourceStopTreeRequest {
    /// The row the person pressed stop on: either a session's root process or
    /// one process inside that session's tree.
    pub root_pid: u32,
    /// The session the row belongs to, when the panel knows it.
    pub owned_id: Option<String>,
    /// Exactly the process ids the confirmation dialog named.
    pub expected_pids: Vec<u32>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ResourceStopTreeReceipt {
    pub action: String,
    pub root_pid: u32,
    pub owned_id: Option<String>,
    pub signal: String,
    pub follow_up_signal: String,
    pub grace_seconds: u64,
    pub stopped_pids: Vec<u32>,
    pub already_gone_pids: Vec<u32>,
    pub message: String,
}

/// What a stop request resolves to before any signal is sent. Keeping the
/// choice of targets separate from the sending is what lets it be tested
/// without a real process anywhere near it.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ResourceStopPlan {
    pub owner_index: usize,
    pub root_pid: u32,
    /// Deepest process first, so children get the signal before their parent
    /// can restart them.
    pub targets: Vec<u32>,
    /// Process ids the dialog named that have already exited.
    pub already_gone: Vec<u32>,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ResourceStopTargetError {
    ProtectedPid,
    UnownedRoot,
    RootGone,
    RequestedOutsideTree,
}

fn resource_stop_target_message(error: ResourceStopTargetError) -> String {
    match error {
        ResourceStopTargetError::ProtectedPid => {
            "That process is the app itself and cannot be stopped from here".to_string()
        }
        ResourceStopTargetError::UnownedRoot => {
            "Only processes the app started can be stopped from this panel".to_string()
        }
        ResourceStopTargetError::RootGone => {
            "That process has already exited; refresh the panel".to_string()
        }
        ResourceStopTargetError::RequestedOutsideTree => {
            "The process tree changed since it was listed; refresh and try again".to_string()
        }
    }
}

/// Work out which process ids a stop request may signal.
///
/// The rules, in order: the app's own process is never a target; the row has to
/// sit inside a tree the app started; the tree is re-read now rather than
/// trusted from the panel; and every still-running process the dialog named has
/// to still belong to that tree, which is what catches a process id that was
/// reused by something else between the sample and the click.
fn select_stop_targets(
    root_pid: u32,
    app_pid: u32,
    owners: &[ResourceSampleOwner],
    processes: &[ObservedProcess],
    requested: &[u32],
) -> Result<ResourceStopPlan, ResourceStopTargetError> {
    if root_pid <= 1 || root_pid == app_pid {
        return Err(ResourceStopTargetError::ProtectedPid);
    }

    let mut targets = descendant_distances(root_pid, processes);
    if targets.is_empty() {
        return Err(ResourceStopTargetError::RootGone);
    }

    let owner_index = owners
        .iter()
        .enumerate()
        .filter_map(|(index, owner)| {
            descendant_distances(owner.root_pid, processes)
                .into_iter()
                .find(|(pid, _)| *pid == root_pid)
                .map(|(_, distance)| (distance, index))
        })
        .min()
        .map(|(_, index)| index)
        .ok_or(ResourceStopTargetError::UnownedRoot)?;

    if targets.iter().any(|(pid, _)| *pid == app_pid) {
        return Err(ResourceStopTargetError::ProtectedPid);
    }
    targets.sort_by(|left, right| right.1.cmp(&left.1).then_with(|| left.0.cmp(&right.0)));
    let target_pids = targets.iter().map(|(pid, _)| *pid).collect::<Vec<_>>();
    let target_set = target_pids.iter().copied().collect::<HashSet<_>>();

    let alive = processes
        .iter()
        .map(|process| process.pid)
        .collect::<HashSet<_>>();
    let mut already_gone = Vec::new();
    for pid in requested {
        if !alive.contains(pid) {
            already_gone.push(*pid);
        } else if !target_set.contains(pid) {
            return Err(ResourceStopTargetError::RequestedOutsideTree);
        }
    }
    already_gone.sort_unstable();

    Ok(ResourceStopPlan {
        owner_index,
        root_pid,
        targets: target_pids,
        already_gone,
    })
}

/// Stop one session's process tree, or one stray process inside it.
///
/// Nothing here runs on its own: the panel only ever calls this after a person
/// has read a dialog naming the exact process ids. Each target is asked to stop
/// first, and only a process still running five seconds later is forced.
#[tauri::command]
pub async fn stop_resource_process_tree(
    request: ResourceStopTreeRequest,
    registry: State<'_, ResourceRegistry>,
    terminal_registry: State<'_, crate::terminal::TerminalRegistry>,
    agent_runtime: State<'_, crate::agent_conversation::manager::AgentRuntimeManager>,
    lsp_registry: State<'_, crate::files::lsp::SourceLspRegistry>,
) -> Result<ResourceStopTreeReceipt, String> {
    let system = Arc::clone(&registry.sample_system);
    let terminal_registry = terminal_registry.inner().clone();
    let agent_runtime = agent_runtime.inner().clone();
    let lsp_registry = lsp_registry.inner().clone();

    tauri::async_runtime::spawn_blocking(move || {
        // The same owner list the panel was drawn from, language servers
        // included, so a row a person can see is a row they can act on.
        let owners = resource_sample_owners(&terminal_registry, &agent_runtime, &lsp_registry)?;
        let processes = {
            let mut system = system
                .lock()
                .map_err(|_| "Resource sampler is unavailable".to_string())?;
            observe_processes(&mut system, std::process::id())
        };

        let plan = select_stop_targets(
            request.root_pid,
            std::process::id(),
            &owners,
            &processes,
            &request.expected_pids,
        )
        .map_err(resource_stop_target_message)?;

        let owner = &owners[plan.owner_index];
        if let (Some(expected), Some(actual)) = (request.owned_id.as_deref(), owner.owned_id.as_deref())
        {
            if expected != actual {
                return Err("The session that owns this process changed; refresh first".to_string());
            }
        }

        let mut stopped = Vec::new();
        for pid in &plan.targets {
            if unsafe { libc::kill(*pid as i32, libc::SIGTERM) } == 0 {
                stopped.push(*pid);
            } else {
                let error = std::io::Error::last_os_error();
                if error.raw_os_error() == Some(libc::ESRCH) {
                    continue;
                }
                stderr_log!("resources: could not stop process {pid}: {error}");
            }
        }
        if stopped.is_empty() {
            return Err("Nothing was left to stop; refresh the panel".to_string());
        }

        force_stop_survivors_after_grace(stopped.clone());

        let message = format!(
            "Asked {} {} to stop; anything still running in {RESOURCE_STOP_GRACE_SECONDS}s is forced.",
            stopped.len(),
            if stopped.len() == 1 {
                "process"
            } else {
                "processes"
            }
        );
        Ok(ResourceStopTreeReceipt {
            action: "stop-resource-process-tree".to_string(),
            root_pid: plan.root_pid,
            owned_id: owner.owned_id.clone(),
            signal: "SIGTERM".to_string(),
            follow_up_signal: "SIGKILL".to_string(),
            grace_seconds: RESOURCE_STOP_GRACE_SECONDS,
            stopped_pids: stopped,
            already_gone_pids: plan.already_gone,
            message,
        })
    })
    .await
    .map_err(|error| format!("Resource stop task failed: {error}"))?
}

/// Wait out the grace period on a thread of its own, then force whatever is
/// still running. A process that already exited is left alone: its id could
/// belong to something else by then, so the check comes first.
fn force_stop_survivors_after_grace(pids: Vec<u32>) {
    std::thread::spawn(move || {
        std::thread::sleep(std::time::Duration::from_secs(RESOURCE_STOP_GRACE_SECONDS));
        for pid in pids {
            if unsafe { libc::kill(pid as i32, 0) } != 0 {
                continue;
            }
            if unsafe { libc::kill(pid as i32, libc::SIGKILL) } == 0 {
                stderr_log!("resources: forced process {pid} after the stop grace period");
            }
        }
    });
}

#[tauri::command]
pub fn restart_language_server_root(
    _request: ResourceRootRequest,
) -> Result<ResourceUnavailable, String> {
    Err("Language-server restart belongs to the isolated lifecycle lane".to_string())
}

#[tauri::command]
pub async fn set_active_source_root(
    request: ResourceRootRequest,
    registry: State<'_, ResourceRegistry>,
) -> Result<String, String> {
    let path = PathBuf::from(request.root);
    let canonical = path
        .canonicalize()
        .map_err(|error| format!("Source root is unavailable: {error}"))?;
    if !canonical.is_dir() {
        return Err("Source root is not a directory".to_string());
    }
    let mut active = registry
        .active_source_root
        .lock()
        .map_err(|_| "Resource registry is unavailable".to_string())?;
    *active = Some(canonical.clone());
    Ok(canonical.display().to_string())
}

#[tauri::command]
pub fn apply_resource_memory_pressure(_level: String) -> Result<ResourceUnavailable, String> {
    Err("Memory-pressure actions are not available until the lifecycle lane is rebuilt".to_string())
}

#[tauri::command]
pub fn read_language_server_log(
    _request: ResourceRootRequest,
) -> Result<ResourceUnavailable, String> {
    Err("Language-server logs belong to the isolated lifecycle lane".to_string())
}

fn resource_action_error_message(error: ResourceActionError) -> String {
    match error {
        ResourceActionError::External => "External resources cannot be stopped".to_string(),
        ResourceActionError::StaleSnapshot => {
            "The resource snapshot is stale; refresh first".to_string()
        }
        ResourceActionError::ProcessGroupChanged => {
            "The process group changed; refresh first".to_string()
        }
    }
}

fn validate_disk_scan_root(path: &PathBuf) -> Result<(), String> {
    let canonical = path
        .canonicalize()
        .map_err(|error| format!("Disk scan root is unavailable: {error}"))?;
    if canonical.parent().is_none() || canonical == PathBuf::from("/") {
        return Err("The filesystem root is not an explicit workspace root".to_string());
    }
    if let Some(home) = std::env::var_os("HOME")
        .map(PathBuf::from)
        .and_then(|path| path.canonicalize().ok())
    {
        if canonical == home {
            return Err("The home folder is not an explicit workspace root".to_string());
        }
    }
    Ok(())
}

fn validate_cleanup_path(path: &PathBuf) -> Result<(), String> {
    if path == &PathBuf::from("/") {
        return Err("The filesystem root cannot be cleaned".to_string());
    }
    let metadata = fs::symlink_metadata(path)
        .map_err(|error| format!("Cleanup entry is unavailable: {error}"))?;
    if !metadata.is_dir() {
        return Err("Cleanup entry is not a directory".to_string());
    }
    if metadata.file_type().is_symlink() {
        return Err("Symlink cleanup is refused".to_string());
    }
    let canonical = path
        .canonicalize()
        .map_err(|error| format!("Cleanup entry is unavailable: {error}"))?;
    if canonical.parent().is_none() {
        return Err("The filesystem root cannot be cleaned".to_string());
    }
    if let Some(home) = std::env::var_os("HOME")
        .map(PathBuf::from)
        .and_then(|value| value.canonicalize().ok())
    {
        if canonical == home {
            return Err("The home folder cannot be cleaned".to_string());
        }
    }
    Ok(())
}

fn scan_disk_entry(request: &ResourceCleanupRequest, canonical: &PathBuf) -> Result<u64, String> {
    let report = scan_disk_roots(
        &[DiskScanRoot {
            repository_id: request.repository_id.clone(),
            workspace_id: request.workspace_id.clone(),
            path: canonical.clone(),
            kind: request.kind,
            protection: request.protection,
        }],
        DiskScanOptions {
            max_depth: request.max_depth.unwrap_or(3),
            max_entries: request.max_entries.unwrap_or(2_000),
        },
        || true,
    )?;
    report
        .entries
        .first()
        .map(|entry| entry.bytes)
        .ok_or_else(|| "Cleanup entry disappeared during revalidation".to_string())
}

#[cfg(test)]
mod tests {
    use super::*;

    fn observed(
        pid: u32,
        parent_pid: Option<u32>,
        name: &str,
        cpu_percent: f32,
        physical_footprint_bytes: u64,
    ) -> ObservedProcess {
        ObservedProcess {
            pid,
            parent_pid,
            name: name.to_string(),
            cpu_percent,
            physical_footprint_bytes,
            rss_bytes: physical_footprint_bytes,
        }
    }

    fn owner(
        root_pid: u32,
        owned_id: &str,
        label: &str,
        kind: ResourceSampleSessionKind,
        workspace: &str,
    ) -> ResourceSampleOwner {
        ResourceSampleOwner {
            root_pid,
            owned_id: Some(owned_id.to_string()),
            label: label.to_string(),
            kind,
            workspace: workspace.to_string(),
        }
    }

    fn fixture_root(label: &str) -> PathBuf {
        std::env::temp_dir().join(format!(
            "mcb-resources-{label}-{}-{}",
            std::process::id(),
            SystemTime::now()
                .duration_since(UNIX_EPOCH)
                .expect("system clock should be after the epoch")
                .as_nanos()
        ))
    }

    fn git(cwd: &Path, args: &[&str]) -> std::process::Output {
        Command::new("git")
            .current_dir(cwd)
            .args(args)
            .output()
            .expect("fixture git command should start")
    }

    #[test]
    fn resource_identity_uses_git_repository_and_worktree_roots() {
        let root = fixture_root("identity");
        let repository = root.join("fixture-repo");
        fs::create_dir_all(&repository).expect("fixture repository should be created");
        assert!(git(&repository, &["init", "--quiet"]).status.success());
        fs::write(repository.join("README.md"), "fixture\n")
            .expect("fixture file should be written");
        assert!(git(&repository, &["add", "."]).status.success());
        assert!(git(
            &repository,
            &[
                "-c",
                "user.name=Resource Fixture",
                "-c",
                "user.email=resource-fixture@example.test",
                "commit",
                "--quiet",
                "-m",
                "fixture",
            ]
        )
        .status
        .success());

        let primary_nested = repository.join("src").join("nested");
        fs::create_dir_all(&primary_nested).expect("nested primary checkout should be created");
        assert_eq!(
            resource_path_labels(
                primary_nested
                    .to_str()
                    .expect("primary path should be UTF-8")
            ),
            (
                Some("fixture-repo".to_string()),
                Some("fixture-repo".to_string())
            )
        );

        let worktree = root.join("feature-worktree");
        assert!(git(
            &repository,
            &[
                "worktree",
                "add",
                "--detach",
                "--quiet",
                worktree.to_str().expect("worktree path should be UTF-8"),
            ]
        )
        .status
        .success());
        let worktree_nested = worktree.join("src").join("nested");
        fs::create_dir_all(&worktree_nested).expect("nested worktree checkout should be created");
        assert_eq!(
            resource_path_labels(
                worktree_nested
                    .to_str()
                    .expect("worktree path should be UTF-8")
            ),
            (
                Some("fixture-repo".to_string()),
                Some("feature-worktree".to_string())
            )
        );

        let plain = root.join("plain").join("nested");
        fs::create_dir_all(&plain).expect("non-git fixture should be created");
        assert_eq!(
            resource_path_labels(plain.to_str().expect("plain path should be UTF-8")),
            (Some("plain".to_string()), Some("nested".to_string()))
        );

        fs::remove_dir_all(root).expect("resource identity fixture should be cleaned up");
    }

    #[test]
    fn descendant_walk_includes_the_root_and_every_generation_once() {
        let processes = vec![
            observed(1, None, "app", 0.0, 1),
            observed(2, Some(1), "child-a", 0.0, 1),
            observed(3, Some(2), "grandchild", 0.0, 1),
            observed(4, Some(1), "child-b", 0.0, 1),
            observed(5, Some(99), "unrelated", 0.0, 1),
        ];

        assert_eq!(descendant_process_ids(1, &processes), vec![1, 2, 4, 3]);
        assert!(descendant_process_ids(99, &processes).is_empty());
    }

    #[test]
    fn sample_groups_owned_trees_and_keeps_unclaimed_app_parts() {
        let processes = vec![
            observed(1, None, "MacCommandBar", 1.0, 100),
            observed(2, Some(1), "com.apple.WebKit.WebContent", 2.0, 90),
            observed(10, Some(1), "zsh", 3.0, 80),
            observed(11, Some(10), "cargo", 4.0, 70),
            observed(20, Some(1), "acp-adapter", 5.0, 60),
            observed(21, Some(20), "node", 6.0, 50),
            observed(99, None, "external", 90.0, 900),
        ];
        let owners = vec![
            owner(
                10,
                "terminal-a",
                "Terminal 1",
                ResourceSampleSessionKind::Terminal,
                "project / workspace",
            ),
            owner(
                20,
                "conversation-a",
                "Agent conversation",
                ResourceSampleSessionKind::Conversation,
                "project / workspace",
            ),
        ];

        let sample = build_resource_sample(123, 1, &processes, &owners);

        assert_eq!(sample.generated_at_ms, 123);
        assert_eq!(sample.totals.process_count, 6);
        assert_eq!(sample.totals.physical_footprint_bytes, 450);
        assert_eq!(sample.totals.cpu_percent, 21.0);
        assert_eq!(sample.app.parts.len(), 2);
        assert_eq!(sample.app.parts[0].label, "Main process");
        assert_eq!(sample.app.parts[1].label, "Web content");
        assert_eq!(sample.groups.len(), 1);
        assert_eq!(sample.groups[0].sessions.len(), 2);
        assert_eq!(sample.groups[0].sessions[0].processes.len(), 2);
        assert_eq!(sample.groups[0].sessions[1].processes.len(), 2);
    }

    #[test]
    fn nested_owner_root_claims_its_closest_process_tree() {
        let processes = vec![
            observed(1, None, "app", 0.0, 1),
            observed(10, Some(1), "terminal", 0.0, 1),
            observed(11, Some(10), "adapter", 0.0, 1),
            observed(12, Some(11), "helper", 0.0, 1),
        ];
        let owners = vec![
            owner(
                10,
                "terminal-a",
                "Terminal 1",
                ResourceSampleSessionKind::Terminal,
                "workspace",
            ),
            owner(
                11,
                "conversation-a",
                "Agent conversation",
                ResourceSampleSessionKind::Conversation,
                "workspace",
            ),
        ];

        let sample = build_resource_sample(123, 1, &processes, &owners);
        let terminal = &sample.groups[0].sessions[0];
        let conversation = &sample.groups[0].sessions[1];

        assert_eq!(
            terminal
                .processes
                .iter()
                .map(|part| part.pid)
                .collect::<Vec<_>>(),
            vec![10]
        );
        assert_eq!(
            conversation
                .processes
                .iter()
                .map(|part| part.pid)
                .collect::<HashSet<_>>(),
            HashSet::from([11, 12])
        );
        assert_eq!(sample.totals.process_count, 4);
    }

    /// The tree used by every stop test: the app owns a terminal (10) with a
    /// child (11) and a grandchild (12), and something unrelated runs at 99.
    fn stop_fixture() -> (Vec<ObservedProcess>, Vec<ResourceSampleOwner>) {
        let processes = vec![
            observed(1, None, "app", 0.0, 1),
            observed(10, Some(1), "zsh", 0.0, 1),
            observed(11, Some(10), "cargo", 0.0, 1),
            observed(12, Some(11), "rustc", 0.0, 1),
            observed(99, None, "external", 0.0, 1),
        ];
        let owners = vec![owner(
            10,
            "terminal-a",
            "Terminal 1",
            ResourceSampleSessionKind::Terminal,
            "project / workspace",
        )];
        (processes, owners)
    }

    #[test]
    fn stop_targets_cover_the_session_tree_deepest_process_first() {
        let (processes, owners) = stop_fixture();

        let plan = select_stop_targets(10, 1, &owners, &processes, &[10, 11, 12])
            .expect("an owned session root should resolve to a stop plan");

        assert_eq!(plan.owner_index, 0);
        assert_eq!(plan.root_pid, 10);
        assert_eq!(plan.targets, vec![12, 11, 10]);
        assert!(plan.already_gone.is_empty());
    }

    #[test]
    fn stop_targets_accept_one_process_inside_an_owned_tree() {
        let (processes, owners) = stop_fixture();

        let plan = select_stop_targets(11, 1, &owners, &processes, &[11, 12])
            .expect("a process inside an owned tree should resolve to a stop plan");

        assert_eq!(plan.targets, vec![12, 11]);
    }

    #[test]
    fn stop_targets_report_processes_that_already_exited() {
        let (processes, owners) = stop_fixture();

        let plan = select_stop_targets(10, 1, &owners, &processes, &[10, 11, 12, 77])
            .expect("a named process that has exited should not fail the plan");

        assert_eq!(plan.already_gone, vec![77]);
        assert_eq!(plan.targets, vec![12, 11, 10]);
    }

    #[test]
    fn stop_targets_refuse_the_app_unowned_trees_and_strangers_in_the_list() {
        let (processes, owners) = stop_fixture();

        assert_eq!(
            select_stop_targets(1, 1, &owners, &processes, &[1]),
            Err(ResourceStopTargetError::ProtectedPid)
        );
        assert_eq!(
            select_stop_targets(99, 1, &owners, &processes, &[99]),
            Err(ResourceStopTargetError::UnownedRoot)
        );
        assert_eq!(
            select_stop_targets(10, 1, &owners, &processes, &[10, 99]),
            Err(ResourceStopTargetError::RequestedOutsideTree)
        );
        let empty = Vec::new();
        assert_eq!(
            select_stop_targets(10, 1, &owners, &empty, &[10]),
            Err(ResourceStopTargetError::RootGone)
        );
    }

    /// The web view's helpers are launched by the system on the app's
    /// behalf, so their parent is launchd and a walk down from the app never
    /// meets them. The kernel still knows whose they are, and Activity
    /// Monitor lists them under the app: so does this.
    #[test]
    fn webkit_helpers_the_kernel_says_are_ours_belong_to_the_app() {
        // The app itself, launched from a terminal: the terminal is what the
        // kernel holds responsible for it, and for the helpers it started.
        assert!(webkit_helper_of_app(
            "com.apple.WebKit.WebContent",
            Some(500),
            1_000,
            100,
            Some(500),
            90,
        ));
        // Launched from the Finder, the app answers for itself.
        assert!(webkit_helper_of_app(
            "com.apple.WebKit.Networking",
            Some(100),
            1_000,
            100,
            Some(100),
            90,
        ));
        // Another program's helper, however similar its name.
        assert!(!webkit_helper_of_app(
            "com.apple.WebKit.GPU",
            Some(700),
            1_000,
            100,
            Some(500),
            90,
        ));
        // A helper older than the app was not started for it.
        assert!(!webkit_helper_of_app(
            "com.apple.WebKit.GPU",
            Some(500),
            80,
            100,
            Some(500),
            90,
        ));
        // Not a WebKit helper at all.
        assert!(!webkit_helper_of_app(
            "node",
            Some(500),
            1_000,
            100,
            Some(500),
            90
        ));
    }

    #[test]
    fn app_helpers_are_named_the_way_activity_monitor_names_them() {
        let label = |name: &str| app_part_label(&observed(20, Some(1), name, 0.0, 1), 10);
        assert_eq!(label("com.apple.WebKit.WebContent"), "Web content");
        assert_eq!(label("com.apple.WebKit.Networking"), "Networking");
        assert_eq!(label("com.apple.WebKit.GPU"), "Graphics and Media");
    }

    /// Activity Monitor measures a macOS process by its physical footprint.
    #[cfg(target_os = "macos")]
    #[test]
    fn a_process_is_measured_by_its_physical_footprint() {
        let processes = observe_processes(&mut System::new(), std::process::id());
        let this = processes
            .iter()
            .find(|process| process.pid == std::process::id())
            .expect("the running process is observed");
        let current = mcb_core::scanners::resources::phys_footprint_bytes(std::process::id())
            .expect("the running process has a physical footprint");
        assert!(this.physical_footprint_bytes > 0);
        assert!(
            this.physical_footprint_bytes.abs_diff(current) <= 4 * 1024 * 1024,
            "two adjacent live samples should describe the same process footprint"
        );
    }
}
