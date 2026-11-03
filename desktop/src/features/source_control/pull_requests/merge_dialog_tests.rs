use super::MergeRequest;
#[test]
fn draft_or_unconfirmed_targets_cannot_open_merge_and_current_heads_are_required() {
    assert!(!MergeRequest::available("OPEN", true, "MERGEABLE"));
    assert!(!MergeRequest::available("CLOSED", false, "MERGEABLE"));
    assert!(!MergeRequest::available("OPEN", false, "UNKNOWN"));
    assert!(MergeRequest::available("OPEN", false, "MERGEABLE"));
    let request = MergeRequest {
        number: 7,
        head: "a".repeat(40),
        base: "b".repeat(40),
        head_branch: "feature".into(),
        base_branch: "main".into(),
        method: "squash".into(),
    };
    assert_eq!(request.args().unwrap()["expectedHead"], "a".repeat(40));
    assert_eq!(request.args().unwrap()["expectedBase"], "b".repeat(40));
    assert_eq!(request.args().unwrap()["method"], "squash");
    assert!(
        MergeRequest {
            method: "force".into(),
            ..request.clone()
        }
        .args()
        .is_err()
    );
    assert!(
        MergeRequest {
            head: "feature".into(),
            ..request
        }
        .args()
        .is_err()
    );
}
