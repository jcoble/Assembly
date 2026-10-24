//! The Resource Manager's disk section: where the space actually went.
//!
//! Process usage answers "what is running now"; this answers the other question
//! the owner keeps having to answer by hand — which folders have quietly grown
//! to tens of gigabytes. It looks only for the shapes that are known to get
//! large: a Rust build folder, an installed dependency folder, a repository's
//! own history, the extra checkouts beside it, and the transcript and usage
//! stores the app reads.
//!
//! Two rules keep this safe. Nothing is ever removed without a person pressing
//! a button in a dialog that names the exact folder, and only two categories
//! offer that button at all — a Rust build folder and an installed dependency
//! folder, both of which a build command puts back. Repository history, extra
//! checkouts and anything the app stores are shown and never offered.
//!
//! Measuring is slow, so every root's answer is kept for five minutes and the
//! walk itself is capped in both depth and number of entries.

use crate::debug_log::stderr_log;
use serde::{Deserialize, Serialize};
use std::collections::HashMap;
use std::fs;
use std::path::{Path, PathBuf};
use std::sync::{Mutex, OnceLock};
use std::time::{SystemTime, UNIX_EPOCH};
use tauri::{AppHandle, State};

/// How deep the search for large folders looks inside a workspace. Four levels
/// finds `target`, `node_modules` and a nested package's own folders without
/// walking a whole monorepo.
const DISCOVERY_MAX_DEPTH: usize = 4;

/// How deep a single folder is measured. A dependency tree can nest far, so
/// this is generous; the entry budget below is what actually bounds the work.
const MEASURE_MAX_DEPTH: usize = 24;

/// The most filesystem entries one measurement will look at.
const MEASURE_MAX_ENTRIES: usize = 400_000;

/// How long a measured root stays good for.
const CACHE_TTL_MS: u128 = 5 * 60 * 1_000;

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "kebab-case")]
pub enum DiskUsageCategory {
    /// A Rust build folder — `target` beside a `Cargo.toml`.
    CargoTarget,
    /// An installed dependency folder — `node_modules`.
    NodeModules,
    /// A repository's own history — `.git`.
    GitDirectory,
    /// An extra checkout of the same repository.
    Worktree,
    /// A folder of agent transcripts the app reads.
    TranscriptStore,
    /// The app's own usage database.
    UsageStore,
}

/// The two categories a build command puts back, and so the only two the panel
/// offers to remove.
pub fn disk_category_is_reclaimable(category: DiskUsageCategory) -> bool {
    matches!(
        category,
        DiskUsageCategory::CargoTarget | DiskUsageCategory::NodeModules
    )
}

