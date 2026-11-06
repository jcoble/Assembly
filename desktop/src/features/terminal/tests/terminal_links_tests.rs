//! Finding a link: the URL under a cell on its row, and an OSC 8 link's cells.

use super::url_at;
use crate::features::terminal::terminal_grid::TerminalGrid;

#[test]
fn each_scheme_is_found() {
    for url in [
        "http://example.com/a",
        "https://example.com/a",
        "mailto:me@example.com",
        "ftp://example.com/a",
        "ssh://example.com/a",
        "git://example.com/a",
        "tel:+15551234567",
        "magnet:?xt=urn:btih:abc",
        "gemini://example.com/a",
        "gopher://example.com/a",
        "news:comp.lang.rust",
        "assembly://session?id=abc",
    ] {
        let row = format!("see {url} now");
        let found = url_at(&row, 4 + 2);
        assert_eq!(found, Some((4, 4 + url.len(), url.to_string())), "{url}");
    }
}

#[test]
fn eight_characters_or_fewer_is_not_a_link() {
    assert_eq!(url_at("tel:1234", 1), None);
    assert_eq!(url_at("tel:12345", 1), Some((0, 9, "tel:12345".into())));
}

#[test]
fn trailing_punctuation_is_left_off() {
    for mark in ['.', ',', ';', '!', '?', ')', ']'] {
        let row = format!("https://example.com/a{mark}");
        let url = "https://example.com/a";
        assert_eq!(url_at(&row, 2), Some((0, url.len(), url.into())), "{mark}");
    }
    let row = "(https://example.com/a).";
    let url = "https://example.com/a";
    assert_eq!(url_at(row, 3), Some((1, 1 + url.len(), url.into())));
}

#[test]
fn a_column_off_the_url_is_not_on_it() {
    let row = "see https://example.com/a now";
    assert!(url_at(row, 4).is_some());
    assert_eq!(url_at(row, 3), None);
    assert_eq!(url_at(row, 25), None);
}

#[test]
fn a_url_is_found_on_its_own_row_only() {
    // Wider than the grid: the program's line wraps, and the link is the part on the row pointed at.
    let mut grid = TerminalGrid::new(20, 3);
    grid.feed(b"https://example.com/long/path");
    let link = grid.link_at(2, 0).expect("the first row's part is a link");
    assert_eq!(link.url, "https://example.com/");
    assert_eq!(link.line_spans, vec![(0, 0, 20)]);
    assert_eq!(grid.link_at(2, 1), None, "long/path has no scheme");
}

#[test]
fn printed_file_text_is_not_a_link() {
    let row = "file:///etc/passwd https://example.com/a";
    assert_eq!(url_at(row, 3), None);
    assert!(url_at(row, 21).is_some(), "the URL beside it is found");
    let mut grid = TerminalGrid::new(60, 3);
    grid.feed(row.as_bytes());
    assert_eq!(grid.link_at(3, 0), None);
    assert!(grid.link_at(21, 0).is_some());
}

#[test]
fn an_osc8_link_is_found_and_spans_rows() {
    let mut grid = TerminalGrid::new(10, 3);
    // Fifteen letters, ten on the first row and five on the next, with other text after.
    grid.feed(b"\x1b]8;;https://example.com/a\x07abcdefghijklmno\x1b]8;;\x07 after");
    for (column, line) in [(0, 0), (9, 0), (0, 1), (4, 1)] {
        let link = grid.link_at(column, line).expect("the link's cells");
        assert_eq!(link.url, "https://example.com/a");
        assert_eq!(link.line_spans, vec![(0, 0, 10), (1, 0, 5)]);
    }
    assert_eq!(grid.link_at(7, 1), None);
}
