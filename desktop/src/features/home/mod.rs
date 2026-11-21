//! The Home button and the providers and connections panel it opens. The panel shows what the backend pushes on
//! `settings_state` and computes nothing of its own.

pub mod home_button;
pub mod home_panel;
mod home_panel_advanced;
mod home_panel_rows;
pub mod settings_state;
mod state_words;

#[cfg(test)]
mod home_panel_tests;
