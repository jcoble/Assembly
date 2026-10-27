use super::{FOLD_LINES, shown};

// Pins: g0-session#21
#[test]
fn a_prompt_longer_than_ten_lines_folds_until_opened() {
    let ten: String = (1..=FOLD_LINES).map(|n| format!("{n}\n")).collect();
    assert_eq!(shown(ten.trim_end(), false), (ten.trim_end(), false));
    let long = format!("{ten}11");
    let (text, more) = shown(&long, false);
    assert!(more);
    assert_eq!(text.lines().count(), FOLD_LINES);
    assert!(text.ends_with("10"));
    assert_eq!(shown(&long, true), (long.as_str(), true));
}
