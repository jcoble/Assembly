//! Pieces of interface every feature shares.

pub mod center_panes;
pub mod diff;
pub mod expand_row;
#[cfg(test)]
mod expand_row_tests;
pub mod filter_pills;
#[cfg(test)]
mod filter_pills_tests;
pub mod pane_names;

pub mod ask;
pub mod row_drop_down;
#[cfg(test)]
mod row_drop_down_tests;
pub mod tab_stack;
pub mod three_line_row;

pub mod markdown;

pub mod activity;
