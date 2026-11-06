use super::source_resolution::{build_args, parse_reply};
use serde_json::json;
#[test]
fn current_route_discovery_always_sends_explicit_nullable_route_and_never_an_engine_alias() {
    assert_eq!(build_args(None).unwrap(), json!({"routeMachine":null}));
    assert_eq!(
        build_args(Some("saved exact name")).unwrap(),
        json!({"routeMachine":"saved exact name"})
    );
    assert!(build_args(Some("")).is_err());
    assert!(build_args(Some("  ")).is_err());
}
#[test]
fn local_discovery_requires_actual_welcomed_owner_and_a_closed_reply() {
    let value = json!({"machine":"actual-local","generation":0,"routeMachine":null});
    assert_eq!(
        parse_reply(value.clone(), None, Some("actual-local"))
            .unwrap()
            .machine,
        "actual-local"
    );
    assert!(parse_reply(value.clone(), None, None).is_err());
    assert!(parse_reply(value.clone(), None, Some("other")).is_err());
    let mut missing = value.clone();
    missing.as_object_mut().unwrap().remove("routeMachine");
    assert!(parse_reply(missing, None, Some("actual-local")).is_err());
    let mut extra = value;
    extra["fallback"] = json!(true);
    assert!(parse_reply(extra, None, Some("actual-local")).is_err());
}
#[test]
fn remote_discovery_preserves_the_exact_saved_route_and_positive_generation() {
    let value = json!({"machine":"actual-remote","generation":7,"routeMachine":"saved exact name"});
    let resolved = parse_reply(
        value.clone(),
        Some("saved exact name"),
        Some("actual-local"),
    )
    .unwrap();
    assert_eq!(resolved.generation, 7);
    assert_eq!(resolved.route_machine.as_deref(), Some("saved exact name"));
    assert!(parse_reply(value.clone(), Some("other"), Some("actual-local")).is_err());
    let mut zero = value.clone();
    zero["generation"] = json!(0);
    assert!(parse_reply(zero, Some("saved exact name"), Some("actual-local")).is_err());
    let mut wrong = value;
    wrong["machine"] = json!("actual-local");
    assert!(parse_reply(wrong, Some("saved exact name"), Some("actual-local")).is_err());
}
