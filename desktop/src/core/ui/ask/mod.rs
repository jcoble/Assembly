//! Shared ask payload, schema and draft. The owning surface submits the checked answer.
pub mod form;
#[cfg(test)]
mod form_tests;
pub mod payload;
#[cfg(test)]
mod payload_tests;
pub mod presentation;
#[cfg(test)]
mod presentation_tests;

pub mod controls;

pub mod need_row;
#[cfg(test)]
mod need_row_tests;
