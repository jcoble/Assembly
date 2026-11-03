//! Fetch, Pull, Push and Publish as the pane asks for them: the arguments from what the pane showed, or the reason the
//! action can't run. The Actions menu shows the same reason it would refuse with.
use super::git_status::GitStatus;
use serde_json::{Value, json};

pub fn request(
    action: &str,
    status: Option<&GitStatus>,
    remote: Option<&str>,
    remotes: &[String],
) -> Result<Value, String> {
    if action == "fetch" {
        return if remotes.is_empty() && !status.is_some_and(|status| status.has_upstream) {
            Err("no remote".into())
        } else {
            Ok(json!({}))
        };
    }
    let status = status.ok_or("Refresh Source control before synchronizing.")?;
    let branch = status.branch.as_ref().ok_or("detached HEAD")?;
    if status.head.is_none() {
        return Err("This branch has no commits.".into());
    }
    match action {
        "pull" | "push" if status.has_upstream => Ok(json!({"expectedBranch":branch})),
        "pull" | "push" => Err("no upstream: publish first".into()),
        "publish" => {
            let remote = remote
                .filter(|remote| remotes.iter().any(|known| known.as_str() == *remote))
                .ok_or("Choose a current repository remote.")?;
            Ok(json!({"expectedBranch":branch,"remote":remote}))
        }
        _ => Err("Unknown Git operation.".into()),
    }
}
#[cfg(test)]
#[path = "git_sync_tests.rs"]
mod tests;
