use super::BoardTaskResult;
use serde_json::json;

#[test]
fn board_task_steps_round_trip() {
    let step = json!({"index": 1, "text": "Retry the case", "tool": "test", "detail": "dotnet test",
        "state": "running", "badge": "high-impact", "explanation": "Second attempt", "progress": 40,
        "startedAt": "2026-10-29T12:00:00+00:00", "endedAt": null, "attempt": 2, "maxAttempts": 3,
        "questionKey": "scope", "requiredScope": "issues:create", "scopeGranted": false});
    let value = json!({"steps": [step.clone()], "questions": [], "feed": [],
        "nextBeforeSeq": null, "nextQuestionCursor": null});
    let result: BoardTaskResult = serde_json::from_value(value).expect("task with steps");
    assert_eq!(serde_json::to_value(&result.steps).unwrap(), json!([step]));
}
