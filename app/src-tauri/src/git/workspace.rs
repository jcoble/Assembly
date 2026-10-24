//! Working-copy git actions for the source-control panel: staging, committing,
//! remotes, discarding changes, branches, stashes, and pull requests.
//!
//! Every command builds an argument array and hands it to `git` (or `gh`)
//! directly — nothing goes through a shell, so a branch name or a path can
//! never turn into another command.
//!
//! THE DESTRUCTIVE ONES. `discard_git_paths`, `discard_all_git_changes` and
//! `pop_git_stash` throw work away that git cannot give back. They exist here
//! as plain commands with no timer, no retry and no "helpful" sweep of
//! neighbouring files: the panel asks the person first, and the argument list
//! is exactly the files they were shown. The argument builders below are pure
//! so the tests can read the command that would run without running it.

use std::path::{Path, PathBuf};
use std::process::Command;

use serde::{Deserialize, Serialize};
use serde_json::Value;

use crate::bounded_process;
use crate::git::pull_requests::{format_gh_spawn_error, format_process_failure, summarize_checks};
use crate::{
    project_git_status_sync, run_git_text, run_git_with_paths, validate_git_relative_paths,
    validate_git_root, GitActionResult,
};

/// The most open pull requests one panel read will ask GitHub for.
const PULL_REQUEST_LIST_LIMIT: usize = 30;

pub(crate) fn stage_git_paths_sync(
    root: PathBuf,
    paths: Vec<String>,
) -> Result<GitActionResult, String> {
    validate_git_root(&root)?;
    let validated_paths = validate_git_relative_paths(&paths)?;
    run_git_with_paths(&root, &["add"], &validated_paths)?;
    Ok(GitActionResult {
        message: format_git_path_action_message("Staged", validated_paths.len()),
        status: project_git_status_sync(root)?,
    })
}

pub(crate) fn unstage_git_paths_sync(
    root: PathBuf,
    paths: Vec<String>,
) -> Result<GitActionResult, String> {
    validate_git_root(&root)?;
    let validated_paths = validate_git_relative_paths(&paths)?;
    run_git_with_paths(&root, &["restore", "--staged"], &validated_paths)?;
    Ok(GitActionResult {
        message: format_git_path_action_message("Unstaged", validated_paths.len()),
        status: project_git_status_sync(root)?,
    })
}

pub(crate) fn commit_git_repository_sync(
    root: PathBuf,
    message: String,
) -> Result<GitActionResult, String> {
    validate_git_root(&root)?;
    let message = message.trim();
    if message.is_empty() {
        return Err("Commit message is required".to_string());
    }
    if message.contains('\0') {
        return Err("Commit message cannot contain null bytes".to_string());
    }
    if !git_has_staged_changes(&root)? {
        return Err("No staged changes to commit".to_string());
    }

    run_git_text(&root, &["commit", "-m", message])?;
    Ok(GitActionResult {
        message: "Committed staged changes".to_string(),
        status: project_git_status_sync(root)?,
    })
}

pub(crate) fn fetch_git_repository_sync(root: PathBuf) -> Result<GitActionResult, String> {
    validate_git_root(&root)?;
    run_git_text(&root, &["fetch", "--prune"])?;
    Ok(GitActionResult {
        message: "Fetched repository remotes".to_string(),
        status: project_git_status_sync(root)?,
    })
}

pub(crate) fn pull_git_repository_sync(root: PathBuf) -> Result<GitActionResult, String> {
    validate_git_root(&root)?;
    run_git_text(&root, &["pull", "--ff-only"])?;
    Ok(GitActionResult {
        message: "Pulled fast-forward updates".to_string(),
        status: project_git_status_sync(root)?,
    })
}

pub(crate) fn push_git_repository_sync(root: PathBuf) -> Result<GitActionResult, String> {
    validate_git_root(&root)?;
    run_git_text(&root, &["push"])?;
    Ok(GitActionResult {
        message: "Pushed current branch".to_string(),
        status: project_git_status_sync(root)?,
    })
}

