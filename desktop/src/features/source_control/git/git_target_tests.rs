use super::GitTarget;
use crate::core::socket::row_source::ExpectedRowSource;
use serde_json::json;
// Pins: g9#10, g9#26
#[test]
fn a_write_carries_the_original_source_and_root_beside_its_own_fields_and_a_read_only_the_source() {
    let target = GitTarget {
        session: "s1".into(),
        source: Some(ExpectedRowSource {
            machine: "owner".into(),
            generation: 7,
            route_machine: Some("workbox-test".into()),
        }),
        checkout_root: Some("/actual/root".into()),
    };
    let write = target.write_args(json!({"session":"s1","expectedHead":"abc"}));
    assert_eq!(write["expectedHead"], "abc");
    assert_eq!(write["expectedCheckoutRoot"], "/actual/root");
    assert_eq!(write["expectedSource"]["machine"], "owner");
    let read = target.read_args(json!({"session":"s1"}));
    assert_eq!(read["expectedSource"]["generation"], 7);
    assert!(read.get("expectedCheckoutRoot").is_none());
}
#[test]
fn an_unqualified_local_target_adds_nothing() {
    assert_eq!(
        GitTarget::new("s1").write_args(json!({"session":"s1"})),
        json!({"session":"s1"})
    );
}
