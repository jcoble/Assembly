//! Native browser child-webview bridge.
//!
//! The browser model owns presentation state in the main webview. This module
//! owns exactly one native child view per `(workspace_id, tab_id)` and keeps
//! that view alive while it is measured, hidden, moved, or shown again.
//! Remote pages receive only the bounded inspector initialization script; no
//! generic page-evaluation command or cookie/storage bridge is exposed.
//! Native browser commands are async because synchronous Tauri commands run on the UI thread.

#[cfg(test)] mod external_open_tests;
use std::collections::HashMap;
use std::future::Future;
use std::hash::{Hash, Hasher};
use std::pin::Pin;
use std::process::Command;
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::sync::{mpsc, Arc, Mutex};
use std::thread;
use std::time::Duration;

use crate::debug_log::stderr_log;
use serde::{Deserialize, Serialize};
use tauri::webview::PageLoadEvent;
use tauri::{Emitter, Manager, WebviewBuilder, WebviewUrl};
use uuid::Uuid;

const BROWSER_INSPECTOR_SCRIPT: &str = include_str!("inspector.js");
const BROWSER_INSPECTOR_POLL_SCRIPT: &str =
    "JSON.stringify(window.__mcbBrowserInspector?.take?.() ?? null)";
const BROWSER_INSPECTOR_POLL_INTERVAL: Duration = Duration::from_millis(80);
const BROWSER_INSPECTOR_POLL_TIMEOUT: Duration = Duration::from_millis(250);
const BROWSER_SNAPSHOT_TIMEOUT: Duration = Duration::from_secs(5);

type SnapshotResult = Result<(Vec<u8>, u32, u32), BrowserCommandError>;
type SnapshotFuture = Pin<Box<dyn Future<Output = SnapshotResult> + Send + 'static>>;

pub const BROWSER_TAB_NAVIGATION_EVENT: &str = "browser-tab-navigation";
pub const BROWSER_TAB_LOAD_EVENT: &str = "browser-tab-load";
pub const BROWSER_ELEMENT_SELECTED_EVENT: &str = "browser-element-selected";
pub const BROWSER_TAB_CLOSED_EVENT: &str = "browser-tab-closed";

const MAX_WORKSPACE_ID_BYTES: usize = 256;
const MAX_TAB_ID_BYTES: usize = 256;
const MAX_PROFILE_ID_BYTES: usize = 128;
const MAX_BROWSER_URL_BYTES: usize = 8 * 1024;
const MAX_TITLE_BYTES: usize = 2 * 1024;
const MAX_SELECTOR_BYTES: usize = 2 * 1024;
const MAX_TEXT_CHARS: usize = 500;
const MAX_ACCESSIBLE_NAME_CHARS: usize = 500;
const MAX_ROLE_CHARS: usize = 64;
const MAX_CLASSES: usize = 32;
const MAX_CLASS_NAME_BYTES: usize = 128;
const MAX_INSPECTOR_PAYLOAD_BYTES: usize = 64 * 1024;
const MAX_BROWSER_COORDINATE: f64 = 100_000.0;
const MAX_BROWSER_SIZE: f64 = 16_384.0;

/// Bounds are logical coordinates relative to the main Tauri window.
#[derive(Debug, Clone, Copy, Deserialize, Serialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct BrowserBounds {
    pub x: f64,
    pub y: f64,
    pub width: f64,
    pub height: f64,
}

#[derive(Debug, Clone, Deserialize, Serialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct BrowserViewport {
    pub preset: Option<String>,
    pub width: Option<f64>,
    pub height: Option<f64>,
}

#[derive(Debug, Clone, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct BrowserTabInput {
    pub workspace_id: String,
    pub tab_id: String,
    pub generation: u64,
    pub url: String,
    pub bounds: Option<BrowserBounds>,
    pub viewport: Option<BrowserViewport>,
    pub profile_id: Option<String>,
}

#[derive(Debug, Clone, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct BrowserTarget {
    pub workspace_id: String,
    pub tab_id: String,
    pub generation: u64,
}

#[derive(Debug, Clone, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct BrowserBoundsInput {
    pub workspace_id: String,
    pub tab_id: String,
    pub generation: u64,
    pub bounds: BrowserBounds,
}

#[derive(Debug, Clone, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct BrowserWorkspaceTarget {
    pub workspace_id: String,
}

#[derive(Debug, Clone, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct BrowserNavigationInput {
    pub workspace_id: String,
    pub tab_id: String,
    pub generation: u64,
    pub url: String,
}

#[derive(Debug, Clone, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct BrowserViewportInput {
    pub workspace_id: String,
    pub tab_id: String,
    pub generation: u64,
    pub viewport: BrowserViewport,
}

#[derive(Debug, Clone, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct BrowserPickerInput {
    pub workspace_id: String,
    pub tab_id: String,
    pub generation: u64,
    pub mode: BrowserPickerMode,
}

#[derive(Debug, Clone, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct BrowserRectInspectionInput {
    pub workspace_id: String,
    pub tab_id: String,
    pub generation: u64,
    pub rect: BrowserRect,
}

#[derive(Debug, Clone, Copy, Deserialize, Serialize, PartialEq, Eq)]
#[serde(rename_all = "lowercase")]
pub enum BrowserPickerMode {
    Grab,
    Annotation,
}

#[derive(Debug, Clone, Serialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct BrowserTabResult {
    pub tab_id: String,
    pub generation: u64,
}

#[derive(Debug, Clone, Serialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct BrowserMarkupCapture {
    pub mime_type: String,
    pub bytes: Vec<u8>,
    pub width: u32,
    pub height: u32,
    pub source_hash: String,
}

#[derive(Debug, Clone, Serialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct BrowserTabNavigationEvent {
    pub workspace_id: String,
    pub tab_id: String,
    pub generation: u64,
    pub url: String,
    pub title: String,
    pub can_go_back: bool,
    pub can_go_forward: bool,
}

#[derive(Debug, Clone, Copy, Deserialize, Serialize, PartialEq, Eq)]
#[serde(rename_all = "lowercase")]
pub enum BrowserLoadPhase {
    Started,
    Finished,
}

#[derive(Debug, Clone, Serialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct BrowserTabLoadEvent {
    pub workspace_id: String,
    pub tab_id: String,
    pub generation: u64,
    pub phase: BrowserLoadPhase,
    pub url: String,
    pub title: String,
}

#[derive(Debug, Clone, Copy, Deserialize, Serialize, PartialEq, Eq)]
#[serde(rename_all = "lowercase")]
pub enum BrowserPickerStatus {
    Selected,
    Unavailable,
}

#[derive(Debug, Clone, Serialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct BrowserElementSelectedEvent {
    pub workspace_id: String,
    pub tab_id: String,
    pub generation: u64,
    pub status: BrowserPickerStatus,
    pub reason: Option<String>,
    pub url: Option<String>,
    pub title: Option<String>,
    pub selector: Option<String>,
    pub accessible_name: Option<String>,
    pub text_snippet: Option<String>,
    pub rect: Option<BrowserRect>,
    pub classes: Vec<String>,
    pub class_count: usize,
    pub source_hash: String,
}

#[derive(Debug, Clone, Serialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct BrowserElementMetadata {
    pub selector: Option<String>,
    /// The element's own ARIA role attribute when it has one.
    pub role: Option<String>,
    pub accessible_name: Option<String>,
    pub text_snippet: Option<String>,
    pub rect: Option<BrowserRect>,
    pub classes: Vec<String>,
    pub class_count: usize,
}

#[derive(Debug, Clone, Serialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct BrowserTabClosedEvent {
    pub workspace_id: String,
    pub tab_id: String,
    pub generation: u64,
}

#[derive(Debug, Clone, Copy, Deserialize, Serialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct BrowserRect {
    pub x: f64,
    pub y: f64,
    pub width: f64,
    pub height: f64,
}

#[derive(Debug, Clone, Copy, Serialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub enum BrowserErrorCode {
    InvalidIdentity,
    InvalidUrl,
    InvalidBounds,
    InvalidProfile,
    UnknownWorkspace,
    UnknownTab,
    IdentityMismatch,
    StaleGeneration,
    GenerationNotMonotonic,
    Native,
    InvalidPickerPayload,
    Unsupported,
}

#[derive(Debug, Clone, Serialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct BrowserCommandError {
    pub code: BrowserErrorCode,
    pub message: String,
}

impl BrowserCommandError {
    fn new(code: BrowserErrorCode, message: impl Into<String>) -> Self {
        Self {
            code,
            message: message.into(),
        }
    }
}

#[derive(Debug, Clone)]
pub(crate) struct BrowserProfile {
    /// This is intentionally opaque. It is never accepted as a filesystem path
    /// and is not sent to remote page content.
    id: String,
    store_identifier: [u8; 16],
}

#[derive(Debug, Clone)]
struct NativePageLoad {
    phase: BrowserLoadPhase,
    url: String,
    generation: u64,
}

#[derive(Debug, Clone)]
struct NativePickerPayload {
    status: BrowserPickerStatus,
    reason: Option<String>,
    url: Option<String>,
    title: Option<String>,
    selector: Option<String>,
    role: Option<String>,
    accessible_name: Option<String>,
    text_snippet: Option<String>,
    rect: Option<BrowserRect>,
    classes: Vec<String>,
    class_count: usize,
}

#[derive(Clone)]
pub(crate) struct BrowserViewCallbacks {
    on_page_load: Arc<dyn Fn(NativePageLoad) + Send + Sync>,
    on_title_changed: Arc<dyn Fn(u64, String) + Send + Sync>,
}

/// The native view seam is deliberately small so lifecycle and validation
/// tests can use a headless fake without starting a Tauri event loop.
pub(crate) trait BrowserView: Send + Sync {
    fn set_bounds(&self, bounds: BrowserBounds) -> Result<(), BrowserCommandError>;
    fn show(&self) -> Result<(), BrowserCommandError>;
    fn hide(&self) -> Result<(), BrowserCommandError>;
    fn close(&self) -> Result<(), BrowserCommandError>;
    fn navigate(&self, url: &str, generation: u64) -> Result<(), BrowserCommandError>;
    fn reload(&self) -> Result<(), BrowserCommandError>;
    fn go_back(&self) -> Result<(), BrowserCommandError>;
    fn go_forward(&self) -> Result<(), BrowserCommandError>;
    fn set_viewport(&self, _viewport: &BrowserViewport) -> Result<(), BrowserCommandError> {
        Ok(())
    }
    /// Returns a future for a rendered PNG still and its pixel size. The future
    /// is important: WebKit answers snapshots on the main thread, so callers
    /// must await the answer instead of blocking the thread that services it.
    fn snapshot(&self) -> SnapshotFuture {
        Box::pin(std::future::ready(Err(BrowserCommandError::new(
            BrowserErrorCode::Unsupported,
            "This view cannot render a snapshot",
        ))))
    }
    fn eval(&self, script: &str) -> Result<(), BrowserCommandError>;
    fn eval_with_callback(
        &self,
        script: &str,
        callback: Box<dyn Fn(String) + Send + 'static>,
    ) -> Result<(), BrowserCommandError>;
    fn open_devtools(&self) -> Result<(), BrowserCommandError>;
}

pub(crate) trait BrowserViewFactory: Send + Sync {
    fn create(
        &self,
        app: Option<&tauri::AppHandle>,
        input: &BrowserTabInput,
        profile: &BrowserProfile,
        callbacks: BrowserViewCallbacks,
    ) -> Result<Arc<dyn BrowserView>, BrowserCommandError>;
}

trait BrowserEventSink: Send + Sync {
    fn navigation(&self, event: BrowserTabNavigationEvent);
    fn load(&self, event: BrowserTabLoadEvent);
    fn element_selected(&self, event: BrowserElementSelectedEvent);
    fn closed(&self, event: BrowserTabClosedEvent);
}

struct TauriBrowserEventSink {
    app: tauri::AppHandle,
}

impl BrowserEventSink for TauriBrowserEventSink {
    fn navigation(&self, event: BrowserTabNavigationEvent) {
        let _ = self.app.emit(BROWSER_TAB_NAVIGATION_EVENT, event);
    }

