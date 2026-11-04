//! `git/branches` as the pane reads it: the local branches, newest first, the remotes and the default branch.
use serde::Deserialize;

#[derive(Clone, Debug, Default, Deserialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct GitBranch {
    pub name: String,
    #[serde(default)]
    pub current: bool,
    #[serde(default)]
    pub upstream: Option<String>,
    #[serde(default)]
    pub worktree_folder: Option<String>,
    #[serde(default)]
    pub subject: String,
}
#[derive(Clone, Debug, Default, Deserialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct GitBranchList {
    #[serde(default)]
    pub branches: Vec<GitBranch>,
    #[serde(default)]
    pub remotes: Vec<String>,
    #[serde(default)]
    pub default_branch: Option<String>,
}
