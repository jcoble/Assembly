//! Files and Git operations run on the machine that owns the workspace.
use std::path::PathBuf;
use serde::{de::DeserializeOwned, Serialize};
use serde_json::Value;
use crate::git::commands as git;
use crate::git::workspace as changes;

fn arg<T: DeserializeOwned>(args: &Value, name: &str) -> Result<T, String> {
    serde_json::from_value(args.get(name).cloned().unwrap_or(Value::Null))
        .map_err(|error| format!("Invalid workspace {name}: {error}"))
}
fn path(args: &Value, name: &str) -> Result<PathBuf, String> {
    let value: String = arg(args, name)?;
    if !value.starts_with('/') || value.contains('\0') { return Err(format!("Workspace {name} must be an absolute path")); }
    Ok(PathBuf::from(value))
}
fn json(value: impl Serialize) -> Result<Value, String> {
    serde_json::to_value(value).map_err(|error| error.to_string())
}

pub(super) async fn execute(operation: String, args: Value) -> Result<Value, String> {
    // Both accept `~`, so they run before the absolute-path check below and
    // apply that check themselves after expanding it.
    if operation == "list_folders" || operation == "inspect_project_folder" {
        let requested: String = arg(&args, "path")?;
        return tokio::task::spawn_blocking(move || match operation.as_str() {
            "list_folders" => json(crate::project_folders::list_folders_sync(&requested)?),
            _ => json(crate::project_folders::inspect_project_folder_sync(&requested)?),
        })
        .await
        .map_err(|error| error.to_string())?;
    }
    for key in ["root", "path", "directory", "source", "target"] {
        if args.get(key).is_some() { path(&args, key)?; }
    }
    // Reuse the same Git implementations as the local Tauri commands.
    match operation.as_str() {
        "discard_git_paths" => return json(changes::discard_git_paths(arg(&args, "root")?, arg(&args, "paths")?).await?),
        "discard_all_git_changes" => return json(changes::discard_all_git_changes(arg(&args, "root")?, arg(&args, "includeUntracked")?).await?),
        "list_git_branches" => return json(changes::list_git_branches(arg(&args, "root")?).await?),
        "create_git_branch" => return json(changes::create_git_branch(arg(&args, "root")?, arg(&args, "name")?, arg(&args, "checkout")?).await?),
        "switch_git_branch" => return json(changes::switch_git_branch(arg(&args, "root")?, arg(&args, "name")?).await?),
        "stash_git_changes" => return json(changes::stash_git_changes(arg(&args, "root")?, arg(&args, "includeUntracked")?, arg(&args, "message")?).await?),
        "pop_git_stash" => return json(changes::pop_git_stash(arg(&args, "root")?, arg(&args, "index")?).await?),
        "list_git_stashes" => return json(changes::list_git_stashes(arg(&args, "root")?).await?),
        "amend_git_commit" => return json(changes::amend_git_commit(arg(&args, "root")?, arg(&args, "message")?).await?),
        "project_git_status" => return json(git::project_git_status(path(&args, "root")?.to_string_lossy().into()).await?),
        "read_source_git_diff" => return json(git::read_source_git_diff(arg(&args, "root")?, arg(&args, "path")?).await?),
        "read_git_branch_diff" => return json(git::read_git_branch_diff(arg(&args, "root")?).await?),
        "stage_git_paths" => return json(git::stage_git_paths(arg(&args, "root")?, arg(&args, "paths")?).await?),
        "unstage_git_paths" => return json(git::unstage_git_paths(arg(&args, "root")?, arg(&args, "paths")?).await?),
        "commit_git_repository" => return json(git::commit_git_repository(arg(&args, "root")?, arg(&args, "message")?).await?),
        "fetch_git_repository" => return json(git::fetch_git_repository(arg(&args, "root")?).await?),
        "pull_git_repository" => return json(git::pull_git_repository(arg(&args, "root")?).await?),
        "push_git_repository" => return json(git::push_git_repository(arg(&args, "root")?).await?),
        // `url`, not `source`: a URL is not an absolute path. Out of the 14-second tier below.
        "workspace_git_clone" => {
            let (url, folder): (String, PathBuf) = (arg(&args, "url")?, path(&args, "path")?);
            return json(tokio::task::spawn_blocking(move || crate::project_folders::git_clone(&url, &folder)).await.map_err(|e| e.to_string())??);
        }
        "add_git_remote" => return json(git::add_git_remote(arg(&args, "root")?, arg(&args, "name")?, arg(&args, "url")?).await?),
        "publish_git_repository" => return json(git::publish_git_repository(arg(&args, "root")?, arg(&args, "expectedBranch")?, arg(&args, "remote")?).await?),
        "read_git_commit_history" => return json(git::read_git_commit_history(arg(&args, "root")?, arg(&args, "cursor")?, arg(&args, "relativePath")?).await?),
        "read_git_commit_files" => return json(git::read_git_commit_files(arg(&args, "root")?, arg(&args, "sha")?).await?),
        "read_git_commit_file_diff" => return json(git::read_git_commit_file_diff(arg(&args, "root")?, arg(&args, "sha")?, arg(&args, "relativePath")?).await?),
        "list_project_worktrees" => return json(git::list_project_worktrees(arg(&args, "root")?).await?),
        "list_repository_checkouts" => return json(git::list_repository_checkouts(arg(&args, "roots")?).await?),
        "remove_project_worktree" => return json(git::remove_project_worktree(arg(&args, "root")?, arg(&args, "path")?, arg(&args, "force")?).await?),
        "archive_project_worktree" => return json(git::archive_project_worktree(arg(&args, "root")?, arg(&args, "path")?).await?),
        "project_root_is_bare" => return json(crate::project_folders::project_root_is_bare(arg(&args, "root")?).await?),
        // Hosted PR commands run gh here, with the same stale-head guards as the Mac.
        "list_github_pull_requests" => return json(crate::git::github::list_github_pull_requests(arg(&args, "query")?).await?),
        "read_github_pull_request" => return json(crate::git::github::read_github_pull_request(arg(&args, "root")?, arg(&args, "number")?).await?),
        "read_github_pull_request_file" => return json(crate::git::github::read_github_pull_request_file(arg(&args, "query")?).await?),
        "submit_github_pull_request_review" => return json(crate::git::github::submit_github_pull_request_review(arg(&args, "submission")?).await?),
        "reply_github_pull_request_comment" => return json(crate::git::github::reply_github_pull_request_comment(arg(&args, "reply")?).await?),
        "comment_github_pull_request" => return json(crate::git::github::comment_github_pull_request(arg(&args, "comment")?).await?),
        "merge_github_pull_request" => return json(crate::git::github::merge_github_pull_request(arg(&args, "request")?).await?),
        _ => {}
    }
    tokio::task::spawn_blocking(move || {
        let cancellation = crate::files::discovery::SourceScanCancellation::until(std::time::Instant::now() + std::time::Duration::from_secs(14));
        match operation.as_str() {
            "validate_project_root" => json(crate::files::discovery::root::validate_project_root_sync_while(path(&args, "path")?, || cancellation.ensure_active().is_ok())),
            "list_source_directory" => json(crate::files::discovery::walk::list_source_directory_sync_with_cancellation(path(&args, "root")?, path(&args, "directory")?, arg::<Option<bool>>(&args, "includeExcluded")?.unwrap_or(false), cancellation)?),
            "list_source_files" => json(crate::files::discovery::walk::list_source_files_sync_with_cancellation(path(&args, "root")?, arg::<Option<usize>>(&args, "limit")?.unwrap_or(crate::files::discovery::DEFAULT_SOURCE_LIST_LIMIT), arg(&args, "query")?, cancellation)?),
            "search_source_tree" => json(crate::files::discovery::search::search_source_tree_sync(path(&args, "root")?, arg(&args, "query")?, arg(&args, "pageSize")?, arg(&args, "cursor")?, arg::<Option<bool>>(&args, "includeExcluded")?.unwrap_or(false), cancellation)?),
            "find_source_definitions_in_root" => json(crate::files::discovery::symbols::find_source_definitions_in_root_sync(path(&args, "root")?, arg(&args, "symbolName")?, arg(&args, "limit")?)?),
            "find_source_references_in_root" => json(crate::files::discovery::references::find_source_references_in_root_sync(path(&args, "root")?, arg(&args, "symbolName")?, arg(&args, "limit")?)?),
            "read_source_file" => json(Some(crate::files::io::read_source_file_sync(path(&args, "path")?)?)),
            "read_source_image" => json(crate::files::io::read_source_image_sync(path(&args, "path")?)?),
            "write_source_file" => json(crate::files::io::write_source_file_sync(path(&args, "path")?, arg(&args, "content")?, arg(&args, "expectedRevision")?)?),
            "workspace_exists" => json(path(&args, "path")?.try_exists().map_err(|e| e.to_string())?),
            "workspace_mkdir" => { std::fs::create_dir(path(&args, "path")?).map_err(|e| e.to_string())?; json(()) },
            "workspace_git_init" => json(crate::project_folders::git_init(&path(&args, "path")?)?),
            "workspace_rename" => { std::fs::rename(path(&args, "source")?, path(&args, "target")?).map_err(|e| e.to_string())?; json(()) },
            "workspace_copy_file" => { std::fs::copy(path(&args, "source")?, path(&args, "target")?).map_err(|e| e.to_string())?; json(()) },
            "workspace_write_text" => { std::fs::write(path(&args, "path")?, arg::<String>(&args, "content")?).map_err(|e| e.to_string())?; json(()) },
            "workspace_read_dir" => {
                let entries = std::fs::read_dir(path(&args, "path")?).map_err(|e| e.to_string())?
                    .map(|entry| { let entry = entry.map_err(|e| e.to_string())?; let kind = entry.file_type().map_err(|e| e.to_string())?;
                        Ok(serde_json::json!({"name": entry.file_name().to_string_lossy(), "isDirectory": kind.is_dir(), "isFile": kind.is_file(), "isSymlink": kind.is_symlink()}))
                    }).collect::<Result<Vec<_>, String>>()?;
                json(entries)
            }
            "move_to_trash" => { trash::delete(path(&args, "path")?).map_err(|e| e.to_string())?; json(()) },
            _ => Err(format!("{operation} is not available for remote workspaces")),
        }
    }).await.map_err(|error| error.to_string())?
}

