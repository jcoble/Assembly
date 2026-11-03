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
#[cfg(test)]
#[path = "git_target_tests.rs"]
mod tests;
