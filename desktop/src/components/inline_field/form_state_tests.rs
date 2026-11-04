use std::rc::Rc;

use super::super::field::FieldValue;
use super::super::state::SaveRequest;
use super::{FormFailure, FormRule, FormSave, FormState, FormValues};
use gpui_kit::SharedString;

fn values(name: &str, destination: &str) -> FormValues {
    FormValues(vec![
        ("name".into(), FieldValue::Text(name.into())),
        ("destination".into(), FieldValue::Text(destination.into())),
    ])
}

fn editing() -> FormState {
    let mut form = FormState::new(values("old", "box"));
    form.begin();
    form
}

fn error(key: &str, message: &str) -> (SharedString, String) {
    (key.to_owned().into(), message.to_owned())
}

fn sent(save: FormSave) -> (SaveRequest, FormValues) {
    match save {
        FormSave::Sent(request, values) => (request, values),
        _ => panic!("the form didn't send"),
    }
}

fn blocked(save: FormSave) -> Vec<(SharedString, String)> {
    match save {
        FormSave::Blocked(errors) => errors,
        _ => panic!("the form wasn't blocked"),
    }
}

#[test]
fn any_invalid_field_blocks_the_save_and_shows_every_error() {
    let mut form = editing();
    let errors = vec![error("name", "Required"), error("destination", "Required")];
    assert_eq!(blocked(form.save(values("", ""), errors.clone())), errors);
    assert_eq!(form.errors(), errors);
    assert!(form.editing());
    assert!(!form.pending());
    assert_eq!(form.committed(), &values("old", "box"));
}

#[test]
fn form_rule_can_fail_across_fields() {
    let rule: FormRule = Rc::new(|values| {
        if values.0[0].1 == values.0[1].1 {
            vec![("destination".into(), "Must differ from the name".to_owned())]
        } else {
            Vec::new()
        }
    });
    let mut form = editing().with_rule(rule);
    let errors = blocked(form.save(values("same", "same"), Vec::new()));
    assert_eq!(errors, [error("destination", "Must differ from the name")]);
    sent(form.save(values("name", "other"), Vec::new()));
}

#[test]
fn an_unchanged_form_just_closes() {
    let mut form = editing();
    assert!(matches!(form.save(values("old", "box"), Vec::new()), FormSave::Closed));
    assert!(!form.editing());
}

#[test]
fn one_request_carries_every_value() {
    let mut form = editing();
    let (request, sent_values) = sent(form.save(values("new", "elsewhere"), Vec::new()));
    assert_eq!(sent_values, values("new", "elsewhere"));
    assert!(form.pending());
    assert!(matches!(form.save(values("newer", "x"), Vec::new()), FormSave::Ignored));
    assert!(form.complete(request, Ok(values("new", "elsewhere"))));
    assert!(!form.editing());
    assert_eq!(form.committed(), &values("new", "elsewhere"));
}

#[test]
fn refused_keeps_all_drafts_and_stays_editing() {
    let mut form = editing();
    let (request, _) = sent(form.save(values("new", "elsewhere"), Vec::new()));
    assert!(form.complete(request, Err(FormFailure::Refused("No".into()))));
    assert!(form.editing());
    assert!(!form.pending());
    assert_eq!(form.draft(), &values("new", "elsewhere"));
    assert_eq!(form.error(), Some("No"));
    assert_eq!(form.committed(), &values("old", "box"));
    sent(form.save(values("new", "elsewhere"), Vec::new()));
}

#[test]
fn field_errors_from_the_owner_land_on_their_fields() {
    let mut form = editing();
    let (request, _) = sent(form.save(values("new", "bad"), Vec::new()));
    let errors = vec![error("destination", "Can't reach it")];
    assert!(form.complete(request, Err(FormFailure::Fields(errors.clone()))));
    assert_eq!(form.errors(), errors);
    assert_eq!(form.focus_target(), Some(&SharedString::from("destination")));
    assert!(form.editing());
}

#[test]
fn an_uncertain_save_sends_nothing_more_until_a_value_arrives() {
    let mut form = editing();
    let (request, _) = sent(form.save(values("new", "box"), Vec::new()));
    assert!(form.complete(request, Err(FormFailure::Uncertain("Not confirmed".into()))));
    assert!(form.uncertain());
    assert!(matches!(form.save(values("new", "box"), Vec::new()), FormSave::Ignored));
    form.set_committed(values("new", "box"));
    assert!(!form.uncertain());
    assert!(!form.editing());
}

#[test]
fn stale_completion_is_ignored() {
    let mut form = editing();
    let (request, _) = sent(form.save(values("new", "box"), Vec::new()));
    form.set_committed(values("pushed", "box"));
    assert!(!form.complete(request, Ok(values("stale", "box"))));
    assert_eq!(form.committed(), &values("pushed", "box"));
}

#[test]
fn cancel_restores_everything_and_clears_errors() {
    let mut form = editing();
    blocked(form.save(values("", ""), vec![error("name", "Required")]));
    assert!(form.cancel());
    assert!(!form.editing());
    assert!(form.errors().is_empty());
    assert_eq!(form.draft(), &values("old", "box"));
}

#[test]
fn pushed_value_keeps_open_drafts() {
    let mut form = editing();
    blocked(form.save(values("mine", ""), vec![error("destination", "Required")]));
    form.set_committed(values("theirs", "box"));
    assert!(form.editing());
    assert_eq!(form.committed(), &values("theirs", "box"));
    assert_eq!(form.draft(), &values("mine", ""));
}

#[test]
fn focus_target_is_the_first_invalid_field() {
    let mut form = editing();
    // In the form's order, whichever order the errors came in.
    let errors = vec![error("destination", "Required"), error("name", "Required")];
    blocked(form.save(values("", ""), errors));
    assert_eq!(form.focus_target(), Some(&SharedString::from("name")));
}
