//! Folders that become projects: inspecting one, listing a machine's folders,
//! the one-time import of the project roots saved by older versions, and
//! matching session folders to this Mac's projects.

use std::collections::{HashMap, HashSet};
use std::path::{Path, PathBuf};
use std::process::Command;
use std::time::Instant;

use mcb_core::scanners::sessions::AgentSessionRecord;
use mcb_core::session_store::{ProjectRow, SessionStore};
use serde::{Deserialize, Serialize};

use crate::agent_conversation::manager::AgentRuntimeManager;

/// Settings written by versions before the project registry. Read once by
/// `import_saved_project_roots`, then deleted.
const SAVED_ROOTS_KEY: &str = "new-session.custom-project-roots";
const LAST_ROOT_KEY: &str = "new-session.last-project-root";
const MAX_LISTED_FOLDERS: usize = 500;

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct ProjectFolderInspection {
    pub root_path: String,
    pub title: String,
    pub repo_key: String,
    pub is_git: bool,
}

#[derive(Clone, Debug, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct FolderListing {
    pub path: String,
    pub directories: Vec<String>,
    pub truncated: bool,
}

/// Turns `~`, `~/x` and an empty path into paths under `$HOME`.
fn expand_home(path: &str) -> Result<PathBuf, String> {
    if path.is_empty() || path == "~" || path.starts_with("~/") {
        let home = std::env::var("HOME").map_err(|_| "The home folder is unavailable.".to_string())?;
        return Ok(PathBuf::from(home).join(path.strip_prefix('~').unwrap_or("").trim_start_matches('/')));
    }
    Ok(PathBuf::from(path))
}

fn git_text(dir: &Path, args: &[&str]) -> Option<String> {
    let output = Command::new("git").arg("-C").arg(dir).args(args).output().ok()?;
    output.status.success().then(|| String::from_utf8_lossy(&output.stdout).trim().to_owned())
}

/// The absolute git common directory of `dir`: one per repository, shared by
/// its main checkout and every worktree. Read from git's own files, so no git
/// process starts. `None` outside git, for a folder that is gone, and for a
/// relative path (which would resolve against our own folder).
pub(crate) fn git_common_dir(dir: &str) -> Option<String> {
    let dir = Path::new(dir);
    if !dir.is_absolute() {
        return None;
    }
    for folder in std::fs::canonicalize(dir).ok()?.ancestors() {
        let dot_git = folder.join(".git");
        let git_dir = if dot_git.is_file() {
            let text = std::fs::read_to_string(&dot_git).ok()?;
            folder.join(text.strip_prefix("gitdir:")?.trim())
        } else if dot_git.join("HEAD").is_file() {
            // Git skips a `.git` folder that is not a repository, such as an empty one.
            dot_git
        } else if folder.join("HEAD").is_file() && folder.join("objects").is_dir() && folder.join("refs").is_dir() {
            // A bare repository is its own git dir.
            folder.to_path_buf()
        } else {
            continue;
        };
        // A worktree's git dir names the shared one, relative to itself or absolute.
        let common = match std::fs::read_to_string(git_dir.join("commondir")) {
            Ok(text) => git_dir.join(text.trim()),
            Err(_) => git_dir,
        };
        return std::fs::canonicalize(common).ok().map(|path| path.to_string_lossy().into_owned());
    }
    None
}

fn to_json(value: &impl Serialize) -> Result<String, String> {
    serde_json::to_string(value).map_err(|error| error.to_string())
}

/// Each folder's local project, as `(folder, project_id)`, for the folders
/// that have one. The path rules run first, in SQL. The folders they leave
/// unmatched then join a project whose root shares their git common directory,
/// again in SQL, after reading git's files for each such folder and local root.
pub(crate) fn match_local_folders(store: &SessionStore, folders: &[String]) -> Result<Vec<(String, String)>, String> {
    match_folders(store, "local", folders, git_common_dir)
}