pub(crate) fn publish_git_repository_sync(
    root: PathBuf,
    expected_branch: String,
    remote: String,
) -> Result<GitActionResult, String> {
    validate_git_root(&root)?;
    let status = project_git_status_sync(root.clone())?;
    if status.branch.as_deref() != Some(expected_branch.as_str()) || status.has_upstream {
        return Err(
            "The branch changed or already has an upstream. Refresh Source Control and try again."
                .to_string(),
        );
    }
    if expected_branch.is_empty() || expected_branch == "HEAD" {
        return Err("Select a branch before publishing.".to_string());
    }
    let remotes = run_git_text(&root, &["remote"])?;
    if !remotes.lines().any(|name| name == remote) {
        return Err("The selected remote is no longer configured for this repository.".to_string());
    }
    let destination = format!("HEAD:refs/heads/{expected_branch}");
    run_git_text(
        &root,
        &["push", "--set-upstream", "--", &remote, &destination],
    )?;
    Ok(GitActionResult {
        message: format!("Published {expected_branch} to {remote}"),
        status: project_git_status_sync(root)?,
    })
}

pub(crate) fn add_git_remote_sync(
    root: String,
    name: String,
    url: String,
) -> Result<GitActionResult, String> {
    let root = PathBuf::from(root);
    validate_git_root(&root)?;
    let name = name.trim();
    let url = url.trim();
    if name.is_empty() || url.is_empty() {
        return Err("A remote name and URL are required.".to_string());
    }
    if name.starts_with('-') || url.starts_with('-') {
        return Err("The remote name and URL cannot start with a hyphen.".to_string());
    }
    run_git_text(&root, &["remote", "add", "--", name, url])?;
    Ok(GitActionResult {
        message: format!("Added remote {name}"),
        status: project_git_status_sync(root)?,
    })
}

fn git_has_staged_changes(root: &Path) -> Result<bool, String> {
    let root_arg = root.display().to_string();
    let output = bounded_process::output(
        Command::new("git").arg("-C").arg(root_arg).args([
            "diff",
            "--cached",
            "--quiet",
            "--exit-code",
        ]),
        "git diff --cached",
        bounded_process::LOCAL_COMMAND_TIMEOUT,
    )
    .map_err(|error| format!("Could not run git diff --cached: {error}"))?;

    if output.status.success() {
        return Ok(false);
    }

    if output.status.code() == Some(1) {
        return Ok(true);
    }

    let stderr = String::from_utf8_lossy(&output.stderr).trim().to_string();
    Err(if stderr.is_empty() {
        format!("git diff --cached exited with {}", output.status)
    } else {
        stderr
    })
}

