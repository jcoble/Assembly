//! Pieces of interface every feature shares.

pub mod access;
pub mod center_panes;
pub mod changed_tree;
pub mod diff;
pub mod diff_header;
pub mod expand_row;
#[cfg(test)]
mod expand_row_tests;
pub mod file_icon;
pub mod filter_pills;
#[cfg(test)]
mod filter_pills_tests;
pub mod pane_names;

pub mod ask;
pub mod row_drop_down;
#[cfg(test)]
mod row_drop_down_tests;
pub mod spinner;
pub mod tab_stack;
pub mod three_line_row;

pub mod markdown;
pub mod reasoned_item;

pub mod activity;
