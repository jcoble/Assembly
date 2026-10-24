pub(crate) mod paths;
pub(crate) mod references;
pub(crate) mod root;
pub(crate) mod search;
pub(crate) mod symbols;
pub(crate) mod walk;

use std::collections::HashMap;
use std::path::Path;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Mutex};
use std::time::Instant;
use tauri::Emitter;
use tauri_plugin_fs::FsExt;
use crate::files::discovery::paths::{
    normalized_relative_source_path,
};
use crate::files::types::{
    SourceScanProgressEvent, SourceScanStats, SourceSkippedDirectory,
};

pub(crate) const DEFAULT_SOURCE_LIST_LIMIT: usize = 10_000;
const MAX_SOURCE_LIST_LIMIT: usize = 25_000;
const DEFAULT_SOURCE_SEARCH_LIMIT: usize = 50;
const MAX_SOURCE_SEARCH_LIMIT: usize = 200;
const DEFAULT_SOURCE_DEFINITION_LIMIT: usize = 20;
const MAX_SOURCE_DEFINITION_LIMIT: usize = 100;
const DEFAULT_SOURCE_REFERENCE_LIMIT: usize = 50;
const MAX_SOURCE_REFERENCE_LIMIT: usize = 200;
/// How long one batched margin-count request may spend before it answers with
/// whatever it has counted so far. This is a ceiling, not a wait: a pass that
/// finishes early answers early. A four-thousand-file C# and TypeScript
/// project measures around 400ms in a release build, so this leaves room for a
/// cold disk before giving up on the rest.
const DEFAULT_REFERENCE_COUNT_DEADLINE_MS: u64 = 1_500;
const MAX_REFERENCE_COUNT_DEADLINE_MS: u64 = 10_000;
const MAX_SOURCE_SCAN_SKIPPED_DIRECTORY_SAMPLES: usize = 16;
const SOURCE_SCAN_PROGRESS_EVENT: &str = "source_scan_progress";
const SOURCE_SCAN_PROGRESS_INTERVAL: usize = 64;

#[derive(Default)]
pub(crate) struct SourceScanRegistry {
    scans: Mutex<HashMap<String, Arc<AtomicBool>>>,
}

impl SourceScanRegistry {
    fn register(&self, scan_id: &str) -> Arc<AtomicBool> {
        let mut scans = self
            .scans
            .lock()
            .expect("source scan registry lock poisoned");
        Arc::clone(
            scans
                .entry(scan_id.to_string())
                .or_insert_with(|| Arc::new(AtomicBool::new(false))),
        )
    }

    pub(crate) fn cancel(&self, scan_id: &str) -> bool {
        let mut scans = self
            .scans
            .lock()
            .expect("source scan registry lock poisoned");
        let was_registered = scans.contains_key(scan_id);
        let cancelled = scans
            .entry(scan_id.to_string())
            .or_insert_with(|| Arc::new(AtomicBool::new(false)));
        cancelled.store(true, Ordering::Relaxed);
        was_registered
    }

    pub(crate) fn unregister(&self, scan_id: &str) {
        self.scans
            .lock()
            .expect("source scan registry lock poisoned")
            .remove(scan_id);
    }
}

pub(crate) struct SourceScanCancellation {
    cancelled: Arc<AtomicBool>,
    progress: Option<Arc<dyn Fn(SourceScanStats) + Send + Sync>>,
    /// When set, the walk stops itself at this moment even if nobody cancelled
    /// it. Only the batched margin-count walk uses this; the file-list scan the
    /// user watches runs to completion.
    deadline: Option<Instant>,
}

impl SourceScanCancellation {
    fn new(
        cancelled: Arc<AtomicBool>,
        progress: Option<Arc<dyn Fn(SourceScanStats) + Send + Sync>>,
    ) -> Self {
        Self {
            cancelled,
            progress,
            deadline: None,
        }
    }

    pub(crate) fn none() -> Self {
        Self::new(Arc::new(AtomicBool::new(false)), None)
    }

    pub(crate) fn until(deadline: Instant) -> Self {
        Self {
            cancelled: Arc::new(AtomicBool::new(false)),
            progress: None,
            deadline: Some(deadline),
        }
    }

