use super::{FieldValue, NumberFormat, TextShape, digits_rule, email_rule};

fn text(value: &str) -> FieldValue {
    FieldValue::Text(value.into())
}

#[test]
fn email_rule_takes_an_address_or_nothing() {
    for good in ["", "a@b.co", " a.b@c.example.com "] {
        assert!(email_rule(&text(good)).is_ok(), "{good}");
    }
    for bad in ["a", "a@b", "@b.co", "a@@b.co", "a b@c.co", "a@b."] {
        assert!(email_rule(&text(bad)).is_err(), "{bad}");
    }
}

#[test]
fn digits_rule_takes_the_lengths_given_or_nothing() {
    assert!(digits_rule(&text(""), &[5, 9], "no").is_ok());
    assert!(digits_rule(&text("12345"), &[5, 9], "no").is_ok());
    assert_eq!(
        digits_rule(&text("1234"), &[5, 9], "no"),
        Err("no".to_owned())
    );
}

#[test]
fn numbers_show_in_their_format() {
    let shown = |format, value| TextShape::Number(format).shown(value);
    assert_eq!(shown(NumberFormat::Currency, "1234.5"), "$1,234.50");
    assert_eq!(shown(NumberFormat::Percent, "7.25"), "7.25%");
    assert_eq!(shown(NumberFormat::Integer, "12000"), "12,000");
    assert_eq!(shown(NumberFormat::Decimal, "1234.125"), "1,234.125");
    assert_eq!(shown(NumberFormat::Zip, "123456789"), "12345-6789");
    assert_eq!(TextShape::Phone.shown("5551234567"), "555-123-4567");
}

#[test]
fn a_saved_number_never_ends_in_a_point() {
    assert_eq!(
        TextShape::Number(NumberFormat::Currency).saved("100."),
        "100"
    );
    assert_eq!(TextShape::Plain.saved("100."), "100.");
}
