//! `OutFrame` is a frame the window sends (plan 5.1). `t` names it.

use serde::Serialize;
use serde_json::Value;

#[derive(Serialize)]
#[serde(tag = "t", rename_all = "lowercase")]
pub(super) enum OutFrame<'a> {
    Hello {
        token: &'a str,
        protocol: u32,
    },
    Call {
        id: u64,
        feature: &'a str,
        method: &'a str,
        args: &'a Value,
    },
    Cancel {
        id: u64,
    },
    /// `after` only on the `rows` topic: the rows after that seq.
    Sub {
        id: u64,
        topic: &'a str,
        #[serde(skip_serializing_if = "Option::is_none")]
        key: Option<&'a str>,
        #[serde(skip_serializing_if = "Option::is_none")]
        after: Option<i64>,
    },
    Unsub {
        id: u64,
    },
}