    fn load(&self, event: BrowserTabLoadEvent) {
        let _ = self.app.emit(BROWSER_TAB_LOAD_EVENT, event);
    }

    fn element_selected(&self, event: BrowserElementSelectedEvent) {
        let _ = self.app.emit(BROWSER_ELEMENT_SELECTED_EVENT, event);
    }

    fn closed(&self, event: BrowserTabClosedEvent) {
        let _ = self.app.emit(BROWSER_TAB_CLOSED_EVENT, event);
    }
}

struct BrowserTab {
    workspace_id: String,
    tab_id: String,
    generation: u64,
    url: String,
    title: String,
    can_go_back: bool,
    can_go_forward: bool,
    viewport: Option<BrowserViewport>,
    view: Arc<dyn BrowserView>,
    sink: Arc<dyn BrowserEventSink>,
    picker: Option<PickerState>,
}

#[derive(Debug, Clone, Copy)]
struct PickerState {
    generation: u64,
    epoch: u64,
}

struct BrowserWorkspace {
    profile: BrowserProfile,
    last_generation: u64,
    tabs: HashMap<String, BrowserTab>,
}

struct BrowserRegistryInner {
    workspaces: Mutex<HashMap<String, BrowserWorkspace>>,
    factory: Arc<dyn BrowserViewFactory>,
    next_picker_epoch: Mutex<u64>,
}

/// Application state for all native browser workspaces.
pub struct BrowserRegistry {
    inner: Arc<BrowserRegistryInner>,
}

impl Default for BrowserRegistry {
    fn default() -> Self {
        Self::with_factory(Arc::new(TauriBrowserViewFactory))
    }
}

impl BrowserRegistry {
    pub(crate) fn with_factory(factory: Arc<dyn BrowserViewFactory>) -> Self {
        Self {
            inner: Arc::new(BrowserRegistryInner {
                workspaces: Mutex::new(HashMap::new()),
                factory,
                next_picker_epoch: Mutex::new(0),
            }),
        }
    }

    pub(crate) fn live_view_count(&self) -> Result<usize, String> {
        let workspaces = self
            .inner
            .workspaces
            .lock()
            .map_err(|_| "Browser registry is unavailable".to_string())?;
        Ok(workspaces
            .values()
            .map(|workspace| workspace.tabs.len())
            .sum())
    }

    fn next_picker_epoch(&self) -> u64 {
        let mut epoch = self
            .inner
            .next_picker_epoch
            .lock()
            .expect("browser picker epoch mutex poisoned");
        *epoch = epoch.saturating_add(1);
        *epoch
    }

    fn callbacks(&self, workspace_id: &str, tab_id: &str) -> BrowserViewCallbacks {
        let key = BrowserTabKey::new(workspace_id, tab_id);
        let weak = Arc::downgrade(&self.inner);
        let title_weak = weak.clone();
        let page_key = key.clone();
        let title_key = key;
        BrowserViewCallbacks {
            on_page_load: Arc::new(move |event| {
                if let Some(inner) = weak.upgrade() {
                    inner.handle_page_load(&page_key, event);
                }
            }),
            on_title_changed: Arc::new(move |generation, title| {
                if let Some(inner) = title_weak.upgrade() {
                    inner.handle_title_changed(&title_key, generation, title);
                }
            }),
        }
    }

    fn create_tab(
        &self,
        app: Option<&tauri::AppHandle>,
        input: BrowserTabInput,
        sink: Arc<dyn BrowserEventSink>,
    ) -> Result<BrowserTabResult, BrowserCommandError> {
        validate_identity(&input.workspace_id, "workspace")?;
        validate_identity(&input.tab_id, "tab")?;
        let url = normalize_browser_url(&input.url, true)?;
        let bounds = input.bounds.map(clamp_browser_bounds).transpose()?;
        if let Some(profile_id) = input.profile_id.as_deref() {
            validate_profile_id(profile_id)?;
        }
        if input.generation == 0 {
            return Err(BrowserCommandError::new(
                BrowserErrorCode::GenerationNotMonotonic,
                "Browser tab generation must be greater than zero",
            ));
        }

        let mut workspaces = self
            .inner
            .workspaces
            .lock()
            .expect("browser registry mutex poisoned");
        if let Some(workspace) = workspaces.get(&input.workspace_id) {
            if let Some(tab) = workspace.tabs.get(&input.tab_id) {
                if tab.generation == input.generation
                    && tab.url == url.as_deref().unwrap_or_default()
                {
                    // Idempotent retries must never create a second native view.
                    return Ok(BrowserTabResult {
                        tab_id: tab.tab_id.clone(),
                        generation: tab.generation,
                    });
                }
                return Err(BrowserCommandError::new(
                    BrowserErrorCode::IdentityMismatch,
                    "That browser tab already exists with a different generation or URL",
                ));
            }
            if input.generation <= workspace.last_generation {
                return Err(BrowserCommandError::new(
                    BrowserErrorCode::GenerationNotMonotonic,
                    "New browser tabs must advance the workspace generation",
                ));
            }
        }

        let workspace = workspaces
            .entry(input.workspace_id.clone())
            .or_insert_with(|| BrowserWorkspace {
                profile: BrowserProfile::new(input.profile_id.as_deref()),
                last_generation: 0,
                tabs: HashMap::new(),
            });
        if let Some(profile_id) = input.profile_id.as_deref() {
            if workspace.profile.id != profile_id {
                return Err(BrowserCommandError::new(
                    BrowserErrorCode::InvalidProfile,
                    "That browser workspace is already bound to a different profile",
                ));
            }
        }
        let profile = workspace.profile.clone();
        let callbacks = self.callbacks(&input.workspace_id, &input.tab_id);
        let view = self
            .inner
            .factory
            .create(app, &input, &profile, callbacks)?;
        if let Some(bounds) = bounds {
            // The factory already built the view at this rectangle. It is not
            // set again here: these are the bounds the workspace was holding,
            // not ones the panel measured, and a view placed by them would
            // count as placed and come on screen over the middle of the shell.
            stderr_log!(
                "browser: view created tab={} x={} y={} w={} h={}",
                input.tab_id,
                bounds.x.round(),
                bounds.y.round(),
                bounds.width.round(),
                bounds.height.round()
            );
        }
        view.show()?;

        let tab = BrowserTab {
            workspace_id: input.workspace_id.clone(),
            tab_id: input.tab_id.clone(),
            generation: input.generation,
            url: url.clone().unwrap_or_default(),
            title: title_from_url(url.as_deref()),
            can_go_back: false,
            can_go_forward: false,
            viewport: input.viewport.clone(),
            view,
            sink,
            picker: None,
        };
        let workspace = workspaces
            .get_mut(&input.workspace_id)
            .expect("workspace inserted above");
        workspace.last_generation = input.generation;
        workspace.tabs.insert(input.tab_id.clone(), tab);
        Ok(BrowserTabResult {
            tab_id: input.tab_id,
            generation: input.generation,
        })
    }

