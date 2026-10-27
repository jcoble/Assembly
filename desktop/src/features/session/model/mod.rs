//! What the timeline draws, worked out from the rows: plain data and functions with no GPUI, so tests run them
//! directly. Ported from rows/store.ts and session/*.ts.

pub mod change;
pub mod entries;
#[cfg(test)]
mod entries_tests;
pub mod fold;
#[cfg(test)]
mod fold_tests;
pub mod follow;
pub mod item;
pub mod kept;
pub mod markdown_text;
pub mod notices;
#[cfg(test)]
mod notices_tests;
pub mod reveal;
pub mod session_view;
#[cfg(test)]
mod session_view_tests;
#[cfg(test)]
mod test_rows;
pub mod turn_line;
#[cfg(test)]
mod turn_line_tests;
