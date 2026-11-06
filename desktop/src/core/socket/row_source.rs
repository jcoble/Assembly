//! Captured server-validated row source. This wire value is an assertion, never an ownership grant.
//! New navigation receives it only from sessions/open_target; it never refreshes a saved route by name.
use serde::{Deserialize, Serialize};
use serde_json::{Value, json};

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct ExpectedRowSource {
    pub machine: String,
    pub generation: u64,
    pub route_machine: Option<String>,
}

pub(crate) fn page_args(
    session: &str,
    before: i64,
    limit: usize,
    source: Option<&ExpectedRowSource>,
) -> Value {
    let mut args = json!({"session":session,"before":before,"limit":limit});
    if let Some(source) = source {
        args["expectedSource"] = json!(source);
    }
    args
}

pub(crate) fn page_key(session: &str, source: Option<&ExpectedRowSource>) -> String {
    match source {
        Some(source) => format!("rows/page:{}", json!([source, session])),
        None => format!("rows/page:{session}"),
    }
}
