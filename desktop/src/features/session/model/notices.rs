//! `Notice` is how a failed run reads: the provider's sentence lifted out of its JSON, and "the
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
}
