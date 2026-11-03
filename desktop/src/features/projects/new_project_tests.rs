use super::new_project::{Kind, NewProjectInput};
// Pins: new-project companion1
#[test]
fn creation_names_one_real_machine_and_keeps_clone_source_separate() {
    let input = NewProjectInput {
        machine: "Workbox Test".into(),
        parent: "/owned parent".into(),
        name: " ü-new ".into(),
        kind: Kind::Clone,
        source: "../literal source".into(),
    };
    let args = input.args().unwrap();
    assert_eq!(args["machine"], "Workbox Test");
    assert_eq!(args["parentPath"], "/owned parent");
    assert_eq!(args["name"], "ü-new");
    assert_eq!(args["source"], "../literal source");
    assert_eq!(args["kind"], "clone");
    assert!(args.get("session").is_none());
    let empty = NewProjectInput {
        kind: Kind::Empty,
        ..input
    };
    assert!(empty.args().unwrap()["source"].is_null());
}
// Pins: new-project companion1
#[test]
fn invalid_names_and_missing_parent_or_clone_source_are_refused_before_start() {
    let input = NewProjectInput {
        machine: "local".into(),
        parent: "/owned".into(),
        name: "project".into(),
        kind: Kind::Git,
        source: String::new(),
    };
    for name in ["", " ", ".", "..", "a/b", "a\\b", "a\0b"] {
        assert!(
            NewProjectInput {
                name: name.into(),
                ..input.clone()
            }
            .args()
            .is_err(),
            "{name:?}"
        );
    }
    assert!(
        NewProjectInput {
            parent: String::new(),
            ..input.clone()
        }
        .args()
        .is_err()
    );
    assert!(
        NewProjectInput {
            kind: Kind::Clone,
            ..input
        }
        .args()
        .is_err()
    );
}
