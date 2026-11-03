use super::{observe,CheckoutObservation,SessionKey};
use crate::core::engine::{EngineItem,EngineKey};
use serde_json::json;
fn row(checkout:&str,state:&str,operation:&str)->EngineItem {
    EngineItem{key:EngineKey{machine:"actual-owner".into(),kind:"session".into(),id:"accepted-session".into()},version:4,
        value:json!({"entityKind":"session","id":"accepted-session","machine":"actual-owner","version":4,
            "provider":"fixture-provider","model":null,"folder":"/actual/root","title":null,"parentId":null,"parentMachine":null,
            "runId":null,"state":state,"activityState":"idle","epoch":1,"holder":"local","projectId":"picked-project",
            "checkoutState":checkout,"checkoutOperationId":operation,"checkoutReason":null})}
}
#[test]
fn the_matching_checkout_and_start_must_both_be_ready_before_the_first_prompt() {
    let key=SessionKey{machine:"actual-owner".into(),id:"accepted-session".into()};
    assert_eq!(observe(&key,Some("picked-project"),Some("accepted-operation"),&row("pending","starting","accepted-operation")),CheckoutObservation::Waiting);
    assert_eq!(observe(&key,Some("picked-project"),Some("accepted-operation"),&row("ready","starting","accepted-operation")),CheckoutObservation::Waiting);
    assert_eq!(observe(&key,Some("picked-project"),Some("accepted-operation"),&row("ready","idle","accepted-operation")),CheckoutObservation::Ready);
    assert_eq!(observe(&key,Some("picked-project"),Some("accepted-operation"),&row("ready","idle","another-operation")),CheckoutObservation::Changed);
}
#[test]
fn refusal_keeps_the_actual_reason_and_a_different_owner_or_project_never_confirms_it() {
    let key=SessionKey{machine:"actual-owner".into(),id:"accepted-session".into()};
    let mut refused=row("refused","idle","accepted-operation");refused.value["checkoutReason"]=json!("tracked files would be overwritten");
    assert_eq!(observe(&key,Some("picked-project"),Some("accepted-operation"),&refused),CheckoutObservation::Refused(Some("tracked files would be overwritten".into())));
    let mut wrong=refused.clone();wrong.key.machine="replacement-owner".into();
    assert_eq!(observe(&key,Some("picked-project"),Some("accepted-operation"),&wrong),CheckoutObservation::Changed);
    assert_eq!(observe(&key,Some("other-project"),Some("accepted-operation"),&refused),CheckoutObservation::Changed);
    refused.value.as_object_mut().unwrap().remove("checkoutOperationId");
    assert_eq!(observe(&key,Some("picked-project"),Some("accepted-operation"),&refused),CheckoutObservation::Waiting);
}

#[test]
fn no_project_ready_idle_uses_explicit_null_binding_and_never_invents_a_checkout_operation() {
    let key=SessionKey{machine:"actual-owner".into(),id:"accepted-session".into()};
    let mut ordinary=row("ready","idle","unused");
    ordinary.value["projectId"]=json!(null);ordinary.value["checkoutOperationId"]=json!(null);
    assert_eq!(observe(&key,None,None,&ordinary),CheckoutObservation::Ready);
    ordinary.value["state"]=json!("starting");
    assert_eq!(observe(&key,None,None,&ordinary),CheckoutObservation::Waiting);
    ordinary.value["state"]=json!("idle");ordinary.value.as_object_mut().unwrap().remove("checkoutOperationId");
    assert_eq!(observe(&key,None,None,&ordinary),CheckoutObservation::Waiting);
}
