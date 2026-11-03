//! Canonical metadata/answer semantics, independent of widget layout or provider execution.
use serde_json::{Value,json};
use crate::core::engine::{EngineItem,EngineKey,engine_items::NeedItem,EngineRefusal};
use super::need_row::{typed_answer,failure_state,NeedRowState};
fn need(mode:Option<&str>)->NeedItem {
    let mut value=json!({"entityKind":"needs_me","machine":"owner","id":"need","version":3,"sessionId":"session","kind":"question","state":"open","text":"Actual question","optionsJson":"[\"legacy\"]"});
    if let Some(mode)=mode {value["inputMode"]=json!(mode);value["options"]=json!([{"id":"actual:z","label":"First actual label","kind":"allow_once"},{"id":"actual:a","label":"Second actual label","kind":"reject_once"}]);value["freeText"]=json!(true);}
    NeedItem::of(&EngineItem{key:EngineKey{machine:"owner".into(),kind:"needs_me".into(),id:"need".into()},version:3,value}).unwrap()
}
#[test]
fn absent_typed_presentation_never_infers_controls_from_raw_options_or_question_kind() {
    assert!(typed_answer(&need(None),Value::String("legacy".into())).is_err());
}
#[test]
fn typed_permission_and_choice_keep_actual_ids_and_distinct_existing_wire_values() {
    assert_eq!(typed_answer(&need(Some("permission")),json!({"optionId":"actual:z"})).unwrap(),json!({"optionId":"actual:z"}));
    assert!(typed_answer(&need(Some("permission")),json!({"optionId":"invented"})).is_err());
    assert_eq!(typed_answer(&need(Some("choice")),json!("actual:a")).unwrap(),json!("actual:a"));
    assert!(typed_answer(&need(Some("choice")),json!({"optionId":"actual:a"})).is_err());
}
#[test]
fn explicit_text_keeps_exact_whitespace_and_refuses_over_ui_budget_without_truncating() {
    assert_eq!(typed_answer(&need(Some("text")),json!("  Exact text\n")).unwrap(),json!("  Exact text\n"));
    assert!(typed_answer(&need(Some("text")),json!("x".repeat(601))).is_err());
}
#[test]
fn typed_refusals_and_unknown_outcomes_do_not_become_success_or_automatic_retries() {
    assert!(matches!(failure_state(&EngineRefusal{code:"stale_epoch".into(),message:"actual refusal".into()}),NeedRowState::Stale{..}));
    assert!(matches!(failure_state(&EngineRefusal{code:"already_resolved".into(),message:"actual refusal".into()}),NeedRowState::Refused{..}));
    assert!(matches!(failure_state(&EngineRefusal{code:"outcome_unknown".into(),message:"actual unknown outcome".into()}),NeedRowState::Failed{..}));
}

#[test]
fn need_controls_only_join_the_actual_machine_wide_needs_scope() {
    use crate::core::engine::EngineScope;
    use super::need_row::validate_scope;
    let need=need(Some("text"));
    let scope=EngineScope{machine:"owner".into(),topic:"needs_me".into(),key:None};
    assert!(validate_scope(&need.key,&scope).is_ok());
    assert!(validate_scope(&need.key,&EngineScope{machine:"another-owner".into(),..scope.clone()}).is_err());
    assert!(validate_scope(&need.key,&EngineScope{key:Some("session".into()),..scope.clone()}).is_err());
    assert!(validate_scope(&need.key,&EngineScope{topic:"sessions".into(),..scope}).is_err());
}

#[test]
fn cancelled_and_dismissed_needs_do_not_claim_an_answer_and_phone_origin_is_not_inferred() {
    use super::need_row::settled_state;
    let mut need=need(Some("text"));
    for state in ["cancelled","dismissed"] {need.state=state.into();assert!(matches!(settled_state(&need,false),NeedRowState::Closed{state:actual} if actual.as_ref()==state));}
    need.state="answered".into();need.answered_origin=Some(crate::core::engine::engine_items::CommandOrigin{channel:"phone".into(),device_id:Some("real-device".into()),surface:None});
    assert!(matches!(settled_state(&need,false),NeedRowState::Answered{here:false,by} if by.as_ref()=="on phone"));
    need.answered_origin=None;need.answered_by=None;
    assert!(matches!(settled_state(&need,false),NeedRowState::Answered{by,..} if by.as_ref()=="elsewhere"));
}

#[test]
fn a_version_conflict_preserves_the_open_editor_state() {
    assert!(matches!(failure_state(&EngineRefusal{code:"version_conflict".into(),message:"Actual need version changed".into()}),NeedRowState::Open));
}

#[test]
fn from_and_with_labels_require_both_actual_owners_and_the_viewed_relation() {
    use crate::core::engine::SessionKey;
    use super::need_row::need_relation;
    let mut need=need(Some("text"));
    need.addressee_session_id=Some("same".into());
    let parent=SessionKey{machine:"parent-owner".into(),id:"same".into()};
    let collision=SessionKey{machine:"owner".into(),id:"same".into()};
    assert!(need_relation(&need,&parent).is_none());
    need.addressee_machine=Some(parent.machine.clone());
    assert_eq!(need_relation(&need,&parent),Some(("From",need.session.clone())));
    assert_eq!(need_relation(&need,&need.session),Some(("With",parent)));
    assert!(need_relation(&need,&collision).is_none());
}