fn match_folders(store: &SessionStore, machine: &str, folders: &[String], common_dir: impl Fn(&str) -> Option<String>) -> Result<Vec<(String, String)>, String> {
    let mut matches = store.match_folders_by_path(&to_json(&folders)?, machine).map_err(|error| error.to_string())?;
    let matched: HashSet<&str> = matches.iter().map(|(folder, _)| folder.as_str()).collect();
    let leftovers: Vec<(&str, String)> = folders.iter().map(String::as_str)
        .filter(|folder| !matched.contains(folder))
        .filter_map(|folder| Some((folder, common_dir(folder)?))).collect();
    if leftovers.is_empty() { return Ok(matches); }
    let roots: Vec<(String, String)> = store.project_roots(machine).map_err(|error| error.to_string())?
        .into_iter().filter_map(|root| {
            let common = common_dir(&root)?;
            Some((root, common))
        }).collect();
    matches.extend(store.match_folders_by_common_dir(&to_json(&leftovers)?, &to_json(&roots)?, machine)
        .map_err(|error| error.to_string())?);
    Ok(matches)
}

/// Files older local sessions under their projects when a local project was
/// added since the last run (amendment A1); otherwise it returns before any
/// UPDATE or git call. Either way it writes one line to backend.log.
pub(crate) fn backfill_local_session_projects(store: &SessionStore) -> Result<(), String> {
    let started = Instant::now();
    let Some(newest) = store.project_newer_than_backfill().map_err(|error| error.to_string())? else {
        crate::debug_log::stderr_log!("project back-fill: no new projects");
        return Ok(());
    };
    let folders = store.unassigned_local_session_cwds().map_err(|error| error.to_string())?;
    let matches = match_local_folders(store, &folders)?;
    let filed = store.assign_session_projects(&to_json(&matches)?).map_err(|error| error.to_string())?;
    store.mark_project_backfill(newest).map_err(|error| error.to_string())?;
    crate::debug_log::stderr_log!(
        "project back-fill: filed {filed} sessions; {} of {} folders matched; {} ms",
        matches.len(),
        folders.len(),
        started.elapsed().as_millis()
    );
    Ok(())
}

/// Puts each scanned session's project group on it: its folder matched as the back-fill does on
/// its owning machine, with reported git common directories for remote folders; then
/// the group from one SQL statement over the distinct folders. No folder gets no project.
pub(crate) fn attach_scanned_project_groups(store: &SessionStore, machine: &str, sessions: &mut [AgentSessionRecord], common_dir: impl Fn(&str) -> Option<String>) -> Result<(), String> {
    let folders: Vec<String> = sessions
        .iter()
        .filter_map(|session| session.project_path.clone())
        .filter(|folder| !folder.is_empty())
        .collect::<HashSet<_>>()
        .into_iter()
        .collect();
    let matches: HashMap<String, String> = match_folders(store, machine, &folders, common_dir)?.into_iter().collect();
    let pairs: Vec<(&str, Option<&str>)> = folders
        .iter()
        .map(|folder| (folder.as_str(), matches.get(folder).map(String::as_str)))
        .chain([("", None)])
        .collect();
    let groups: HashMap<String, (String, String)> = store
        .project_groups(&to_json(&pairs)?)
        .map_err(|error| error.to_string())?
        .into_iter()
        .map(|(folder, key, label)| (folder, (key, label)))
        .collect();
    for session in sessions.iter_mut() {
        if let Some((key, label)) = groups.get(session.project_path.as_deref().unwrap_or("")) {
            session.project_group_key.clone_from(key);
            session.project_group_label.clone_from(label);
        }
    }
    Ok(())
}