    fn validate_target<'a>(
        &'a self,
        target: &BrowserTarget,
    ) -> Result<std::sync::MutexGuard<'a, HashMap<String, BrowserWorkspace>>, BrowserCommandError>
    {
        validate_identity(&target.workspace_id, "workspace")?;
        validate_identity(&target.tab_id, "tab")?;
        if target.generation == 0 {
            return Err(BrowserCommandError::new(
                BrowserErrorCode::StaleGeneration,
                "Browser target generation is missing",
            ));
        }
        Ok(self
            .inner
            .workspaces
            .lock()
            .expect("browser registry mutex poisoned"))
    }

    fn require_tab<'a>(
        workspaces: &'a HashMap<String, BrowserWorkspace>,
        target: &BrowserTarget,
    ) -> Result<&'a BrowserTab, BrowserCommandError> {
        let workspace = workspaces.get(&target.workspace_id).ok_or_else(|| {
            BrowserCommandError::new(
                BrowserErrorCode::UnknownWorkspace,
                "That browser workspace is no longer available",
            )
        })?;
        let tab = workspace.tabs.get(&target.tab_id).ok_or_else(|| {
            BrowserCommandError::new(
                BrowserErrorCode::UnknownTab,
                "That browser tab is no longer available",
            )
        })?;
        if tab.workspace_id != target.workspace_id || tab.tab_id != target.tab_id {
            return Err(BrowserCommandError::new(
                BrowserErrorCode::IdentityMismatch,
                "Browser tab identity does not match its workspace",
            ));
        }
        if tab.generation != target.generation {
            return Err(BrowserCommandError::new(
                BrowserErrorCode::StaleGeneration,
                "The browser tab target is stale",
            ));
        }
        Ok(tab)
    }

    fn set_bounds(
        &self,
        target: BrowserTarget,
        bounds: BrowserBounds,
    ) -> Result<(), BrowserCommandError> {
        let bounds = clamp_browser_bounds(bounds)?;
        let workspaces = self.validate_target(&target)?;
        let tab = Self::require_tab(&workspaces, &target)?;
        // Geometry only — never the page's address or its contents.
        stderr_log!(
            "browser: bounds set tab={} x={} y={} w={} h={}",
            target.tab_id,
            bounds.x.round(),
            bounds.y.round(),
            bounds.width.round(),
            bounds.height.round()
        );
        tab.view.set_bounds(bounds)
    }

    fn show_tab(&self, target: BrowserTarget) -> Result<(), BrowserCommandError> {
        let mut workspaces = self.validate_target(&target)?;
        let workspace = workspaces.get_mut(&target.workspace_id).ok_or_else(|| {
            BrowserCommandError::new(
                BrowserErrorCode::UnknownWorkspace,
                "That browser workspace is no longer available",
            )
        })?;
        let tab = workspace.tabs.get(&target.tab_id).ok_or_else(|| {
            BrowserCommandError::new(
                BrowserErrorCode::UnknownTab,
                "That browser tab is no longer available",
            )
        })?;
        if tab.generation != target.generation {
            return Err(BrowserCommandError::new(
                BrowserErrorCode::StaleGeneration,
                "The browser tab target is stale",
            ));
        }
        for other in workspace.tabs.values() {
            if other.tab_id != target.tab_id {
                let _ = other.view.eval("window.__mcbBrowserInspector?.cancel?.();");
                other.view.hide()?;
            }
        }
        stderr_log!("browser: view shown tab={}", target.tab_id);
        tab.view.show()
    }

    fn hide_workspace(&self, target: BrowserWorkspaceTarget) -> Result<(), BrowserCommandError> {
        validate_identity(&target.workspace_id, "workspace")?;
        let mut workspaces = self
            .inner
            .workspaces
            .lock()
            .expect("browser registry mutex poisoned");
        let workspace = workspaces.get_mut(&target.workspace_id).ok_or_else(|| {
            BrowserCommandError::new(
                BrowserErrorCode::UnknownWorkspace,
                "That browser workspace is no longer available",
            )
        })?;
        stderr_log!("browser: workspace hidden tabs={}", workspace.tabs.len());
        for tab in workspace.tabs.values_mut() {
            let _ = tab.view.eval("window.__mcbBrowserInspector?.cancel?.();");
            tab.picker = None;
            tab.view.hide()?;
        }
        Ok(())
    }

    fn set_viewport(&self, input: BrowserViewportInput) -> Result<(), BrowserCommandError> {
        let target = BrowserTarget {
            workspace_id: input.workspace_id,
            tab_id: input.tab_id,
            generation: input.generation,
        };
        validate_browser_viewport(&input.viewport)?;
        let mut workspaces = self.validate_target(&target)?;
        let tab = workspaces
            .get_mut(&target.workspace_id)
            .and_then(|workspace| workspace.tabs.get_mut(&target.tab_id))
            .ok_or_else(|| {
                BrowserCommandError::new(
                    BrowserErrorCode::UnknownTab,
                    "That browser tab is no longer available",
                )
            })?;
        if tab.generation != target.generation {
            return Err(BrowserCommandError::new(
                BrowserErrorCode::StaleGeneration,
                "The browser tab target is stale",
            ));
        }
        tab.view.set_viewport(&input.viewport)?;
        tab.viewport = Some(input.viewport);
        Ok(())
    }

    fn navigate(&self, input: BrowserNavigationInput) -> Result<(), BrowserCommandError> {
        let url = normalize_browser_url(&input.url, false)?.ok_or_else(|| {
            BrowserCommandError::new(
                BrowserErrorCode::InvalidUrl,
                "Browser navigation requires an http, https, or file address",
            )
        })?;
        let target = BrowserTarget {
            workspace_id: input.workspace_id,
            tab_id: input.tab_id,
            generation: input.generation,
        };
        validate_identity(&target.workspace_id, "workspace")?;
        validate_identity(&target.tab_id, "tab")?;
        let mut workspaces = self
            .inner
            .workspaces
            .lock()
            .expect("browser registry mutex poisoned");
        let workspace = workspaces.get_mut(&target.workspace_id).ok_or_else(|| {
            BrowserCommandError::new(
                BrowserErrorCode::UnknownWorkspace,
                "That browser workspace is no longer available",
            )
        })?;
        let tab = workspace.tabs.get_mut(&target.tab_id).ok_or_else(|| {
            BrowserCommandError::new(
                BrowserErrorCode::UnknownTab,
                "That browser tab is no longer available",
            )
        })?;
        if target.generation <= tab.generation {
            return Err(BrowserCommandError::new(
                BrowserErrorCode::GenerationNotMonotonic,
                "Navigation must advance the browser tab generation",
            ));
        }
        tab.view.navigate(&url, target.generation)?;
        tab.generation = target.generation;
        workspace.last_generation = workspace.last_generation.max(target.generation);
        tab.url = url.clone();
        tab.title = title_from_url(Some(&url));
        tab.can_go_back = true;
        tab.can_go_forward = false;
        tab.picker = None;
        Ok(())
    }

    fn reload(&self, target: BrowserTarget) -> Result<(), BrowserCommandError> {
        let workspaces = self.validate_target(&target)?;
        let tab = Self::require_tab(&workspaces, &target)?;
        tab.view.reload()
    }

    fn go_back(&self, target: BrowserTarget) -> Result<(), BrowserCommandError> {
        let mut workspaces = self.validate_target(&target)?;
        let tab = Self::require_tab(&workspaces, &target)?;
        tab.view.go_back()?;
        let tab = workspaces
            .get_mut(&target.workspace_id)
            .and_then(|workspace| workspace.tabs.get_mut(&target.tab_id))
            .expect("validated browser tab disappeared while locked");
        tab.can_go_forward = true;
        Ok(())
    }

    fn go_forward(&self, target: BrowserTarget) -> Result<(), BrowserCommandError> {
        let mut workspaces = self.validate_target(&target)?;
        let tab = Self::require_tab(&workspaces, &target)?;
        tab.view.go_forward()?;
        let tab = workspaces
            .get_mut(&target.workspace_id)
            .and_then(|workspace| workspace.tabs.get_mut(&target.tab_id))
            .expect("validated browser tab disappeared while locked");
        tab.can_go_back = true;
        Ok(())
    }

    fn close_tab(&self, target: BrowserTarget) -> Result<(), BrowserCommandError> {
        let mut workspaces = self.validate_target(&target)?;
        let workspace = workspaces.get_mut(&target.workspace_id).ok_or_else(|| {
            BrowserCommandError::new(
                BrowserErrorCode::UnknownWorkspace,
                "That browser workspace is no longer available",
            )
        })?;
        let view = workspace
            .tabs
            .get(&target.tab_id)
            .ok_or_else(|| {
                BrowserCommandError::new(
                    BrowserErrorCode::UnknownTab,
                    "That browser tab is no longer available",
                )
            })?
            .view
            .clone();
        let _ = view.eval("window.__mcbBrowserInspector?.cancel?.();");
        view.close()?;
        let tab = workspace
            .tabs
            .remove(&target.tab_id)
            .expect("validated browser tab disappeared while locked");
        tab.sink.closed(BrowserTabClosedEvent {
            workspace_id: target.workspace_id,
            tab_id: target.tab_id,
            generation: tab.generation,
        });
        Ok(())
    }

    fn clear_workspace_data(
        &self,
        target: BrowserWorkspaceTarget,
    ) -> Result<(), BrowserCommandError> {
        validate_identity(&target.workspace_id, "workspace")?;
        let mut workspaces = self
            .inner
            .workspaces
            .lock()
            .expect("browser registry mutex poisoned");
        {
            let workspace = workspaces.get(&target.workspace_id).ok_or_else(|| {
                BrowserCommandError::new(
                    BrowserErrorCode::UnknownWorkspace,
                    "That browser workspace is no longer available",
                )
            })?;
            for tab in workspace.tabs.values() {
                let _ = tab.view.eval("window.__mcbBrowserInspector?.cancel?.();");
                tab.view.close()?;
            }
        }
        workspaces.remove(&target.workspace_id);
        Ok(())
    }

    fn arm_picker(&self, input: BrowserPickerInput) -> Result<(), BrowserCommandError> {
        let target = BrowserTarget {
            workspace_id: input.workspace_id,
            tab_id: input.tab_id,
            generation: input.generation,
        };
        let epoch = self.next_picker_epoch();
        let (view, key) = {
            let mut workspaces = self.validate_target(&target)?;
            let tab = Self::require_tab(&workspaces, &target)?;
            tab.view.eval("window.__mcbBrowserInspector?.arm?.();")?;
            let view = tab.view.clone();
            let key = BrowserTabKey::new(&target.workspace_id, &target.tab_id);
            let tab = workspaces
                .get_mut(&target.workspace_id)
                .and_then(|workspace| workspace.tabs.get_mut(&target.tab_id))
                .expect("validated browser tab disappeared while locked");
            tab.picker = Some(PickerState {
                generation: target.generation,
                epoch,
            });
            (view, key)
        };
        self.start_picker_poll(view, key, target, epoch);
        Ok(())
    }

    fn cancel_picker(&self, target: BrowserTarget) -> Result<(), BrowserCommandError> {
        let mut workspaces = self.validate_target(&target)?;
        let tab = Self::require_tab(&workspaces, &target)?;
        tab.view.eval("window.__mcbBrowserInspector?.cancel?.();")?;
        let tab = workspaces
            .get_mut(&target.workspace_id)
            .and_then(|workspace| workspace.tabs.get_mut(&target.tab_id))
            .expect("validated browser tab disappeared while locked");
        tab.picker = None;
        Ok(())
    }

    async fn inspect_rect(
        &self,
        input: BrowserRectInspectionInput,
    ) -> Result<Option<BrowserElementMetadata>, BrowserCommandError> {
        let rect = validate_inspection_rect(input.rect)?;
        let target = BrowserTarget {
            workspace_id: input.workspace_id,
            tab_id: input.tab_id,
            generation: input.generation,
        };
        let view = {
            let workspaces = self.validate_target(&target)?;
            Self::require_tab(&workspaces, &target)?.view.clone()
        };
        let coordinates = serde_json::to_string(&[rect.x, rect.y, rect.width, rect.height])
            .expect("finite browser rectangle failed to serialize");
        let script = format!(
            "JSON.stringify(window.__mcbBrowserInspector?.inspectRect?.(...{coordinates}) ?? null)"
        );
        let (sender, mut receiver) = tokio::sync::mpsc::channel(1);
        view.eval_with_callback(
            &script,
            Box::new(move |value| {
                let _ = sender.try_send(value);
            }),
        )?;
        let raw = tokio::time::timeout(BROWSER_INSPECTOR_POLL_TIMEOUT, receiver.recv())
            .await
            .map_err(|_| {
                BrowserCommandError::new(
                    BrowserErrorCode::Native,
                    "Timed out waiting for page metadata",
                )
            })?
            .ok_or_else(|| {
                BrowserCommandError::new(
                    BrowserErrorCode::Native,
                    "The page metadata query ended without an answer",
                )
            })?;
        if raw.len() > MAX_INSPECTOR_PAYLOAD_BYTES {
            return Err(BrowserCommandError::new(
                BrowserErrorCode::InvalidPickerPayload,
                "Browser inspector payload is too large",
            ));
        }
        let payload = parse_picker_payload(&raw)?;
        if payload.status == BrowserPickerStatus::Unavailable {
            return Ok(None);
        }
        let page_url = payload
            .url
            .as_deref()
            .and_then(|url| normalize_browser_url(url, false).ok().flatten());
        let current_url = {
            let workspaces = self.validate_target(&target)?;
            Self::require_tab(&workspaces, &target)?.url.clone()
        };
        if page_url.as_deref() != Some(current_url.as_str()) {
            return Err(BrowserCommandError::new(
                BrowserErrorCode::InvalidPickerPayload,
                "The inspected page changed before metadata was returned",
            ));
        }
        let classes: Vec<String> = payload
            .classes
            .into_iter()
            .take(MAX_CLASSES)
            .filter_map(|class_name| bounded_utf8_bytes(Some(&class_name), MAX_CLASS_NAME_BYTES))
            .collect();
        Ok(Some(BrowserElementMetadata {
            selector: bounded_utf8_bytes(payload.selector.as_deref(), MAX_SELECTOR_BYTES),
            role: bounded_chars(payload.role.as_deref().unwrap_or_default(), MAX_ROLE_CHARS),
            accessible_name: bounded_chars(
                payload.accessible_name.as_deref().unwrap_or_default(),
                MAX_ACCESSIBLE_NAME_CHARS,
            ),
            text_snippet: bounded_chars(
                payload.text_snippet.as_deref().unwrap_or_default(),
                MAX_TEXT_CHARS,
            ),
            rect: sanitize_rect(payload.rect),
            class_count: payload.class_count.min(MAX_CLASSES).max(classes.len()),
            classes,
        }))
    }

    fn start_picker_poll(
        &self,
        view: Arc<dyn BrowserView>,
        key: BrowserTabKey,
        target: BrowserTarget,
        epoch: u64,
    ) {
        let weak = Arc::downgrade(&self.inner);
        thread::spawn(move || loop {
            thread::sleep(BROWSER_INSPECTOR_POLL_INTERVAL);
            let Some(inner) = weak.upgrade() else { break };
            if !inner.picker_is_active(&key, target.generation, epoch) {
                break;
            }
            let (sender, receiver) = mpsc::sync_channel(1);
            let callback = Box::new(move |value: String| {
                let _ = sender.send(value);
            });
            if view
                .eval_with_callback(BROWSER_INSPECTOR_POLL_SCRIPT, callback)
                .is_err()
            {
                inner.emit_picker_unavailable(
                    &key,
                    target.generation,
                    epoch,
                    "inspector-unavailable",
                );
                break;
            }
            let Ok(value) = receiver.recv_timeout(BROWSER_INSPECTOR_POLL_TIMEOUT) else {
                continue;
            };
            if value.trim().is_empty() || value.trim() == "null" || value.trim() == "\"null\"" {
                continue;
            }
            inner.handle_picker_payload(&key, target.generation, epoch, &value);
            break;
        });
    }

    fn open_devtools(&self, target: BrowserTarget) -> Result<(), BrowserCommandError> {
        let workspaces = self.validate_target(&target)?;
        let tab = Self::require_tab(&workspaces, &target)?;
        #[cfg(debug_assertions)]
        {
            tab.view.open_devtools()
        }
        #[cfg(not(debug_assertions))]
        {
            let _ = tab;
            Err(BrowserCommandError::new(
                BrowserErrorCode::Unsupported,
                "Browser devtools are available only in debug builds",
            ))
        }
    }

    fn open_external(&self, target: BrowserTarget) -> Result<(), BrowserCommandError> {
        let workspaces = self.validate_target(&target)?;
        let tab = Self::require_tab(&workspaces, &target)?;
        let url = normalize_browser_url(&tab.url, false)?.ok_or_else(|| BrowserCommandError::new(BrowserErrorCode::InvalidUrl, "The browser tab has no safe address to open"))?;
        open_external_url(&url)
    }

    async fn capture(
        &self,
        target: BrowserTarget,
    ) -> Result<BrowserMarkupCapture, BrowserCommandError> {
        self.capture_with_timeout(target, BROWSER_SNAPSHOT_TIMEOUT)
            .await
    }

    /// Waits without occupying the async worker or the main thread. Keeping
    /// the timeout here also covers test views and any future native backend
    /// that starts a snapshot but never answers.
    async fn capture_with_timeout(
        &self,
        target: BrowserTarget,
        timeout: Duration,
    ) -> Result<BrowserMarkupCapture, BrowserCommandError> {
        let view = {
            let workspaces = self.validate_target(&target)?;
            Self::require_tab(&workspaces, &target)?.view.clone()
        };
        // The registry lock is released before the snapshot: the view waits on
        // the page rendering a still, and holding the lock for that long would
        // stall every other browser command.
        let (bytes, width, height) = tokio::time::timeout(timeout, view.snapshot())
            .await
            .map_err(|_| {
                BrowserCommandError::new(
                    BrowserErrorCode::Native,
                    "Timed out waiting for the page snapshot",
                )
            })??;
        // Snapshotting can outlive the tab that requested it. Revalidate after
        // the await so a released workspace cannot receive a late still.
        {
            let workspaces = self.validate_target(&target)?;
            Self::require_tab(&workspaces, &target)?;
        }
        Ok(BrowserMarkupCapture {
            mime_type: "image/png".to_string(),
            bytes,
            width,
            height,
            source_hash: String::new(),
        })
    }

    /// Called by the application event loop before Tauri exits. Every child
    /// view is closed and removed from the registry; polling threads observe the
    /// missing tab and stop on their next bounded interval.
    pub fn shutdown(&self) {
        let mut workspaces = self
            .inner
            .workspaces
            .lock()
            .expect("browser registry mutex poisoned");
        for workspace in workspaces.values_mut() {
            for (_, tab) in workspace.tabs.drain() {
                let _ = tab.view.eval("window.__mcbBrowserInspector?.cancel?.();");
                let _ = tab.view.close();
            }
        }
        workspaces.clear();
    }
}

