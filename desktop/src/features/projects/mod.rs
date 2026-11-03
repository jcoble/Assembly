//! Projects collection and creation, alongside the isolated sample screen used by the preview.
mod project_dialog;
mod projects_screen;
mod sample;
pub use projects_screen::{ProjectsEvent, ProjectsSampleScreen};
pub use sample::Machine;

pub mod new_project;

#[cfg(test)]
mod new_project_tests;

pub mod new_project_dialog;

pub mod folder_browser;

pub mod projects;
