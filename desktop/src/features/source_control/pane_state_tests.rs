use super::{history_pane::HistoryPane, pull_requests::pr_pane::PrPane};
use crate::features::changes::changes_pane::ChangesPane;
use gpui_kit::component::dock::{PanelInfo, PanelState};
use serde_json::{Value, json};
fn state(name: &str, info: Value) -> PanelState {
    PanelState {
        panel_name: name.into(),
        children: vec![],
        info: PanelInfo::panel(info),
    }
}
#[test]
fn restored_git_tabs_keep_actual_identity_and_refuse_a_different_session_before_rebuild() {
    let history = state(
        "Git history",
        json!({"session":"session","path":"/actual/root/ü.rs"}),
    );
    assert_eq!(
        HistoryPane::workspace_key(&history, "session").unwrap(),
        "git-history:session"
    );
    assert!(HistoryPane::workspace_key(&history, "other").is_err());
    let changes = state(
        "Changes",
        json!({"session":"session","sha":"a".repeat(40),"path":"ü.rs","title":"Commit title","split":true}),
    );
    assert_eq!(
        ChangesPane::workspace_key(&changes, "session").unwrap(),
        "Changes"
    );
    assert!(ChangesPane::workspace_key(&changes, "other").is_err());
    assert!(
        ChangesPane::workspace_key(
            &state(
                "Changes",
                json!({"session":"session","sha":"branch","path":"file"})
            ),
            "session"
        )
        .is_err()
    );
    assert!(
        HistoryPane::workspace_key(
            &state("Git history", json!({"session":"session","path":42})),
            "session"
        )
        .is_err()
    );
    assert!(
        ChangesPane::workspace_key(
            &state(
                "Changes",
                json!({"session":"session","path":"bad\u{0}path"})
            ),
            "session"
        )
        .is_err()
    );
}
#[test]
fn pr_global_queue_grants_no_session_target_and_a_session_target_must_match_its_workspace() {
    assert_eq!(
        PrPane::workspace_key(&state("Pull requests", json!({"session":null})), "session").unwrap(),
        "pull-requests"
    );
    assert_eq!(
        PrPane::workspace_key(
            &state("Pull requests", json!({"session":"session"})),
            "session"
        )
        .unwrap(),
        "pull-requests"
    );
    assert!(
        PrPane::workspace_key(
            &state("Pull requests", json!({"session":"other"})),
            "session"
        )
        .is_err()
    );
    assert!(
        PrPane::workspace_key(&state("Pull requests", json!({"session":7})), "session").is_err()
    );
    assert!(
        PrPane::workspace_key(
            &state("Source control", json!({"session":"session"})),
            "session"
        )
        .is_err()
    );
}
