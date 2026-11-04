use super::{CheckoutSeen, checkout_sentence, observe_checkout};
use serde_json::json;
// Pins: g9#6
#[test]
fn only_a_row_carrying_our_operation_ends_the_wait() {
    let row = |state: &str, operation: &str| json!({"checkoutState":state,"checkoutOperationId":operation,"checkoutReason":"head_changed"});
    assert_eq!(
        observe_checkout(&row("ready", "older"), "ours"),
        CheckoutSeen::Waiting
    );
    assert_eq!(
        observe_checkout(&row("pending", "ours"), "ours"),
        CheckoutSeen::Waiting
    );
    assert_eq!(
        observe_checkout(&row("ready", "ours"), "ours"),
        CheckoutSeen::Ready
    );
    assert_eq!(
        observe_checkout(&row("refused", "ours"), "ours"),
        CheckoutSeen::Refused(Some("head_changed".into()))
    );
    assert_eq!(observe_checkout(&json!({}), "ours"), CheckoutSeen::Waiting);
}
#[test]
fn a_refused_branch_name_shows_the_backends_sentence() {
    assert_eq!(
        checkout_sentence("invalid_branch"),
        "That isn't a valid branch name."
    );
    assert_eq!(
        checkout_sentence("feature/x is already checked out elsewhere."),
        "feature/x is already checked out elsewhere."
    );
}
