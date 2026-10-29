use super::state_label;
use crate::features::session::rows::subagent::SubagentPayload;

fn subagent(state: &str, stop_reason: Option<&str>, error: Option<&str>) -> SubagentPayload {
    SubagentPayload {
        session_id: "acp-1".into(),
        title: "Look into it".into(),
        description: None,
        state: state.into(),
        stop_reason: stop_reason.map(String::from),
        error: error.map(String::from),
        can_stop: true,
        tool_call_id: None,
        helper: false,
    }
}

// Pins: g0-session#17
#[test]
fn a_subagent_says_whether_it_runs_finished_failed_or_was_stopped() {
    assert_eq!(state_label(&subagent("running", None, None)), "Running");
    assert_eq!(
        state_label(&subagent("requires_action", None, None)),
        "Waiting for you"
    );
    assert_eq!(state_label(&subagent("idle", None, None)), "Finished");
    assert_eq!(state_label(&subagent("idle", None, Some("boom"))), "Failed");
    assert_eq!(
        state_label(&subagent("idle", Some("cancelled"), None)),
        "Stopped"
    );
    assert_eq!(state_label(&subagent("idle", None, Some(""))), "Finished");
    assert_eq!(state_label(&subagent("paused", None, None)), "paused");
}
