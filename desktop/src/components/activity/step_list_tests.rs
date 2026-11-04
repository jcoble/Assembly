use gpui_kit::SharedString;

use super::{StepMark, StepRight, StepView, format_elapsed};

#[test]
fn elapsed_reads_by_one_rule() {
    assert_eq!(format_elapsed(320), SharedString::from("320ms"));
    assert_eq!(format_elapsed(12_400), SharedString::from("12.4s"));
    assert_eq!(format_elapsed(148_000), SharedString::from("2m 28"));
    assert_eq!(format_elapsed(125_000), SharedString::from("2m 05"));
    assert_eq!(format_elapsed(-5), SharedString::from("0ms"));
}

#[test]
fn only_a_step_with_more_to_say_opens() {
    let plain = StepView {
        key: "a".into(),
        mark: StepMark::Done,
        label: "Built".into(),
        tool: None,
        detail: None,
        explanation: None,
        pills: Vec::new(),
        right: StepRight::None,
        attempt: None,
        scope: None,
        closed: false,
    };
    assert!(!plain.has_more());
    assert!(
        StepView {
            detail: Some("cargo build".into()),
            ..plain.clone()
        }
        .has_more()
    );
    assert!(
        StepView {
            explanation: Some("Why".into()),
            ..plain.clone()
        }
        .has_more()
    );
    assert!(
        StepView {
            scope: Some(("repo".into(), true)),
            ..plain
        }
        .has_more()
    );
}