#[cfg(test)]
mod tests {
    use super::*;
    #[tokio::test]
    async fn remote_workspace_plain_text_definitions_and_references() {
        let root = std::env::temp_dir().join(format!("assembly-lookup-{}", uuid::Uuid::new_v4()));
        std::fs::create_dir(&root).unwrap();
        std::fs::write(root.join("Widget.cs"), "public class Widget {}\n").unwrap();
        std::fs::write(root.join("Usage.cs"), "public class Usage {\n    Widget value;\n}\n").unwrap();
        let args = serde_json::json!({"root": root, "symbolName": "Widget", "limit": 10});
        let definitions = execute("find_source_definitions_in_root".into(), args.clone()).await.unwrap();
        let references = execute("find_source_references_in_root".into(), args.clone()).await.unwrap();
        assert_eq!(definitions.as_array().unwrap().len(), 1);
        assert_eq!(definitions[0]["path"], root.join("Widget.cs").to_string_lossy().as_ref());
        assert_eq!(definitions[0]["line"], 1);
        let references = references.as_array().unwrap();
        assert_eq!(references.len(), 2);
        assert!(references.iter().any(|target| target["path"] == root.join("Usage.cs").to_string_lossy().as_ref() && target["line"] == 2));
        for operation in ["find_source_definitions_in_root", "find_source_references_in_root"] {
            for limit in [0, 1] {
                let mut bounded = args.clone();
                bounded["limit"] = serde_json::json!(limit);
                assert_eq!(execute(operation.into(), bounded).await.unwrap().as_array().unwrap().len(), limit);
            }
        }
        std::fs::remove_dir_all(root).unwrap();
    }
    #[tokio::test]
    async fn remote_workspace_reads_writes_and_git_use_requested_root() {
        let root = std::env::temp_dir().join(format!("assembly-workspace-{}", uuid::Uuid::new_v4()));
        std::fs::create_dir(&root).unwrap();
        std::process::Command::new("git")
            .args(["init", "-b", "main"])
            .current_dir(&root)
            .output()
            .unwrap();
        let file = root.join("hello.txt");
        let run = async {
            execute("workspace_write_text".into(), serde_json::json!({"path": file, "content": "remote content"})).await.unwrap();
            let read = execute("read_source_file".into(), serde_json::json!({"path": file})).await.unwrap();
            assert_eq!(read["content"], "remote content");
            let saved = execute("write_source_file".into(), serde_json::json!({
                "path": file,
                "content": "updated remote content",
                "expectedRevision": read["revision"]
            })).await.unwrap();
            assert_eq!(saved["content"], "updated remote content");
            let listing = execute("list_source_directory".into(), serde_json::json!({"root": root, "directory": root})).await.unwrap();
            assert!(listing.as_array().unwrap().iter().any(|entry| entry["path"] == file.to_string_lossy().as_ref()));
            let status = execute("project_git_status".into(), serde_json::json!({"root": root})).await.unwrap();
            assert!(status.to_string().contains("hello.txt"));
        };
        run.await;
        std::fs::remove_dir_all(root).unwrap();
    }
    #[tokio::test]
    async fn remote_workspace_git_init_and_clone() {
        let root = std::env::temp_dir().join(format!("assembly-new-project-{}", uuid::Uuid::new_v4()));
        let made = root.join("made");
        let copy = root.join("copy");
        std::fs::create_dir_all(&made).unwrap();
        execute("workspace_git_init".into(), serde_json::json!({"path": made})).await.unwrap();
        execute("workspace_git_clone".into(), serde_json::json!({"url": made, "path": copy})).await.unwrap();
        for folder in [&made, &copy] {
            assert!(crate::project_folders::inspect_project_folder_sync(&folder.to_string_lossy()).unwrap().is_git, "{folder:?}");
        }
        std::fs::remove_dir_all(root).unwrap();
    }
    #[tokio::test]
    async fn unsupported_and_relative_workspace_requests_are_rejected() {
        assert!(execute("run_terminal_command".into(), serde_json::json!({"root": "/tmp"})).await.unwrap_err().contains("not available"));
        assert!(execute("workspace_write_text".into(), serde_json::json!({"path": "relative", "content": "no"})).await.unwrap_err().contains("absolute"));
    }
    #[tokio::test]
    async fn github_requests_reach_the_hosted_pr_validation() {
        let query = serde_json::json!({"query": {"roots": [], "mode": "open", "pageSize": 30}});
        assert!(execute("list_github_pull_requests".into(), query).await.unwrap_err().contains("between one and 32"));
        let merge = serde_json::json!({"request": {"root": "/tmp", "number": 1, "expectedHeadSha": "a".repeat(40), "expectedBaseSha": "b".repeat(40), "method": "delete"}});
        assert_eq!(execute("merge_github_pull_request".into(), merge).await.unwrap_err(), "Invalid pull request merge target or method");
    }
}