fn format_git_path_action_message(action: &str, count: usize) -> String {
    let noun = if count == 1 { "path" } else { "paths" };
    format!("{action} {count} {noun}")
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct GitBranchSummary {
    pub name: String,
    pub is_current: bool,
    pub upstream: String,
    /// The folder where this branch is checked out, or '' when no worktree has it.
    pub worktree_path: String,
    pub subject: String,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct GitBranchList {
    pub current: String,
    pub branches: Vec<GitBranchSummary>,
    pub remotes: Vec<String>,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct GitStashEntry {
    pub index: usize,
    pub label: String,
    pub description: String,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct PullRequestSummary {
    pub number: u64,
    pub title: String,
    pub url: String,
    pub state: String,
    pub head_branch: String,
    pub is_draft: bool,
    pub checks: String,
    pub check_summary: String,
}

/// Which of the paths a discard was asked for are tracked, and which are files
/// git has never seen. They need different commands, so the split is worked out
/// before either one runs.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct DiscardPlan {
    pub tracked: Vec<String>,
    pub untracked: Vec<String>,
}

// ── pure argument builders (what the tests read) ─────────────────────────────

/// Put the named files back to how HEAD has them, in the index and on disk at
/// once. `--source=HEAD` is what makes a file staged AND changed come all the
/// way back rather than only losing its unstaged half.
pub fn build_discard_tracked_args(paths: &[String]) -> Vec<String> {
    let mut args = vec![
        "restore".to_string(),
        "--source=HEAD".to_string(),
        "--staged".to_string(),
        "--worktree".to_string(),
        "--".to_string(),
    ];
    args.extend(paths.iter().cloned());
    args
}

/// Delete files git has never tracked. `-f` because git refuses without it and
/// `-d` so a new folder goes with its contents; no `-x`, so anything the repo
/// deliberately ignores (build output, `.env`) is left alone.
pub fn build_discard_untracked_args(paths: &[String]) -> Vec<String> {
    let mut args = vec![
        "clean".to_string(),
        "-f".to_string(),
        "-d".to_string(),
        "--".to_string(),
    ];
    args.extend(paths.iter().cloned());
    args
}

/// Every command an "everything" discard runs, in order. Untracked files are
/// only deleted when the person asked for that as well.
pub fn build_discard_all_args(include_untracked: bool) -> Vec<Vec<String>> {
    let mut commands = vec![vec![
        "restore".to_string(),
        "--source=HEAD".to_string(),
        "--staged".to_string(),
        "--worktree".to_string(),
        "--".to_string(),
        ".".to_string(),
    ]];
    if include_untracked {
        commands.push(vec![
            "clean".to_string(),
            "-f".to_string(),
            "-d".to_string(),
        ]);
    }
    commands
}

pub fn build_branch_create_args(name: &str, checkout: bool) -> Vec<String> {
    if checkout {
        vec![
            "switch".to_string(),
            "--create".to_string(),
            name.to_string(),
        ]
    } else {
        vec!["branch".to_string(), name.to_string()]
    }
}

pub fn build_branch_switch_args(name: &str) -> Vec<String> {
    vec!["switch".to_string(), name.to_string()]
}

pub fn build_stash_push_args(include_untracked: bool, message: &str) -> Vec<String> {
    let mut args = vec!["stash".to_string(), "push".to_string()];
    if include_untracked {
        args.push("--include-untracked".to_string());
    }
    let message = message.trim();
    if !message.is_empty() {
        args.push("--message".to_string());
        args.push(message.to_string());
    }
    args
}

pub fn build_stash_pop_args(index: Option<usize>) -> Vec<String> {
    let mut args = vec!["stash".to_string(), "pop".to_string()];
    if let Some(index) = index {
        args.push(format!("stash@{{{index}}}"));
    }
    args
}

/// An amend with no message keeps the one the commit already has, which is what
/// "amend" means when the box was left empty.
pub fn build_amend_args(message: &str) -> Vec<String> {
    let message = message.trim();
    if message.is_empty() {
        vec![
            "commit".to_string(),
            "--amend".to_string(),
            "--no-edit".to_string(),
        ]
    } else {
        vec![
            "commit".to_string(),
            "--amend".to_string(),
            "--message".to_string(),
            message.to_string(),
        ]
    }
}

pub fn build_gh_pr_list_args(limit: usize) -> Vec<String> {
    vec![
        "pr".to_string(),
        "list".to_string(),
        "--state".to_string(),
        "open".to_string(),
        "--limit".to_string(),
        limit.to_string(),
        "--json".to_string(),
        "number,title,url,state,isDraft,headRefName,statusCheckRollup".to_string(),
    ]
}

/// Split the asked-for paths by what git already knows about them. Anything the
/// status does not mention is treated as tracked: `git restore` on a path with
/// nothing to restore says so and changes nothing, whereas `git clean` on a
/// file git does track would delete work.
pub fn plan_discard(paths: &[String], untracked: &[String]) -> DiscardPlan {
    let mut plan = DiscardPlan::default();
    for path in paths {
        if untracked.iter().any(|known| known == path) {
            plan.untracked.push(path.clone());
        } else {
            plan.tracked.push(path.clone());
        }
    }
    plan
}

/// `git branch --format=…` writes one line per branch, five fields separated by
/// the unit separator, with `*` marking the checked-out one.
pub fn parse_branch_list(output: &str) -> GitBranchList {
    let mut current = String::new();
    let mut branches = Vec::new();
    for line in output.lines() {
        let line = line.trim_end();
        if line.trim().is_empty() {
            continue;
        }
        let mut fields = line.split('\u{1f}');
        let marker = fields.next().unwrap_or_default();
        let name = fields.next().unwrap_or_default().trim().to_string();
        if name.is_empty() {
            continue;
        }
        let upstream = fields.next().unwrap_or_default().trim().to_string();
        let worktree_path = fields.next().unwrap_or_default().trim().to_string();
        let subject = fields.next().unwrap_or_default().trim().to_string();
        let is_current = marker.trim() == "*";
        if is_current {
            current = name.clone();
        }
        branches.push(GitBranchSummary { name, is_current, upstream, worktree_path, subject });
    }
    GitBranchList { current, branches, remotes: Vec::new() }
}

/// `git stash list` writes `stash@{0}: WIP on main: abc123 subject`. The index
/// is what a pop needs; the rest is what a person reads.
pub fn parse_stash_list(output: &str) -> Vec<GitStashEntry> {
    output
        .lines()
        .filter(|line| !line.trim().is_empty())
        .enumerate()
        .map(|(index, line)| {
            let (label, description) = match line.split_once(':') {
                Some((label, rest)) => (label.trim().to_string(), rest.trim().to_string()),
                None => (format!("stash@{{{index}}}"), line.trim().to_string()),
            };
            GitStashEntry {
                index,
                label,
                description,
            }
        })
        .collect()
}

/// Read what `gh pr list --json …` answered. A field `gh` did not send is not a
/// failure — an older `gh` omits the check rollup, and a pull request with no
/// checks is an ordinary thing.
pub fn parse_pull_request_list(raw: &str) -> Result<Vec<PullRequestSummary>, String> {
    let value: Value = serde_json::from_str(raw)
        .map_err(|error| format!("gh pr list returned invalid JSON: {error}"))?;
    let Value::Array(entries) = value else {
        return Err("gh pr list did not return a list".to_string());
    };
    Ok(entries
        .iter()
        .filter_map(|entry| {
            let number = entry.get("number").and_then(Value::as_u64)?;
            let (checks, check_summary) = summarize_checks(entry.get("statusCheckRollup"));
            Some(PullRequestSummary {
                number,
                title: entry
                    .get("title")
                    .and_then(Value::as_str)
                    .unwrap_or_default()
                    .to_string(),
                url: entry
                    .get("url")
                    .and_then(Value::as_str)
                    .unwrap_or_default()
                    .to_string(),
                state: entry
                    .get("state")
                    .and_then(Value::as_str)
                    .unwrap_or("OPEN")
                    .to_string(),
                head_branch: entry
                    .get("headRefName")
                    .and_then(Value::as_str)
                    .unwrap_or_default()
                    .to_string(),
                is_draft: entry
                    .get("isDraft")
                    .and_then(Value::as_bool)
                    .unwrap_or(false),
                checks,
                check_summary,
            })
        })
        .collect())
}

/// Branch names reach git as bare arguments. Anything that could be read as an
/// option, or that git itself would refuse, is turned away here with a sentence
/// a person can act on.
pub fn validate_new_branch_name(name: &str) -> Result<String, String> {
    let name = name.trim();
    if name.is_empty() {
        return Err("Type a branch name first".to_string());
    }
    if name.starts_with('-') {
        return Err("A branch name cannot start with a dash".to_string());
    }
    if name.len() > 200 {
        return Err("That branch name is too long".to_string());
    }
    let refused = ['\0', ' ', '\t', '~', '^', ':', '?', '*', '[', '\\'];
    if name.chars().any(|character| {
        refused.contains(&character) || character.is_control() || character.is_whitespace()
    }) {
        return Err("Branch names cannot contain spaces or any of ~ ^ : ? * [ \\".to_string());
    }
    if name.contains("..")
        || name.ends_with('/')
        || name.starts_with('/')
        || name.ends_with(".lock")
    {
        return Err("That is not a name git will accept for a branch".to_string());
    }
    Ok(name.to_string())
}

fn describe_count(count: usize, singular: &str) -> String {
    if count == 1 {
        format!("1 {singular}")
    } else {
        format!("{count} {singular}s")
    }
}

// ── the commands ────────────────────────────────────────────────────────────

#[tauri::command]
pub async fn discard_git_paths(
    root: String,
    paths: Vec<String>,
) -> Result<GitActionResult, String> {
    tauri::async_runtime::spawn_blocking(move || discard_git_paths_sync(PathBuf::from(root), paths))
        .await
        .map_err(|error| format!("Discard task failed: {error}"))?
}

#[tauri::command]
pub async fn discard_all_git_changes(
    root: String,
    include_untracked: bool,
) -> Result<GitActionResult, String> {
    tauri::async_runtime::spawn_blocking(move || {
        discard_all_git_changes_sync(PathBuf::from(root), include_untracked)
    })
    .await
    .map_err(|error| format!("Discard task failed: {error}"))?
}

#[tauri::command]
pub async fn list_git_branches(root: String) -> Result<GitBranchList, String> {
    tauri::async_runtime::spawn_blocking(move || list_git_branches_sync(PathBuf::from(root)))
        .await
        .map_err(|error| format!("Branch list task failed: {error}"))?
}

#[tauri::command]
pub async fn create_git_branch(
    root: String,
    name: String,
    checkout: bool,
) -> Result<GitActionResult, String> {
    tauri::async_runtime::spawn_blocking(move || {
        create_git_branch_sync(PathBuf::from(root), name, checkout)
    })
    .await
    .map_err(|error| format!("Branch task failed: {error}"))?
}

#[tauri::command]
pub async fn switch_git_branch(root: String, name: String) -> Result<GitActionResult, String> {
    tauri::async_runtime::spawn_blocking(move || switch_git_branch_sync(PathBuf::from(root), name))
        .await
        .map_err(|error| format!("Branch task failed: {error}"))?
}

#[tauri::command]
pub async fn stash_git_changes(
    root: String,
    include_untracked: bool,
    message: Option<String>,
) -> Result<GitActionResult, String> {
    tauri::async_runtime::spawn_blocking(move || {
        stash_git_changes_sync(
            PathBuf::from(root),
            include_untracked,
            message.unwrap_or_default(),
        )
    })
    .await
    .map_err(|error| format!("Stash task failed: {error}"))?
}

#[tauri::command]
pub async fn pop_git_stash(root: String, index: Option<usize>) -> Result<GitActionResult, String> {
    tauri::async_runtime::spawn_blocking(move || pop_git_stash_sync(PathBuf::from(root), index))
        .await
        .map_err(|error| format!("Stash task failed: {error}"))?
}

#[tauri::command]
pub async fn list_git_stashes(root: String) -> Result<Vec<GitStashEntry>, String> {
    tauri::async_runtime::spawn_blocking(move || list_git_stashes_sync(PathBuf::from(root)))
        .await
        .map_err(|error| format!("Stash list task failed: {error}"))?
}

#[tauri::command]
pub async fn amend_git_commit(
    root: String,
    message: Option<String>,
) -> Result<GitActionResult, String> {
    tauri::async_runtime::spawn_blocking(move || {
        amend_git_commit_sync(PathBuf::from(root), message.unwrap_or_default())
    })
    .await
    .map_err(|error| format!("Amend task failed: {error}"))?
}

#[tauri::command]
pub async fn list_open_pull_requests(root: String) -> Result<Vec<PullRequestSummary>, String> {
    tauri::async_runtime::spawn_blocking(move || list_open_pull_requests_sync(PathBuf::from(root)))
        .await
        .map_err(|error| format!("Pull request list task failed: {error}"))?
}

// ── the work behind them ────────────────────────────────────────────────────

fn run_git_args(root: &Path, args: &[String]) -> Result<String, String> {
    run_git_text(root, &args.iter().map(String::as_str).collect::<Vec<_>>())
}

fn untracked_paths(root: &Path) -> Result<Vec<String>, String> {
    let status = project_git_status_sync(root.to_path_buf())?;
    Ok(status
        .files
        .iter()
        .filter(|file| file.badge == "?")
        .map(|file| file.relative_path.clone())
        .collect())
}

fn discard_git_paths_sync(root: PathBuf, paths: Vec<String>) -> Result<GitActionResult, String> {
    validate_git_root(&root)?;
    let paths = validate_git_relative_paths(&paths)?;
    let plan = plan_discard(&paths, &untracked_paths(&root)?);

    if !plan.tracked.is_empty() {
        run_git_args(&root, &build_discard_tracked_args(&plan.tracked))?;
    }
    if !plan.untracked.is_empty() {
        run_git_args(&root, &build_discard_untracked_args(&plan.untracked))?;
    }

    Ok(GitActionResult {
        message: format!(
            "Discarded changes in {}",
            describe_count(paths.len(), "file")
        ),
        status: project_git_status_sync(root)?,
    })
}

fn discard_all_git_changes_sync(
    root: PathBuf,
    include_untracked: bool,
) -> Result<GitActionResult, String> {
    validate_git_root(&root)?;
    for args in build_discard_all_args(include_untracked) {
        run_git_args(&root, &args)?;
    }
    Ok(GitActionResult {
        message: if include_untracked {
            "Discarded every change and deleted the new files".to_string()
        } else {
            "Discarded every change to tracked files".to_string()
        },
        status: project_git_status_sync(root)?,
    })
}

fn list_git_branches_sync(root: PathBuf) -> Result<GitBranchList, String> {
    validate_git_root(&root)?;
    let output = run_git_text(
        &root,
        &[
            "branch",
            "--list",
            "--sort=-committerdate",
            "--format=%(HEAD)\u{1f}%(refname:short)\u{1f}%(upstream:short)\u{1f}%(worktreepath)\u{1f}%(contents:subject)",
        ],
    )?;
    let mut list = parse_branch_list(&output);
    list.remotes = run_git_text(&root, &["remote"])?
        .lines()
        .map(str::to_string)
        .collect();
    Ok(list)
}

fn create_git_branch_sync(
    root: PathBuf,
    name: String,
    checkout: bool,
) -> Result<GitActionResult, String> {
    validate_git_root(&root)?;
    let name = validate_new_branch_name(&name)?;
    run_git_args(&root, &build_branch_create_args(&name, checkout))?;
    Ok(GitActionResult {
        message: if checkout {
            format!("Created {name} and switched to it")
        } else {
            format!("Created {name}")
        },
        status: project_git_status_sync(root)?,
    })
}

fn switch_git_branch_sync(root: PathBuf, name: String) -> Result<GitActionResult, String> {
    validate_git_root(&root)?;
    let name = validate_new_branch_name(&name)?;
    run_git_args(&root, &build_branch_switch_args(&name))?;
    Ok(GitActionResult {
        message: format!("Switched to {name}"),
        status: project_git_status_sync(root)?,
    })
}

fn stash_git_changes_sync(
    root: PathBuf,
    include_untracked: bool,
    message: String,
) -> Result<GitActionResult, String> {
    validate_git_root(&root)?;
    if message.contains('\0') {
        return Err("A stash message cannot contain null bytes".to_string());
    }
    let output = run_git_args(&root, &build_stash_push_args(include_untracked, &message))?;
    let saved = !output.contains("No local changes to save");
    Ok(GitActionResult {
        message: if saved {
            "Stashed the working copy".to_string()
        } else {
            "Nothing to stash — the working copy is clean".to_string()
        },
        status: project_git_status_sync(root)?,
    })
}

fn pop_git_stash_sync(root: PathBuf, index: Option<usize>) -> Result<GitActionResult, String> {
    validate_git_root(&root)?;
    run_git_args(&root, &build_stash_pop_args(index))?;
    Ok(GitActionResult {
        message: "Restored the stashed changes".to_string(),
        status: project_git_status_sync(root)?,
    })
}

fn list_git_stashes_sync(root: PathBuf) -> Result<Vec<GitStashEntry>, String> {
    validate_git_root(&root)?;
    let output = run_git_text(&root, &["stash", "list"])?;
    Ok(parse_stash_list(&output))
}

fn amend_git_commit_sync(root: PathBuf, message: String) -> Result<GitActionResult, String> {
    validate_git_root(&root)?;
    if message.contains('\0') {
        return Err("A commit message cannot contain null bytes".to_string());
    }
    run_git_text(&root, &["rev-parse", "--verify", "HEAD"])
        .map_err(|_| "There is no commit here yet, so there is nothing to amend".to_string())?;
    run_git_args(&root, &build_amend_args(&message))?;
    Ok(GitActionResult {
        message: "Amended the last commit".to_string(),
        status: project_git_status_sync(root)?,
    })
}

fn list_open_pull_requests_sync(root: PathBuf) -> Result<Vec<PullRequestSummary>, String> {
    validate_git_root(&root)?;
    let output = bounded_process::output(
        Command::new("gh")
            .current_dir(&root)
            .args(build_gh_pr_list_args(PULL_REQUEST_LIST_LIMIT)),
        "gh pr list",
        bounded_process::NETWORK_COMMAND_TIMEOUT,
    )
    .map_err(format_gh_spawn_error)?;
    if !output.status.success() {
        return Err(format_process_failure(
            "gh pr list",
            &output.stderr,
            &output.stdout,
        ));
    }
    parse_pull_request_list(&String::from_utf8_lossy(&output.stdout))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn discarding_a_tracked_file_restores_index_and_disk_from_head() {
        assert_eq!(
            build_discard_tracked_args(&["src/main.rs".to_string()]),
            vec![
                "restore",
                "--source=HEAD",
                "--staged",
                "--worktree",
                "--",
                "src/main.rs"
            ]
        );
    }

    #[test]
    fn discarding_an_untracked_file_cleans_it_without_touching_ignored_files() {
        let args = build_discard_untracked_args(&["notes.md".to_string()]);
        assert_eq!(args, vec!["clean", "-f", "-d", "--", "notes.md"]);
        assert!(
            !args.iter().any(|arg| arg == "-x"),
            "ignored files are never deleted by a discard"
        );
    }

    #[test]
    fn discard_all_only_cleans_when_untracked_files_were_asked_for() {
        assert_eq!(build_discard_all_args(false).len(), 1);
        let both = build_discard_all_args(true);
        assert_eq!(both.len(), 2);
        assert_eq!(both[1], vec!["clean", "-f", "-d"]);
    }

    #[test]
    fn discard_plan_sends_only_known_untracked_paths_to_clean() {
        let plan = plan_discard(
            &["a.rs".to_string(), "new.md".to_string()],
            &["new.md".to_string()],
        );
        assert_eq!(plan.tracked, vec!["a.rs".to_string()]);
        assert_eq!(plan.untracked, vec!["new.md".to_string()]);
    }

    #[test]
    fn an_unknown_path_is_restored_rather_than_deleted() {
        let plan = plan_discard(&["ghost.rs".to_string()], &[]);
        assert_eq!(plan.tracked, vec!["ghost.rs".to_string()]);
        assert!(plan.untracked.is_empty());
    }

    #[test]
    fn branch_arguments_separate_creating_from_switching() {
        assert_eq!(
            build_branch_create_args("tsk-808-git-panel", true),
            vec!["switch", "--create", "tsk-808-git-panel"]
        );
        assert_eq!(
            build_branch_create_args("tsk-808-git-panel", false),
            vec!["branch", "tsk-808-git-panel"]
        );
        assert_eq!(build_branch_switch_args("main"), vec!["switch", "main"]);
    }

    #[test]
    fn stash_arguments_carry_untracked_and_message_only_when_asked() {
        assert_eq!(build_stash_push_args(false, "  "), vec!["stash", "push"]);
        assert_eq!(
            build_stash_push_args(true, "before the rebase"),
            vec![
                "stash",
                "push",
                "--include-untracked",
                "--message",
                "before the rebase"
            ]
        );
        assert_eq!(build_stash_pop_args(None), vec!["stash", "pop"]);
        assert_eq!(
            build_stash_pop_args(Some(2)),
            vec!["stash", "pop", "stash@{2}"]
        );
    }

    #[test]
    fn an_empty_amend_message_keeps_the_existing_one() {
        assert_eq!(
            build_amend_args("   "),
            vec!["commit", "--amend", "--no-edit"]
        );
        assert_eq!(
            build_amend_args("fix the parser"),
            vec!["commit", "--amend", "--message", "fix the parser"]
        );
    }

    #[test]
    fn branch_names_that_git_or_a_shell_would_misread_are_refused() {
        assert_eq!(
            validate_new_branch_name("tsk 808"),
            Err("Branch names cannot contain spaces or any of ~ ^ : ? * [ \\".to_string())
        );
        assert!(validate_new_branch_name("feature:one").is_err());
        assert!(validate_new_branch_name("--force").is_err());
        assert!(validate_new_branch_name("").is_err());
        assert!(validate_new_branch_name("feature/a..b").is_err());
        assert_eq!(
            validate_new_branch_name("  tsk-808-git-panel  "),
            Ok("tsk-808-git-panel".to_string())
        );
    }

    #[test]
    fn branch_list_marks_the_checked_out_branch() {
        let list = parse_branch_list(
            "*\u{1f}main\u{1f}origin/main\u{1f}/repo\u{1f}latest work\n \u{1f}spike\u{1f}\u{1f}/wt/spike\u{1f}try it\n",
        );
        assert_eq!(list.current, "main");
        assert_eq!(list.branches.len(), 2);
        assert!(list.branches[0].is_current);
        assert_eq!(list.branches[0].upstream, "origin/main");
        assert!(!list.branches[1].is_current);
        assert_eq!(list.branches[1].upstream, "");
        assert_eq!(list.branches[1].worktree_path, "/wt/spike");
        assert_eq!(list.branches[1].subject, "try it");
    }

    #[test]
    fn stash_list_keeps_the_index_a_pop_needs() {
        let stashes = parse_stash_list("stash@{0}: WIP on main: abc123 subject\nstash@{1}: on x\n");
        assert_eq!(stashes.len(), 2);
        assert_eq!(stashes[0].index, 0);
        assert_eq!(stashes[0].label, "stash@{0}");
        assert_eq!(stashes[0].description, "WIP on main: abc123 subject");
        assert_eq!(stashes[1].index, 1);
    }

    #[test]
    fn gh_pr_list_asks_for_open_requests_with_their_checks() {
        let args = build_gh_pr_list_args(30);
        assert_eq!(
            &args[..6],
            &["pr", "list", "--state", "open", "--limit", "30"]
        );
        assert!(args.last().unwrap().contains("statusCheckRollup"));
    }

    #[test]
    fn pull_request_list_summarises_checks_and_tolerates_missing_fields() {
        let parsed = parse_pull_request_list(
            r#"[
              {"number":24,"title":"Add the git panel","url":"https://example.test/pull/24",
               "state":"OPEN","isDraft":false,"headRefName":"tsk-808",
               "statusCheckRollup":[{"status":"COMPLETED","conclusion":"SUCCESS"},
                                    {"status":"IN_PROGRESS","conclusion":""}]},
              {"number":25}
            ]"#,
        )
        .unwrap();
        assert_eq!(parsed.len(), 2);
        assert_eq!(parsed[0].head_branch, "tsk-808");
        assert_eq!(parsed[0].checks, "pending");
        assert_eq!(parsed[0].check_summary, "1 passing · 1 pending · 0 failing");
        assert_eq!(parsed[1].title, "");
        assert_eq!(parsed[1].checks, "none");
        assert_eq!(parsed[1].state, "OPEN");
    }

    #[test]
    fn a_pull_request_list_that_is_not_a_list_is_an_error() {
        assert!(parse_pull_request_list("{}").is_err());
        assert!(parse_pull_request_list("not json").is_err());
    }
}
