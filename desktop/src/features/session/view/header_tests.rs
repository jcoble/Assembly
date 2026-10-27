use serde_json::json;

use super::{Header, read};

fn header(parent: Option<&str>) -> Header {
    let item = json!({ "sessionId": "s1", "agent": "claude", "repoUrl": "/r", "title": "Fix it",
        "machine": null, "parentSessionId": parent, "acpSessionId": null, "updatedAt": 1 });
    Header {
        item: read(item),
        _feed: None,
    }
}

// A helper's header links its parent, and the pane draws no composer for it.
// Pins: g0-session#20
#[test]
fn a_helper_session_is_known_by_its_parent() {
    assert!(header(Some("p1")).is_helper());
    assert!(!header(None).is_helper());
    assert!(
        !Header::default().is_helper(),
        "nothing shown yet is no helper"
    );
}