#[derive(Debug, Clone, PartialEq, Eq, Hash)]
struct BrowserTabKey {
    workspace_id: String,
    tab_id: String,
}

impl BrowserTabKey {
    fn new(workspace_id: &str, tab_id: &str) -> Self {
        Self {
            workspace_id: workspace_id.to_string(),
            tab_id: tab_id.to_string(),
        }
    }
}

impl BrowserRegistryInner {
    fn picker_is_active(&self, key: &BrowserTabKey, generation: u64, epoch: u64) -> bool {
        let workspaces = self
            .workspaces
            .lock()
            .expect("browser registry mutex poisoned");
        workspaces
            .get(&key.workspace_id)
            .and_then(|workspace| workspace.tabs.get(&key.tab_id))
            .and_then(|tab| tab.picker)
            .is_some_and(|picker| picker.generation == generation && picker.epoch == epoch)
    }

    fn handle_page_load(&self, key: &BrowserTabKey, event: NativePageLoad) {
        let (sink, load, navigation) = {
            let mut workspaces = self
                .workspaces
                .lock()
                .expect("browser registry mutex poisoned");
            let Some(tab) = workspaces
                .get_mut(&key.workspace_id)
                .and_then(|workspace| workspace.tabs.get_mut(&key.tab_id))
            else {
                return;
            };
            if event.generation != tab.generation {
                return;
            }
            let Ok(event_url) = normalize_browser_url(&event.url, true) else {
                return;
            };
            let url = event_url.unwrap_or_default();
            if event.phase == BrowserLoadPhase::Finished {
                tab.url = url.clone();
                tab.title = title_from_url(Some(&url));
            }
            let load = BrowserTabLoadEvent {
                workspace_id: tab.workspace_id.clone(),
                tab_id: tab.tab_id.clone(),
                generation: tab.generation,
                phase: event.phase,
                url: url.clone(),
                title: tab.title.clone(),
            };
            let navigation =
                (event.phase == BrowserLoadPhase::Finished).then(|| BrowserTabNavigationEvent {
                    workspace_id: tab.workspace_id.clone(),
                    tab_id: tab.tab_id.clone(),
                    generation: tab.generation,
                    url,
                    title: tab.title.clone(),
                    can_go_back: tab.can_go_back,
                    can_go_forward: tab.can_go_forward,
                });
            (tab.sink.clone(), load, navigation)
        };
        sink.load(load);
        if let Some(navigation) = navigation {
            sink.navigation(navigation);
        }
    }

    fn handle_title_changed(&self, key: &BrowserTabKey, generation: u64, title: String) {
        let mut workspaces = self
            .workspaces
            .lock()
            .expect("browser registry mutex poisoned");
        let Some(tab) = workspaces
            .get_mut(&key.workspace_id)
            .and_then(|workspace| workspace.tabs.get_mut(&key.tab_id))
        else {
            return;
        };
        if tab.generation != generation {
            return;
        }
        tab.title = bounded_chars(&title, MAX_TITLE_BYTES)
            .unwrap_or_else(|| title_from_url(Some(&tab.url)));
    }

    fn emit_picker_unavailable(
        &self,
        key: &BrowserTabKey,
        generation: u64,
        epoch: u64,
        reason: &str,
    ) {
        let event = {
            let mut workspaces = self
                .workspaces
                .lock()
                .expect("browser registry mutex poisoned");
            let Some(tab) = workspaces
                .get_mut(&key.workspace_id)
                .and_then(|workspace| workspace.tabs.get_mut(&key.tab_id))
            else {
                return;
            };
            if !tab
                .picker
                .is_some_and(|picker| picker.generation == generation && picker.epoch == epoch)
            {
                return;
            }
            tab.picker = None;
            (
                tab.sink.clone(),
                BrowserElementSelectedEvent {
                    workspace_id: tab.workspace_id.clone(),
                    tab_id: tab.tab_id.clone(),
                    generation: tab.generation,
                    status: BrowserPickerStatus::Unavailable,
                    reason: bounded_chars(reason, 200),
                    url: Some(tab.url.clone()).filter(|url| !url.is_empty()),
                    title: Some(tab.title.clone()).filter(|title| !title.is_empty()),
                    selector: None,
                    accessible_name: None,
                    text_snippet: None,
                    rect: None,
                    classes: Vec::new(),
                    class_count: 0,
                    source_hash: String::new(),
                },
            )
        };
        event.0.element_selected(event.1);
    }

    fn handle_picker_payload(&self, key: &BrowserTabKey, generation: u64, epoch: u64, raw: &str) {
        if raw.len() > MAX_INSPECTOR_PAYLOAD_BYTES {
            self.emit_picker_unavailable(key, generation, epoch, "inspector-payload-too-large");
            return;
        }
        let payload = match parse_picker_payload(raw) {
            Ok(payload) => payload,
            Err(_) => {
                self.emit_picker_unavailable(key, generation, epoch, "invalid-inspector-payload");
                return;
            }
        };
        let event = {
            let mut workspaces = self
                .workspaces
                .lock()
                .expect("browser registry mutex poisoned");
            let Some(tab) = workspaces
                .get_mut(&key.workspace_id)
                .and_then(|workspace| workspace.tabs.get_mut(&key.tab_id))
            else {
                return;
            };
            let Some(picker) = tab.picker else { return };
            if picker.generation != generation
                || picker.epoch != epoch
                || tab.generation != generation
            {
                return;
            }
            tab.picker = None;
            let mut event = BrowserElementSelectedEvent {
                workspace_id: tab.workspace_id.clone(),
                tab_id: tab.tab_id.clone(),
                generation: tab.generation,
                status: payload.status,
                reason: payload
                    .reason
                    .as_deref()
                    .and_then(|reason| bounded_chars(reason, 200)),
                url: None,
                title: None,
                selector: None,
                accessible_name: None,
                text_snippet: None,
                rect: None,
                classes: Vec::new(),
                class_count: 0,
                source_hash: String::new(),
            };
            if payload.status == BrowserPickerStatus::Unavailable {
                event.reason = event
                    .reason
                    .or_else(|| Some("inspector-unavailable".to_string()));
                event.url = Some(tab.url.clone()).filter(|url| !url.is_empty());
                event.title = Some(tab.title.clone()).filter(|title| !title.is_empty());
            } else {
                let payload_url = payload
                    .url
                    .as_deref()
                    .and_then(|url| normalize_browser_url(url, false).ok().flatten());
                if payload_url.is_none() {
                    event.status = BrowserPickerStatus::Unavailable;
                    event.reason = Some("unsafe-page-url".to_string());
                } else if payload_url.as_deref() != Some(tab.url.as_str()) {
                    event.status = BrowserPickerStatus::Unavailable;
                    event.reason = Some("page-changed".to_string());
                } else {
                    event.url = payload_url;
                    event.title = bounded_chars(
                        payload.title.as_deref().unwrap_or_default(),
                        MAX_TITLE_BYTES,
                    );
                    event.selector =
                        bounded_utf8_bytes(payload.selector.as_deref(), MAX_SELECTOR_BYTES);
                    event.accessible_name = bounded_chars(
                        payload.accessible_name.as_deref().unwrap_or_default(),
                        MAX_ACCESSIBLE_NAME_CHARS,
                    );
                    event.text_snippet = bounded_chars(
                        payload.text_snippet.as_deref().unwrap_or_default(),
                        MAX_TEXT_CHARS,
                    );
                    event.rect = sanitize_rect(payload.rect);
                    event.classes = payload
                        .classes
                        .into_iter()
                        .take(MAX_CLASSES)
                        .filter_map(|class_name| {
                            bounded_utf8_bytes(Some(&class_name), MAX_CLASS_NAME_BYTES)
                        })
                        .collect();
                    event.class_count = payload
                        .class_count
                        .min(MAX_CLASSES)
                        .max(event.classes.len());
                    event.source_hash = browser_source_hash(&event);
                }
            }
            (tab.sink.clone(), event)
        };
        event.0.element_selected(event.1);
    }
}

#[tauri::command]
// Native view creation must run in the UI-thread IPC handler. Dispatching it
// to the async pool holds the registry lock while add_child waits for the UI
// thread, which can itself be waiting for that lock in a page-load callback.
pub fn create_browser_tab(
    app: tauri::AppHandle,
    registry: tauri::State<'_, BrowserRegistry>,
    input: BrowserTabInput,
) -> Result<BrowserTabResult, BrowserCommandError> {
    registry.create_tab(
        Some(&app),
        input,
        Arc::new(TauriBrowserEventSink { app: app.clone() }),
    )
}

#[tauri::command]
pub async fn set_browser_tab_bounds(
    registry: tauri::State<'_, BrowserRegistry>,
    input: BrowserBoundsInput,
) -> Result<(), BrowserCommandError> {
    registry.set_bounds(
        BrowserTarget {
            workspace_id: input.workspace_id,
            tab_id: input.tab_id,
            generation: input.generation,
        },
        input.bounds,
    )
}

#[tauri::command]
pub async fn set_browser_tab_viewport(
    registry: tauri::State<'_, BrowserRegistry>,
    input: BrowserViewportInput,
) -> Result<(), BrowserCommandError> {
    registry.set_viewport(input)
}

#[tauri::command]
pub async fn show_browser_tab(
    registry: tauri::State<'_, BrowserRegistry>,
    input: BrowserTarget,
) -> Result<(), BrowserCommandError> {
    registry.show_tab(input)
}

#[tauri::command]
pub async fn hide_browser_workspace(
    registry: tauri::State<'_, BrowserRegistry>,
    input: BrowserWorkspaceTarget,
) -> Result<(), BrowserCommandError> {
    registry.hide_workspace(input)
}

#[tauri::command]
pub async fn navigate_browser_tab(
    registry: tauri::State<'_, BrowserRegistry>,
    input: BrowserNavigationInput,
) -> Result<(), BrowserCommandError> {
    registry.navigate(input)
}

#[tauri::command]
pub async fn reload_browser_tab(
    registry: tauri::State<'_, BrowserRegistry>,
    input: BrowserTarget,
) -> Result<(), BrowserCommandError> {
    registry.reload(input)
}

#[tauri::command]
pub async fn go_back_browser_tab(
    registry: tauri::State<'_, BrowserRegistry>,
    input: BrowserTarget,
) -> Result<(), BrowserCommandError> {
    registry.go_back(input)
}

#[tauri::command]
pub async fn go_forward_browser_tab(
    registry: tauri::State<'_, BrowserRegistry>,
    input: BrowserTarget,
) -> Result<(), BrowserCommandError> {
    registry.go_forward(input)
}

