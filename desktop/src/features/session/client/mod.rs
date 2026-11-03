//! The session view's rows and calls, through a small trait so tests can answer them.

pub mod fake_client;
pub mod session_client;
pub mod socket_session_client;

pub(super) mod open_target;
#[cfg(test)]
mod open_target_tests;

pub(super) mod source_resolution;
#[cfg(test)]
mod source_resolution_tests;
