use crate::core::engine::{EngineItem,SessionKey,engine_items::SessionItem};
use serde_json::Value;

/// Projection of one actual held Engine row. The caller also owns the captured connection cut and scope lease.
#[derive(Debug,PartialEq)]
pub enum CheckoutObservation { Waiting, Ready, Refused(Option<String>), Changed }
pub fn observe(key:&SessionKey,project:Option<&str>,operation:Option<&str>,item:&EngineItem)->CheckoutObservation {
    if item.key.machine!=key.machine||item.key.id!=key.id||item.key.kind!="session" {return CheckoutObservation::Changed;}
    let Some(session)=SessionItem::of(item) else{return CheckoutObservation::Waiting;};
    let Some(actual_project)=item.value.get("projectId") else{return CheckoutObservation::Waiting;};
    if !matches_nullable(actual_project,project)||item.value["holder"]!="local" {return CheckoutObservation::Changed;}
    let Some(actual_operation)=item.value.get("checkoutOperationId") else{return CheckoutObservation::Waiting;};
    if !matches_nullable(actual_operation,operation) {return CheckoutObservation::Changed;}
    match item.value["checkoutState"].as_str() {
        Some("refused")=>CheckoutObservation::Refused(item.value["checkoutReason"].as_str().map(String::from)),
        Some("ready") if session.state=="idle"&&session.activity_state=="idle"=>CheckoutObservation::Ready,
        _=>CheckoutObservation::Waiting,
    }
}
fn matches_nullable(value:&Value,expected:Option<&str>)->bool {
    match expected {Some(expected)=>value.as_str()==Some(expected),None=>value.is_null()}
}
#[cfg(test)]
#[path="checkout_wait_tests.rs"]
mod tests;
