//! Finds a URL under a cell of the terminal. Pure functions. Stub until Task 8.

/// The URL on `row` under `column`: its start, its end (exclusive) and its text.
#[allow(dead_code)]
pub fn url_at(_row: &str, _column: usize) -> Option<(usize, usize, String)> {
    None
}

#[cfg(test)]
#[path = "terminal_links_tests.rs"]
mod tests;
