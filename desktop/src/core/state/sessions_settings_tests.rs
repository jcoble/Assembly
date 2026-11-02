use serde_json::json;

use super::{children, depth, parse_depth};

#[test]
fn depth_reads_only_representable_positive_integers_without_a_product_maximum() {
    assert_eq!(depth(None), 1);
    assert_eq!(depth(Some(&json!(9))), 9);
    assert_eq!(depth(Some(&json!(i32::MAX))), i32::MAX);
    assert_eq!(depth(Some(&json!(i32::MAX as i64 + 1))), 1);
    assert_eq!(depth(Some(&json!(1.5))), 1);
    assert_eq!(depth(Some(&json!("9"))), 1);
    assert_eq!(depth(Some(&json!(0))), 1);
}

#[test]
fn typed_depth_accepts_trimmed_integers_and_rejects_invalid_or_overflowing_drafts() {
    assert_eq!(parse_depth(" 9 "), Ok(9));
    assert_eq!(parse_depth("2147483647"), Ok(i32::MAX));
    assert!(parse_depth("").is_err());
    assert!(parse_depth("0").is_err());
    assert!(parse_depth("2.5").is_err());
    assert_eq!(
        parse_depth("2147483648"),
        Err("That number is too large to store.")
    );
}

#[test]
fn children_keep_the_existing_default_and_range() {
    assert_eq!(children(None), 5);
    assert_eq!(children(Some(&json!(25))), 10);
    assert_eq!(children(Some(&json!(0))), 1);
    assert_eq!(children(Some(&json!(5.0))), 5);
}
