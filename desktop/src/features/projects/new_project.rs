use serde::Deserialize;
use serde_json::{Value, json};
#[derive(Clone, Copy, Debug, PartialEq)]
pub enum Kind {
    Empty,
    Git,
    Clone,
}
impl Kind {
    pub const ALL: [Self; 3] = [Self::Empty, Self::Git, Self::Clone];
    pub fn label(self) -> &'static str {
        match self {
            Self::Empty => "Empty folder",
            Self::Git => "Empty folder with git",
            Self::Clone => "Copy of a repository",
        }
    }
    fn wire(self) -> &'static str {
        match self {
            Self::Empty => "empty",
            Self::Git => "git",
            Self::Clone => "clone",
        }
    }
}
#[derive(Clone)]
pub struct NewProjectInput {
    pub machine: String,
    pub parent: String,
    pub name: String,
    pub kind: Kind,
    pub source: String,
}
impl NewProjectInput {
    pub fn args(&self) -> Result<Value, String> {
        let name = self.name.trim();
        let parent = self.parent.trim();
        let source = self.source.trim();
        if self.machine.is_empty() {
            return Err("Choose a machine.".into());
        }
        if name.is_empty() || matches!(name, "." | "..") || name.contains(['/', '\\', '\0']) {
            return Err("Choose one folder name, without path separators.".into());
        }
        if parent.is_empty() {
            return Err("Choose a parent folder.".into());
        }
        if self.kind == Kind::Clone && source.is_empty() {
            return Err("A repository URL or path is required for clone.".into());
        }
        Ok(
            json!({"machine":self.machine,"parentPath":parent,"name":name,"kind":self.kind.wire(),"source":if self.kind==Kind::Clone{Some(source)}else{None}}),
        )
    }
}
pub use super::projects::ProjectRecord;
#[derive(Clone, Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct FolderInspection {
    pub root_path: String,
    pub title: String,
    pub repo_key: Option<String>,
    pub is_git: bool,
    pub common_dir: Option<String>,
    pub unborn: bool,
}
#[derive(Clone, Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct NewProjectResult {
    pub project: ProjectRecord,
    pub inspection: FolderInspection,
    pub already_existed: bool,
}