pub fn disk_category_label(category: DiskUsageCategory) -> &'static str {
    match category {
        DiskUsageCategory::CargoTarget => "Rust build output",
        DiskUsageCategory::NodeModules => "Installed dependencies",
        DiskUsageCategory::GitDirectory => "Repository history",
        DiskUsageCategory::Worktree => "Extra checkout",
        DiskUsageCategory::TranscriptStore => "Transcript store",
        DiskUsageCategory::UsageStore => "Usage database",
    }
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct DiskUsageEntry {
    pub id: String,
    pub label: String,
    pub path: String,
    pub category: DiskUsageCategory,
    pub category_label: String,
    pub bytes: u64,
    pub reclaimable: bool,
    /// True when the entry hit the walk's entry budget, so its size is a floor.
    pub truncated: bool,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct DiskUsageSection {
    pub id: String,
    pub label: String,
    pub root: String,
    pub bytes: u64,
    pub measured_at_ms: u128,
    pub entries: Vec<DiskUsageEntry>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct DiskUsageReport {
    pub generated_at_ms: u128,
    pub total_bytes: u64,
    pub sections: Vec<DiskUsageSection>,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DiskUsageRequest {
    /// Skip the five minute cache — what the panel's refresh button sends.
    #[serde(default)]
    pub refresh: bool,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DiskReclaimRequest {
    pub path: String,
    pub category: DiskUsageCategory,
    /// The size the dialog showed, so the receipt can say what was freed.
    pub expected_bytes: u64,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct DiskReclaimReceipt {
    pub action: String,
    pub path: String,
    pub category: DiskUsageCategory,
    /// The size the dialog showed, kept beside the size actually removed so a
    /// stale measurement is visible rather than silent.
    pub listed_bytes: u64,
    pub reclaimed_bytes: u64,
    pub message: String,
}

/// What a folder is, judged from its name and one fact about its parent.
///
/// The Cargo check is why the sibling flag is a parameter rather than a
/// filesystem call: a folder called `target` in a project with no `Cargo.toml`
/// beside it is somebody's own folder, not build output, and must never be
/// offered for removal.
pub fn categorize_workspace_folder(
    name: &str,
    has_cargo_manifest_sibling: bool,
) -> Option<DiskUsageCategory> {
    match name {
        "node_modules" => Some(DiskUsageCategory::NodeModules),
        "target" if has_cargo_manifest_sibling => Some(DiskUsageCategory::CargoTarget),
        ".git" => Some(DiskUsageCategory::GitDirectory),
        _ => None,
    }
}

/// Folders the search never walks into: their contents can only be more of the
/// same, and a matched folder is measured whole anyway.
fn is_search_stop_folder(name: &str) -> bool {
    matches!(
        name,
        "node_modules" | "target" | ".git" | ".build" | "DerivedData" | ".venv"
    )
}

fn now_ms() -> u128 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .unwrap_or_default()
        .as_millis()
}

fn cache() -> &'static Mutex<HashMap<PathBuf, DiskUsageSection>> {
    static CACHE: OnceLock<Mutex<HashMap<PathBuf, DiskUsageSection>>> = OnceLock::new();
    CACHE.get_or_init(|| Mutex::new(HashMap::new()))
}

fn cached_section(root: &Path, refresh: bool) -> Option<DiskUsageSection> {
    if refresh {
        return None;
    }
    let entries = cache().lock().ok()?;
    let section = entries.get(root)?;
    (now_ms().saturating_sub(section.measured_at_ms) <= CACHE_TTL_MS).then(|| section.clone())
}

fn store_section(root: &Path, section: &DiskUsageSection) {
    if let Ok(mut entries) = cache().lock() {
        entries.insert(root.to_path_buf(), section.clone());
    }
}

fn forget_cached_sections() {
    if let Ok(mut entries) = cache().lock() {
        entries.clear();
    }
}

/// Add up a folder, skipping symlinks so a link out of the tree is never
/// counted or, later, followed by a removal.
fn measure_folder(path: &Path) -> (u64, bool) {
    let mut visited = 0usize;
    let mut truncated = false;
    let bytes = measure_folder_at(path, 0, &mut visited, &mut truncated);
    (bytes, truncated)
}

fn measure_folder_at(path: &Path, depth: usize, visited: &mut usize, truncated: &mut bool) -> u64 {
    if *visited >= MEASURE_MAX_ENTRIES {
        *truncated = true;
        return 0;
    }
    *visited += 1;

    let Ok(metadata) = fs::symlink_metadata(path) else {
        return 0;
    };
    if metadata.file_type().is_symlink() {
        return 0;
    }
    if !metadata.is_dir() {
        return metadata.len();
    }
    if depth >= MEASURE_MAX_DEPTH {
        *truncated = true;
        return 0;
    }
    let Ok(children) = fs::read_dir(path) else {
        return 0;
    };
    children
        .flatten()
        .map(|child| measure_folder_at(&child.path(), depth + 1, visited, truncated))
        .sum()
}

/// Walk a workspace looking for the folder shapes that get large.
///
/// Returned paths are what the caller measures; nothing is measured here so the
/// search itself stays cheap and can be read in a test.
pub fn find_workspace_disk_folders(root: &Path) -> Vec<(PathBuf, DiskUsageCategory)> {
    let mut found = Vec::new();
    find_workspace_disk_folders_at(root, 0, &mut found);
    found.sort_by(|left, right| left.0.cmp(&right.0));
    found
}

fn find_workspace_disk_folders_at(
    path: &Path,
    depth: usize,
    found: &mut Vec<(PathBuf, DiskUsageCategory)>,
) {
    if depth > DISCOVERY_MAX_DEPTH {
        return;
    }
    let Ok(children) = fs::read_dir(path) else {
        return;
    };
    let has_cargo_manifest = path.join("Cargo.toml").is_file();
    for child in children.flatten() {
        let child_path = child.path();
        let Ok(metadata) = fs::symlink_metadata(&child_path) else {
            continue;
        };
        if !metadata.is_dir() || metadata.file_type().is_symlink() {
            continue;
        }
        let Some(name) = child_path.file_name().map(|value| value.to_string_lossy()) else {
            continue;
        };
        if let Some(category) = categorize_workspace_folder(name.as_ref(), has_cargo_manifest) {
            found.push((child_path, category));
            continue;
        }
        if is_search_stop_folder(name.as_ref()) {
            continue;
        }
        find_workspace_disk_folders_at(&child_path, depth + 1, found);
    }
}

/// The other checkouts of the same repository, asked of git rather than guessed
/// from a folder layout.
fn find_repository_worktrees(root: &Path) -> Vec<PathBuf> {
    let Ok(output) = std::process::Command::new("git")
        .current_dir(root)
        .args(["worktree", "list", "--porcelain"])
        .output()
    else {
        return Vec::new();
    };
    if !output.status.success() {
        return Vec::new();
    }
    String::from_utf8_lossy(&output.stdout)
        .lines()
        .filter_map(|line| line.strip_prefix("worktree "))
        .map(PathBuf::from)
        .filter(|path| path.canonicalize().ok().as_deref() != Some(root))
        .collect()
}

fn entry_for(path: PathBuf, category: DiskUsageCategory, root: Option<&Path>) -> DiskUsageEntry {
    let (bytes, truncated) = measure_folder(&path);
    let label = root
        .and_then(|root| path.strip_prefix(root).ok())
        .map(|relative| relative.display().to_string())
        .filter(|relative| !relative.is_empty())
        .unwrap_or_else(|| {
            path.file_name()
                .map(|name| name.to_string_lossy().into_owned())
                .unwrap_or_else(|| path.display().to_string())
        });
    DiskUsageEntry {
        id: path.display().to_string(),
        label,
        path: path.display().to_string(),
        category,
        category_label: disk_category_label(category).to_string(),
        bytes,
        reclaimable: disk_category_is_reclaimable(category),
        truncated,
    }
}

fn measure_workspace_section(label: &str, root: &Path) -> DiskUsageSection {
    let mut entries = find_workspace_disk_folders(root)
        .into_iter()
        .map(|(path, category)| entry_for(path, category, Some(root)))
        .collect::<Vec<_>>();
    entries.extend(
        find_repository_worktrees(root)
            .into_iter()
            .map(|path| entry_for(path, DiskUsageCategory::Worktree, None)),
    );
    entries.sort_by(|left, right| {
        right
            .bytes
            .cmp(&left.bytes)
            .then_with(|| left.path.cmp(&right.path))
    });
    DiskUsageSection {
        id: root.display().to_string(),
        label: label.to_string(),
        root: root.display().to_string(),
        bytes: entries.iter().map(|entry| entry.bytes).sum(),
        measured_at_ms: now_ms(),
        entries,
    }
}

/// Stores read by history/transcript/{codex,claude}.rs, plus the usage database.
/// Antigravity has no transcript reader; these stores are never offered for removal.
fn measure_app_store_section(database_path: Option<PathBuf>) -> DiskUsageSection {
    let mut entries = Vec::new();
    if let Some(home) = std::env::var_os("HOME").map(PathBuf::from) {
        for relative in [".claude/projects", ".codex/sessions", ".codex/archived_sessions"] {
            let path = home.join(relative);
            if path.is_dir() {
                entries.push(entry_for(
                    path,
                    DiskUsageCategory::TranscriptStore,
                    Some(&home),
                ));
            }
        }
    }
    if let Some(path) = database_path.filter(|path| path.exists()) {
        entries.push(entry_for(path, DiskUsageCategory::UsageStore, None));
    }
    entries.sort_by(|left, right| {
        right
            .bytes
            .cmp(&left.bytes)
            .then_with(|| left.path.cmp(&right.path))
    });
    DiskUsageSection {
        id: "app-stores".to_string(),
        label: "App stores".to_string(),
        root: String::new(),
        bytes: entries.iter().map(|entry| entry.bytes).sum(),
        measured_at_ms: now_ms(),
        entries,
    }
}

/// Measure the workspaces behind the sessions that are running, plus the app's
/// own stores. Every root is cached for five minutes.
#[tauri::command]
pub async fn read_resource_disk_usage(
    app: AppHandle,
    request: DiskUsageRequest,
    terminal_registry: State<'_, crate::terminal::TerminalRegistry>,
    agent_runtime: State<'_, crate::agent_conversation::manager::AgentRuntimeManager>,
) -> Result<DiskUsageReport, String> {
    let terminal_registry = terminal_registry.inner().clone();
    let agent_runtime = agent_runtime.inner().clone();
    let database_path = crate::usage_history::usage_db_path(&app).ok();

    tauri::async_runtime::spawn_blocking(move || {
        let roots = crate::resources::resource_workspace_roots(&terminal_registry, &agent_runtime)?;
        let mut sections = Vec::new();
        for root in roots {
            if let Some(section) = cached_section(&root.path, request.refresh) {
                sections.push(section);
                continue;
            }
            let section = measure_workspace_section(&root.label, &root.path);
            store_section(&root.path, &section);
            sections.push(section);
        }
        sections.push(measure_app_store_section(database_path));
        Ok(DiskUsageReport {
            generated_at_ms: now_ms(),
            total_bytes: sections.iter().map(|section| section.bytes).sum(),
            sections,
        })
    })
    .await
    .map_err(|error| format!("Disk usage task failed: {error}"))?
}

/// Remove one build or dependency folder, after a person has read a dialog
/// naming it. Everything the dialog claimed is checked again here.
#[tauri::command]
pub async fn reclaim_resource_disk_entry(
    request: DiskReclaimRequest,
) -> Result<DiskReclaimReceipt, String> {
    tauri::async_runtime::spawn_blocking(move || {
        if !disk_category_is_reclaimable(request.category) {
            return Err("Only build output and installed dependencies can be removed".to_string());
        }
        let path = PathBuf::from(&request.path);
        let canonical = validate_reclaim_path(&path, request.category)?;
        let (bytes, _) = measure_folder(&canonical);
        fs::remove_dir_all(&canonical)
            .map_err(|error| format!("Could not remove {}: {error}", canonical.display()))?;
        forget_cached_sections();
        stderr_log!(
            "resources: reclaimed {} bytes from {}",
            bytes,
            canonical.display()
        );
        Ok(DiskReclaimReceipt {
            action: "reclaim-resource-disk-entry".to_string(),
            path: canonical.display().to_string(),
            category: request.category,
            listed_bytes: request.expected_bytes,
            reclaimed_bytes: bytes,
            message: format!("Removed {}. A build will put it back.", canonical.display()),
        })
    })
    .await
    .map_err(|error| format!("Disk reclaim task failed: {error}"))?
}

/// The checks that stand between a button and `remove_dir_all`: a real folder,
/// not a link, not the home folder or the filesystem root, and named the way
/// its category says it should be.
fn validate_reclaim_path(path: &Path, category: DiskUsageCategory) -> Result<PathBuf, String> {
    let metadata = fs::symlink_metadata(path)
        .map_err(|error| format!("That folder is unavailable: {error}"))?;
    if metadata.file_type().is_symlink() {
        return Err("A link is never removed".to_string());
    }
    if !metadata.is_dir() {
        return Err("That entry is not a folder".to_string());
    }
    let canonical = path
        .canonicalize()
        .map_err(|error| format!("That folder is unavailable: {error}"))?;
    if canonical.parent().is_none() {
        return Err("The filesystem root cannot be removed".to_string());
    }
    if let Some(home) = std::env::var_os("HOME")
        .map(PathBuf::from)
        .and_then(|home| home.canonicalize().ok())
    {
        if canonical == home {
            return Err("The home folder cannot be removed".to_string());
        }
    }
    let name = canonical
        .file_name()
        .map(|value| value.to_string_lossy().into_owned())
        .unwrap_or_default();
    let has_cargo_manifest = canonical
        .parent()
        .map(|parent| parent.join("Cargo.toml").is_file())
        .unwrap_or(false);
    if categorize_workspace_folder(&name, has_cargo_manifest) != Some(category) {
        return Err("That folder is no longer what it was listed as; scan again".to_string());
    }
    Ok(canonical)
}

#[cfg(test)]
mod tests {
    mod archived_sessions;
    use super::*;
    fn fixture_root(label: &str) -> PathBuf {
        let root = std::env::temp_dir().join(format!(
            "mcb-disk-{label}-{}-{}",
            std::process::id(),
            now_ms()
        ));
        fs::create_dir_all(&root).expect("fixture root should be created");
        root
    }

    fn write(path: &Path, bytes: usize) {
        fs::create_dir_all(path.parent().expect("fixture file should have a parent"))
            .expect("fixture folder should be created");
        fs::write(path, vec![b'x'; bytes]).expect("fixture file should be written");
    }

    #[test]
    fn a_target_folder_counts_as_build_output_only_beside_a_cargo_manifest() {
        assert_eq!(
            categorize_workspace_folder("target", true),
            Some(DiskUsageCategory::CargoTarget)
        );
        assert_eq!(categorize_workspace_folder("target", false), None);
        assert_eq!(
            categorize_workspace_folder("node_modules", false),
            Some(DiskUsageCategory::NodeModules)
        );
        assert_eq!(
            categorize_workspace_folder(".git", false),
            Some(DiskUsageCategory::GitDirectory)
        );
        assert_eq!(categorize_workspace_folder("src", true), None);
    }

    #[test]
    fn only_build_output_and_dependencies_may_be_removed() {
        assert!(disk_category_is_reclaimable(DiskUsageCategory::CargoTarget));
        assert!(disk_category_is_reclaimable(DiskUsageCategory::NodeModules));
        for category in [
            DiskUsageCategory::GitDirectory,
            DiskUsageCategory::Worktree,
            DiskUsageCategory::TranscriptStore,
            DiskUsageCategory::UsageStore,
        ] {
            assert!(!disk_category_is_reclaimable(category));
        }
    }

    #[test]
    fn the_search_finds_known_hogs_and_does_not_walk_inside_them() {
        let root = fixture_root("search");
        write(&root.join("Cargo.toml"), 10);
        write(&root.join("target/debug/build.bin"), 100);
        write(&root.join("target/debug/node_modules/inner/file"), 50);
        write(&root.join("web/node_modules/pkg/index.js"), 40);
        write(&root.join("web/target/output"), 30);
        write(&root.join(".git/objects/pack/pack-1"), 20);
        write(&root.join("src/main.rs"), 5);

        let found = find_workspace_disk_folders(&root);
        let paths = found
            .iter()
            .map(|(path, category)| {
                (
                    path.strip_prefix(&root)
                        .expect("found path should sit under the fixture root")
                        .display()
                        .to_string(),
                    *category,
                )
            })
            .collect::<Vec<_>>();

        assert!(paths.contains(&(".git".to_string(), DiskUsageCategory::GitDirectory)));
        assert!(paths.contains(&("target".to_string(), DiskUsageCategory::CargoTarget)));
        assert!(paths.contains(&(
            "web/node_modules".to_string(),
            DiskUsageCategory::NodeModules
        )));
        // `web` has no Cargo.toml, so its `target` is somebody's own folder.
        assert!(!paths.iter().any(|(path, _)| path == "web/target"));
        // Nothing inside a matched folder is reported a second time.
        assert!(!paths.iter().any(|(path, _)| path.starts_with("target/")));

        let (bytes, truncated) = measure_folder(&root.join("target"));
        assert_eq!(bytes, 150);
        assert!(!truncated);

        fs::remove_dir_all(&root).expect("fixture should be cleaned up");
    }

    #[test]
    fn a_folder_that_is_not_what_it_was_listed_as_is_refused() {
        let root = fixture_root("validate");
        let plain = root.join("target");
        fs::create_dir_all(&plain).expect("fixture folder should be created");

        // No Cargo.toml beside it, so it is not build output.
        assert!(validate_reclaim_path(&plain, DiskUsageCategory::CargoTarget).is_err());

        write(&root.join("Cargo.toml"), 4);
        assert_eq!(
            validate_reclaim_path(&plain, DiskUsageCategory::CargoTarget)
                .expect("build output beside a manifest should validate"),
            plain
                .canonicalize()
                .expect("fixture folder should canonicalize")
        );
        assert!(validate_reclaim_path(&plain, DiskUsageCategory::NodeModules).is_err());

        fs::remove_dir_all(&root).expect("fixture should be cleaned up");
    }
}
