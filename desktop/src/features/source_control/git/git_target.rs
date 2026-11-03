//! What a write was decided against: the session, the original source of the read and the checkout root that read
//! reported. It is captured with the read and carried unchanged, never taken again at click or reconcile.
use crate::core::socket::row_source::ExpectedRowSource;
use serde_json::{Value, json};

#[derive(Clone, Debug, Default, PartialEq)]
pub struct GitTarget {
    pub session: String,
    pub source: Option<ExpectedRowSource>,
    pub checkout_root: Option<String>,
}
impl GitTarget {
    pub fn new(session: impl Into<String>) -> Self {
        Self {
            session: session.into(),
            ..Self::default()
        }
    }
    /// A read carries the original source alone.
    pub fn read_args(&self, mut args: Value) -> Value {
        if let (Some(fields), Some(source)) = (args.as_object_mut(), &self.source) {
            fields.insert("expectedSource".into(), json!(source));
        }
        args
    }
    /// A write carries the source and the observed root next to its own fingerprint, head or path fields.
    pub fn write_args(&self, args: Value) -> Value {
        let mut args = self.read_args(args);
        if let (Some(fields), Some(root)) = (args.as_object_mut(), &self.checkout_root) {
            fields.insert("expectedCheckoutRoot".into(), json!(root));
        }
        args
    }
}
/// A project's pull request action: the project, the source it was read through, and what that read showed.
/// There is no session here, so nothing stands in for one.
#[derive(Clone, Debug, PartialEq)]
pub struct ProjectTarget {
    pub project_id: String,
    pub source: ExpectedRowSource,
    pub repository: String,
    pub checkout_root: String,
}
/// Where an operation runs. Its host, start, status and cancel all forward this unchanged.
#[derive(Clone, Debug, PartialEq)]
pub enum OperationScope {
    Session(GitTarget),
    Project(ProjectTarget),
}
impl OperationScope {
    pub fn session(&self) -> Option<String> {
        match self {
            Self::Session(target) => Some(target.session.clone()),
            Self::Project(_) => None,
        }
    }
    pub fn read_args(&self, mut args: Value) -> Value {
        match self {
            Self::Session(target) => target.read_args(args),
            Self::Project(target) => {
                if let Some(fields) = args.as_object_mut() {
                    fields.insert("projectId".into(), json!(target.project_id));
                    fields.insert("expectedSource".into(), json!(target.source));
                }
                args
            }
        }
    }
    /// Only a start names the repository and root; status and cancel go by the operation.
    pub fn write_args(&self, args: Value) -> Value {
        match self {
            Self::Session(target) => target.write_args(args),
            Self::Project(target) => {
                let mut args = self.read_args(args);
                if let Some(fields) = args.as_object_mut() {
                    fields.insert("expectedCheckoutRoot".into(), json!(target.checkout_root));
                    fields.insert("expectedRepository".into(), json!(target.repository));
                }
                args
            }
        }
    }
}
#[cfg(test)]
#[path = "git_target_tests.rs"]
mod tests;
