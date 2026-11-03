use super::CreatePrInput;
#[test]
fn no_create_without_an_actual_base_and_title() {
    let input = CreatePrInput {
        base: String::new(),
        title: "Title".into(),
        description: "Exact\nbody".into(),
        draft: false,
    };
    assert!(input.args().is_err());
    assert!(
        CreatePrInput {
            base: "actual-default".into(),
            title: "  ".into(),
            ..input.clone()
        }
        .args()
        .is_err()
    );
    let args = CreatePrInput {
        base: " actual-default ".into(),
        title: " Title ".into(),
        draft: true,
        ..input
    }
    .args()
    .unwrap();
    assert_eq!(args["base"], "actual-default");
    assert_eq!(args["title"], "Title");
    assert_eq!(args["description"], "Exact\nbody");
    assert_eq!(args["draft"], true);
}
