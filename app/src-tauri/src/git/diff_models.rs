//! Full text models for Monaco's native diff editor.
//!
//! The existing Git commands still own repository access and status. This
//! module only reads the two bounded text blobs the UI needs after those
//! commands have already validated the repository and relative path.

use std::path::Path;
use std::process::Command;

const MAX_DIFF_MODEL_BYTES: usize = 512 * 1024;

pub(crate) struct GitDiffModels {
    pub(crate) original_content: Option<String>,
    pub(crate) modified_content: Option<String>,
}

pub(crate) fn working_tree_models(
    root: &Path,
    relative_path: &str,
    worktree_path: &Path,
) -> Result<GitDiffModels, String> {
    Ok(GitDiffModels {
        original_content: read_revision_text(root, "HEAD", relative_path)?,
        modified_content: read_file_text(worktree_path)?,
    })
}

pub(crate) fn commit_models(
    root: &Path,
    sha: &str,
    relative_path: &str,
) -> Result<GitDiffModels, String> {
    Ok(GitDiffModels {
        original_content: read_revision_text(root, &format!("{sha}^"), relative_path)?,
        modified_content: read_revision_text(root, sha, relative_path)?,
    })
}

fn read_file_text(path: &Path) -> Result<Option<String>, String> {
    let bytes = match std::fs::read(path) {
        Ok(bytes) => bytes,
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => {
            return Ok(Some(String::new()))
        }
        Err(error) => return Err(format!("Could not read Git diff model from disk: {error}")),
    };
    Ok(bounded_text(bytes))
}

fn read_revision_text(
    root: &Path,
    revision: &str,
    relative_path: &str,
) -> Result<Option<String>, String> {
    let object = format!("{revision}:{relative_path}");
    let output = Command::new("git")
        .arg("-C")
        .arg(root)
        .args(["show", object.as_str()])
        .output()
        .map_err(|error| format!("Could not read Git diff revision: {error}"))?;

    // A new file has no HEAD blob; a deleted file has no blob in the selected
    // revision. In both cases the honest side of the diff is an empty model.
    if !output.status.success() {
        return Ok(Some(String::new()));
    }

    Ok(bounded_text(output.stdout))
}

fn bounded_text(bytes: Vec<u8>) -> Option<String> {
    if bytes.len() > MAX_DIFF_MODEL_BYTES || bytes.contains(&0) {
        return None;
    }
    String::from_utf8(bytes).ok()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn bounded_text_rejects_binary_and_oversized_models() {
        assert_eq!(
            bounded_text(b"hello\n".to_vec()),
            Some("hello\n".to_string())
        );
        assert_eq!(bounded_text(vec![b'a', 0, b'b']), None);
        assert_eq!(bounded_text(vec![b'a'; MAX_DIFF_MODEL_BYTES + 1]), None);
    }
}