#[tauri::command]
pub async fn close_browser_tab(
    registry: tauri::State<'_, BrowserRegistry>,
    input: BrowserTarget,
) -> Result<(), BrowserCommandError> {
    registry.close_tab(input)
}

#[tauri::command]
pub async fn clear_browser_workspace_data(
    registry: tauri::State<'_, BrowserRegistry>,
    input: BrowserWorkspaceTarget,
) -> Result<(), BrowserCommandError> {
    registry.clear_workspace_data(input)
}

#[tauri::command]
pub async fn arm_browser_element_picker(
    registry: tauri::State<'_, BrowserRegistry>,
    input: BrowserPickerInput,
) -> Result<(), BrowserCommandError> {
    registry.arm_picker(input)
}

#[tauri::command]
pub async fn cancel_browser_element_picker(
    registry: tauri::State<'_, BrowserRegistry>,
    input: BrowserTarget,
) -> Result<(), BrowserCommandError> {
    registry.cancel_picker(input)
}

#[tauri::command]
pub async fn inspect_browser_rect(
    registry: tauri::State<'_, BrowserRegistry>,
    input: BrowserRectInspectionInput,
) -> Result<Option<BrowserElementMetadata>, BrowserCommandError> {
    registry.inspect_rect(input).await
}

#[tauri::command]
pub async fn capture_browser_viewport(
    registry: tauri::State<'_, BrowserRegistry>,
    input: BrowserTarget,
) -> Result<BrowserMarkupCapture, BrowserCommandError> {
    registry.capture(input).await
}

#[tauri::command]
pub async fn open_browser_tab_devtools(
    registry: tauri::State<'_, BrowserRegistry>,
    input: BrowserTarget,
) -> Result<(), BrowserCommandError> {
    registry.open_devtools(input)
}

#[tauri::command]
pub async fn open_browser_tab_external(
    registry: tauri::State<'_, BrowserRegistry>,
    input: BrowserTarget,
) -> Result<(), BrowserCommandError> {
    registry.open_external(input)
}

impl BrowserProfile {
    fn new(requested_id: Option<&str>) -> Self {
        // Every recreated view shares the application's one durable website data store.
        let store_identifier = *b"mcb-browser-main";
        Self {
            id: requested_id
                .map(ToOwned::to_owned)
                .unwrap_or_else(|| format!("workspace-{}", Uuid::new_v4().simple())),
            store_identifier,
        }
    }

    #[cfg(not(target_os = "macos"))]
    fn store_directory_name(&self) -> String {
        self.store_identifier
            .iter()
            .map(|byte| format!("{byte:02x}"))
            .collect()
    }
}

/// Asks a WKWebView to draw the page it is showing. The answer arrives later
/// on the main thread, so the completion handler encodes it and sends the
/// result down the channel the caller is waiting on.
///
/// # Safety
///
/// `wk` must be a live WKWebView owned by the calling (main) thread.
#[cfg(target_os = "macos")]
unsafe fn take_wk_snapshot(
    wk: *mut objc2::runtime::AnyObject,
    tx: tokio::sync::mpsc::Sender<Result<(Vec<u8>, u32, u32), String>>,
) -> Result<(), String> {
    use objc2::rc::Retained;
    use objc2::runtime::AnyObject;
    use objc2::{class, msg_send};
    if wk.is_null() {
        return Err("The page's native view is gone".to_string());
    }
    // SAFETY: the caller guarantees the receiver; the configuration is a
    // freshly allocated object owned by this function, and the block outlives
    // the call because WebKit retains it until it answers.
    unsafe {
        let config: Retained<AnyObject> = msg_send![class!(WKSnapshotConfiguration), new];
        let block = block2::RcBlock::new(move |image: *mut AnyObject, error: *mut AnyObject| {
            let _ = tx.try_send(encode_snapshot_png(image, error));
        });
        let () =
            msg_send![&*wk, takeSnapshotWithConfiguration: &*config, completionHandler: &*block];
    }
    Ok(())
}

/// Turns the image WebKit drew into PNG bytes and its pixel size, or explains
/// in plain English why it could not.
///
/// # Safety
///
/// `image` must be nil or an NSImage, and `error` nil or an NSError.
#[cfg(target_os = "macos")]
unsafe fn encode_snapshot_png(
    image: *mut objc2::runtime::AnyObject,
    error: *mut objc2::runtime::AnyObject,
) -> Result<(Vec<u8>, u32, u32), String> {
    use objc2::rc::Retained;
    use objc2::runtime::AnyObject;
    use objc2::{class, msg_send};
    // SAFETY: every message below is sent to an object the caller vouched
    // for, or to one returned by the previous step and null-checked here.
    unsafe {
        if image.is_null() {
            if !error.is_null() {
                let description: Option<Retained<AnyObject>> =
                    msg_send![&*error, localizedDescription];
                if let Some(description) = description {
                    let utf8: *const std::ffi::c_char = msg_send![&*description, UTF8String];
                    if !utf8.is_null() {
                        if let Ok(text) = std::ffi::CStr::from_ptr(utf8).to_str() {
                            return Err(text.to_string());
                        }
                    }
                }
            }
            return Err("The page could not be drawn".to_string());
        }
        let tiff: Option<Retained<AnyObject>> = msg_send![&*image, TIFFRepresentation];
        let Some(tiff) = tiff else {
            return Err("The drawn page held no image data".to_string());
        };
        let rep: Option<Retained<AnyObject>> =
            msg_send![class!(NSBitmapImageRep), imageRepWithData: &*tiff];
        let Some(rep) = rep else {
            return Err("The drawn page could not be read as an image".to_string());
        };
        let width: isize = msg_send![&*rep, pixelsWide];
        let height: isize = msg_send![&*rep, pixelsHigh];
        if width <= 0 || height <= 0 {
            return Err("The drawn page had no size".to_string());
        }
        let properties: Retained<AnyObject> = msg_send![class!(NSDictionary), dictionary];
        // 4 is NSBitmapImageFileTypePNG.
        let png: Option<Retained<AnyObject>> =
            msg_send![&*rep, representationUsingType: 4usize, properties: &*properties];
        let Some(png) = png else {
            return Err("The drawn page could not be encoded as PNG".to_string());
        };
        let bytes: *const u8 = msg_send![&*png, bytes];
        let length: usize = msg_send![&*png, length];
        if bytes.is_null() || length == 0 {
            return Err("The encoded page image was empty".to_string());
        }
        Ok((
            std::slice::from_raw_parts(bytes, length).to_vec(),
            width as u32,
            height as u32,
        ))
    }
}

struct TauriBrowserView {
    webview: tauri::Webview,
    navigation_generation: Arc<AtomicU64>,
    pending_initial_url: Mutex<Option<tauri::Url>>,
    /// Whether the view has been given a rectangle the panel measured. Until
    /// then the only rectangle it has is whatever the workspace was holding —
    /// on a restored session, the floating default over the middle of the
    /// shell — so it waits off screen rather than painting a frame there.
    placed: AtomicBool,
    /// A show asked for before that rectangle arrived, honoured once it does.
    pending_show: AtomicBool,
}

impl BrowserView for TauriBrowserView {
    fn set_bounds(&self, bounds: BrowserBounds) -> Result<(), BrowserCommandError> {
        // The child view is placed in the window's content view, which under
        // the overlay title bar starts at the window top like the document
        // does, so the measured rectangle needs no title-bar correction.
        stderr_log!(
            "browser: view placed x={} y={} w={} h={}",
            bounds.x.round(),
            bounds.y.round(),
            bounds.width.round(),
            bounds.height.round()
        );
        #[cfg(target_os = "linux")]
        self.webview
            .with_webview(move |platform| {
                use gtk::prelude::*;
                let view = platform.inner();
                view.set_margin_start(bounds.x.round() as i32);
                view.set_margin_top(bounds.y.round() as i32);
                let parent = view.parent().expect("browser view has its overlay parent");
                view.set_margin_end(
                    (parent.allocated_width() - (bounds.x + bounds.width).round() as i32).max(0),
                );
                view.set_margin_bottom(
                    (parent.allocated_height() - (bounds.y + bounds.height).round() as i32).max(0),
                );
            })
            .map_err(native_error)?;
        #[cfg(not(target_os = "linux"))]
        self.webview
            .set_bounds(tauri::Rect {
                position: tauri::Position::Logical(tauri::LogicalPosition::new(bounds.x, bounds.y)),
                size: tauri::Size::Logical(tauri::LogicalSize::new(bounds.width, bounds.height)),
            })
            .map_err(native_error)?;
        self.placed.store(true, Ordering::Release);
        if let Some(url) = self
            .pending_initial_url
            .lock()
            .expect("browser initial URL mutex poisoned")
            .take()
        {
            self.navigate_url(url)?;
        }
        if self.pending_show.swap(false, Ordering::AcqRel) {
            self.webview.show().map_err(native_error)?;
        }
        Ok(())
    }

    fn show(&self) -> Result<(), BrowserCommandError> {
        if !self.placed.load(Ordering::Acquire) {
            self.pending_show.store(true, Ordering::Release);
            return Ok(());
        }
        self.webview.show().map_err(native_error)
    }

    fn hide(&self) -> Result<(), BrowserCommandError> {
        self.pending_show.store(false, Ordering::Release);
        self.webview.hide().map_err(native_error)
    }

    fn close(&self) -> Result<(), BrowserCommandError> {
        // wry connects the page's `ipc` handler on the page's own content
        // manager with a closure holding the page (wry 0.55.1
        // webkitgtk/mod.rs:638). That cycle keeps a closed page and its web
        // process alive, so drop the manager's handlers before closing. This is
        // best effort: the page must still close if it can't be reached.
        #[cfg(target_os = "linux")]
        let _ = self.webview.with_webview(|platform| {
            use gtk::glib::prelude::*;
            let manager: gtk::glib::Object = platform.inner().property("user-content-manager");
            // SAFETY: only drops the manager's signal handlers; the page is closed next.
            unsafe { manager.run_dispose() };
        });
        self.webview.close().map_err(native_error)
    }

    fn navigate(&self, url: &str, generation: u64) -> Result<(), BrowserCommandError> {
        let previous_generation = self
            .navigation_generation
            .swap(generation, Ordering::AcqRel);
        let parsed = tauri::Url::parse(url).map_err(|_| {
            BrowserCommandError::new(BrowserErrorCode::InvalidUrl, "Browser URL is invalid")
        })?;
        if let Err(error) = self.navigate_url(parsed) {
            self.navigation_generation
                .store(previous_generation, Ordering::Release);
            return Err(error);
        }
        Ok(())
    }

    fn reload(&self) -> Result<(), BrowserCommandError> {
        self.webview.reload().map_err(native_error)
    }

    fn go_back(&self) -> Result<(), BrowserCommandError> {
        self.webview
            .eval("window.history.back();")
            .map_err(native_error)
    }

    fn go_forward(&self) -> Result<(), BrowserCommandError> {
        self.webview
            .eval("window.history.forward();")
            .map_err(native_error)
    }

    #[cfg(target_os = "macos")]
    fn snapshot(&self) -> SnapshotFuture {
        let (tx, mut rx) = tokio::sync::mpsc::channel(1);
        let scheduled = self.webview.with_webview(move |platform| {
            // Main thread: start WebKit's snapshot. Its completion handler also
            // runs here, but sending the answer never blocks this thread.
            let failure = tx.clone();
            // SAFETY: `inner` is this view's live WKWebView, and the call is
            // made on the thread that owns it.
            if let Err(message) = unsafe { take_wk_snapshot(platform.inner().cast(), tx) } {
                let _ = failure.try_send(Err(message));
            }
        });
        if let Err(error) = scheduled {
            return Box::pin(std::future::ready(Err(native_error(error))));
        }
        // Async runtime worker: awaiting yields the worker until WebKit's main-
        // thread callback sends an answer. Never replace this with recv().
        Box::pin(async move {
            match rx.recv().await {
                Some(Ok(result)) => Ok(result),
                Some(Err(message)) => {
                    Err(BrowserCommandError::new(BrowserErrorCode::Native, message))
                }
                None => Err(BrowserCommandError::new(
                    BrowserErrorCode::Native,
                    "The page snapshot ended without an answer",
                )),
            }
        })
    }

    fn eval(&self, script: &str) -> Result<(), BrowserCommandError> {
        self.webview.eval(script).map_err(native_error)
    }

