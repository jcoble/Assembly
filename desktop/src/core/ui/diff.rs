//! The kit owns patch parsing, layout, scrolling and source line identity.
use gpui_kit::component::diff::{DiffFile, DiffParseOptions};

pub fn parse_patch(text: &str, truncated: bool) -> Result<Vec<DiffFile>, String> {
    // A cut is a backend fact; ordinary malformed hunks must still fail parsing.
    DiffFile::parse_with(text, DiffParseOptions::new().truncated_hunks(truncated))
        .map_err(|error| format!("Couldn't read the supplied diff: {error}"))
}

pub enum SuppliedPatch {
    Unified(Vec<DiffFile>),
    /// Kit 0.8.1 deliberately rejects combined patches. Keep their exact Git bytes in its readonly editor.
    Combined(String),
}
pub fn supplied_patch(text: &str, truncated: bool) -> Result<SuppliedPatch, String> {
    if text.lines().any(|line| line.starts_with("diff --cc ") || line.starts_with("diff --combined ") || line.starts_with("@@@ ")) {
        return Ok(SuppliedPatch::Combined(text.to_string()));
    }
    parse_patch(text, truncated).map(SuppliedPatch::Unified)
}

#[cfg(test)]
#[path = "diff_tests.rs"]
mod tests;
