use std::collections::BTreeMap;
use std::path::PathBuf;

use mcb_core::scanners::worktrees::{repository_checkouts, RepositoryCheckout};

use crate::git::workspace::{
    add_git_remote_sync, commit_git_repository_sync, fetch_git_repository_sync, publish_git_repository_sync,
    pull_git_repository_sync, push_git_repository_sync, stage_git_paths_sync,
    unstage_git_paths_sync,
};

use crate::{
    archive_project_worktree_sync,
    list_git_repository_summaries_sync, list_project_worktrees_sync,
    project_git_status_sync, read_git_commit_file_diff_sync, read_git_commit_files_sync,
    read_git_branch_diff_sync, read_git_commit_history_page_sync, read_source_git_diff_sync,
    remove_project_worktree_sync, GitActionResult, GitBranchDiff, GitCommitFileChange, GitHistoryPage, GitRepositorySummary,
    ProjectGitStatus, ProjectWorktree, ProjectWorktreeActionResult, ProjectWorktreeArchiveResult,
    RuntimeContextProject, SourceGitDiff,
};

#[tauri::command]
pub(crate) async fn project_git_status(root: String) -> Result<ProjectGitStatus, String> {
    tauri::async_runtime::spawn_blocking(move || project_git_status_sync(PathBuf::from(root)))
        .await
        .map_err(|error| format!("Git status task failed: {error}"))?
}

#[tauri::command]
pub(crate) async fn read_source_git_diff(
    root: String,
    path: String,
) -> Result<SourceGitDiff, String> {
    tauri::async_runtime::spawn_blocking(move || {
        read_source_git_diff_sync(PathBuf::from(root), PathBuf::from(path))
    })
    .await
    .map_err(|error| format!("Git diff task failed: {error}"))?
}

#[tauri::command]
pub(crate) async fn read_git_branch_diff(root: String) -> Result<GitBranchDiff, String> {
    tauri::async_runtime::spawn_blocking(move || read_git_branch_diff_sync(PathBuf::from(root)))
        .await
        .map_err(|error| format!("Git branch diff task failed: {error}"))?
}

#[tauri::command]
pub(crate) async fn stage_git_paths(
    root: String,
    paths: Vec<String>,
) -> Result<GitActionResult, String> {
    tauri::async_runtime::spawn_blocking(move || stage_git_paths_sync(PathBuf::from(root), paths))
        .await
        .map_err(|error| format!("Git stage task failed: {error}"))?
}

#[tauri::command]
pub(crate) async fn unstage_git_paths(
    root: String,
    paths: Vec<String>,
) -> Result<GitActionResult, String> {
    tauri::async_runtime::spawn_blocking(move || unstage_git_paths_sync(PathBuf::from(root), paths))
        .await
        .map_err(|error| format!("Git unstage task failed: {error}"))?
}

#[tauri::command]
pub(crate) async fn commit_git_repository(
    root: String,
    message: String,
) -> Result<GitActionResult, String> {
    tauri::async_runtime::spawn_blocking(move || {
        commit_git_repository_sync(PathBuf::from(root), message)
    })
    .await
    .map_err(|error| format!("Git commit task failed: {error}"))?
}

#[tauri::command]
pub(crate) async fn fetch_git_repository(root: String) -> Result<GitActionResult, String> {
    tauri::async_runtime::spawn_blocking(move || fetch_git_repository_sync(PathBuf::from(root)))
        .await
        .map_err(|error| format!("Git fetch task failed: {error}"))?
}

#[tauri::command]
pub(crate) async fn pull_git_repository(root: String) -> Result<GitActionResult, String> {
    tauri::async_runtime::spawn_blocking(move || pull_git_repository_sync(PathBuf::from(root)))
        .await
        .map_err(|error| format!("Git pull task failed: {error}"))?
}

#[tauri::command]
pub(crate) async fn push_git_repository(root: String) -> Result<GitActionResult, String> {
    tauri::async_runtime::spawn_blocking(move || push_git_repository_sync(PathBuf::from(root)))
        .await
        .map_err(|error| format!("Git push task failed: {error}"))?
}