/// Checks a folder before it becomes a project. Reads only; never runs `git init`.
pub(crate) fn inspect_project_folder_sync(path: &str) -> Result<ProjectFolderInspection, String> {
    let requested = expand_home(path)?;
    if !requested.is_absolute() {
        return Err("Folder path must be absolute".into());
    }
    let canonical = std::fs::canonicalize(requested)
        .map_err(|_| "That folder does not exist.".to_string())?;
    if !canonical.is_dir() {
        return Err("That path is a file. Choose a folder.".into());
    }
    if project_root_is_bare_sync(&canonical) {
        return Err("This is a bare repository, so it has no working files. Choose one of its checkouts instead.".into());
    }
    if git_text(&canonical, &["rev-parse", "--is-inside-git-dir"]).as_deref() == Some("true") {
        return Err("This is git's internal folder. Choose the repository folder instead.".into());
    }
    let root_path = canonical.to_string_lossy().into_owned();
    let folder_name = canonical
        .file_name()
        .map(|name| name.to_string_lossy().into_owned())
        .unwrap_or_else(|| root_path.clone());
    let Some(top) = git_text(&canonical, &["rev-parse", "--show-toplevel"]) else {
        return Ok(ProjectFolderInspection { root_path, title: folder_name, repo_key: String::new(), is_git: false });
    };
    if top != root_path {
        return Err(format!("This folder is inside the repository at {top}. Choose that folder instead."));
    }
    let url = git_text(&canonical, &["config", "--get", "remote.upstream.url"])
        .or_else(|| git_text(&canonical, &["config", "--get", "remote.origin.url"]))
        .unwrap_or_default();
    let repo_key = normalize_repo_key(&url);
    let title = match repo_key.rsplit('/').next() {
        Some(last) if !repo_key.is_empty() => last.to_owned(),
        _ => folder_name,
    };
    Ok(ProjectFolderInspection { root_path, title, repo_key, is_git: true })
}

/// A new project folder may not be made inside a repository; `parent` is the
/// inspection of the folder it would be made in.
pub(crate) fn refuse_repository_parent(parent: &ProjectFolderInspection) -> Result<(), String> {
    if parent.is_git {
        return Err(format!("That would be inside the repository at {}. Choose a folder outside it.", parent.root_path));
    }
    Ok(())
}

/// Makes the new project folder `path` a repository.
pub(crate) fn git_init(path: &Path) -> Result<(), String> {
    crate::run_git_text(path, &["init"])
        .map(drop)
        .map_err(|error| format!("Git could not set up the folder: {error}"))
}

/// Makes the new project folder `path` a clone of `source`, a URL or a path.
/// Git refuses a `path` that exists and is not empty.
pub(crate) fn git_clone(source: &str, path: &Path) -> Result<(), String> {
    let parent = path.parent().unwrap_or(path);
    crate::run_git_text(parent, &["clone", "--", source, &path.to_string_lossy()])
        .map(drop)
        .map_err(|error| format!("The repository could not be copied: {error}"))
}

/// `host/owner/repo` for https, ssh and `user@host:path` remote URLs; `''` otherwise.
pub(crate) fn normalize_repo_key(url: &str) -> String {
    let url = url.trim();
    let (host, path) = if let Some(rest) = url.strip_prefix("https://").or_else(|| url.strip_prefix("ssh://")) {
        let Some((authority, path)) = rest.split_once('/') else { return String::new() };
        let host = authority.rsplit('@').next().unwrap_or("");
        (host.split(':').next().unwrap_or(""), path)
    } else if let Some((user_host, path)) = url.split_once(':').filter(|(left, _)| left.contains('@') && !left.contains('/')) {
        (user_host.rsplit('@').next().unwrap_or(""), path)
    } else {
        return String::new();
    };
    let path = path.trim_matches('/');
    let path = path.strip_suffix(".git").unwrap_or(path).trim_end_matches('/');
    if host.is_empty() || path.is_empty() {
        return String::new();
    }
    format!("{}/{path}", host.to_lowercase())
}

/// The folders directly inside `path`, sorted, at most 500.
pub(crate) fn list_folders_sync(path: &str) -> Result<FolderListing, String> {
    let requested = expand_home(path)?;
    if !requested.is_absolute() {
        return Err("Folder path must be absolute".into());
    }
    let root = std::fs::canonicalize(requested).map_err(|e| e.to_string())?;
    let mut directories = Vec::new();
    for entry in std::fs::read_dir(&root).map_err(|e| e.to_string())? {
        let entry = entry.map_err(|e| e.to_string())?;
        if entry.path().is_dir() { directories.push(entry.file_name().to_string_lossy().into_owned()); }
        if directories.len() > MAX_LISTED_FOLDERS { break; }
    }
    let truncated = directories.len() > MAX_LISTED_FOLDERS;
    directories.truncate(MAX_LISTED_FOLDERS);
    directories.sort();
    Ok(FolderListing { path: root.to_string_lossy().into_owned(), directories, truncated })
}

#[tauri::command]
pub(crate) async fn list_folders(path: String) -> Result<FolderListing, String> {
    tauri::async_runtime::spawn_blocking(move || list_folders_sync(&path))
        .await
        .map_err(|error| error.to_string())?
}

