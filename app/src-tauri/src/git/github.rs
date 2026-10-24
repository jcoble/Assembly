//! Hosted GitHub reads for the Pull Requests center tab. Local Git state is not
//! used as the source of PR truth; it supplies only validated repository IDs.

use std::collections::BTreeMap;
use std::path::{Path, PathBuf};
use std::process::Command;

use serde::{Deserialize, Serialize};
use serde_json::{json, Value};

use crate::bounded_process;

const MAX_ROOTS: usize = 32;
const MAX_PAGE_SIZE: u16 = 30;
const MAX_RESPONSE_BYTES: usize = 2_000_000;
const SEARCH_QUERY: &str = "query($query:String!,$first:Int!,$after:String){search(query:$query,type:ISSUE_ADVANCED,first:$first,after:$after){issueCount pageInfo{hasNextPage endCursor} nodes{... on PullRequest{number title url isDraft updatedAt state headRefName baseRefName repository{nameWithOwner} author{login}}}}}";

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct GithubPullRequestQuery {
    pub roots: Vec<String>,
    pub project_filter: Option<String>,
    pub search: Option<String>,
    pub cursor: Option<String>,
    pub page_size: u16,
}

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct GithubPullRequestSummary {
    pub repository: String,
    pub local_root: String,
    pub number: u64,
    pub title: String,
    pub url: String,
    pub is_draft: bool,
    pub updated_at: String,
    pub state: String,
    pub author: String,
    pub head_branch: String,
    pub base_branch: String,
}

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct GithubPullRequestPage {
    pub items: Vec<GithubPullRequestSummary>,
    pub next_cursor: Option<String>,
    pub total_count: u64,
}

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct GithubComment {
    pub id: String,
    pub author: String,
    pub body: String,
    pub created_at: String,
    pub path: Option<String>,
    pub line: Option<u64>,
    pub side: Option<String>,
    pub reply_to_id: Option<u64>,
}

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct GithubReview {
    pub author: String,
    pub body: String,
    pub state: String,
    pub submitted_at: String,
}

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct GithubCheck {
    pub name: String,
    pub status: String,
    pub conclusion: String,
    pub url: String,
}

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct GithubCommit {
    pub oid: String,
    pub headline: String,
    pub author: String,
    pub committed_at: String,
}

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct GithubFile {
    pub path: String,
    pub previous_path: Option<String>,
    pub status: String,
    pub additions: u64,
    pub deletions: u64,
    pub patch: Option<String>,
}

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct GithubPullRequestDetail {
    pub repository: String,
    pub local_root: String,
    pub number: u64,
    pub title: String,
    pub body: String,
    pub url: String,
    pub author: String,
    pub head_branch: String,
    pub base_branch: String,
    pub head_sha: String,
    pub base_sha: String,
    pub state: String,
    pub is_draft: bool,
    pub mergeable: String,
    pub review_decision: String,
    pub created_at: String,
    pub merged_at: String,
    pub merged_by: String,
    pub closed_at: String,
    pub commits: Vec<GithubCommit>,
    pub comments: Vec<GithubComment>,
    pub reviews: Vec<GithubReview>,
    pub reviewers: Vec<String>,
    pub checks: Vec<GithubCheck>,
    pub files: Vec<GithubFile>,
    pub more_files: bool,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct GithubFileVersionQuery {
    root: String,
    path: String,
    previous_path: Option<String>,
    status: String,
    base_sha: String,
    head_sha: String,
}

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct GithubFileVersions {
    original_content: String,
    modified_content: String,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct GithubReviewLine {
    path: String,
    line: u64,
    side: String,
    body: String,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct GithubReviewSubmission {
    root: String,
    number: u64,
    expected_head_sha: String,
    body: String,
    comments: Vec<GithubReviewLine>,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct GithubReviewReply {
    root: String,
    number: u64,
    expected_head_sha: String,
    comment_id: u64,
    body: String,
}

/// A conversation comment: it goes on the pull request in any state, like
/// GitHub's own "Leave a comment" box.
#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct GithubIssueComment {
    root: String,
    number: u64,
    body: String,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct GithubMergeRequest {
    root: String,
    number: u64,
    expected_head_sha: String,
    expected_base_sha: String,
    method: String,
}

#[tauri::command]
pub async fn list_github_pull_requests(
    query: GithubPullRequestQuery,
) -> Result<GithubPullRequestPage, String> {
    tauri::async_runtime::spawn_blocking(move || list_sync(query))
        .await
        .map_err(|error| format!("Pull request list task failed: {error}"))?
}

#[tauri::command]
pub async fn read_github_pull_request(root: String, number: u64) -> Result<GithubPullRequestDetail, String> {
    tauri::async_runtime::spawn_blocking(move || read_detail_sync(&PathBuf::from(root), number))
        .await
        .map_err(|error| format!("Pull request detail task failed: {error}"))?
}

#[tauri::command]
pub async fn read_github_pull_request_file(query: GithubFileVersionQuery) -> Result<GithubFileVersions, String> {
    tauri::async_runtime::spawn_blocking(move || read_file_versions_sync(query))
        .await
        .map_err(|error| format!("Pull request file task failed: {error}"))?
}

#[tauri::command]
pub async fn submit_github_pull_request_review(submission: GithubReviewSubmission) -> Result<String, String> {
    tauri::async_runtime::spawn_blocking(move || submit_review_sync(submission))
        .await
        .map_err(|error| format!("Pull request review task failed: {error}"))?
}

#[tauri::command]
pub async fn reply_github_pull_request_comment(reply: GithubReviewReply) -> Result<String, String> {
    tauri::async_runtime::spawn_blocking(move || reply_comment_sync(reply))
        .await
        .map_err(|error| format!("Pull request reply task failed: {error}"))?
}

#[tauri::command]
pub async fn comment_github_pull_request(comment: GithubIssueComment) -> Result<String, String> {
    tauri::async_runtime::spawn_blocking(move || comment_sync(comment))
        .await
        .map_err(|error| format!("Pull request comment task failed: {error}"))?
}

#[tauri::command]
pub async fn merge_github_pull_request(request: GithubMergeRequest) -> Result<String, String> {
    tauri::async_runtime::spawn_blocking(move || merge_sync(request))
        .await
        .map_err(|error| format!("Pull request merge task failed: {error}"))?
}

fn merge_sync(request: GithubMergeRequest) -> Result<String, String> {
    if request.number == 0
        || ![&request.expected_head_sha, &request.expected_base_sha].iter().all(|sha| sha.len() == 40 && sha.bytes().all(|byte| byte.is_ascii_hexdigit()))
        || !matches!(request.method.as_str(), "merge" | "squash" | "rebase")
    {
        return Err("Invalid pull request merge target or method".to_string());
    }
    let (repository, root) = repository_for_root(Path::new(&request.root))?;
    let detail = read_detail_sync(&root, request.number)?;
    if detail.state != "OPEN" || detail.is_draft || detail.head_sha != request.expected_head_sha || detail.base_sha != request.expected_base_sha {
        return Err("This pull request changed, closed, or is still a draft. Refresh it before merging.".to_string());
    }
    if detail.mergeable != "MERGEABLE" {
        return Err("GitHub does not report this pull request as mergeable. Refresh it before trying again.".to_string());
    }
    let endpoint = format!("repos/{repository}/pulls/{}/merge", request.number);
    let bytes = serde_json::to_vec(&json!({"sha": request.expected_head_sha, "merge_method": request.method})).map_err(|error| error.to_string())?;
    let output = bounded_process::output_with_input(
        Command::new("gh").current_dir(&root).args(["api", "--method", "PUT", &endpoint, "--input", "-"]),
        "Merge GitHub pull request",
        bounded_process::NETWORK_COMMAND_TIMEOUT,
        Some(&bytes),
    ).map_err(|error| format!("Merge outcome is uncertain: {error}. Refresh the PR before trying again."))?;
    if !output.status.success() { return Err(format!("Merge failed: {}", brief_stderr(&output.stderr))); }
    let value: Value = serde_json::from_slice(&output.stdout).map_err(|_| "Merge outcome is uncertain. Refresh the PR before trying again.".to_string())?;
    if value.get("merged").and_then(Value::as_bool) != Some(true) {
        return Err("Merge outcome is uncertain. Refresh the PR before trying again.".to_string());
    }
    Ok(detail.url)
}

fn comment_sync(comment: GithubIssueComment) -> Result<String, String> {
    if comment.number == 0 || comment.body.trim().is_empty() || comment.body.len() > 20_000 {
        return Err("Invalid pull request comment".to_string());
    }
    let (repository, root) = repository_for_root(Path::new(&comment.root))?;
    let endpoint = format!("repos/{repository}/issues/{}/comments", comment.number);
    let bytes = serde_json::to_vec(&json!({"body": comment.body.trim()})).map_err(|error| error.to_string())?;
    let output = bounded_process::output_with_input(
        Command::new("gh").current_dir(&root).args(["api", "--method", "POST", &endpoint, "--input", "-"]),
        "Comment on GitHub pull request",
        bounded_process::NETWORK_COMMAND_TIMEOUT,
        Some(&bytes),
    ).map_err(|error| format!("Comment outcome is uncertain: {error}. Refresh the PR before trying again."))?;
    if !output.status.success() { return Err(format!("Comment was not confirmed: {}. Refresh the PR before trying again.", brief_stderr(&output.stderr))); }
    let value: Value = serde_json::from_slice(&output.stdout).map_err(|_| "Comment outcome is uncertain. Refresh the PR before trying again.".to_string())?;
    value.get("html_url").and_then(Value::as_str).map(str::to_string).ok_or_else(|| "Comment outcome is uncertain. Refresh the PR before trying again.".to_string())
}

fn reply_comment_sync(reply: GithubReviewReply) -> Result<String, String> {
    if reply.number == 0 || reply.comment_id == 0 || reply.body.trim().is_empty() || reply.body.len() > 10_000 {
        return Err("Invalid review reply".to_string());
    }
    let (repository, root) = repository_for_root(Path::new(&reply.root))?;
    let detail = read_detail_sync(&root, reply.number)?;
    if detail.state != "OPEN" || detail.head_sha != reply.expected_head_sha {
        return Err("This pull request changed or closed. Refresh it before replying.".to_string());
    }
    if !detail.comments.iter().any(|comment| comment.id == reply.comment_id.to_string() && comment.path.is_some() && comment.reply_to_id.is_none()) {
        return Err("That review thread is no longer available. Refresh the PR.".to_string());
    }
    let endpoint = format!("repos/{repository}/pulls/{}/comments/{}/replies", reply.number, reply.comment_id);
    let bytes = serde_json::to_vec(&json!({"body": reply.body.trim()})).map_err(|error| error.to_string())?;
    let output = bounded_process::output_with_input(
        Command::new("gh").current_dir(&root).args(["api", "--method", "POST", &endpoint, "--input", "-"]),
        "Reply to GitHub review comment",
        bounded_process::NETWORK_COMMAND_TIMEOUT,
        Some(&bytes),
    ).map_err(|error| format!("Reply outcome is uncertain: {error}. Refresh the PR before trying again."))?;
    if !output.status.success() { return Err(format!("Reply was not confirmed: {}. Refresh the PR before trying again.", brief_stderr(&output.stderr))); }
    let value: Value = serde_json::from_slice(&output.stdout).map_err(|_| "Reply outcome is uncertain. Refresh the PR before trying again.".to_string())?;
    value.get("html_url").and_then(Value::as_str).map(str::to_string).ok_or_else(|| "Reply outcome is uncertain. Refresh the PR before trying again.".to_string())
}

fn submit_review_sync(submission: GithubReviewSubmission) -> Result<String, String> {
    if submission.number == 0 || submission.expected_head_sha.len() != 40 || !submission.expected_head_sha.bytes().all(|byte| byte.is_ascii_hexdigit()) {
        return Err("Invalid pull request target".to_string());
    }
    if submission.body.trim().is_empty() {
        return Err("Write an overall review comment before submitting".to_string());
    }
    if submission.body.len() > 20_000 || submission.comments.len() > 20 {
        return Err("Review draft is too large".to_string());
    }
    let (repository, root) = repository_for_root(Path::new(&submission.root))?;
    let detail = read_detail_sync(&root, submission.number)?;
    if detail.state != "OPEN" || detail.head_sha != submission.expected_head_sha {
        return Err("This pull request changed or closed. Refresh and inspect it before submitting.".to_string());
    }
    let mut comments = Vec::new();
    for comment in &submission.comments {
        if comment.body.trim().is_empty() || comment.body.len() > 10_000 || comment.line == 0 || !matches!(comment.side.as_str(), "LEFT" | "RIGHT") || !detail.files.iter().any(|file| file.path == comment.path) {
            return Err("A line comment has an invalid target or body".to_string());
        }
        comments.push(json!({"path": comment.path, "line": comment.line, "side": comment.side, "body": comment.body.trim()}));
    }
    let payload = json!({"commit_id": detail.head_sha, "body": submission.body.trim(), "event": "COMMENT", "comments": comments});
    let bytes = serde_json::to_vec(&payload).map_err(|error| error.to_string())?;
    let endpoint = format!("repos/{repository}/pulls/{}/reviews", submission.number);
    let output = bounded_process::output_with_input(
        Command::new("gh").current_dir(&root).args(["api", "--method", "POST", &endpoint, "--input", "-"]),
        "Submit GitHub pull request review",
        bounded_process::NETWORK_COMMAND_TIMEOUT,
        Some(&bytes),
    ).map_err(|error| format!("Review outcome is uncertain: {error}. Refresh the PR before trying again."))?;
    if !output.status.success() { return Err(format!("Review was not confirmed: {}. Refresh the PR before trying again.", brief_stderr(&output.stderr))); }
    let value: Value = serde_json::from_slice(&output.stdout).map_err(|_| "Review outcome is uncertain. Refresh the PR before trying again.".to_string())?;
    value.get("html_url").and_then(Value::as_str).map(str::to_string).ok_or_else(|| "Review outcome is uncertain. Refresh the PR before trying again.".to_string())
}

fn read_file_versions_sync(query: GithubFileVersionQuery) -> Result<GithubFileVersions, String> {
    let (repository, root) = repository_for_root(Path::new(&query.root))?;
    if ![&query.base_sha, &query.head_sha].iter().all(|sha| sha.len() == 40 && sha.bytes().all(|byte| byte.is_ascii_hexdigit())) {
        return Err("Invalid pull request revision".to_string());
    }
    let path = encode_file_path(&query.path)?;
    let previous_path = encode_file_path(query.previous_path.as_deref().unwrap_or(&query.path))?;
    let original_content = if query.status == "added" {
        String::new()
    } else {
        read_raw_file(&root, &repository, &previous_path, &query.base_sha)?
    };
    let modified_content = if query.status == "removed" {
        String::new()
    } else {
        read_raw_file(&root, &repository, &path, &query.head_sha)?
    };
    Ok(GithubFileVersions { original_content, modified_content })
}

fn encode_file_path(path: &str) -> Result<String, String> {
    if path.is_empty() || path.starts_with('/') || path.split('/').any(|segment| segment.is_empty() || segment == "." || segment == "..") {
        return Err("Invalid pull request file path".to_string());
    }
    let mut encoded = String::new();
    for byte in path.bytes() {
        if byte.is_ascii_alphanumeric() || matches!(byte, b'-' | b'_' | b'.' | b'/' | b'~') {
            encoded.push(byte as char);
        } else {
            encoded.push_str(&format!("%{byte:02X}"));
        }
    }
    Ok(encoded)
}

fn read_raw_file(root: &Path, repository: &str, path: &str, sha: &str) -> Result<String, String> {
    let endpoint = format!("repos/{repository}/contents/{path}?ref={sha}");
    let output = bounded_process::output(
        Command::new("gh").current_dir(root).args(["api", "-H", "Accept: application/vnd.github.raw+json", &endpoint]),
        "GitHub pull request file",
        bounded_process::NETWORK_COMMAND_TIMEOUT,
    ).map_err(|error| format!("Could not read GitHub file: {error}"))?;
    if !output.status.success() { return Err(format!("GitHub file read failed: {}", brief_stderr(&output.stderr))); }
    if output.stdout.len() > MAX_RESPONSE_BYTES { return Err("Pull request file is too large for side-by-side diff".to_string()); }
    String::from_utf8(output.stdout).map_err(|_| "Pull request file is not UTF-8 text".to_string())
}

fn read_detail_sync(root: &Path, number: u64) -> Result<GithubPullRequestDetail, String> {
    if number == 0 { return Err("Pull request number must be positive".to_string()); }
    let (repository, canonical_root) = repository_for_root(root)?;
    let view = run_gh_json(
        &canonical_root,
        &["pr", "view", &number.to_string(), "-R", &repository, "--json", "number,title,body,url,author,headRefName,baseRefName,headRefOid,baseRefOid,state,isDraft,mergeable,reviewDecision,createdAt,mergedAt,mergedBy,closedAt,commits,comments,reviews,reviewRequests,statusCheckRollup"],
        "GitHub pull request detail",
    )?;
    if view.get("number").and_then(Value::as_u64) != Some(number) {
        return Err("GitHub returned the wrong pull request".to_string());
    }
    let files_url = format!("repos/{repository}/pulls/{number}/files?per_page=100&page=1");
    let files = run_gh_json(&canonical_root, &["api", &files_url], "GitHub changed files")?;
    let files = files.as_array().ok_or("GitHub returned invalid changed files")?;
    let comments_url = format!("repos/{repository}/pulls/{number}/comments?per_page=100&page=1");
    let review_comments = run_gh_json(&canonical_root, &["api", &comments_url], "GitHub review comments")?;
    let review_comments = review_comments.as_array().ok_or("GitHub returned invalid review comments")?;

    let mut comments = Vec::new();
    if let Some(issue_comments) = view.get("comments").and_then(Value::as_array) {
        for comment in issue_comments { comments.push(parse_comment(comment, false)); }
    }
    for comment in review_comments { comments.push(parse_comment(comment, true)); }
    let reviews = view.get("reviews").and_then(Value::as_array).map(|rows| rows.iter().map(|row| GithubReview {
        author: string_at(row, "/author/login"),
        body: string_at(row, "/body"),
        state: string_at(row, "/state"),
        submitted_at: string_at(row, "/submittedAt"),
    }).collect()).unwrap_or_default();
    let commits = view.get("commits").and_then(Value::as_array).map(|rows| rows.iter().map(|row| GithubCommit {
        oid: string_at(row, "/oid"),
        headline: string_at(row, "/messageHeadline"),
        author: string_at(row, "/authors/0/login"),
        committed_at: string_at(row, "/committedDate"),
    }).collect()).unwrap_or_default();
    let reviewers = view.get("reviewRequests").and_then(Value::as_array).map(|rows| rows.iter().filter_map(|row| row.get("login").or_else(|| row.get("name")).and_then(Value::as_str).map(str::to_string)).collect()).unwrap_or_default();
    let checks = view.get("statusCheckRollup").and_then(Value::as_array).map(|rows| rows.iter().map(|row| GithubCheck {
        name: string_at(row, "/name"),
        status: string_at(row, "/status"),
        conclusion: string_at(row, "/conclusion"),
        url: string_at(row, "/detailsUrl"),
    }).collect()).unwrap_or_default();
    let parsed_files = files.iter().filter_map(|file| Some(GithubFile {
        path: file.get("filename")?.as_str()?.to_string(),
        previous_path: file.get("previous_filename").and_then(Value::as_str).map(str::to_string),
        status: string_at(file, "/status"),
        additions: file.get("additions").and_then(Value::as_u64).unwrap_or(0),
        deletions: file.get("deletions").and_then(Value::as_u64).unwrap_or(0),
        patch: file.get("patch").and_then(Value::as_str).map(str::to_string),
    })).collect();
    Ok(GithubPullRequestDetail {
        repository,
        local_root: canonical_root.to_string_lossy().to_string(),
        number,
        title: string_at(&view, "/title"),
        body: string_at(&view, "/body"),
        url: string_at(&view, "/url"),
        author: string_at(&view, "/author/login"),
        head_branch: string_at(&view, "/headRefName"),
        base_branch: string_at(&view, "/baseRefName"),
        head_sha: string_at(&view, "/headRefOid"),
        base_sha: string_at(&view, "/baseRefOid"),
        state: string_at(&view, "/state"),
        is_draft: view.get("isDraft").and_then(Value::as_bool).unwrap_or(false),
        mergeable: string_at(&view, "/mergeable"),
        review_decision: string_at(&view, "/reviewDecision"),
        created_at: string_at(&view, "/createdAt"),
        merged_at: string_at(&view, "/mergedAt"),
        merged_by: string_at(&view, "/mergedBy/login"),
        closed_at: string_at(&view, "/closedAt"),
        commits,
        comments,
        reviews,
        reviewers,
        checks,
        files: parsed_files,
        more_files: files.len() == 100,
    })
}

fn parse_comment(value: &Value, inline: bool) -> GithubComment {
    let author = string_at(value, "/author/login");
    let created_at = string_at(value, "/createdAt");
    GithubComment {
        id: value.get("id").map(|id| id.as_str().map(str::to_string).unwrap_or_else(|| id.to_string())).unwrap_or_default(),
        author: if author.is_empty() { string_at(value, "/user/login") } else { author },
        body: string_at(value, "/body"),
        created_at: if created_at.is_empty() { string_at(value, "/created_at") } else { created_at },
        path: inline.then(|| string_at(value, "/path")),
        line: value.get("line").and_then(Value::as_u64).or_else(|| value.get("original_line").and_then(Value::as_u64)),
        side: value.get("side").and_then(Value::as_str).map(str::to_string),
        reply_to_id: value.get("in_reply_to_id").and_then(Value::as_u64),
    }
}

fn string_at(value: &Value, path: &str) -> String {
    value.pointer(path).and_then(Value::as_str).unwrap_or("").to_string()
}

fn run_gh_json(cwd: &Path, args: &[&str], description: &str) -> Result<Value, String> {
    let output = bounded_process::output(
        Command::new("gh").current_dir(cwd).args(args),
        description,
        bounded_process::NETWORK_COMMAND_TIMEOUT,
    ).map_err(|error| format!("Could not run {description}: {error}"))?;
    if !output.status.success() { return Err(format!("{description} failed: {}", brief_stderr(&output.stderr))); }
    if output.stdout.len() > MAX_RESPONSE_BYTES { return Err(format!("{description} returned too much data")); }
    serde_json::from_slice(&output.stdout).map_err(|error| format!("{description} returned invalid data: {error}"))
}

fn list_sync(query: GithubPullRequestQuery) -> Result<GithubPullRequestPage, String> {
    if query.roots.is_empty() || query.roots.len() > MAX_ROOTS {
        return Err("Choose between one and 32 known project folders".to_string());
    }
    if query.cursor.as_ref().is_some_and(|cursor| cursor.len() > 512) {
        return Err("Pull request page cursor is too long".to_string());
    }
    let search = query.search.as_deref().unwrap_or("").trim();
    if search.len() > 200 || search.contains(['\0', '\n', '\r']) {
        return Err("Pull request search must be one short line".to_string());
    }

    let mut repositories = BTreeMap::<String, PathBuf>::new();
    for root in &query.roots {
        let path = PathBuf::from(root);
        if let Ok((repository, canonical_root)) = repository_for_root(&path) {
            if query.project_filter.as_deref().is_some_and(|filter| filter != root) {
                continue;
            }
            repositories.entry(repository).or_insert(canonical_root);
        }
    }
    if repositories.is_empty() {
        return Err("No selected project has a GitHub origin remote".to_string());
    }

    let search_query = build_search_query(&repositories, search);
    let input = json!({
        "query": SEARCH_QUERY,
        "variables": {
            "query": search_query,
            "first": query.page_size.clamp(1, MAX_PAGE_SIZE),
            "after": query.cursor
        }
    });
    let bytes = serde_json::to_vec(&input).map_err(|error| error.to_string())?;
    let cwd = repositories.values().next().expect("nonempty repositories");
    let output = bounded_process::output_with_input(
        Command::new("gh").current_dir(cwd).args(["api", "graphql", "--input", "-"]),
        "GitHub pull request search",
        bounded_process::NETWORK_COMMAND_TIMEOUT,
        Some(&bytes),
    ).map_err(|error| format!("Could not search GitHub pull requests: {error}"))?;
    if !output.status.success() {
        return Err(format!("GitHub pull request search failed: {}", brief_stderr(&output.stderr)));
    }
    if output.stdout.len() > MAX_RESPONSE_BYTES {
        return Err("GitHub returned too much pull request data".to_string());
    }
    parse_search_response(&output.stdout, &repositories)
}

fn repository_for_root(root: &Path) -> Result<(String, PathBuf), String> {
    crate::validate_git_root(root)?;
    let canonical = root.canonicalize().map_err(|error| error.to_string())?;
    let output = bounded_process::output(
        Command::new("git").current_dir(&canonical).args(["remote", "get-url", "origin"]),
        "Read GitHub origin",
        bounded_process::LOCAL_COMMAND_TIMEOUT,
    ).map_err(|error| error.to_string())?;
    if !output.status.success() { return Err("No origin remote".to_string()); }
    let remote = String::from_utf8_lossy(&output.stdout);
    let repository = parse_github_remote(remote.trim())?;
    Ok((repository, canonical))
}

fn parse_github_remote(remote: &str) -> Result<String, String> {
    let path = remote.strip_prefix("git@github.com:")
        .or_else(|| remote.strip_prefix("ssh://git@github.com/"))
        .or_else(|| remote.strip_prefix("https://github.com/"))
        .ok_or_else(|| "Origin is not on github.com".to_string())?;
    let path = path.trim_end_matches('/').strip_suffix(".git").unwrap_or(path.trim_end_matches('/'));
    let parts: Vec<_> = path.split('/').collect();
    if parts.len() != 2 || parts.iter().any(|part| part.is_empty() || !part.chars().all(|ch| ch.is_ascii_alphanumeric() || matches!(ch, '-' | '_' | '.'))) {
        return Err("Origin is not a GitHub owner/repository path".to_string());
    }
    Ok(format!("{}/{}", parts[0], parts[1]))
}

/// The front end builds the state, scope, sort and text terms (GitHub search
/// syntax, so `head:`/`author:` work); the repositories stay server-side. A
/// `repo:` the person types only widens the search, and rows from repositories
/// outside the known projects are dropped when the response is parsed.
/// Advanced search joins terms with AND, so several repositories form one OR group.
fn build_search_query(repositories: &BTreeMap<String, PathBuf>, search: &str) -> String {
    let repos = repositories.keys().map(|name| format!("repo:{name}")).collect::<Vec<_>>();
    let mut terms = vec!["is:pr".to_string()];
    terms.push(if repos.len() > 1 { format!("({})", repos.join(" OR ")) } else { repos.join(" ") });
    if !search.is_empty() {
        terms.push(search.to_string());
    }
    terms.join(" ")
}

fn parse_search_response(bytes: &[u8], repositories: &BTreeMap<String, PathBuf>) -> Result<GithubPullRequestPage, String> {
    let value: Value = serde_json::from_slice(bytes).map_err(|error| format!("GitHub returned invalid search data: {error}"))?;
    if let Some(message) = value.pointer("/errors/0/message").and_then(Value::as_str) {
        return Err(format!("GitHub search failed: {message}"));
    }
    let search = value.pointer("/data/search").ok_or("GitHub returned no pull request search data")?;
    let nodes = search.get("nodes").and_then(Value::as_array).ok_or("GitHub returned no pull request rows")?;
    let mut items = Vec::with_capacity(nodes.len());
    for node in nodes {
        let Some(repository) = node.pointer("/repository/nameWithOwner").and_then(Value::as_str) else { continue };
        let Some(root) = repositories.get(repository) else { continue };
        let Some(number) = node.get("number").and_then(Value::as_u64) else { continue };
        let Some(url) = node.get("url").and_then(Value::as_str) else { continue };
        items.push(GithubPullRequestSummary {
            repository: repository.to_string(),
            local_root: root.to_string_lossy().to_string(),
            number,
            title: node.get("title").and_then(Value::as_str).unwrap_or("").to_string(),
            url: url.to_string(),
            is_draft: node.get("isDraft").and_then(Value::as_bool).unwrap_or(false),
            updated_at: node.get("updatedAt").and_then(Value::as_str).unwrap_or("").to_string(),
            state: node.get("state").and_then(Value::as_str).unwrap_or("").to_string(),
            author: node.pointer("/author/login").and_then(Value::as_str).unwrap_or("").to_string(),
            head_branch: node.get("headRefName").and_then(Value::as_str).unwrap_or("").to_string(),
            base_branch: node.get("baseRefName").and_then(Value::as_str).unwrap_or("").to_string(),
        });
    }
    let page = search.get("pageInfo").ok_or("GitHub returned no search page")?;
    Ok(GithubPullRequestPage {
        items,
        next_cursor: if page.get("hasNextPage").and_then(Value::as_bool) == Some(true) {
            page.get("endCursor").and_then(Value::as_str).map(str::to_string)
        } else { None },
        total_count: search.get("issueCount").and_then(Value::as_u64).unwrap_or(0),
    })
}

fn brief_stderr(bytes: &[u8]) -> String {
    String::from_utf8_lossy(bytes).trim().chars().take(400).collect()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn remote_names_only_github_repositories() {
        assert_eq!(parse_github_remote("git@github.com:owner/repo.git").unwrap(), "owner/repo");
        assert_eq!(parse_github_remote("https://github.com/owner/repo").unwrap(), "owner/repo");
        assert!(parse_github_remote("https://github.com.evil/owner/repo").is_err());
        assert!(parse_github_remote("https://github.com/owner/repo/extra").is_err());
    }

    #[test]
    fn search_keeps_repositories_server_side_and_passes_terms_through() {
        let repositories = BTreeMap::from([("owner/repo".to_string(), PathBuf::from("/repo"))]);
        assert_eq!(build_search_query(&repositories, "is:merged head:fix sort:updated-desc"), "is:pr repo:owner/repo is:merged head:fix sort:updated-desc");
        assert_eq!(build_search_query(&repositories, ""), "is:pr repo:owner/repo");
        let two = BTreeMap::from([("owner/one".to_string(), PathBuf::from("/one")), ("owner/two".to_string(), PathBuf::from("/two"))]);
        assert_eq!(build_search_query(&two, "is:open"), "is:pr (repo:owner/one OR repo:owner/two) is:open");
    }

    #[test]
    fn file_paths_are_encoded_without_changing_path_segments() {
        assert_eq!(encode_file_path("src/hello world#1.ts").unwrap(), "src/hello%20world%231.ts");
        assert!(encode_file_path("../secret").is_err());
        assert!(encode_file_path("/absolute").is_err());
    }

    #[test]
    fn comment_rejects_an_empty_or_oversized_body_before_accessing_github() {
        let comment = |number: u64, body: String| GithubIssueComment { root: "/nonexistent".to_string(), number, body };
        assert_eq!(comment_sync(comment(7, "  \n ".to_string())).unwrap_err(), "Invalid pull request comment");
        assert_eq!(comment_sync(comment(7, "x".repeat(20_001))).unwrap_err(), "Invalid pull request comment");
        assert_eq!(comment_sync(comment(0, "hello".to_string())).unwrap_err(), "Invalid pull request comment");
    }

    #[test]
    fn merge_rejects_invalid_target_before_accessing_github() {
        let request = GithubMergeRequest {
            root: "/nonexistent".to_string(),
            number: 1,
            expected_head_sha: "a".repeat(40),
            expected_base_sha: "b".repeat(40),
            method: "delete".to_string(),
        };
        assert_eq!(merge_sync(request).unwrap_err(), "Invalid pull request merge target or method");
    }
}
