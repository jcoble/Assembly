use super::position;

#[test]
fn navigation_clamps_one_based_character_positions_without_splitting_utf8() {
    let text = "é🦀\r\nlast\n";
    assert_eq!(position(text, 1, 2), "é".len());
    assert_eq!(position(text, 1, 99), "é🦀".len());
    assert_eq!(position(text, 2, 2), "é🦀\r\nl".len());
    assert_eq!(position(text, 999, 999), text.len());
    assert_eq!(position(text, 0, 0), 0);
}
