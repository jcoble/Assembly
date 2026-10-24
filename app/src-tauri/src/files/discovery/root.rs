use std::path::{Path, PathBuf};
use std::process::Command;
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::Arc;
use crate::normalized_path_string;
use crate::files::types::{
    ProjectRootValidationResult,
};

#[derive(Default)]
pub(crate) struct ProjectRootValidationOwner {
    generation: Arc<AtomicU64>,
}

impl ProjectRootValidationOwner {
    pub(crate) fn advance(&self, generation: u64) -> bool {
        generation >= self.generation.fetch_max(generation, Ordering::AcqRel)
    }

    pub(crate) fn cancel(&self, generation: u64) {
        let _ = self.generation.compare_exchange(
            generation,
            generation.saturating_add(1),
            Ordering::AcqRel,
            Ordering::Acquire,
        );
    }

    pub(crate) fn generation(&self) -> Arc<AtomicU64> {
        Arc::clone(&self.generation)
    }
}

#[cfg(test)]
pub(crate) fn validate_project_root_sync(path: PathBuf) -> ProjectRootValidationResult {
    validate_project_root_sync_while(path, || true).unwrap_or_else(|| ProjectRootValidationResult {
        path: String::new(),
        exists: false,
        is_directory: false,
        is_git_repository: false,
        git_root: None,
        message: "Project validation was cancelled".to_string(),
    })
}

pub(crate) fn validate_project_root_sync_while(
    path: PathBuf,
    is_current: impl Fn() -> bool,
) -> Option<ProjectRootValidationResult> {
    let path_label = path.display().to_string();
    if !is_current() {
        return None;
    }
    let metadata = std::fs::metadata(&path);
    let Ok(metadata) = metadata else {
        return Some(ProjectRootValidationResult {
            path: path_label,
            exists: false,
            is_directory: false,
            is_git_repository: false,
            git_root: None,
            message: "Project path not found".to_string(),
        });
    };

    if !metadata.is_dir() {
        return Some(ProjectRootValidationResult {
            path: path_label,
            exists: true,
            is_directory: false,
            is_git_repository: false,
            git_root: None,
            message: "Project path points to a file. Choose the repository folder instead."
                .to_string(),
        });
    }

    if !is_current() {
        return None;
    }
    let git_root_path = git_repository_root_while(&path, is_current)?;
    let is_git_repository = git_root_path
        .as_ref()
        .is_some_and(|git_root| paths_refer_to_same_location(git_root, &path));
    let git_root = git_root_path
        .as_ref()
        .map(|root| normalized_path_string(root));
    Some(ProjectRootValidationResult {
        path: path_label,
        exists: true,
        is_directory: true,
        is_git_repository,
        git_root: git_root.clone(),
        message: if is_git_repository {
            "Project root ready".to_string()
        } else if let Some(git_root) = git_root {
            format!("Folder is inside a Git repository. Add {git_root} for full project context.")
        } else {
            "Folder is not a Git repository. Source browsing will work, but Git/worktree panels may be unavailable."
                .to_string()
        },
    })
}

fn git_repository_root_while(
    path: &Path,
    is_current: impl Fn() -> bool,
) -> Option<Option<PathBuf>> {
    let mut current = Some(path);
    while let Some(candidate) = current {
        if !is_current() {
            return None;
        }
        if candidate.join(".git").exists() {
            return Some(Some(candidate.to_path_buf()));
        }
        current = candidate.parent();
    }

    if !is_current() {
        return None;
    }
    let output = match Command::new("git")
        .args([
            "-C",
            path.to_str().unwrap_or_default(),
            "rev-parse",
            "--show-toplevel",
        ])
        .output()
    {
        Ok(output) => output,
        Err(_) => return Some(None),
    };

    if !is_current() {
        return None;
    }
    if !output.status.success() {
        return Some(None);
    }
    let stdout = String::from_utf8_lossy(&output.stdout);
    let root = stdout.trim().to_string();
    if root.is_empty() {
        Some(None)
    } else {
        Some(Some(PathBuf::from(root)))
    }
}

fn paths_refer_to_same_location(left: &Path, right: &Path) -> bool {
    let normalized_left = std::fs::canonicalize(left)
        .map(|path| normalized_path_string(&path))
        .unwrap_or_else(|_| normalized_path_string(left));
    let normalized_right = std::fs::canonicalize(right)
        .map(|path| normalized_path_string(&path))
        .unwrap_or_else(|_| normalized_path_string(right));
    normalized_left == normalized_right
}
