use std::path::PathBuf;
use std::sync::atomic::Ordering;
use tauri_plugin_fs::FsExt;

use crate::{
    run_path_action, run_source_file_action, run_terminal_command_action,
    run_terminal_path_action, PathAction, SourceFileAction,
};
use crate::files::discovery::{
    DEFAULT_SOURCE_LIST_LIMIT, SourceScanRegistry, allow_workspace_root_in_fs_scope, source_scan_cancellation_for_command,
};
use crate::files::discovery::references::{
    count_source_references_sync, find_source_references_in_root_sync, find_source_references_sync,
};
use crate::files::discovery::root::{
    ProjectRootValidationOwner, validate_project_root_sync_while,
};
use crate::files::discovery::search::{
    search_source_files_sync, search_source_tree_sync,
};
use crate::files::discovery::symbols::{
    find_source_definitions_in_root_sync, find_source_definitions_sync,
};
use crate::files::discovery::walk::{
    list_source_directory_sync_with_cancellation, list_source_files_sync_with_cancellation,
};
use crate::files::io::{
    SourceFileReadOwner, read_source_file_sync, read_source_file_sync_while, read_source_image_sync, write_source_file_sync,
};
use crate::files::types::{
    ProjectRootValidationResult, SourceDefinitionTarget, SourceDirectoryEntry, SourcePreview, SourceRecord, SourceReferenceCountResult, SourceReferenceTarget, SourceScanResult, SourceSearchMatch, SourceTreeSearchPage,
};

#[tauri::command]
pub(crate) async fn list_source_files(
    app: tauri::AppHandle,
    scan_registry: tauri::State<'_, SourceScanRegistry>,
    root: String,
    limit: Option<usize>,
    query: Option<String>,
    scan_id: Option<String>,
) -> Result<SourceScanResult, String> {
    allow_workspace_root_in_fs_scope(&app, &root);
    let cancellation =
        source_scan_cancellation_for_command(&app, &scan_registry, scan_id.as_deref());
    let scan_id_for_cleanup = scan_id.clone();
    let result = tauri::async_runtime::spawn_blocking(move || {
        list_source_files_sync_with_cancellation(
            PathBuf::from(root),
            limit.unwrap_or(DEFAULT_SOURCE_LIST_LIMIT),
            query,
            cancellation,
        )
    })
    .await
    .map_err(|error| format!("Source scan task failed: {error}"))?;

    if let Some(scan_id) = &scan_id_for_cleanup {
        scan_registry.unregister(scan_id);
    }

    result
}

#[tauri::command]
pub(crate) async fn list_source_directory(
    app: tauri::AppHandle,
    scan_registry: tauri::State<'_, SourceScanRegistry>,
    root: String,
    directory: String,
    include_excluded: Option<bool>,
    scan_id: Option<String>,
) -> Result<Vec<SourceDirectoryEntry>, String> {
    let _read = crate::resources::begin_source_directory_read();
    allow_workspace_root_in_fs_scope(&app, &root);
    let cancellation =
        source_scan_cancellation_for_command(&app, &scan_registry, scan_id.as_deref());
    let scan_id_for_cleanup = scan_id.clone();
    let result = tauri::async_runtime::spawn_blocking(move || {
        list_source_directory_sync_with_cancellation(
            PathBuf::from(root),
            PathBuf::from(directory),
            include_excluded.unwrap_or(false),
            cancellation,
        )
    })
    .await
    .map_err(|error| format!("Source directory task failed: {error}"))?;

    if let Some(scan_id) = &scan_id_for_cleanup {
        scan_registry.unregister(scan_id);
    }

    result
}

#[tauri::command]
pub(crate) async fn cancel_source_scan(
    scan_registry: tauri::State<'_, SourceScanRegistry>,
    scan_id: String,
) -> Result<bool, String> {
    Ok(scan_registry.cancel(&scan_id))
}

#[tauri::command]
pub(crate) async fn validate_project_root(
    owner: tauri::State<'_, ProjectRootValidationOwner>,
    path: String,
    generation: u64,
) -> Result<Option<ProjectRootValidationResult>, String> {
    if !owner.advance(generation) {
        return Ok(None);
    }
    let current_generation = owner.generation();
    tauri::async_runtime::spawn_blocking(move || {
        validate_project_root_sync_while(PathBuf::from(path), || {
            current_generation.load(Ordering::Acquire) == generation
        })
    })
    .await
    .map_err(|error| format!("Project root validation task failed: {error}"))
}

#[tauri::command]
pub(crate) fn cancel_project_root_validation(
    owner: tauri::State<'_, ProjectRootValidationOwner>,
    generation: u64,
) {
    owner.cancel(generation);
}

