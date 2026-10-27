//! `Notice` is how a failed run or a compaction reads: the provider's sentence lifted out of its JSON, and "the
//! conversation is full" said plainly. A cancelled run is the person's own doing, so it has none. Ported from
//! chat/notices.ts.

#[derive(Clone, Debug, PartialEq)]
pub struct Notice {
    pub label: String,
    pub message: String,
    pub hint: Option<String>,
}

impl Notice {
    pub fn of_error(_message: &str, _code: Option<&str>) -> Option<Notice> {
        todo!()
    }

    /// "Context compacted · 180k → 12k", or "Context compacted" without both counts.
    pub fn compaction(_pre_tokens: i64, _post_tokens: Option<i64>) -> String {
        todo!()
    }
}