    fn eval_with_callback(
        &self,
        script: &str,
        callback: Box<dyn Fn(String) + Send + 'static>,
    ) -> Result<(), BrowserCommandError> {
        self.webview
            .eval_with_callback(script, callback)
            .map_err(native_error)
    }

    fn open_devtools(&self) -> Result<(), BrowserCommandError> {
        #[cfg(debug_assertions)]
        {
            self.webview.open_devtools();
            Ok(())
        }
        #[cfg(not(debug_assertions))]
        {
            Err(BrowserCommandError::new(
                BrowserErrorCode::Unsupported,
                "Browser devtools are available only in debug builds",
            ))
        }
    }
}

impl TauriBrowserView {
    fn navigate_url(&self, url: tauri::Url) -> Result<(), BrowserCommandError> {
        #[cfg(target_os = "macos")]
        if url.scheme() == "file" {
            let path = url.to_file_path().map_err(|_| {
                BrowserCommandError::new(
                    BrowserErrorCode::InvalidUrl,
                    "Local file address is invalid",
                )
            })?;
            let read_root = path
                .parent()
                .map(std::path::Path::to_path_buf)
                .ok_or_else(|| {
                    BrowserCommandError::new(
                        BrowserErrorCode::InvalidUrl,
                        "Local file address is invalid",
                    )
                })?;
            return self
                .webview
                .with_webview(move |platform| {
                    use objc2_foundation::{NSString, NSURL};
                    use objc2_web_kit::WKWebView;

                    let path = NSString::from_str(&path.to_string_lossy());
                    let read_root = NSString::from_str(&read_root.to_string_lossy());
                    let target_url = NSURL::fileURLWithPath(&path);
                    let read_root_url = NSURL::fileURLWithPath_isDirectory(&read_root, true);
                    // SAFETY: Tauri runs this closure on the native WebView
                    // thread and `inner` is its live WKWebView pointer.
                    unsafe {
                        let wk = &*platform.inner().cast::<WKWebView>();
                        let _ = wk.loadFileURL_allowingReadAccessToURL(
                            &target_url,
                            &read_root_url,
                        );
                    }
                })
                .map_err(native_error);
        }

        self.webview.navigate(url).map_err(native_error)
    }
}

struct TauriBrowserViewFactory;

impl BrowserViewFactory for TauriBrowserViewFactory {
    fn create(
        &self,
        app: Option<&tauri::AppHandle>,
        input: &BrowserTabInput,
        profile: &BrowserProfile,
        callbacks: BrowserViewCallbacks,
    ) -> Result<Arc<dyn BrowserView>, BrowserCommandError> {
        let app = app.ok_or_else(|| {
            BrowserCommandError::new(
                BrowserErrorCode::Native,
                "A running Tauri application is required for a native browser tab",
            )
        })?;
        let window = app.get_window("main").ok_or_else(|| {
            BrowserCommandError::new(
                BrowserErrorCode::Native,
                "The main Tauri window is not available for a browser tab",
            )
        })?;
        let initial_url =
            normalize_browser_url(&input.url, true)?.unwrap_or_else(|| "about:blank".to_string());
        let initial_url = tauri::Url::parse(&initial_url).map_err(|_| {
            BrowserCommandError::new(BrowserErrorCode::InvalidUrl, "Browser URL is invalid")
        })?;
        let local_initial_url = (initial_url.scheme() == "file").then(|| initial_url.clone());
        let builder_url = local_initial_url
            .as_ref()
            .map(|_| tauri::Url::parse("about:blank").expect("about:blank is valid"))
            .unwrap_or(initial_url);
        let label = format!("mcb-browser-{}", Uuid::new_v4().simple());
        let navigation_generation = Arc::new(AtomicU64::new(input.generation));
        let page_load = callbacks.on_page_load.clone();
        let title_changed = callbacks.on_title_changed.clone();
        let page_generation = navigation_generation.clone();
        let title_generation = navigation_generation.clone();
        let mut builder = WebviewBuilder::new(label, WebviewUrl::External(builder_url))
            .initialization_script(BROWSER_INSPECTOR_SCRIPT)
            .on_navigation(|url| normalize_browser_url(url.as_str(), true).is_ok())
            .on_page_load(move |_, payload| {
                let phase = match payload.event() {
                    PageLoadEvent::Started => BrowserLoadPhase::Started,
                    PageLoadEvent::Finished => BrowserLoadPhase::Finished,
                };
                page_load(NativePageLoad {
                    phase,
                    url: payload.url().to_string(),
                    generation: page_generation.load(Ordering::Acquire),
                });
            })
            .on_document_title_changed(move |_, title| {
                title_changed(title_generation.load(Ordering::Acquire), title);
            });

        #[cfg(target_os = "macos")]
        {
            // WKWebView ignores `data_directory`; its persistent website data
            // store is selected by identifier instead.
            builder = builder.data_store_identifier(profile.store_identifier);
        }
        #[cfg(not(target_os = "macos"))]
        {
            let profile_directory = app
                .path()
                .app_data_dir()
                .map_err(native_error)?
                .join("browser-profiles")
                .join(profile.store_directory_name());
            builder = builder.data_directory(profile_directory);
        }

        let bounds = input
            .bounds
            .map(clamp_browser_bounds)
            .transpose()?
            .unwrap_or(BrowserBounds {
                x: 0.0,
                y: 0.0,
                width: 1.0,
                height: 1.0,
            });
        let webview = window
            .add_child(
                builder,
                tauri::LogicalPosition::new(bounds.x, bounds.y),
                tauri::LogicalSize::new(bounds.width, bounds.height),
            )
            .map_err(native_error)?;
        // Off screen until the panel says where it goes: see `placed`.
        webview.hide().map_err(native_error)?;
        #[cfg(target_os = "linux")]
        webview
            .with_webview(move |platform| {
                use gtk::prelude::*;
                let child = platform.inner();
                let vbox = child
                    .parent()
                    .expect("new browser view has its Tauri parent")
                    .downcast::<gtk::Box>()
                    .expect("Tauri packs new browser views into a GTK box");
                vbox.remove(&child);
                // Tauri packs Linux child views beside the shell in its vertical
                // box. An overlay keeps the shell full-size and lets browser
                // bounds position the native page over its measured host.
                let overlay = vbox
                    .children()
                    .into_iter()
                    .find_map(|widget| widget.downcast::<gtk::Overlay>().ok())
                    .unwrap_or_else(|| {
                        let overlay = gtk::Overlay::new();
                        let shell = vbox
                            .children()
                            .into_iter()
                            .find(|widget| widget.type_().name() == "WebKitWebView")
                            .expect("the main window contains its shell webview");
                        vbox.remove(&shell);
                        overlay.add(&shell);
                        vbox.pack_start(&overlay, true, true, 0);
                        overlay.show();
                        overlay
                    });
                child.set_halign(gtk::Align::Fill);
                child.set_valign(gtk::Align::Fill);
                overlay.add_overlay(&child);
            })
            .map_err(native_error)?;
        let view = Arc::new(TauriBrowserView {
            webview,
            navigation_generation,
            pending_initial_url: Mutex::new(local_initial_url),
            placed: AtomicBool::new(false),
            pending_show: AtomicBool::new(false),
        });
        Ok(view)
    }
}

fn native_error(error: impl std::fmt::Display) -> BrowserCommandError {
    BrowserCommandError::new(BrowserErrorCode::Native, error.to_string())
}

fn normalize_browser_url(
    raw: &str,
    allow_blank: bool,
) -> Result<Option<String>, BrowserCommandError> {
    let value = raw.trim();
    if value.is_empty() {
        return if allow_blank {
            Ok(None)
        } else {
            Err(BrowserCommandError::new(
                BrowserErrorCode::InvalidUrl,
                "Browser URL is required",
            ))
        };
    }
    if value.len() > MAX_BROWSER_URL_BYTES {
        return Err(BrowserCommandError::new(
            BrowserErrorCode::InvalidUrl,
            "Browser URL is too long",
        ));
    }
    if value.eq_ignore_ascii_case("about:blank") && allow_blank {
        return Ok(None);
    }
    let parsed = tauri::Url::parse(value).map_err(|_| {
        BrowserCommandError::new(BrowserErrorCode::InvalidUrl, "Browser URL is invalid")
    })?;
    if !parsed.username().is_empty() || parsed.password().is_some() {
        return Err(BrowserCommandError::new(
            BrowserErrorCode::InvalidUrl,
            "Browser addresses cannot include sign-in information",
        ));
    }
    match parsed.scheme() {
        "http" | "https" => Ok(Some(parsed.to_string())),
        "file" => normalize_local_file_url(parsed).map(Some),
        _ => Err(BrowserCommandError::new(
            BrowserErrorCode::InvalidUrl,
            "Only http, https, and file URLs are allowed",
        )),
    }
}

fn normalize_local_file_url(mut url: tauri::Url) -> Result<String, BrowserCommandError> {
    if url
        .host_str()
        .is_some_and(|host| !host.is_empty() && host != "localhost")
    {
        return Err(BrowserCommandError::new(
            BrowserErrorCode::InvalidUrl,
            "Local file addresses cannot name another computer",
        ));
    }
    if url.query().is_some() {
        return Err(BrowserCommandError::new(
            BrowserErrorCode::InvalidUrl,
            "Local file addresses cannot include a query",
        ));
    }
    let fragment = url.fragment().map(str::to_string);
    url.set_fragment(None);
    let path = url.to_file_path().map_err(|_| {
        BrowserCommandError::new(
            BrowserErrorCode::InvalidUrl,
            "Local file address is invalid",
        )
    })?;
    let metadata = std::fs::metadata(&path).map_err(|error| {
        let message = if error.kind() == std::io::ErrorKind::NotFound {
            "Local file does not exist".to_string()
        } else {
            format!("Local file cannot be opened: {error}")
        };
        BrowserCommandError::new(BrowserErrorCode::InvalidUrl, message)
    })?;
    if !metadata.is_file() {
        return Err(BrowserCommandError::new(
            BrowserErrorCode::InvalidUrl,
            "Local browser address must point to a file",
        ));
    }
    let canonical = std::fs::canonicalize(path).map_err(|error| {
        BrowserCommandError::new(
            BrowserErrorCode::InvalidUrl,
            format!("Local file cannot be opened: {error}"),
        )
    })?;
    let mut normalized = tauri::Url::from_file_path(canonical).map_err(|_| {
        BrowserCommandError::new(
            BrowserErrorCode::InvalidUrl,
            "Local file address is invalid",
        )
    })?;
    normalized.set_fragment(fragment.as_deref());
    Ok(normalized.to_string())
}

fn validate_identity(value: &str, label: &str) -> Result<(), BrowserCommandError> {
    if value.trim().is_empty()
        || value.len() > MAX_WORKSPACE_ID_BYTES.max(MAX_TAB_ID_BYTES)
        || value.chars().any(char::is_control)
    {
        return Err(BrowserCommandError::new(
            BrowserErrorCode::InvalidIdentity,
            format!("Browser {label} identity is invalid"),
        ));
    }
    Ok(())
}

fn validate_profile_id(value: &str) -> Result<(), BrowserCommandError> {
    if value.trim().is_empty()
        || value.len() > MAX_PROFILE_ID_BYTES
        || value.chars().any(char::is_control)
        || value.contains('/')
        || value.contains('\\')
        || value.contains("..")
    {
        return Err(BrowserCommandError::new(
            BrowserErrorCode::InvalidProfile,
            "Browser profile id must be a short opaque label",
        ));
    }
    Ok(())
}

fn clamp_browser_bounds(bounds: BrowserBounds) -> Result<BrowserBounds, BrowserCommandError> {
    if !bounds.x.is_finite()
        || !bounds.y.is_finite()
        || !bounds.width.is_finite()
        || !bounds.height.is_finite()
        || bounds.x.abs() > MAX_BROWSER_COORDINATE
        || bounds.y.abs() > MAX_BROWSER_COORDINATE
        || bounds.width <= 0.0
        || bounds.height <= 0.0
        || bounds.width > MAX_BROWSER_SIZE
        || bounds.height > MAX_BROWSER_SIZE
    {
        return Err(BrowserCommandError::new(
            BrowserErrorCode::InvalidBounds,
            "Browser bounds are outside the allowed range",
        ));
    }
    Ok(bounds)
}

