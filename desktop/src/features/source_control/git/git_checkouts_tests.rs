use super::{CheckoutChoice, CheckoutHint, GitCheckouts, checkout_gone};
use crate::core::socket::checkout_selector::CheckoutSelector;
use crate::core::socket::error_code::ErrorCode;
use crate::core::socket::row_source::ExpectedRowSource;
use gpui_kit::component::dock::PanelInfo;
use serde_json::json;

fn checkouts() -> GitCheckouts {
    serde_json::from_value(json!({"sessionRoot":"/wt/own","worktrees":[
        {"path":"/wt/feature","branch":"feature"},{"path":"/wt/detached","branch":null}]}))
    .unwrap()
}
fn labels(choices: Vec<CheckoutChoice>) -> Vec<String> {
    choices.into_iter().map(|choice| choice.label).collect()
}
#[test]
fn source_control_names_the_session_folder_then_each_checkout_and_its_branch() {
    let choices = checkouts().scope_choices();
    assert_eq!(choices[0].path, None, "the own choice sends no selector");
    assert_eq!(labels(choices), ["own (session)", "feature — feature", "detached"]);
}
#[test]
fn history_names_each_repository_with_its_branch() {
    assert_eq!(
        labels(checkouts().repository_choices(Some("main"))),
        ["own (main)", "feature (feature)", "detached"]
    );
    assert_eq!(labels(checkouts().repository_choices(None))[0], "own");
}
#[test]
fn only_a_listed_checkout_gets_a_selector_and_it_expects_the_session_folder() {
    assert_eq!(
        checkouts().selector("/wt/feature"),
        Some(CheckoutSelector {
            path: "/wt/feature".into(),
            expected_root: "/wt/own".into()
        })
    );
    assert_eq!(checkouts().selector("/elsewhere"), None);
    assert_eq!(checkouts().selector("/wt/own"), None, "the own folder is never a selector");
}
#[test]
fn a_later_answer_stops_offering_a_removed_checkout_or_one_under_another_root() {
    let picked = checkouts().selector("/wt/feature").unwrap();
    assert!(checkouts().offers(&picked));
    let mut removed = checkouts();
    removed.worktrees.remove(0);
    assert!(!removed.offers(&picked));
    let mut moved = checkouts();
    moved.session_root = "/wt/moved".into();
    assert!(!moved.offers(&picked));
}
#[test]
fn only_a_refusal_about_the_checkout_leaves_it() {
    for code in ["checkout_unavailable", "checkout_root_changed", "source_changed"] {
        assert!(checkout_gone(&ErrorCode::Refused { code: code.into() }));
    }
    assert!(!checkout_gone(&ErrorCode::Failed));
    assert!(!checkout_gone(&ErrorCode::NotFound));
    assert!(!checkout_gone(&ErrorCode::Refused { code: "busy".into() }));
}
#[test]
fn a_hint_becomes_a_selector_only_for_its_session_owner_and_root() {
    let own = ExpectedRowSource { machine: "owner".into(), generation: 7, route_machine: None };
    let list: GitCheckouts =
        serde_json::from_value(json!({"sessionRoot":"/own","worktrees":[{"path":"/linked","branch":null}]})).unwrap();
    let hint = CheckoutHint::new("s1", &own, &list.selector("/linked").unwrap());
    assert_eq!(hint.qualified("s1", &own, Some(&list)).map(|selector| selector.path).as_deref(), Some("/linked"));
    assert_eq!(hint.qualified("s1", &own, None), None, "no fresh answer, no selector");
    assert_eq!(hint.qualified("s2", &own, Some(&list)), None);
    let replaced = ExpectedRowSource { generation: 8, ..own.clone() };
    assert_eq!(hint.qualified("s1", &replaced, Some(&list)), None);
    let moved: GitCheckouts =
        serde_json::from_value(json!({"sessionRoot":"/moved","worktrees":[{"path":"/linked","branch":null}]})).unwrap();
    assert_eq!(hint.qualified("s1", &own, Some(&moved)), None);
    let gone: GitCheckouts = serde_json::from_value(json!({"sessionRoot":"/own","worktrees":[]})).unwrap();
    assert_eq!(hint.qualified("s1", &own, Some(&gone)), None);
}
#[test]
fn a_hint_reads_back_from_a_saved_layout_and_a_bad_one_is_none() {
    let own = ExpectedRowSource { machine: "owner".into(), generation: 7, route_machine: Some("box".into()) };
    let selector = CheckoutSelector { path: "/linked".into(), expected_root: "/own".into() };
    let hint = CheckoutHint::new("s1", &own, &selector);
    let saved = PanelInfo::panel(json!({"checkoutHint":hint}));
    assert_eq!(CheckoutHint::saved(&saved), Some(hint));
    assert_eq!(CheckoutHint::saved(&PanelInfo::panel(json!({}))), None);
    assert_eq!(CheckoutHint::saved(&PanelInfo::panel(json!({"checkoutHint":{"path":"/linked"}}))), None);
}