#[tauri::command]
pub(crate) async fn publish_git_repository(root: String, expected_branch: String, remote: String) -> Result<GitActionResult, String> {
    tauri::async_runtime::spawn_blocking(move || publish_git_repository_sync(PathBuf::from(root), expected_branch, remote))
        .await
        .map_err(|error| format!("Git publish task failed: {error}"))?
}

#[tauri::command]
pub(crate) async fn add_git_remote(root: String, name: String, url: String) -> Result<GitActionResult, String> {
    tauri::async_runtime::spawn_blocking(move || add_git_remote_sync(root, name, url))
        .await
        .map_err(|error| format!("Git add remote task failed: {error}"))?
}

#[tauri::command]
pub(crate) async fn read_git_commit_history(
    root: String,
    cursor: Option<String>,
    relative_path: Option<String>,
) -> Result<GitHistoryPage, String> {
    tauri::async_runtime::spawn_blocking(move || {
        read_git_commit_history_page_sync(PathBuf::from(root), cursor, relative_path)
    })
    .await
    .map_err(|error| format!("Git history task failed: {error}"))?
}

#[tauri::command]
pub(crate) async fn read_git_commit_files(
    root: String,
    sha: String,
) -> Result<Vec<GitCommitFileChange>, String> {
    tauri::async_runtime::spawn_blocking(move || {
        read_git_commit_files_sync(PathBuf::from(root), sha)
    })
    .await
    .map_err(|error| format!("Git commit file list task failed: {error}"))?
}

#[tauri::command]
pub(crate) async fn read_git_commit_file_diff(
    root: String,
    sha: String,
    relative_path: String,
) -> Result<SourceGitDiff, String> {
    tauri::async_runtime::spawn_blocking(move || {
        read_git_commit_file_diff_sync(PathBuf::from(root), sha, relative_path)
    })
    .await
    .map_err(|error| format!("Git commit file diff task failed: {error}"))?
}

#[tauri::command]
pub(crate) async fn list_project_worktrees(root: String) -> Result<Vec<ProjectWorktree>, String> {
    tauri::async_runtime::spawn_blocking(move || list_project_worktrees_sync(PathBuf::from(root)))
        .await
        .map_err(|error| format!("Worktree scan task failed: {error}"))?
}

#[tauri::command]
pub(crate) async fn list_repository_checkouts(
    roots: Vec<String>,
) -> Result<BTreeMap<String, Vec<RepositoryCheckout>>, String> {
    tauri::async_runtime::spawn_blocking(move || {
        roots
            .into_iter()
            .map(|root| {
                let checkouts = repository_checkouts(&root);
                (root, checkouts)
            })
            .collect()
    })
    .await
    .map_err(|error| format!("Checkout scan task failed: {error}"))
}

#[tauri::command]
pub(crate) async fn remove_project_worktree(
    root: String,
    path: String,
    force: Option<bool>,
) -> Result<ProjectWorktreeActionResult, String> {
    let force = force.unwrap_or(false);
    tauri::async_runtime::spawn_blocking(move || {
        remove_project_worktree_sync(PathBuf::from(root), PathBuf::from(path), force)
    })
    .await
    .map_err(|error| format!("Worktree remove task failed: {error}"))?
}

#[tauri::command]
pub(crate) async fn archive_project_worktree(
    root: String,
    path: String,
) -> Result<ProjectWorktreeArchiveResult, String> {
    tauri::async_runtime::spawn_blocking(move || {
        archive_project_worktree_sync(PathBuf::from(root), PathBuf::from(path))
    })
    .await
    .map_err(|error| format!("Worktree archive task failed: {error}"))?
}

#[tauri::command]
pub(crate) async fn list_git_repository_summaries(
    projects: Vec<RuntimeContextProject>,
) -> Result<Vec<GitRepositorySummary>, String> {
    tauri::async_runtime::spawn_blocking(move || list_git_repository_summaries_sync(projects))
        .await
        .map_err(|error| format!("Repository dashboard task failed: {error}"))?
}
