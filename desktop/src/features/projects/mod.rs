//! Projects collection and creation, alongside the isolated sample screen used by the preview.
mod project_dialog;
mod projects_screen;
mod sample;
pub use projects_screen::{ProjectsEvent, ProjectsSampleScreen};

pub mod new_project;

#[cfg(test)]
mod new_project_tests;

pub mod new_project_dialog;

pub mod folder_browser;

// Renaming it would only conflict with the open branches, for a lint about the look of a path.
#[allow(clippy::module_inception)]
pub mod projects;

pub mod projects_view;

pub mod project_chooser;
