//! Git/PR actions owned by the source-control surface.
//!
//! Git and `gh` are invoked with argument arrays, never through a shell. The
//! agent-generation commands use the current ACP session supplied by the
//! caller, so a missing or stale session is reported as an ordinary action
//! error instead of silently falling back to the chat UI.

use std::path::{Path, PathBuf};
use std::process::Command;

use serde::{Deserialize, Serialize};
use serde_json::Value;
use tauri::State;

use crate::agent_conversation::manager::AgentRuntimeManager;
use crate::agent_conversation::providers::AgentPrompt;
use crate::bounded_process;

const MAX_PROMPT_DIFF_BYTES: usize = 160_000;

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct AgentGenerationRequest {
    pub root: String,
    pub owned_id: String,
    pub generation: u64,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct PullRequestContext {
    pub branch: String,
    pub base: String,
    pub commits: String,
    pub diff: String,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct PullRequestDetails {
    pub title: String,
    pub description: String,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct PullRequestCreated {
    pub number: u64,
    pub url: String,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct PullRequestStatus {
    pub number: u64,
    pub url: String,
    pub state: String,
    pub checks: String,
    pub check_summary: String,
}

#[tauri::command]
pub async fn generate_commit_message(
    manager: State<'_, AgentRuntimeManager>,
    request: AgentGenerationRequest,
) -> Result<String, String> {
    let AgentGenerationRequest {
        root,
        owned_id,
        generation,
    } = request;
    let diff = tauri::async_runtime::spawn_blocking(move || {
        read_commit_message_diff_sync(PathBuf::from(root))
    })
    .await
    .map_err(|error| format!("Commit context task failed: {error}"))??;
    let prompt = build_commit_message_prompt(&diff);
    let generated = manager
        .prompt_once(
            &owned_id,
            generation,
            AgentPrompt {
                text: prompt,
                images: Vec::new(),
                attachment_ids: Vec::new(),
            },
        )
        .await?;
    let message = generated.text.trim().to_string();
    if message.is_empty() {
        Err("The active agent returned an empty commit message".to_string())
    } else {
        Ok(message)
    }
}

#[tauri::command]
pub async fn read_pull_request_context(root: String) -> Result<PullRequestContext, String> {
    tauri::async_runtime::spawn_blocking(move || {
        read_pull_request_context_sync(PathBuf::from(root))
    })
    .await
    .map_err(|error| format!("Pull request context task failed: {error}"))?
}

#[tauri::command]
pub async fn generate_pull_request_details(
    manager: State<'_, AgentRuntimeManager>,
    request: AgentGenerationRequest,
) -> Result<PullRequestDetails, String> {
    let AgentGenerationRequest {
        root,
        owned_id,
        generation,
    } = request;
    let context = tauri::async_runtime::spawn_blocking(move || {
        read_pull_request_context_sync(PathBuf::from(root))
    })
    .await
    .map_err(|error| format!("Pull request context task failed: {error}"))??;
    let prompt = build_pull_request_prompt(&context);
    let generated = manager
        .prompt_once(
            &owned_id,
            generation,
            AgentPrompt {
                text: prompt,
                images: Vec::new(),
                attachment_ids: Vec::new(),
            },
        )
        .await?;
    parse_pull_request_details(&generated.text)
}

#[tauri::command]
pub async fn create_pull_request(
    root: String,
    title: String,
    description: String,
    base: String,
    draft: bool,
) -> Result<PullRequestCreated, String> {
    tauri::async_runtime::spawn_blocking(move || {
        create_pull_request_sync(PathBuf::from(root), title, description, base, draft)
    })
    .await
    .map_err(|error| format!("Pull request creation task failed: {error}"))?
}

#[tauri::command]
pub async fn read_pull_request_status(
    root: String,
    branch: String,
) -> Result<PullRequestStatus, String> {
    tauri::async_runtime::spawn_blocking(move || {
        read_pull_request_status_sync(PathBuf::from(root), branch)
    })
    .await
    .map_err(|error| format!("Pull request status task failed: {error}"))?
}

pub fn build_commit_message_prompt(diff: &str) -> String {
    format!(
        "Write one concise commit subject for the following Git diff. Return only the subject, with no Markdown, explanation, issue references, or trailers. Never invent changes that are not present.\n\nGit diff:\n{}",
        bounded_diff(diff)
    )
}

pub fn build_pull_request_prompt(context: &PullRequestContext) -> String {
    format!(
        "Create a pull request title and description from this branch's commits and diff. Return raw JSON only with exactly two string fields: title and description. Keep the title concise, describe the user-visible change and only verification evidenced by the commits or diff, and do not add unrelated or invented claims.\n\nBranch: {}\nBase: {}\nCommits:\n{}\n\nDiff:\n{}",
        context.branch,
        context.base,
        context.commits.trim(),
        bounded_diff(&context.diff)
    )
}

pub fn parse_pull_request_details(raw: &str) -> Result<PullRequestDetails, String> {
    let trimmed = raw.trim().trim_matches('`').trim();
    let candidate = trimmed
        .strip_prefix("json")
        .map(str::trim)
        .unwrap_or(trimmed);
    let json_candidate = candidate
        .find('{')
        .and_then(|start| candidate.rfind('}').map(|end| &candidate[start..=end]));
    let value = json_candidate.unwrap_or(candidate);
    let parsed: PullRequestDetails = serde_json::from_str(value)
        .map_err(|error| format!("The agent returned invalid pull request JSON: {error}"))?;
    if parsed.title.trim().is_empty() {
        return Err("The agent returned an empty pull request title".to_string());
    }
    Ok(PullRequestDetails {
        title: parsed.title.trim().replace(['\n', '\r'], " "),
        description: parsed.description.trim().to_string(),
    })
}

pub fn build_gh_pr_create_args(
    branch: &str,
    base: &str,
    title: &str,
    description: &str,
    draft: bool,
) -> Vec<String> {
    let mut args = vec![
        "pr".to_string(),
        "create".to_string(),
        "--head".to_string(),
        branch.to_string(),
        "--base".to_string(),
        base.to_string(),
        "--title".to_string(),
        title.to_string(),
        "--body".to_string(),
        description.to_string(),
    ];
    if draft {
        args.push("--draft".to_string());
    }
    args
}

pub fn build_git_push_args(branch: &str, has_upstream: bool) -> Vec<String> {
    if has_upstream {
        vec!["push".to_string()]
    } else {
        vec![
            "push".to_string(),
            "--set-upstream".to_string(),
            "origin".to_string(),
            branch.to_string(),
        ]
    }
}

fn read_pull_request_context_sync(root: PathBuf) -> Result<PullRequestContext, String> {
    let root = validate_repository_root(&root)?;
    let branch = run_git(&root, &["symbolic-ref", "--quiet", "--short", "HEAD"])
        .ok()
        .map(|value| value.trim().to_string())
        .filter(|value| !value.is_empty())
        .unwrap_or_else(|| "HEAD".to_string());
    validate_branch_name(&branch)?;

    let base = run_git(
        &root,
        &[
            "symbolic-ref",
            "--quiet",
            "--short",
            "refs/remotes/origin/HEAD",
        ],
    )
    .ok()
    .map(|value| value.trim().trim_start_matches("origin/").to_string())
    .filter(|value| !value.is_empty())
    .unwrap_or_else(|| "main".to_string());
    validate_branch_name(&base)?;

    let range = format!("origin/{base}..HEAD");
    let commits = run_git(&root, &["log", "--format=%h%x1f%s", &range])
        .or_else(|_| run_git(&root, &["log", "-n", "20", "--format=%h%x1f%s"]))
        .unwrap_or_default()
        .lines()
        .map(|line| line.replace('\u{1f}', " "))
        .collect::<Vec<_>>()
        .join("\n");
    let committed_diff = run_git(&root, &["diff", "--binary", &range])
        .or_else(|_| run_git(&root, &["diff", "--binary", &format!("{base}..HEAD")]))
        .unwrap_or_default();
    let staged = run_git(&root, &["diff", "--cached"]).unwrap_or_default();
    let working = run_git(&root, &["diff"]).unwrap_or_default();
    let diff = combine_pull_request_diffs(&committed_diff, &staged, &working);

    Ok(PullRequestContext {
        branch,
        base,
        commits,
        diff,
    })
}

fn read_commit_message_diff_sync(root: PathBuf) -> Result<String, String> {
    let root = validate_repository_root(&root)?;
    let staged = run_git(&root, &["diff", "--cached"]).unwrap_or_default();
    let working = run_git(&root, &["diff"]).unwrap_or_default();
    Ok(combine_diffs(&staged, &working))
}

fn create_pull_request_sync(
    root: PathBuf,
    title: String,
    description: String,
    base: String,
    draft: bool,
) -> Result<PullRequestCreated, String> {
    let root = validate_repository_root(&root)?;
    let context = read_pull_request_context_sync(root.clone())?;
    let title = title.trim();
    let description = description.trim();
    let base = base.trim();
    if title.is_empty() {
        return Err("Pull request title is required".to_string());
    }
    validate_branch_name(base)?;
    let push_args = build_git_push_args(&context.branch, has_upstream(&root));
    run_git(
        &root,
        &push_args.iter().map(String::as_str).collect::<Vec<_>>(),
    )?;

    let args = build_gh_pr_create_args(&context.branch, base, title, description, draft);
    let output = bounded_process::output(
        Command::new("gh").current_dir(&root).args(&args),
        "gh pr create",
        bounded_process::NETWORK_COMMAND_TIMEOUT,
    )
    .map_err(format_gh_spawn_error)?;
    if !output.status.success() {
        return Err(format_process_failure(
            "gh pr create",
            &output.stderr,
            &output.stdout,
        ));
    }
    let stdout = String::from_utf8_lossy(&output.stdout);
    let url = stdout
        .lines()
        .rev()
        .map(str::trim)
        .find(|line| line.starts_with("http://") || line.starts_with("https://"))
        .ok_or_else(|| "gh pr create succeeded but did not return a pull request URL".to_string())?
        .to_string();
    let number = pull_request_number_from_url(&url)
        .ok_or_else(|| "gh pr create returned a URL without a pull request number".to_string())?;
    Ok(PullRequestCreated { number, url })
}

fn pull_request_number_from_url(url: &str) -> Option<u64> {
    url.trim_end_matches('/')
        .rsplit('/')
        .next()
        .and_then(|segment| segment.split('?').next())
        .and_then(|segment| segment.parse::<u64>().ok())
}

fn read_pull_request_status_sync(
    root: PathBuf,
    branch: String,
) -> Result<PullRequestStatus, String> {
    let root = validate_repository_root(&root)?;
    validate_branch_name(&branch)?;
    read_pull_request_status_for_ref(&root, branch.trim())
}

fn read_pull_request_status_for_ref(
    root: &Path,
    branch: &str,
) -> Result<PullRequestStatus, String> {
    let output = bounded_process::output(
        Command::new("gh").current_dir(root).args([
            "pr",
            "view",
            branch,
            "--json",
            "number,url,state,statusCheckRollup",
        ]),
        "gh pr view",
        bounded_process::NETWORK_COMMAND_TIMEOUT,
    )
    .map_err(format_gh_spawn_error)?;
    if !output.status.success() {
        return Err(format_process_failure(
            "gh pr view",
            &output.stderr,
            &output.stdout,
        ));
    }
    let value: Value = serde_json::from_slice(&output.stdout)
        .map_err(|error| format!("gh pr view returned invalid JSON: {error}"))?;
    let number = value
        .get("number")
        .and_then(Value::as_u64)
        .ok_or_else(|| "gh pr view did not return a pull request number".to_string())?;
    let url = value
        .get("url")
        .and_then(Value::as_str)
        .unwrap_or_default()
        .to_string();
    let state = value
        .get("state")
        .and_then(Value::as_str)
        .unwrap_or("UNKNOWN")
        .to_string();
    let (checks, check_summary) = summarize_checks(value.get("statusCheckRollup"));
    Ok(PullRequestStatus {
        number,
        url,
        state,
        checks,
        check_summary,
    })
}

pub(crate) fn summarize_checks(value: Option<&Value>) -> (String, String) {
    let Some(Value::Array(checks)) = value else {
        return ("none".to_string(), "No checks reported".to_string());
    };
    if checks.is_empty() {
        return ("none".to_string(), "No checks reported".to_string());
    }
    let mut failing = 0;
    let mut pending = 0;
    let mut passing = 0;
    for check in checks {
        let conclusion = check
            .get("conclusion")
            .and_then(Value::as_str)
            .unwrap_or_default()
            .to_ascii_uppercase();
        let status = check
            .get("status")
            .and_then(Value::as_str)
            .unwrap_or_default()
            .to_ascii_uppercase();
        if matches!(
            conclusion.as_str(),
            "FAILURE" | "CANCELLED" | "TIMED_OUT" | "ERROR"
        ) {
            failing += 1;
        } else if matches!(status.as_str(), "QUEUED" | "IN_PROGRESS" | "PENDING")
            || conclusion.is_empty()
        {
            pending += 1;
        } else {
            passing += 1;
        }
    }
    let state = if failing > 0 {
        "failing"
    } else if pending > 0 {
        "pending"
    } else {
        "passing"
    };
    (
        state.to_string(),
        format!("{passing} passing · {pending} pending · {failing} failing"),
    )
}

fn validate_repository_root(root: &Path) -> Result<PathBuf, String> {
    let root = root
        .canonicalize()
        .map_err(|error| format!("Could not read Git root: {error}"))?;
    if !root.is_dir() {
        return Err("Git root is not a directory".to_string());
    }
    run_git(&root, &["rev-parse", "--show-toplevel"])?;
    Ok(root)
}

fn validate_branch_name(branch: &str) -> Result<(), String> {
    let branch = branch.trim();
    if branch.is_empty()
        || branch.starts_with('-')
        || branch.contains('\0')
        || branch.chars().any(char::is_whitespace)
    {
        return Err("Branch names must be non-empty and option-safe".to_string());
    }
    Ok(())
}

fn has_upstream(root: &Path) -> bool {
    bounded_process::output(
        Command::new("git").current_dir(root).args([
            "rev-parse",
            "--abbrev-ref",
            "--symbolic-full-name",
            "@{upstream}",
        ]),
        "git rev-parse upstream",
        bounded_process::LOCAL_COMMAND_TIMEOUT,
    )
    .map(|output| output.status.success())
    .unwrap_or(false)
}

fn run_git(root: &Path, args: &[&str]) -> Result<String, String> {
    let timeout = if args
        .first()
        .is_some_and(|arg| matches!(*arg, "fetch" | "pull" | "push"))
    {
        bounded_process::NETWORK_COMMAND_TIMEOUT
    } else {
        bounded_process::LOCAL_COMMAND_TIMEOUT
    };
    let output = bounded_process::output(
        Command::new("git").current_dir(root).args(args),
        &format!("git {}", args.join(" ")),
        timeout,
    )
    .map_err(|error| format!("Could not run git {}: {error}", args.join(" ")))?;
    if !output.status.success() {
        let stderr = String::from_utf8_lossy(&output.stderr).trim().to_string();
        return Err(if stderr.is_empty() {
            format!("git {} exited with {}", args.join(" "), output.status)
        } else {
            stderr
        });
    }
    Ok(String::from_utf8_lossy(&output.stdout).to_string())
}

fn combine_diffs(staged: &str, working: &str) -> String {
    let staged = staged.trim();
    let working = working.trim();
    let combined = match (staged.is_empty(), working.is_empty()) {
        (true, true) => String::new(),
        (false, true) => staged.to_string(),
        (true, false) => working.to_string(),
        (false, false) => format!("## Staged\n{staged}\n\n## Working tree\n{working}"),
    };
    bounded_diff(&combined)
}

fn combine_pull_request_diffs(committed: &str, staged: &str, working: &str) -> String {
    let sections = [
        ("Branch changes", committed),
        ("Staged", staged),
        ("Working tree", working),
    ];
    let combined = sections
        .into_iter()
        .filter_map(|(label, diff)| {
            let diff = diff.trim();
            (!diff.is_empty()).then(|| format!("## {label}\n{diff}"))
        })
        .collect::<Vec<_>>()
        .join("\n\n");
    bounded_diff(&combined)
}

fn bounded_diff(diff: &str) -> String {
    if diff.len() <= MAX_PROMPT_DIFF_BYTES {
        return diff.to_string();
    }
    let mut end = MAX_PROMPT_DIFF_BYTES;
    while !diff.is_char_boundary(end) {
        end = end.saturating_sub(1);
    }
    let mut bounded = diff[..end].to_string();
    bounded.push_str("\n\n[diff truncated]");
    bounded
}

pub(crate) fn format_gh_spawn_error(error: std::io::Error) -> String {
    if error.kind() == std::io::ErrorKind::NotFound {
        "GitHub CLI (`gh`) is not installed or not on PATH".to_string()
    } else {
        format!("Could not start gh: {error}")
    }
}

pub(crate) fn format_process_failure(command: &str, stderr: &[u8], stdout: &[u8]) -> String {
    let stderr = String::from_utf8_lossy(stderr).trim().to_string();
    if !stderr.is_empty() {
        return format!("{command} failed: {stderr}");
    }
    let stdout = String::from_utf8_lossy(stdout).trim().to_string();
    if !stdout.is_empty() {
        return format!("{command} failed: {stdout}");
    }
    format!("{command} failed")
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn commit_prompt_contains_diff_without_trailer_instructions() {
        let prompt = build_commit_message_prompt("diff --git a/file b/file");
        assert!(prompt.contains("diff --git a/file b/file"));
        assert!(prompt.contains("no Markdown"));
        assert!(prompt.contains("trailers"));
    }

    #[test]
    fn pull_request_prompt_assembles_branch_base_commits_and_diff() {
        let prompt = build_pull_request_prompt(&PullRequestContext {
            branch: "feature/example".into(),
            base: "main".into(),
            commits: "abc123 add flow".into(),
            diff: "diff --git a/src/lib.rs b/src/lib.rs".into(),
        });
        assert!(prompt.contains("Branch: feature/example"));
        assert!(prompt.contains("Base: main"));
        assert!(prompt.contains("abc123 add flow"));
        assert!(prompt.contains("diff --git a/src/lib.rs b/src/lib.rs"));
    }

    #[test]
    fn pull_request_diff_keeps_committed_and_pending_sections_distinct() {
        let diff = combine_pull_request_diffs("committed", "staged", "working");
        assert!(diff.contains("## Branch changes\ncommitted"));
        assert!(diff.contains("## Staged\nstaged"));
        assert!(diff.contains("## Working tree\nworking"));
    }

    #[test]
    fn oversized_unicode_diff_is_bounded_on_a_character_boundary() {
        let diff = format!("{}é", "x".repeat(MAX_PROMPT_DIFF_BYTES - 1));
        let bounded = bounded_diff(&diff);
        assert!(bounded.ends_with("[diff truncated]"));
        assert!(std::str::from_utf8(bounded.as_bytes()).is_ok());
    }

    #[test]
    fn gh_arguments_are_ordered_and_draft_is_explicit() {
        assert_eq!(
            build_gh_pr_create_args("feature/a", "main", "Title", "Body", true),
            vec![
                "pr",
                "create",
                "--head",
                "feature/a",
                "--base",
                "main",
                "--title",
                "Title",
                "--body",
                "Body",
                "--draft"
            ]
        );
        assert_eq!(
            build_git_push_args("feature/a", false),
            vec!["push", "--set-upstream", "origin", "feature/a"]
        );
        assert_eq!(build_git_push_args("feature/a", true), vec!["push"]);
    }

    #[test]
    fn pull_request_details_accept_raw_json_or_a_fenced_json_block() {
        let details = parse_pull_request_details(
            r#"```json
{"title":"Fix flow","description":"Adds the panel."}
```"#,
        )
        .unwrap();
        assert_eq!(details.title, "Fix flow");
        assert_eq!(details.description, "Adds the panel.");
    }

    #[test]
    fn pull_request_number_is_parsed_from_gh_url_without_shelling_out() {
        assert_eq!(
            pull_request_number_from_url("https://github.com/example/repo/pull/808"),
            Some(808)
        );
        assert_eq!(
            pull_request_number_from_url("https://github.com/example/repo/pull/808?view=full"),
            Some(808)
        );
        assert_eq!(pull_request_number_from_url("not-a-pr-url"), None);
    }
}