/// Is `root` a bare repository? It has no files to work on, so no session starts there.
pub(crate) fn project_root_is_bare_sync(root: &Path) -> bool {
    git_text(root, &["rev-parse", "--is-bare-repository"]).as_deref() == Some("true")
}

#[tauri::command]
pub(crate) async fn project_root_is_bare(root: String) -> Result<bool, String> {
    tauri::async_runtime::spawn_blocking(move || project_root_is_bare_sync(Path::new(&root)))
        .await
        .map_err(|error| error.to_string())
}

/// Makes an existing folder on this machine a repository. A remote machine answers the
/// same command name through `remote_workspace`.
#[tauri::command]
pub(crate) async fn workspace_git_init(path: String) -> Result<(), String> {
    tauri::async_runtime::spawn_blocking(move || git_init(Path::new(&path)))
        .await
        .map_err(|error| error.to_string())?
}

/// Workflow runs share the project's worktree root, never the session folder.
pub(crate) fn create_project_worktree_sync(root: &Path, branch: &str) -> Result<String, String> {
    let common = git_text(root, &["rev-parse", "--path-format=absolute", "--git-common-dir"])
        .ok_or("Project is not a git repository")?;
    let common = Path::new(&common);
    let repo = if common.file_name().is_some_and(|name| name == ".git") {
        common.parent().ok_or("Repository folder is unavailable")?
    } else { common };
    let path = repo.parent().ok_or("Project worktree root is unavailable")?
        .join("worktrees").join(repo.file_name().ok_or("Repository name is unavailable")?).join(branch);
    for args in [vec!["fetch", "origin", "+refs/heads/main:refs/remotes/origin/main"],
        vec!["worktree", "add", "-b", branch, path.to_str().ok_or("Invalid worktree path")?, "origin/main"]] {
        std::fs::create_dir_all(path.parent().unwrap()).map_err(|error| error.to_string())?;
        let output = Command::new("git").arg("-C").arg(root).args(args).output().map_err(|error| error.to_string())?;
        if !output.status.success() { return Err(String::from_utf8_lossy(&output.stderr).trim_end().into()); }
    }
    Ok(path.to_string_lossy().into_owned())
}

pub(crate) fn new_project_row(machine: String, inspection: ProjectFolderInspection) -> ProjectRow {
    ProjectRow {
        id: uuid::Uuid::new_v4().to_string(),
        machine,
        root_path: inspection.root_path,
        title: inspection.title,
        repo_key: inspection.repo_key,
        created_at_ms: chrono::Utc::now().timestamp_millis(),
        // Computed by the store when the row is read back.
        group_key: String::new(),
        pinned_at_ms: None,
        last_used_ms: None,
    }
}

