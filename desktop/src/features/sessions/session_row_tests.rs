use serde_json::json;

use super::SessionRow;
use crate::core::state::session_item::SessionItem;
use crate::features::sessions::presence::Presence;

fn session(active_run: Option<&str>) -> SessionItem {
    serde_json::from_value(
        json!({ "sessionId": "s1", "agent": "claude", "repoUrl": "/r", "title": null,
        "machine": null, "parentSessionId": null, "acpSessionId": null, "updatedAt": 1,
        "state": "idle", "activeRunId": active_run }),
    )
    .unwrap()
}

// Pins: g0-session#30
#[test]
fn a_working_session_has_a_still_dot() {
    assert_eq!(
        SessionRow::of(&session(Some("r1")), false, false).presence,
        Presence::Working
    );
    assert_eq!(
        SessionRow::of(&session(None), false, false).presence,
        Presence::Idle
    );
}

// Pins: sessions-list#11
#[test]
fn a_session_on_a_machine_that_isnt_answering_shows_offline_first() {
    let row = SessionRow::of(&session(Some("r1")), false, true);
    assert_eq!(row.presence, Presence::Offline);
    assert_eq!(row.presence.word(), Some("offline"));
}