fn validate_browser_viewport(viewport: &BrowserViewport) -> Result<(), BrowserCommandError> {
    if let Some(preset) = viewport.preset.as_deref() {
        if !matches!(
            preset,
            "responsive"
                | "mobile-s"
                | "mobile-m"
                | "mobile-l"
                | "tablet"
                | "laptop"
                | "laptop-l"
                | "desktop"
                | "custom"
        ) {
            return Err(BrowserCommandError::new(
                BrowserErrorCode::InvalidBounds,
                "Browser viewport preset is not supported",
            ));
        }
    }
    for size in [viewport.width, viewport.height].into_iter().flatten() {
        if !size.is_finite() || size <= 0.0 || size > MAX_BROWSER_SIZE {
            return Err(BrowserCommandError::new(
                BrowserErrorCode::InvalidBounds,
                "Browser viewport size is outside the allowed range",
            ));
        }
    }
    Ok(())
}

fn title_from_url(url: Option<&str>) -> String {
    let title = url
        .and_then(|value| tauri::Url::parse(value).ok())
        .and_then(|parsed| {
            if parsed.scheme() == "file" {
                parsed.to_file_path().ok().and_then(|path| {
                    path.file_name()
                        .map(|name| name.to_string_lossy().into_owned())
                })
            } else {
                parsed.host_str().map(str::to_string)
            }
        });
    title
        .and_then(|value| bounded_chars(&value, MAX_TITLE_BYTES))
        .unwrap_or_else(|| "New browser tab".to_string())
}

fn bounded_chars(value: &str, limit: usize) -> Option<String> {
    let trimmed = value.trim();
    if trimmed.is_empty() {
        return None;
    }
    let bounded: String = trimmed.chars().take(limit).collect();
    (!bounded.is_empty()).then_some(bounded)
}

fn bounded_utf8_bytes(value: Option<&str>, limit: usize) -> Option<String> {
    let trimmed = value?.trim();
    if trimmed.is_empty() {
        return None;
    }
    if trimmed.len() <= limit {
        return Some(trimmed.to_string());
    }
    let end = trimmed
        .char_indices()
        .take_while(|(index, _)| *index < limit)
        .map(|(index, character)| index + character.len_utf8())
        .last()
        .unwrap_or(0)
        .min(limit);
    (end > 0).then(|| trimmed[..end].to_string())
}

fn sanitize_rect(rect: Option<BrowserRect>) -> Option<BrowserRect> {
    let rect = rect?;
    (rect.x.is_finite()
        && rect.y.is_finite()
        && rect.width.is_finite()
        && rect.height.is_finite()
        && rect.x.abs() <= MAX_BROWSER_COORDINATE
        && rect.y.abs() <= MAX_BROWSER_COORDINATE
        && rect.width >= 0.0
        && rect.height >= 0.0
        && rect.width <= MAX_BROWSER_SIZE
        && rect.height <= MAX_BROWSER_SIZE)
        .then_some(rect)
}

