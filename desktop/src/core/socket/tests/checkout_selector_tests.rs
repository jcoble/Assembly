use super::{CheckoutSelector, folder_name};
use serde_json::json;

#[test]
fn the_own_checkout_sends_no_selector_and_another_sends_both_fields() {
    let args = json!({"session":"s1"});
    assert_eq!(CheckoutSelector::applied(None, args.clone()), args);
    let selector = CheckoutSelector {
        path: "/wt/other".into(),
        expected_root: "/wt/own".into(),
    };
    assert_eq!(
        CheckoutSelector::applied(Some(&selector), args),
        json!({"session":"s1","checkoutPath":"/wt/other","expectedCheckoutRoot":"/wt/own"})
    );
}
#[test]
fn a_folder_is_named_by_its_last_segment() {
    assert_eq!(folder_name("/wt/other/"), "other");
    assert_eq!(folder_name("other"), "other");
}
