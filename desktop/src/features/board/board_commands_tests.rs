//! Each envelope follows plan 5's command table and serializes as the socket sends it.
use serde_json::json;

use super::*;
use crate::core::engine::workflow_items::RunItem;

fn run(machine: &str, id: &str, version: i64, state: &str) -> RunItem {
    RunItem::decode(
        json!({"entityKind":"run","machine":machine,"id":id,"version":version,"state":state,
        "activeStepIds":[],"paused":false,"reference":false,"ownerEpoch":4}),
    )
    .unwrap()
}

fn id() -> String {
    Uuid::new_v4().to_string()
}

fn receipt(applied: bool, error: Option<&str>) -> Result<CommandReceipt, EngineRefusal> {
    Ok(CommandReceipt {
        applied,
        late: false,
        error: error.map(str::to_owned),
        version: 8,
        seq: 1,
        epoch: 4,
    })
}

#[test]
fn terminal_retry_addresses_a_new_run_at_version_zero() {
    let source = run("remote", "old", 7, "failed");
    let new_id = id();
    let command = run_command(
        &source,
        &BoardRunAction::RetryFailed,
        id(),
        Some(new_id.clone()),
        "request-1".into(),
    )
    .unwrap();
    assert_eq!(command.entity_id, new_id);
    assert_eq!(command.expected_version, 0);
    assert_eq!(command.epoch, 0);
    assert_eq!(
        command.command,
        json!({"kind":"retry_failed","sourceRunId":"old","requestKey":"request-1"})
    );
    assert_eq!(command.machine.as_deref(), Some("remote"));
    let wire = serde_json::to_value(&command).unwrap();
    assert_eq!(wire["entityId"], json!(new_id));
    assert_eq!(wire["expectedVersion"], json!(0));
}

#[test]
fn rerun_is_its_own_kind_and_a_new_run_needs_a_real_id_and_key() {
    let source = run("m", "old", 7, "succeeded");
    let command = run_command(
        &source,
        &BoardRunAction::Rerun,
        id(),
        Some(id()),
        "k".into(),
    )
    .unwrap();
    assert_eq!(command.command["kind"], "rerun_workflow");
    assert!(run_command(&source, &BoardRunAction::Rerun, id(), None, "k".into()).is_err());
    assert!(
        run_command(
            &source,
            &BoardRunAction::Rerun,
            id(),
            Some("old".into()),
            "k".into()
        )
        .is_err()
    );
    assert!(
        run_command(
            &source,
            &BoardRunAction::Rerun,
            id(),
            Some(id()),
            String::new()
        )
        .is_err()
    );
}

#[test]
fn an_existing_run_action_targets_its_source_version_and_epoch() {
    let source = run("remote", "live", 3, "running");
    for (action, kind) in [
        (BoardRunAction::Pause, "pause_workflow"),
        (BoardRunAction::Resume, "resume_workflow"),
        (BoardRunAction::Cancel, "cancel_run"),
        (BoardRunAction::Keep, "keep_succeeded"),
    ] {
        let command = run_command(&source, &action, id(), None, String::new()).unwrap();
        assert_eq!(
            (
                command.entity_id.as_str(),
                command.expected_version,
                command.epoch
            ),
            ("live", 3, 4)
        );
        assert_eq!(command.command, json!({"kind": kind}));
        assert_eq!(command.machine.as_deref(), Some("remote"));
    }
    let grant = BoardRunAction::Grant {
        step_id: "s".into(),
        scope: "files".into(),
    };
    assert_eq!(
        run_command(&source, &grant, id(), None, String::new())
            .unwrap()
            .command,
        json!({"kind":"grant_scope","stepId":"s","scope":"files"})
    );
}

#[test]
fn a_run_with_no_known_owner_is_refused_before_it_is_sent() {
    let mut source = run("m", "live", 3, "running");
    source.owner_epoch = None;
    assert_eq!(
        run_command(&source, &BoardRunAction::Pause, id(), None, String::new())
            .unwrap_err()
            .code,
        "session_unavailable"
    );
}

#[test]
fn only_an_applied_new_run_moves_the_selection() {
    let new_id = id();
    assert_eq!(
        selected_after(
            &BoardRunAction::RetryFailed,
            Some(&new_id),
            &receipt(true, None)
        ),
        Some(new_id.clone())
    );
    assert_eq!(
        selected_after(
            &BoardRunAction::RetryFailed,
            Some(&new_id),
            &receipt(false, Some("version_conflict"))
        ),
        None
    );
    let unknown = Err(EngineRefusal {
        code: "outcome_unknown".into(),
        message: "?".into(),
    });
    assert_eq!(
        selected_after(&BoardRunAction::RetryFailed, Some(&new_id), &unknown),
        None
    );
    assert_eq!(
        selected_after(&BoardRunAction::Pause, None, &receipt(true, None)),
        None
    );
}

#[test]
fn keep_returns_its_command_id_as_the_undo_token_and_undo_sends_it_back() {
    let source = run("m", "failed", 3, "failed");
    let command_id = id();
    let keep = run_command(
        &source,
        &BoardRunAction::Keep,
        command_id.clone(),
        None,
        String::new(),
    )
    .unwrap();
    let token = undo_token(
        &BoardRunAction::Keep,
        &keep.command_id,
        &receipt(true, None),
    )
    .unwrap();
    assert_eq!(token, command_id);
    assert_eq!(
        undo_token(
            &BoardRunAction::Keep,
            &keep.command_id,
            &receipt(false, Some("invalid_transition"))
        ),
        None
    );
    let undo = run_command(
        &source,
        &BoardRunAction::Undo {
            token: token.clone(),
        },
        id(),
        None,
        String::new(),
    )
    .unwrap();
    assert_eq!(
        undo.command,
        json!({"kind":"undo_keep_succeeded","token":token})
    );
    assert_eq!(undo.entity_id, "failed");
    assert!(
        run_command(
            &source,
            &BoardRunAction::Undo {
                token: String::new()
            },
            id(),
            None,
            String::new()
        )
        .is_err()
    );
}

#[test]
fn every_refusal_reads_as_one_plain_line_and_success_as_none() {
    assert_eq!(refusal_line(&receipt(true, None)), None);
    assert_eq!(
        refusal_line(&receipt(false, Some("version_conflict"))).as_deref(),
        Some("This changed a moment ago.")
    );
    assert_eq!(
        refusal_line(&receipt(false, Some("stale_epoch"))).as_deref(),
        Some("This session moved to another machine; try again.")
    );
    let unknown = Err(EngineRefusal {
        code: "outcome_unknown".into(),
        message: "Timed out".into(),
    });
    assert_eq!(
        refusal_line(&unknown).as_deref(),
        Some("Not confirmed. Check the run before trying again.")
    );
    let other = Err(EngineRefusal {
        code: "disconnected".into(),
        message: "The backend is not connected.".into(),
    });
    assert_eq!(
        refusal_line(&other).as_deref(),
        Some("The backend is not connected.")
    );
}
