use super::{StartRequest,CheckoutIntent,ProjectRecord,ExpectedRowSource};
use serde_json::json;
fn source(route:Option<&str>)->ExpectedRowSource {ExpectedRowSource{machine:"actual-owner".into(),generation:if route.is_some(){7}else{0},route_machine:route.map(String::from)}}
fn project(id:&str,machine:&str)->ProjectRecord {
    serde_json::from_value(json!({"id":id,"machine":machine,"rootPath":"/actual/project","title":"same title","repoKey":null,
        "pinnedAt":null,"removedAt":null,"createdAt":1,"lastUsedAt":null})).unwrap()
}
#[test]
fn retries_keep_the_original_client_id_project_copy_checkout_and_provider_choices() {
    let actual=project("remote-copy","Workbox Test");
    let choices=json!({"targetId":"advertised-target","choices":{"model":"observed-model","effort":"observed-effort"}});
    let request=StartRequest::capture("advertised-agent".into(),Some(actual),Some("Workbox Test".into()),Some(CheckoutIntent::Current),source(Some("Workbox Test")),choices.clone()).unwrap();
    let first=request.arguments();let retry=request.arguments();
    assert_eq!(first,retry);assert_eq!(first["projectId"],"remote-copy");assert_eq!(first["machine"],"Workbox Test");
    assert_eq!(first["checkout"],json!({"kind":"current"}));assert_eq!(first["initialConfig"],choices);
    assert!(!first["clientId"].as_str().unwrap().is_empty());
    assert_eq!(request.by_client_arguments(),json!({"clientId":first["clientId"],"machine":"Workbox Test","expectedSource":source(Some("Workbox Test"))}));
    assert!(first.get("repoUrl").is_none());assert!(first.get("workingFolder").is_none());
}
#[test]
fn no_project_supplies_neither_a_root_nor_a_checkout_and_wrong_machine_cannot_retarget_a_project() {
    let none=StartRequest::capture("actual-agent".into(),None,None,None,source(None),json!({"targetId":"actual-target","choices":{}})).unwrap();
    assert!(none.arguments()["projectId"].is_null());assert!(none.arguments()["checkout"].is_null());
    assert!(none.arguments()["machine"].is_null());
    assert!(StartRequest::capture("actual-agent".into(),Some(project("one","Workbox Test")),None,Some(CheckoutIntent::Current),source(None),json!({})).is_err());
    assert!(StartRequest::capture("actual-agent".into(),None,None,Some(CheckoutIntent::Current),source(None),json!({})).is_err());
}

#[test]
fn original_source_is_required_for_both_create_and_reconciliation_and_route_cannot_change() {
    let original=source(Some("Workbox Test"));
    let request=StartRequest::capture("agent".into(),None,Some("Workbox Test".into()),None,original.clone(),json!({})).unwrap();
    assert_eq!(request.arguments()["expectedSource"],json!(original));
    assert_eq!(request.by_client_arguments()["expectedSource"],request.arguments()["expectedSource"]);
    assert!(StartRequest::capture("agent".into(),None,None,None,source(Some("Workbox Test")),json!({})).is_err());
    let mut invalid=source(None);invalid.generation=8;
    assert!(StartRequest::capture("agent".into(),None,None,None,invalid,json!({})).is_err());
}