fn validate_inspection_rect(rect: BrowserRect) -> Result<BrowserRect, BrowserCommandError> {
    if !rect.x.is_finite()
        || !rect.y.is_finite()
        || !rect.width.is_finite()
        || !rect.height.is_finite()
        || rect.x < 0.0
        || rect.y < 0.0
        || rect.width <= 0.0
        || rect.height <= 0.0
        || rect.x > MAX_BROWSER_COORDINATE
        || rect.y > MAX_BROWSER_COORDINATE
        || rect.width > MAX_BROWSER_SIZE
        || rect.height > MAX_BROWSER_SIZE
    {
        return Err(BrowserCommandError::new(
            BrowserErrorCode::InvalidBounds,
            "Browser inspection rectangle is outside the allowed range",
        ));
    }
    Ok(rect)
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
struct BrowserPickerPayloadWire {
    status: BrowserPickerStatus,
    reason: Option<String>,
    url: Option<String>,
    title: Option<String>,
    selector: Option<String>,
    role: Option<String>,
    accessible_name: Option<String>,
    text_snippet: Option<String>,
    rect: Option<BrowserRect>,
    #[serde(default)]
    classes: Vec<String>,
    #[serde(default)]
    class_count: usize,
}

fn parse_picker_payload(raw: &str) -> Result<NativePickerPayload, BrowserCommandError> {
    let value: serde_json::Value = serde_json::from_str(raw).map_err(|_| {
        BrowserCommandError::new(
            BrowserErrorCode::InvalidPickerPayload,
            "Browser inspector payload is not valid JSON",
        )
    })?;
    let value = match value {
        serde_json::Value::String(inner) => serde_json::from_str(&inner).map_err(|_| {
            BrowserCommandError::new(
                BrowserErrorCode::InvalidPickerPayload,
                "Browser inspector payload is not valid JSON",
            )
        })?,
        value => value,
    };
    let payload: BrowserPickerPayloadWire = serde_json::from_value(value).map_err(|_| {
        BrowserCommandError::new(
            BrowserErrorCode::InvalidPickerPayload,
            "Browser inspector payload has an invalid shape",
        )
    })?;
    Ok(NativePickerPayload {
        status: payload.status,
        reason: payload.reason,
        url: payload.url,
        title: payload.title,
        selector: payload.selector,
        role: payload.role,
        accessible_name: payload.accessible_name,
        text_snippet: payload.text_snippet,
        rect: payload.rect,
        class_count: payload.class_count.max(payload.classes.len()),
        classes: payload.classes,
    })
}

fn browser_source_hash(event: &BrowserElementSelectedEvent) -> String {
    let mut hasher = std::collections::hash_map::DefaultHasher::new();
    event.workspace_id.hash(&mut hasher);
    event.tab_id.hash(&mut hasher);
    event.generation.hash(&mut hasher);
    event.url.hash(&mut hasher);
    event.title.hash(&mut hasher);
    event.selector.hash(&mut hasher);
    event.accessible_name.hash(&mut hasher);
    event.text_snippet.hash(&mut hasher);
    event.classes.hash(&mut hasher);
    event.class_count.hash(&mut hasher);
    if let Some(rect) = event.rect {
        rect.x.to_bits().hash(&mut hasher);
        rect.y.to_bits().hash(&mut hasher);
        rect.width.to_bits().hash(&mut hasher);
        rect.height.to_bits().hash(&mut hasher);
    }
    format!("{:016x}", hasher.finish())
}

fn open_external_url(url: &str) -> Result<(), BrowserCommandError> {
    // The system opener also runs local files (a `.command` file is a script), so only web pages go to it.
    if !url.starts_with("http://") && !url.starts_with("https://") {
        return Err(BrowserCommandError::new(BrowserErrorCode::InvalidUrl, "Only http and https addresses open in the external browser"));
    }
    #[cfg(target_os = "macos")]
    let mut command = {
        let mut command = Command::new("open");
        command.arg(url);
        command
    };
    #[cfg(target_os = "windows")]
    let mut command = {
        let mut command = Command::new("cmd");
        command.args(["/C", "start", "", url]);
        command
    };
    #[cfg(all(unix, not(target_os = "macos")))]
    let mut command = {
        let mut command = Command::new("xdg-open");
        command.arg(url);
        command
    };
    command
        .spawn()
        .map(|_| ())
        .map_err(|error| native_error(format!("Could not open browser URL externally: {error}")))
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::sync::atomic::{AtomicUsize, Ordering};
    mod navigation;
    #[derive(Default)]
    struct FakeView {
        close_calls: AtomicUsize,
        set_bounds_calls: AtomicUsize,
        evals: Mutex<Vec<String>>,
        snapshot_never_answers: bool,
        snapshot_delay: Option<Duration>,
    }

    impl BrowserView for FakeView {
        fn set_bounds(&self, _bounds: BrowserBounds) -> Result<(), BrowserCommandError> {
            self.set_bounds_calls.fetch_add(1, Ordering::SeqCst);
            Ok(())
        }

        fn show(&self) -> Result<(), BrowserCommandError> {
            Ok(())
        }

        fn hide(&self) -> Result<(), BrowserCommandError> {
            Ok(())
        }

        fn close(&self) -> Result<(), BrowserCommandError> {
            self.close_calls.fetch_add(1, Ordering::SeqCst);
            Ok(())
        }

        fn navigate(&self, _url: &str, _generation: u64) -> Result<(), BrowserCommandError> {
            Ok(())
        }

        fn reload(&self) -> Result<(), BrowserCommandError> {
            Ok(())
        }

        fn go_back(&self) -> Result<(), BrowserCommandError> {
            Ok(())
        }

        fn go_forward(&self) -> Result<(), BrowserCommandError> {
            Ok(())
        }

        fn snapshot(&self) -> SnapshotFuture {
            if self.snapshot_never_answers {
                return Box::pin(std::future::pending());
            }
            if let Some(delay) = self.snapshot_delay {
                return Box::pin(async move {
                    tokio::time::sleep(delay).await;
                    Ok((vec![1, 2, 3], 4, 5))
                });
            }
            Box::pin(std::future::ready(Ok((vec![1, 2, 3], 4, 5))))
        }

        fn eval(&self, script: &str) -> Result<(), BrowserCommandError> {
            self.evals
                .lock()
                .expect("fake eval mutex poisoned")
                .push(script.to_string());
            Ok(())
        }

        fn eval_with_callback(
            &self,
            _script: &str,
            callback: Box<dyn Fn(String) + Send + 'static>,
        ) -> Result<(), BrowserCommandError> {
            callback("null".to_string());
            Ok(())
        }

        fn open_devtools(&self) -> Result<(), BrowserCommandError> {
            Ok(())
        }
    }

    struct NeverSnapshotFactory;

    impl BrowserViewFactory for NeverSnapshotFactory {
        fn create(
            &self,
            _app: Option<&tauri::AppHandle>,
            _input: &BrowserTabInput,
            _profile: &BrowserProfile,
            _callbacks: BrowserViewCallbacks,
        ) -> Result<Arc<dyn BrowserView>, BrowserCommandError> {
            Ok(Arc::new(FakeView {
                snapshot_never_answers: true,
                ..FakeView::default()
            }))
        }
    }

    struct DelayedSnapshotFactory;

    impl BrowserViewFactory for DelayedSnapshotFactory {
        fn create(
            &self,
            _app: Option<&tauri::AppHandle>,
            _input: &BrowserTabInput,
            _profile: &BrowserProfile,
            _callbacks: BrowserViewCallbacks,
        ) -> Result<Arc<dyn BrowserView>, BrowserCommandError> {
            Ok(Arc::new(FakeView {
                snapshot_delay: Some(Duration::from_millis(30)),
                ..FakeView::default()
            }))
        }
    }

    #[derive(Default)]
    struct FakeFactory {
        views: Mutex<Vec<Arc<FakeView>>>,
    }

    impl BrowserViewFactory for FakeFactory {
        fn create(
            &self,
            _app: Option<&tauri::AppHandle>,
            _input: &BrowserTabInput,
            _profile: &BrowserProfile,
            _callbacks: BrowserViewCallbacks,
        ) -> Result<Arc<dyn BrowserView>, BrowserCommandError> {
            let view = Arc::new(FakeView::default());
            self.views
                .lock()
                .expect("fake factory mutex poisoned")
                .push(view.clone());
            Ok(view)
        }
    }

    #[derive(Default)]
    struct FakeSink {
        selected: Mutex<Vec<BrowserElementSelectedEvent>>,
        closed: Mutex<Vec<BrowserTabClosedEvent>>,
    }

    impl BrowserEventSink for FakeSink {
        fn navigation(&self, _event: BrowserTabNavigationEvent) {}

        fn load(&self, _event: BrowserTabLoadEvent) {}

        fn element_selected(&self, event: BrowserElementSelectedEvent) {
            self.selected
                .lock()
                .expect("fake selected mutex poisoned")
                .push(event);
        }

        fn closed(&self, event: BrowserTabClosedEvent) {
            self.closed
                .lock()
                .expect("fake closed mutex poisoned")
                .push(event);
        }
    }

    fn input(workspace_id: &str, tab_id: &str, generation: u64, url: &str) -> BrowserTabInput {
        BrowserTabInput {
            workspace_id: workspace_id.to_string(),
            tab_id: tab_id.to_string(),
            generation,
            url: url.to_string(),
            bounds: Some(BrowserBounds {
                x: 0.0,
                y: 0.0,
                width: 640.0,
                height: 480.0,
            }),
            viewport: None,
            profile_id: None,
        }
    }

    fn target(workspace_id: &str, tab_id: &str, generation: u64) -> BrowserTarget {
        BrowserTarget {
            workspace_id: workspace_id.to_string(),
            tab_id: tab_id.to_string(),
            generation,
        }
    }

    #[tokio::test(flavor = "current_thread")]
    async fn capture_maps_a_view_snapshot_into_png_markup_capture() {
        let registry = BrowserRegistry::with_factory(Arc::new(FakeFactory::default()));
        registry
            .create_tab(
                None,
                input("workspace", "tab", 1, "https://example.test/"),
                Arc::new(FakeSink::default()),
            )
            .unwrap();

        let capture = registry
            .capture(target("workspace", "tab", 1))
            .await
            .unwrap();

        assert_eq!(capture.mime_type, "image/png");
        assert_eq!(capture.bytes, vec![1, 2, 3]);
        assert_eq!(capture.width, 4);
        assert_eq!(capture.height, 5);
    }

    #[tokio::test(flavor = "current_thread")]
    async fn capture_times_out_when_a_view_never_answers() {
        let registry = BrowserRegistry::with_factory(Arc::new(NeverSnapshotFactory));
        registry
            .create_tab(
                None,
                input("workspace", "tab", 1, "https://example.test/"),
                Arc::new(FakeSink::default()),
            )
            .unwrap();

        let started = std::time::Instant::now();
        let error = registry
            .capture_with_timeout(target("workspace", "tab", 1), Duration::from_millis(20))
            .await
            .unwrap_err();

        assert_eq!(error.code, BrowserErrorCode::Native);
        assert_eq!(error.message, "Timed out waiting for the page snapshot");
        assert!(started.elapsed() < Duration::from_secs(1));
    }

    #[tokio::test(flavor = "current_thread")]
    async fn capture_rejects_a_tab_released_while_snapshotting() {
        let registry = BrowserRegistry::with_factory(Arc::new(DelayedSnapshotFactory));
        let capture_target = target("workspace", "tab", 1);
        registry
            .create_tab(
                None,
                input("workspace", "tab", 1, "https://example.test/"),
                Arc::new(FakeSink::default()),
            )
            .unwrap();

        let capture = registry.capture(capture_target.clone());
        let release = async {
            tokio::time::sleep(Duration::from_millis(5)).await;
            registry.close_tab(capture_target).unwrap();
        };
        let (result, ()) = tokio::join!(capture, release);

        assert_eq!(result.unwrap_err().code, BrowserErrorCode::UnknownTab);
    }

    #[tokio::test(flavor = "current_thread")]
    async fn inspect_rect_rejects_invalid_request_rectangles_before_page_eval() {
        let factory = Arc::new(FakeFactory::default());
        let registry = BrowserRegistry::with_factory(factory.clone());
        registry
            .create_tab(
                None,
                input("workspace", "tab", 1, "https://example.test/"),
                Arc::new(FakeSink::default()),
            )
            .unwrap();

        for rect in [
            BrowserRect {
                x: -1.0,
                y: 0.0,
                width: 10.0,
                height: 10.0,
            },
            BrowserRect {
                x: 0.0,
                y: 0.0,
                width: 0.0,
                height: 10.0,
            },
            BrowserRect {
                x: 0.0,
                y: 0.0,
                width: f64::NAN,
                height: 10.0,
            },
        ] {
            let error = registry
                .inspect_rect(BrowserRectInspectionInput {
                    workspace_id: "workspace".to_string(),
                    tab_id: "tab".to_string(),
                    generation: 1,
                    rect,
                })
                .await
                .unwrap_err();
            assert_eq!(error.code, BrowserErrorCode::InvalidBounds);
        }

        let views = factory.views.lock().unwrap();
        assert!(views[0].evals.lock().unwrap().is_empty());
    }

    #[test]
    fn url_boundary_allows_existing_local_files_and_rejects_unsafe_inputs() {
        for unsafe_url in [
            "file://other-computer/tmp/private.html",
            "javascript:alert(1)",
            "data:text/html,hello",
            "custom://provider/page",
            "https://user:pass@example.test/",
        ] {
            assert!(
                normalize_browser_url(unsafe_url, false).is_err(),
                "unsafe URL was accepted: {unsafe_url}"
            );
        }

        let root = std::env::temp_dir().join(format!("mcb-browser-url-{}", Uuid::new_v4()));
        std::fs::create_dir_all(&root).unwrap();
        let report = root.join("My Report.html");
        std::fs::write(&report, "<h1>Local report</h1>").unwrap();
        let local_url = tauri::Url::from_file_path(&report).unwrap();
        let canonical_url =
            tauri::Url::from_file_path(std::fs::canonicalize(&report).unwrap()).unwrap();
        assert_eq!(
            normalize_browser_url(local_url.as_str(), false).unwrap(),
            Some(canonical_url.to_string())
        );
        assert_eq!(
            normalize_browser_url(&format!("{local_url}#findings"), false).unwrap(),
            Some(format!("{canonical_url}#findings"))
        );
        assert_eq!(
            normalize_browser_url(tauri::Url::from_file_path(&root).unwrap().as_str(), false)
                .unwrap_err()
                .message,
            "Local browser address must point to a file"
        );
        assert_eq!(
            normalize_browser_url(
                tauri::Url::from_file_path(root.join("missing.html"))
                    .unwrap()
                    .as_str(),
                false
            )
            .unwrap_err()
            .message,
            "Local file does not exist"
        );
        std::fs::remove_dir_all(root).unwrap();

        assert_eq!(normalize_browser_url("", true).unwrap(), None);
        assert_eq!(normalize_browser_url("about:blank", true).unwrap(), None);
        assert_eq!(
            normalize_browser_url(" https://example.test/path ", false).unwrap(),
            Some("https://example.test/path".to_string())
        );
        let overlong = "https://example.test/".to_string() + &"x".repeat(MAX_BROWSER_URL_BYTES);
        assert!(normalize_browser_url(&overlong, false).is_err());
    }

    #[test]
    fn registry_insert_remove_and_shutdown_cleanup_on_window_close() {
        let factory = Arc::new(FakeFactory::default());
        let registry = BrowserRegistry::with_factory(factory.clone());
        let sink = Arc::new(FakeSink::default());
        registry
            .create_tab(
                None,
                input("workspace-a", "tab-a", 1, "https://example.test/a"),
                sink.clone(),
            )
            .unwrap();
        assert_eq!(factory.views.lock().unwrap().len(), 1);

        registry
            .close_tab(target("workspace-a", "tab-a", 1))
            .unwrap();
        assert!(registry
            .inner
            .workspaces
            .lock()
            .unwrap()
            .get("workspace-a")
            .is_some_and(|workspace| workspace.tabs.is_empty()));
        assert_eq!(
            factory.views.lock().unwrap()[0]
                .close_calls
                .load(Ordering::SeqCst),
            1
        );
        assert_eq!(sink.closed.lock().unwrap().len(), 1);

        registry
            .create_tab(
                None,
                input("workspace-b", "tab-b", 1, "https://example.test/b"),
                sink,
            )
            .unwrap();
        registry.shutdown();
        assert!(registry.inner.workspaces.lock().unwrap().is_empty());
        assert_eq!(
            factory.views.lock().unwrap()[1]
                .close_calls
                .load(Ordering::SeqCst),
            1
        );
    }

    #[test]
    fn generation_and_identity_guards_reject_stale_targets_and_retries_are_idempotent() {
        let factory = Arc::new(FakeFactory::default());
        let registry = BrowserRegistry::with_factory(factory.clone());
        let sink = Arc::new(FakeSink::default());
        registry
            .create_tab(
                None,
                input("workspace", "tab", 1, "https://example.test/"),
                sink.clone(),
            )
            .unwrap();

        registry
            .create_tab(
                None,
                input("workspace", "tab", 1, "https://example.test/"),
                sink.clone(),
            )
            .unwrap();
        assert_eq!(factory.views.lock().unwrap().len(), 1);

        let stale = registry
            .set_bounds(
                target("workspace", "tab", 2),
                BrowserBounds {
                    x: 0.0,
                    y: 0.0,
                    width: 10.0,
                    height: 10.0,
                },
            )
            .unwrap_err();
        assert_eq!(stale.code, BrowserErrorCode::StaleGeneration);

        let unknown = registry
            .set_bounds(
                target("other", "tab", 1),
                BrowserBounds {
                    x: 0.0,
                    y: 0.0,
                    width: 10.0,
                    height: 10.0,
                },
            )
            .unwrap_err();
        assert_eq!(unknown.code, BrowserErrorCode::UnknownWorkspace);

        let navigation = registry
            .navigate(BrowserNavigationInput {
                workspace_id: "workspace".to_string(),
                tab_id: "tab".to_string(),
                generation: 1,
                url: "https://example.test/next".to_string(),
            })
            .unwrap_err();
        assert_eq!(navigation.code, BrowserErrorCode::GenerationNotMonotonic);

        let mismatch = registry
            .create_tab(
                None,
                input("workspace", "tab", 2, "https://example.test/different"),
                sink,
            )
            .unwrap_err();
        assert_eq!(mismatch.code, BrowserErrorCode::IdentityMismatch);

        registry.inner.handle_page_load(
            &BrowserTabKey::new("workspace", "tab"),
            NativePageLoad {
                phase: BrowserLoadPhase::Finished,
                url: "https://example.test/".to_string(),
                generation: 2,
            },
        );
        assert_eq!(
            registry.inner.workspaces.lock().unwrap()["workspace"].tabs["tab"].generation,
            1
        );
    }

    #[test]
    fn a_new_tab_stays_off_screen_until_the_panel_places_it() {
        let factory = Arc::new(FakeFactory::default());
        let registry = BrowserRegistry::with_factory(factory.clone());
        let sink = Arc::new(FakeSink::default());
        registry
            .create_tab(
                None,
                input("workspace", "tab", 1, "https://example.test/"),
                sink.clone(),
            )
            .unwrap();

        let views = factory.views.lock().unwrap();
        let view = views[0].clone();
        drop(views);
        // Creation asks for the view to be shown, and the bounds it was created
        // with are whatever the workspace held — not a measured rectangle.
        assert_eq!(view.set_bounds_calls.load(Ordering::SeqCst), 0);

        registry
            .set_bounds(
                target("workspace", "tab", 1),
                BrowserBounds {
                    x: 960.0,
                    y: 123.0,
                    width: 640.0,
                    height: 818.0,
                },
            )
            .unwrap();
        assert_eq!(view.set_bounds_calls.load(Ordering::SeqCst), 1);
    }

    #[test]
    fn inspector_payload_cap_emits_unavailable_without_parsing_large_input() {
        let factory = Arc::new(FakeFactory::default());
        let registry = BrowserRegistry::with_factory(factory);
        let sink = Arc::new(FakeSink::default());
        registry
            .create_tab(
                None,
                input("workspace", "tab", 1, "https://example.test/"),
                sink.clone(),
            )
            .unwrap();
        let key = BrowserTabKey::new("workspace", "tab");
        {
            let mut workspaces = registry.inner.workspaces.lock().unwrap();
            workspaces
                .get_mut("workspace")
                .unwrap()
                .tabs
                .get_mut("tab")
                .unwrap()
                .picker = Some(PickerState {
                generation: 1,
                epoch: 99,
            });
        }

        registry.inner.handle_picker_payload(
            &key,
            1,
            99,
            &"x".repeat(MAX_INSPECTOR_PAYLOAD_BYTES + 1),
        );
        let events = sink.selected.lock().unwrap();
        assert_eq!(events.len(), 1);
        assert_eq!(events[0].status, BrowserPickerStatus::Unavailable);
        assert_eq!(
            events[0].reason.as_deref(),
            Some("inspector-payload-too-large")
        );
        assert!(
            registry.inner.workspaces.lock().unwrap()["workspace"].tabs["tab"]
                .picker
                .is_none()
        );
    }
}
