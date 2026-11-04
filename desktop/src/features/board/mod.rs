//! The Build Board: a drop-down of engine state.
//! Spec: https://app.notion.com/p/40b394b0689d812398afe3c4cac4691b

pub mod board_commands;
pub mod board_panel;
pub mod board_prefs;
pub mod board_queries;
pub mod board_sections;
pub mod mute_menu;
pub mod owner_notify;
pub mod question_actions;
pub mod question_row;
pub mod run_card;
#[cfg(test)]
mod run_card_tests;
pub mod run_footer;
pub mod run_steps;
pub mod session_tabs;

gpui_kit::actions!(board, [ToggleBoard]);
