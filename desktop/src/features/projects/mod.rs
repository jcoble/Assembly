//! Projects presentation with explicitly labelled, memory-only sample data.
//! This module is independent of application globals so an isolated preview can host it.
mod project_dialog;
mod projects_screen;
mod sample;
pub use projects_screen::{ProjectsEvent, ProjectsSampleScreen};
pub use sample::Machine;