    #[cfg(test)]
    pub(crate) fn cancelled_for_test() -> Self {
        Self::new(Arc::new(AtomicBool::new(true)), None)
    }

    #[cfg(test)]
    pub(crate) fn active_for_test(progress: impl Fn(SourceScanStats) + Send + Sync + 'static) -> Self {
        Self::new(Arc::new(AtomicBool::new(false)), Some(Arc::new(progress)))
    }

    pub(crate) fn ensure_active(&self) -> Result<(), String> {
        if self.cancelled.load(Ordering::Relaxed) {
            return Err("Source scan cancelled".to_string());
        }
        if self
            .deadline
            .is_some_and(|deadline| Instant::now() >= deadline)
        {
            return Err("Source scan ran out of time".to_string());
        }
        Ok(())
    }

    fn report_progress(&self, progress: SourceScanStats) {
        if let Some(callback) = &self.progress {
            callback(progress);
        }
    }
}

#[derive(Default)]
pub(crate) struct SourceScanWalkProgress {
    pub(super) stats: SourceScanStats,
    next_report_at: usize,
}

impl SourceScanWalkProgress {
    pub(super) fn visit_entry(&mut self, cancellation: &SourceScanCancellation) {
        self.stats.visited_entries += 1;
        if self.stats.visited_entries == 1 || self.stats.visited_entries >= self.next_report_at {
            self.next_report_at = self.stats.visited_entries + SOURCE_SCAN_PROGRESS_INTERVAL;
            self.report(cancellation);
        }
    }

    pub(super) fn match_file(&mut self, cancellation: &SourceScanCancellation) {
        self.stats.matched_files += 1;
        self.report(cancellation);
    }

    pub(super) fn skip_directory(&mut self, root: &Path, path: &Path, name: &str, reason: &str) {
        self.stats.skipped_directories += 1;
        if self.stats.skipped_directory_samples.len() < MAX_SOURCE_SCAN_SKIPPED_DIRECTORY_SAMPLES {
            self.stats
                .skipped_directory_samples
                .push(SourceSkippedDirectory {
                    path: normalized_relative_source_path(root, path),
                    name: name.to_string(),
                    reason: reason.to_string(),
                });
        }
    }

    pub(super) fn skip_unsupported_file(&mut self) {
        self.stats.unsupported_files += 1;
    }

    pub(super) fn skip_unreadable_entry(&mut self) {
        self.stats.unreadable_entries += 1;
    }

    pub(super) fn report(&self, cancellation: &SourceScanCancellation) {
        cancellation.report_progress(self.stats.clone());
    }
}

/// Let the file-system plugin reach everything under a workspace root.
///
/// Which folders those are is decided at run time — a session's checkout, a
/// worktree, a repository someone just opened — so the capability file cannot
/// name them. Listing a folder is the one moment the app is told which folder
/// it is looking at, and the Files panel only ever creates, renames or deletes
/// inside a folder it has listed, so the grant belongs here.
pub(crate) fn allow_workspace_root_in_fs_scope(app: &tauri::AppHandle, root: &str) {
    let Ok(canonical) = std::fs::canonicalize(root) else {
        return;
    };
    if let Err(error) = app.fs_scope().allow_directory(&canonical, true) {
        crate::debug_log::stderr_log!("fs scope: could not allow {}: {error}", canonical.display());
    }
}

pub(crate) fn source_scan_cancellation_for_command(
    app: &tauri::AppHandle,
    scan_registry: &SourceScanRegistry,
    scan_id: Option<&str>,
) -> SourceScanCancellation {
    let Some(scan_id) = scan_id.filter(|value| !value.trim().is_empty()) else {
        return SourceScanCancellation::none();
    };

    let cancelled = scan_registry.register(scan_id);
    let app = app.clone();
    let scan_id = scan_id.to_string();
    let progress = Arc::new(move |snapshot: SourceScanStats| {
        let _ = app.emit(
            SOURCE_SCAN_PROGRESS_EVENT,
            SourceScanProgressEvent {
                scan_id: scan_id.clone(),
                visited_entries: snapshot.visited_entries,
                matched_files: snapshot.matched_files,
            },
        );
    });

    SourceScanCancellation::new(cancelled, Some(progress))
}
