use super::*;

fn job(version: i64, state: JobState, phase: Phase) -> Job {
    Job {
        operation_id: "op".into(),
        version,
        machine: "test".into(),
        state,
        phase,
        bytes_done: 5,
        total_bytes: 10,
        error_code: None,
        result: None,
    }
}

fn result(commit: &str, machine: &str, engine: &str) -> JobResult {
    JobResult {
        commit: commit.into(),
        machine: machine.into(),
        engine: engine.into(),
    }
}

fn expected() -> Expected {
    Expected {
        operation_id: "op".into(),
        machine: "test".into(),
        engine: "engine".into(),
        commit: "abc".into(),
    }
}

fn facts(kind: &str, target: &str) -> BundleFacts {
    BundleFacts {
        sha256: "s".into(),
        bytes: 1,
        commit: "abc".into(),
        target: target.into(),
        protocol: 1,
        format: 5,
        build_kind: kind.into(),
    }
}

#[test]
fn the_job_reads_the_exact_wire_shape() {
    let job: Job = serde_json::from_value(serde_json::json!({
        "operationId": "op", "version": 3, "machine": "test", "state": "running", "phase": "uploading",
        "bytesDone": 5, "totalBytes": 10, "errorCode": null, "result": null
    }))
    .unwrap();
    assert_eq!(job.state, JobState::Running);
    assert_eq!(job.phase, Phase::Uploading);
}

#[test]
fn an_unknown_state_is_refused_rather_than_guessed() {
    let read: Result<Job, _> = serde_json::from_value(serde_json::json!({
        "operationId": "op", "version": 3, "machine": "test", "state": "finished", "phase": "uploading",
        "bytesDone": 5, "totalBytes": 10, "errorCode": null, "result": null
    }));
    assert!(read.is_err());
}

#[test]
fn only_a_newer_update_of_the_same_install_counts() {
    let current = job(3, JobState::Running, Phase::Uploading);
    assert!(Job::takes(None, &current, "op"));
    assert!(Job::takes(None, &current, "OP"));
    assert!(!Job::takes(None, &current, "other"));
    assert!(!Job::takes(
        Some(&current),
        &job(3, JobState::Succeeded, Phase::Connecting),
        "op"
    ));
    assert!(!Job::takes(
        Some(&current),
        &job(2, JobState::Running, Phase::Checking),
        "op"
    ));
    assert!(Job::takes(
        Some(&current),
        &job(4, JobState::Running, Phase::Validating),
        "op"
    ));
}

#[test]
fn cancel_stops_being_offered_once_the_backend_is_being_replaced() {
    for phase in [Phase::Checking, Phase::Uploading, Phase::Validating] {
        assert!(job(1, JobState::Running, phase).cancellable());
    }
    for phase in [Phase::Activating, Phase::Connecting] {
        assert!(!job(1, JobState::Running, phase).cancellable());
    }
    assert!(!job(1, JobState::Failed, Phase::Uploading).cancellable());
    assert!(!job(1, JobState::Uncertain, Phase::Checking).cancellable());
}

#[test]
fn a_success_needs_the_declared_commit_on_the_same_engine() {
    let mut done = job(9, JobState::Succeeded, Phase::Connecting);
    assert_eq!(done.ending(Some(&expected())), Some(Ending::Unproven));
    done.result = Some(result("abc", "test", "engine"));
    assert_eq!(
        done.ending(Some(&expected())),
        Some(Ending::Confirmed(result("abc", "test", "engine")))
    );
    done.result = Some(result("other", "test", "engine"));
    assert_eq!(done.ending(Some(&expected())), Some(Ending::Unproven));
    done.result = Some(result("abc", "test", "other-engine"));
    assert_eq!(done.ending(Some(&expected())), Some(Ending::Unproven));
    done.result = Some(result("abc", "elsewhere", "engine"));
    assert_eq!(done.ending(None), Some(Ending::Unproven));
    assert_eq!(
        job(9, JobState::Uncertain, Phase::Connecting).ending(None),
        None
    );
}

#[test]
fn only_a_development_bundle_for_the_test_platform_can_be_deployed() {
    assert_eq!(facts("development", TARGET).refusal(), None);
    assert!(facts("release", TARGET).refusal().is_some());
    assert!(facts("development", "osx-arm64").refusal().is_some());
}

#[test]
fn progress_stays_within_bounds_and_is_absent_without_a_total() {
    let mut running = job(1, JobState::Running, Phase::Uploading);
    assert_eq!(running.percent(), Some(50.));
    running.bytes_done = 20;
    assert_eq!(running.percent(), Some(100.));
    running.total_bytes = 0;
    assert_eq!(running.percent(), None);
}

#[test]
fn a_failure_says_the_backends_reason_and_an_unlisted_code_stays_general() {
    let mut failed = job(2, JobState::Failed, Phase::Validating);
    failed.error_code = Some("remote_busy".into());
    assert_eq!(state_words(&failed), error_words("remote_busy"));
    failed.error_code = Some("activation_refused".into());
    assert_eq!(
        state_words(&failed),
        "The machine refused the restart before anything changed."
    );
    failed.error_code = Some("anything else".into());
    assert_eq!(state_words(&failed), "The deploy failed.");
    let mut unsure = job(2, JobState::Uncertain, Phase::Connecting);
    assert!(state_words(&unsure).starts_with("The outcome is unknown."));
    unsure.error_code = Some("activation_uncertain".into());
    assert!(state_words(&unsure).starts_with("The outcome is unknown."));
}