#[tauri::command]
pub(crate) async fn read_source_file(
    owner: tauri::State<'_, SourceFileReadOwner>,
    path: String,
    generation: u64,
) -> Result<Option<SourcePreview>, String> {
    if !owner.advance(generation) {
        return Ok(None);
    }
    let current_generation = owner.generation();
    tauri::async_runtime::spawn_blocking(move || {
        read_source_file_sync_while(PathBuf::from(path), || {
            current_generation.load(Ordering::Acquire) == generation
        })
    })
    .await
    .map_err(|error| format!("Source preview task failed: {error}"))?
}

#[tauri::command]
pub(crate) async fn read_source_image(path: String) -> Result<tauri::ipc::Response, String> {
    let bytes = tauri::async_runtime::spawn_blocking(move || {
        read_source_image_sync(PathBuf::from(path))
    })
    .await
    .map_err(|error| format!("Source image task failed: {error}"))??;
    Ok(tauri::ipc::Response::new(bytes))
}

#[tauri::command]
pub(crate) fn cancel_source_file_reads(
    owner: tauri::State<'_, SourceFileReadOwner>,
    generation: u64,
) {
    owner.advance(generation);
}

#[tauri::command]
pub(crate) async fn read_native_csharp_file(
    root: String,
    path: String,
) -> Result<SourcePreview, String> {
    tauri::async_runtime::spawn_blocking(move || {
        let root = std::fs::canonicalize(root)
            .map_err(|error| format!("Could not resolve C# workspace root: {error}"))?;
        let path = std::fs::canonicalize(path)
            .map_err(|error| format!("Could not resolve C# source path: {error}"))?;
        if !path.starts_with(&root) {
            return Err("C# source path is outside the workspace root".to_string());
        }
        read_source_file_sync(path)
    })
    .await
    .map_err(|error| format!("Native C# source read task failed: {error}"))?
}

#[tauri::command]
pub(crate) async fn write_source_file(
    path: String,
    content: String,
    expected_revision: String,
) -> Result<SourcePreview, String> {
    tauri::async_runtime::spawn_blocking(move || {
        write_source_file_sync(PathBuf::from(path), content, expected_revision)
    })
    .await
    .map_err(|error| format!("Source write task failed: {error}"))?
}

#[tauri::command]
pub(crate) async fn open_source_file(path: String) -> Result<(), String> {
    tauri::async_runtime::spawn_blocking(move || {
        run_source_file_action(PathBuf::from(path), SourceFileAction::Open)
    })
    .await
    .map_err(|error| format!("Source open task failed: {error}"))?
}

#[tauri::command]
pub(crate) async fn reveal_source_file(path: String) -> Result<(), String> {
    tauri::async_runtime::spawn_blocking(move || {
        run_source_file_action(PathBuf::from(path), SourceFileAction::Reveal)
    })
    .await
    .map_err(|error| format!("Source reveal task failed: {error}"))?
}

#[tauri::command]
pub(crate) async fn open_path(path: String) -> Result<(), String> {
    tauri::async_runtime::spawn_blocking(move || {
        run_path_action(PathBuf::from(path), PathAction::Open)
    })
    .await
    .map_err(|error| format!("Path open task failed: {error}"))?
}

#[tauri::command]
pub(crate) async fn reveal_path(path: String) -> Result<(), String> {
    tauri::async_runtime::spawn_blocking(move || {
        run_path_action(PathBuf::from(path), PathAction::Reveal)
    })
    .await
    .map_err(|error| format!("Path reveal task failed: {error}"))?
}

#[tauri::command]
pub(crate) async fn open_terminal_path(
    path: String,
    terminal: Option<String>,
) -> Result<(), String> {
    tauri::async_runtime::spawn_blocking(move || {
        run_terminal_path_action(PathBuf::from(path), terminal)
    })
    .await
    .map_err(|error| format!("Terminal open task failed: {error}"))?
}

#[tauri::command]
pub(crate) async fn open_terminal_command(
    path: String,
    command: String,
    terminal: Option<String>,
) -> Result<(), String> {
    tauri::async_runtime::spawn_blocking(move || {
        run_terminal_command_action(PathBuf::from(path), command, terminal)
    })
    .await
    .map_err(|error| format!("Terminal command task failed: {error}"))?
}

#[tauri::command]
pub(crate) async fn search_source_files(
    records: Vec<SourceRecord>,
    query: String,
    limit: Option<usize>,
) -> Result<Vec<SourceSearchMatch>, String> {
    tauri::async_runtime::spawn_blocking(move || search_source_files_sync(records, query, limit))
        .await
        .map_err(|error| format!("Source search task failed: {error}"))?
}

