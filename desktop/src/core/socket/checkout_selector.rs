//! Picks a linked checkout for one read. The backend checks it against what it reports from `git/checkouts`; only a
//! successful answer there offers one, because an older owner would ignore the fields. Writes never carry it.
use serde_json::{Value, json};

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct CheckoutSelector {
    pub path: String,
    /// The session's own folder as `git/checkouts` reported it.
    pub expected_root: String,
}
impl CheckoutSelector {
    /// The session's own checkout sends neither field.
    pub fn applied(selector: Option<&Self>, mut args: Value) -> Value {
        if let (Some(selector), Some(fields)) = (selector, args.as_object_mut()) {
            fields.insert("checkoutPath".into(), json!(selector.path));
            fields.insert("expectedCheckoutRoot".into(), json!(selector.expected_root));
        }
        args
    }
    pub fn folder(&self) -> &str {
        folder_name(&self.path)
    }
}
pub fn folder_name(path: &str) -> &str {
    let path = path.trim_end_matches('/');
    path.rsplit('/').next().unwrap_or(path)
}
#[cfg(test)]
#[path = "tests/checkout_selector_tests.rs"]
mod tests;
