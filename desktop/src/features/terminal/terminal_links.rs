//! Finds a URL under a cell of the terminal. Pure functions. The schemes, the characters a URL takes, the trailing
//! punctuation left off and the length rule are the old renderer's `URL_REGEX` (ghostty-web's link provider), plus
//! `assembly://`.

/// The schemes a URL starts with, matched in any case.
const SCHEMES: [&str; 12] = [
    "https://",
    "http://",
    "mailto:",
    "ftp://",
    "ssh://",
    "git://",
    "tel:",
    "magnet:",
    "gemini://",
    "gopher://",
    "news:",
    "assembly://",
];

/// Left off the end of a URL.
const TRAILING: [char; 7] = ['.', ',', ';', '!', '?', ')', ']'];

/// A URL is longer than this.
const SHORTEST: usize = 8;

/// A character a URL takes after its scheme: `[\w\-.~:/?#@!$&*+,;=%]`.
fn in_url(c: char) -> bool {
    c.is_ascii_alphanumeric() || "_-.~:/?#@!$&*+,;=%".contains(c)
}

/// The scheme starting at `at`, by its length.
fn scheme_at(row: &[char], at: usize) -> Option<usize> {
    SCHEMES.iter().find_map(|scheme| {
        let text = row.get(at..at + scheme.len())?;
        let found = scheme
            .chars()
            .zip(text)
            .all(|(a, b)| a.eq_ignore_ascii_case(b));
        found.then_some(scheme.len())
    })
}

/// The URL on `row` under `column`: its start, its end (exclusive) and its text. Columns count characters.
pub fn url_at(row: &str, column: usize) -> Option<(usize, usize, String)> {
    let row: Vec<char> = row.chars().collect();
    let mut at = 0;
    while at < row.len() {
        let Some(scheme) = scheme_at(&row, at) else {
            at += 1;
            continue;
        };
        let matched = at
            + scheme
            + row[at + scheme..]
                .iter()
                .take_while(|&&c| in_url(c))
                .count();
        // The regex needs one character after the scheme.
        if matched == at + scheme {
            at += 1;
            continue;
        }
        let mut end = matched;
        while end > at && TRAILING.contains(&row[end - 1]) {
            end -= 1;
        }
        if end - at > SHORTEST && (at..end).contains(&column) {
            return Some((at, end, row[at..end].iter().collect()));
        }
        at = matched;
    }
    None
}

#[cfg(test)]
#[path = "tests/terminal_links_tests.rs"]
mod tests;