#[tauri::command]
pub(crate) async fn search_source_tree(
    app: tauri::AppHandle,
    scan_registry: tauri::State<'_, SourceScanRegistry>,
    root: String,
    query: String,
    page_size: Option<usize>,
    cursor: Option<usize>,
    include_excluded: Option<bool>,
    scan_id: Option<String>,
) -> Result<SourceTreeSearchPage, String> {
    allow_workspace_root_in_fs_scope(&app, &root);
    let cancellation =
        source_scan_cancellation_for_command(&app, &scan_registry, scan_id.as_deref());
    let scan_id_for_cleanup = scan_id.clone();
    let result = tauri::async_runtime::spawn_blocking(move || {
        search_source_tree_sync(
            PathBuf::from(root),
            query,
            page_size,
            cursor,
            include_excluded.unwrap_or(false),
            cancellation,
        )
    })
    .await
    .map_err(|error| format!("Source tree search task failed: {error}"))?;

    if let Some(scan_id) = &scan_id_for_cleanup {
        scan_registry.unregister(scan_id);
    }

    result
}

#[tauri::command]
pub(crate) async fn find_source_definitions(
    records: Vec<SourceRecord>,
    symbol_name: String,
    limit: Option<usize>,
) -> Result<Vec<SourceDefinitionTarget>, String> {
    tauri::async_runtime::spawn_blocking(move || {
        find_source_definitions_sync(records, symbol_name, limit)
    })
    .await
    .map_err(|error| format!("Source definition task failed: {error}"))?
}

#[tauri::command]
pub(crate) async fn find_source_definitions_in_root(
    app: tauri::AppHandle,
    root: String,
    symbol_name: String,
    limit: Option<usize>,
) -> Result<Vec<SourceDefinitionTarget>, String> {
    allow_workspace_root_in_fs_scope(&app, &root);
    tauri::async_runtime::spawn_blocking(move || {
        find_source_definitions_in_root_sync(PathBuf::from(root), symbol_name, limit)
    })
    .await
    .map_err(|error| format!("Source definition scan task failed: {error}"))?
}

#[tauri::command]
pub(crate) async fn find_source_references(
    records: Vec<SourceRecord>,
    symbol_name: String,
    limit: Option<usize>,
) -> Result<Vec<SourceReferenceTarget>, String> {
    tauri::async_runtime::spawn_blocking(move || {
        find_source_references_sync(records, symbol_name, limit)
    })
    .await
    .map_err(|error| format!("Source reference task failed: {error}"))?
}

#[tauri::command]
pub(crate) async fn find_source_references_in_root(
    app: tauri::AppHandle,
    root: String,
    symbol_name: String,
    limit: Option<usize>,
) -> Result<Vec<SourceReferenceTarget>, String> {
    allow_workspace_root_in_fs_scope(&app, &root);
    tauri::async_runtime::spawn_blocking(move || {
        find_source_references_in_root_sync(PathBuf::from(root), symbol_name, limit)
    })
    .await
    .map_err(|error| format!("Source reference scan task failed: {error}"))?
}

#[tauri::command]
pub(crate) async fn count_source_references(
    root: String,
    symbol_names: Vec<String>,
    deadline_ms: Option<u64>,
) -> Result<SourceReferenceCountResult, String> {
    tauri::async_runtime::spawn_blocking(move || {
        count_source_references_sync(PathBuf::from(root), symbol_names, deadline_ms)
    })
    .await
    .map_err(|error| format!("Source reference count task failed: {error}"))?
}

/// Move one path to the Finder's Trash.
///
/// The file-system plugin's `remove` deletes for good, and deleting a file from
/// a tree is the kind of press people take back a second later, so the Files
/// panel goes through here instead. The path must sit inside a folder the
/// plugin's scope already covers — the same folders `list_source_files` grants
/// — so this command can never be pointed at somewhere the window has no
/// business changing.
#[tauri::command]
pub(crate) async fn move_to_trash(app: tauri::AppHandle, path: String) -> Result<(), String> {
    let target =
        std::fs::canonicalize(&path).map_err(|error| format!("Could not find {path}: {error}"))?;
    if !app.fs_scope().is_allowed(&target) {
        return Err(format!(
            "{} is outside the folders this window is allowed to change.",
            target.display()
        ));
    }

    tauri::async_runtime::spawn_blocking(move || {
        trash::delete(&target)
            .map_err(|error| format!("Could not move {} to the Trash: {error}", target.display()))
    })
    .await
    .map_err(|error| format!("Trash task failed: {error}"))?
}