/// Turns the project roots saved by older versions into local projects, then
/// deletes the old settings so this runs once.
pub(crate) fn import_saved_project_roots(manager: &AgentRuntimeManager) -> Result<(), String> {
    let Some(saved) = manager.read_app_setting(SAVED_ROOTS_KEY)? else {
        return Ok(());
    };
    let entries = serde_json::from_str::<serde_json::Value>(&saved).unwrap_or_default();
    for path in entries.as_array().into_iter().flatten().filter_map(|entry| entry.get("path")?.as_str()) {
        if !path.starts_with('/') {
            continue;
        }
        match inspect_project_folder_sync(path) {
            Ok(inspection) => {
                manager.add_project(new_project_row("local".into(), inspection))?;
            }
            Err(reason) => crate::debug_log::stderr_log!("skipped saved project root {path}: {reason}"),
        }
    }
    for key in [SAVED_ROOTS_KEY, LAST_ROOT_KEY] {
        manager.store().delete_app_setting(key).map_err(|error| error.to_string())?;
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::path::{Path, PathBuf};
    use std::process::Command;

    const GIT_INTERNAL: &str = "This is git's internal folder. Choose the repository folder instead.";

    fn temp_dir() -> PathBuf {
        let path = std::env::temp_dir().join(format!("assembly-projects-{}", uuid::Uuid::new_v4()));
        std::fs::create_dir_all(&path).unwrap();
        std::fs::canonicalize(path).unwrap()
    }

    fn git(dir: &Path, args: &[&str]) {
        let output = Command::new("git").args(args).current_dir(dir).output().unwrap();
        assert!(output.status.success(), "{}", String::from_utf8_lossy(&output.stderr));
    }

    fn text(path: &Path) -> &str {
        path.to_str().unwrap()
    }

    /// Tests that point `HOME` somewhere else take turns.
    static HOME_LOCK: std::sync::Mutex<()> = std::sync::Mutex::new(());

    /// Runs `body` with `HOME` set to `home`, then restores it.
    fn with_home<T>(home: &Path, body: impl FnOnce() -> T) -> T {
        let _guard = HOME_LOCK.lock().unwrap_or_else(|poisoned| poisoned.into_inner());
        let previous = std::env::var_os("HOME");
        std::env::set_var("HOME", home);
        let result = body();
        match previous {
            Some(value) => std::env::set_var("HOME", value),
            None => std::env::remove_var("HOME"),
        }
        result
    }

    fn repo_with_main(root: &Path) -> PathBuf {
        let repo = root.join("repo");
        std::fs::create_dir_all(&repo).unwrap();
        git(&repo, &["init", "-b", "main"]);
        git(&repo, &["-c", "user.name=t", "-c", "user.email=t@t", "commit", "--allow-empty", "-m", "first"]);
        git(&repo, &["remote", "add", "origin", "git@github.com:jcoble/Mac-Command-Bar.git"]);
        repo
    }

    #[test]
    fn project_root_is_bare_tells_a_bare_repository_from_a_checkout() {
        let root = temp_dir();
        let repo = repo_with_main(&root);
        let bare = root.join("bare");
        git(&root, &["clone", "-q", "--bare", text(&repo), text(&bare)]);
        let plain = root.join("plain");
        std::fs::create_dir_all(&plain).unwrap();
        assert!(project_root_is_bare_sync(&bare));
        assert!(!project_root_is_bare_sync(&repo));
        assert!(!project_root_is_bare_sync(&plain), "a folder with no repository is not bare");
        std::fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn inspect_refuses_git_internal_folders() {
        let root = temp_dir();
        let repo = root.join("repo");
        let bare = root.join("bare.git");
        std::fs::create_dir_all(&repo).unwrap();
        git(&repo, &["init", "-b", "main"]);
        git(&root, &["init", "--bare", text(&bare)]);
        for folder in [repo.join(".git"), repo.join(".git/refs")] {
            assert_eq!(inspect_project_folder_sync(text(&folder)).unwrap_err(), GIT_INTERNAL, "{folder:?}");
        }
        std::fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn inspect_refuses_a_bare_repository_and_a_missing_folder() {
        let root = temp_dir();
        let bare = root.join("bare.git");
        git(&root, &["init", "--bare", text(&bare)]);
        assert_eq!(
            inspect_project_folder_sync(text(&bare)).unwrap_err(),
            "This is a bare repository, so it has no working files. Choose one of its checkouts instead."
        );
        assert_eq!(inspect_project_folder_sync(text(&root.join("missing"))).unwrap_err(), "That folder does not exist.");
        std::fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn a_new_project_folder_may_not_go_inside_a_repository() {
        let root = temp_dir();
        let repo = root.join("repo");
        std::fs::create_dir_all(&repo).unwrap();
        git(&repo, &["init", "-b", "main"]);
        let refusal = format!("That would be inside the repository at {}. Choose a folder outside it.", repo.display());
        assert_eq!(refuse_repository_parent(&inspect_project_folder_sync(text(&repo)).unwrap()).unwrap_err(), refusal);
        assert_eq!(refuse_repository_parent(&inspect_project_folder_sync(text(&root)).unwrap()), Ok(()));
        std::fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn inspect_refuses_a_subfolder_of_a_repository() {
        let repo = temp_dir();
        std::fs::create_dir_all(repo.join("src")).unwrap();
        git(&repo, &["init", "-b", "main"]);
        let error = inspect_project_folder_sync(text(&repo.join("src"))).unwrap_err();
        assert_eq!(
            error,
            format!("This folder is inside the repository at {}. Choose that folder instead.", repo.display())
        );
        std::fs::remove_dir_all(repo).unwrap();
    }

    #[test]
    fn inspect_accepts_a_plain_folder_without_creating_git() {
        let root = temp_dir();
        let folder = root.join("notes");
        std::fs::create_dir_all(&folder).unwrap();
        let inspection = inspect_project_folder_sync(text(&folder)).unwrap();
        assert_eq!(
            inspection,
            ProjectFolderInspection {
                root_path: text(&folder).to_owned(),
                title: "notes".to_owned(),
                repo_key: String::new(),
                is_git: false,
            }
        );
        assert!(!folder.join(".git").exists());
        // A relative path would resolve against the process's working folder.
        assert_eq!(inspect_project_folder_sync("src").unwrap_err(), "Folder path must be absolute");
        std::fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn inspect_reads_title_and_repo_key_from_upstream_then_origin() {
        let root = temp_dir();
        let repo = root.join("main");
        std::fs::create_dir_all(&repo).unwrap();
        git(&repo, &["init", "-b", "main"]);
        git(&repo, &["remote", "add", "origin", "git@github.com:jcoble/Mac-Command-Bar.git"]);
        let inspection = inspect_project_folder_sync(&format!("{}/", text(&repo))).unwrap();
        assert_eq!(inspection.root_path, text(&repo));
        assert_eq!(inspection.title, "Mac-Command-Bar");
        assert_eq!(inspection.repo_key, "github.com/jcoble/Mac-Command-Bar");
        assert!(inspection.is_git);
        git(&repo, &["remote", "add", "upstream", "https://github.com/other/x"]);
        let inspection = inspect_project_folder_sync(text(&repo)).unwrap();
        assert_eq!(inspection.repo_key, "github.com/other/x");
        assert_eq!(inspection.title, "x");
        std::fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn normalize_repo_key_handles_https_ssh_and_scp() {
        for (url, expected) in [
            ("https://user@GitHub.com/a/b.git", "github.com/a/b"),
            ("ssh://git@github.com:22/a/b", "github.com/a/b"),
            ("git@github.com:a/b.git", "github.com/a/b"),
            ("/srv/repo.git", ""),
            ("", ""),
        ] {
            assert_eq!(normalize_repo_key(url), expected, "{url}");
        }
    }

    #[test]
    fn common_dir_matches_git_without_running_it() {
        let root = temp_dir();
        let repo = repo_with_main(&root);
        std::fs::create_dir_all(repo.join("src")).unwrap();
        let linked = root.join("linked");
        git(&repo, &["worktree", "add", "-b", "linked", text(&linked)]);
        let bare = root.join("bare.git");
        let bare_worktree = root.join("bare-worktree");
        git(&root, &["init", "--bare", text(&bare)]);
        git(&bare, &["worktree", "add", text(&bare_worktree)]);
        // An older project saved on the bare folder itself matches its worktrees too.
        for folder in [repo.clone(), repo.join("src"), linked, bare_worktree, bare] {
            let output = Command::new("git")
                .arg("-C").arg(&folder)
                .args(["rev-parse", "--path-format=absolute", "--git-common-dir"])
                .output().unwrap();
            let expected = std::fs::canonicalize(String::from_utf8_lossy(&output.stdout).trim()).unwrap();
            assert_eq!(git_common_dir(text(&folder)).as_deref(), Some(text(&expected)), "{folder:?}");
        }
        // Git ignores an empty .git folder, like the one in this machine's home folder.
        let plain = root.join("plain");
        let empty_git = root.join("empty-git");
        std::fs::create_dir_all(&plain).unwrap();
        std::fs::create_dir_all(empty_git.join(".git")).unwrap();
        assert_eq!(git_common_dir(text(&plain)), None);
        assert_eq!(git_common_dir(text(&empty_git)), None);
        std::fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn list_folders_expands_home_and_lists_only_directories() {
        let home = temp_dir();
        std::fs::create_dir_all(home.join("zeta")).unwrap();
        std::fs::create_dir_all(home.join("alpha")).unwrap();
        std::fs::write(home.join("file.txt"), "not a folder").unwrap();
        let (listed, nested) = with_home(&home, || (list_folders_sync("~"), list_folders_sync("~/alpha")));
        assert_eq!(
            listed.unwrap(),
            FolderListing {
                path: text(&home).to_owned(),
                directories: vec!["alpha".to_owned(), "zeta".to_owned()],
                truncated: false,
            }
        );
        assert_eq!(nested.unwrap().path, text(&home.join("alpha")));
        std::fs::remove_dir_all(home).unwrap();
    }

    fn unfiled_session(owned_id: &str, cwd: &Path) -> mcb_core::session_store::SessionRow {
        mcb_core::session_store::SessionRow {
            owned_id: owned_id.into(), native_session_id: None, provider: "codex".into(), model: None, effort: None,
            cwd: text(cwd).into(), worktree: None, branch: None, title: None, title_source: None, project: None,
            project_id: None, state: "ready".into(), suspended: false, created_at_ms: 1, last_activity_at_ms: 1,
            extra_json: "{}".into(),
        }
    }

    #[test]
    fn backfill_files_worktree_and_subfolder_sessions_once_per_new_project() {
        let root = temp_dir();
        let repo = repo_with_main(&root);
        std::fs::create_dir_all(repo.join("src")).unwrap();
        let worktree = root.join("worktrees/feature");
        git(&repo, &["worktree", "add", "-b", "feature", text(&worktree)]);
        let elsewhere = root.join("elsewhere");
        std::fs::create_dir_all(&elsewhere).unwrap();
        let store = SessionStore::open_in_memory().unwrap();
        for (id, cwd) in [("worktree", &worktree), ("subfolder", &repo.join("src")), ("elsewhere", &elsewhere)] {
            store.upsert_session(&unfiled_session(id, cwd)).unwrap();
        }
        let (project, _) = store
            .insert_or_get_project(&new_project_row("local".into(), inspect_project_folder_sync(text(&repo)).unwrap()))
            .unwrap();

        backfill_local_session_projects(&store).unwrap();
        let project_of = |id: &str| store.get_session(id).unwrap().unwrap().project_id;
        assert_eq!(project_of("worktree").as_deref(), Some(project.id.as_str()), "joined by git common dir");
        assert_eq!(project_of("subfolder").as_deref(), Some(project.id.as_str()), "joined by path");
        assert_eq!(project_of("elsewhere"), None);

        // No newer project: the rerun stops before matching, so a new unfiled row stays unfiled.
        store.upsert_session(&unfiled_session("later", &worktree)).unwrap();
        backfill_local_session_projects(&store).unwrap();
        assert_eq!(project_of("later"), None);
        std::fs::remove_dir_all(root).unwrap();
    }

    #[tokio::test]
    async fn remove_project_keeps_or_deletes_its_sessions() {
        let manager = crate::agent_conversation::manager::AgentRuntimeManager::new(
            crate::agent_conversation::providers::ProviderRegistry::default(),
        );
        let store = manager.store();
        for (project, folder) in [("keep", "/work/keep"), ("drop", "/work/drop")] {
            let row = ProjectRow { id: project.into(), ..new_project_row("local".into(), ProjectFolderInspection {
                root_path: folder.into(), title: project.into(), repo_key: String::new(), is_git: false,
            }) };
            store.insert_or_get_project(&row).unwrap();
        }
        for (owned_id, project) in [("kept", "keep"), ("dropped", "drop")] {
            let mut session = unfiled_session(owned_id, Path::new("/work"));
            session.project_id = Some(project.into());
            store.upsert_session(&session).unwrap();
        }

        manager.remove_project("keep", false).await.unwrap();
        manager.remove_project("drop", true).await.unwrap();
        assert_eq!(store.get_session("kept").unwrap().unwrap().project_id, None);
        assert_eq!(store.get_session("dropped").unwrap(), None);
        assert_eq!(manager.list_projects().unwrap(), vec![]);
        assert_eq!(manager.remove_project("drop", false).await.unwrap_err(), "That project no longer exists.");
    }

    #[tokio::test]
    async fn remove_project_with_deletes_accepts_a_remote_project() {
        let manager = crate::agent_conversation::manager::AgentRuntimeManager::new(
            crate::agent_conversation::providers::ProviderRegistry::default(),
        );
        let row = ProjectRow { id: "remote".into(), ..new_project_row("box".into(), ProjectFolderInspection {
            root_path: "/home/me/repo".into(), title: "repo".into(), repo_key: String::new(), is_git: false,
        }) };
        manager.store().insert_or_get_project(&row).unwrap();
        // The command deletes remote sessions on their machine first; a cached
        // row the machine kept is left here without a project.
        manager.store().file_remote_sessions("box", r#"[["[\"box\",\"s1\"]", "remote", 5, {}]]"#).unwrap();

        manager.remove_project("remote", true).await.unwrap();
        assert_eq!(manager.store().get_project("remote").unwrap(), None);
        assert_eq!(manager.store().get_session(r#"["box","s1"]"#).unwrap().unwrap().project_id, None);
    }

    #[test]
    fn import_saved_project_roots_adds_valid_folders_and_deletes_the_keys() {
        let root = temp_dir();
        let repo = root.join("repo");
        let plain = root.join("plain");
        std::fs::create_dir_all(&repo).unwrap();
        std::fs::create_dir_all(&plain).unwrap();
        git(&repo, &["init", "-b", "main"]);
        let manager = crate::agent_conversation::manager::AgentRuntimeManager::new(
            crate::agent_conversation::providers::ProviderRegistry::default(),
        );
        let saved = serde_json::json!([
            { "id": "a", "name": "Repo", "path": text(&repo) },
            { "id": "b", "name": "Plain", "path": text(&plain) },
            { "id": "c", "name": "Missing", "path": text(&root.join("missing")) },
            { "id": "d", "name": "Relative", "path": "relative/folder" },
        ]);
        manager.write_app_setting(SAVED_ROOTS_KEY, &saved.to_string()).unwrap();
        manager.write_app_setting(LAST_ROOT_KEY, "\"/somewhere\"").unwrap();

        import_saved_project_roots(&manager).unwrap();
        let projects = manager.list_projects().unwrap();
        let mut paths: Vec<_> = projects.iter().map(|project| project.root_path.clone()).collect();
        paths.sort();
        assert_eq!(paths, vec![text(&plain).to_owned(), text(&repo).to_owned()]);
        assert!(projects.iter().all(|project| project.machine == "local"));
        assert_eq!(manager.read_app_setting(SAVED_ROOTS_KEY).unwrap(), None);
        assert_eq!(manager.read_app_setting(LAST_ROOT_KEY).unwrap(), None);

        import_saved_project_roots(&manager).unwrap();
        assert_eq!(manager.list_projects().unwrap(), projects);
        std::fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn git_init_makes_the_new_folder_a_repository() {
        let root = temp_dir();
        let folder = root.join("new");
        std::fs::create_dir(&folder).unwrap();
        git_init(&folder).unwrap();
        assert!(inspect_project_folder_sync(text(&folder)).unwrap().is_git);
        std::fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn git_clone_copies_a_local_bare_repository() {
        let root = temp_dir();
        let repo = repo_with_main(&root);
        std::fs::write(repo.join("README.md"), "hello\n").unwrap();
        git(&repo, &["add", "README.md"]);
        git(&repo, &["-c", "user.name=t", "-c", "user.email=t@t", "commit", "-m", "readme"]);
        let bare = root.join("bare.git");
        git(&root, &["clone", "-q", "--bare", text(&repo), text(&bare)]);
        let copy = root.join("copy");
        git_clone(text(&bare), &copy).unwrap();
        assert!(inspect_project_folder_sync(text(&copy)).unwrap().is_git);
        assert_eq!(std::fs::read_to_string(copy.join("README.md")).unwrap(), "hello\n");
        std::fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn git_clone_refuses_a_non_empty_destination() {
        let root = temp_dir();
        let repo = repo_with_main(&root);
        let copy = root.join("copy");
        std::fs::create_dir(&copy).unwrap();
        std::fs::write(copy.join("notes.txt"), "mine\n").unwrap();
        let error = git_clone(text(&repo), &copy).unwrap_err();
        assert!(error.starts_with("The repository could not be copied: "), "{error}");
        assert!(error.contains("already exists and is not an empty directory"), "{error}");
        assert_eq!(std::fs::read_to_string(copy.join("notes.txt")).unwrap(), "mine\n");
        std::fs::remove_dir_all(root).unwrap();
    }
}
