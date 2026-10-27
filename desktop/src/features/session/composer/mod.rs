//! The composer under the timeline: the draft, send and stop, the "/" menu, the agent's config menus, the context
//! ring and attached images with their uploads.

pub mod agent_config;
pub mod attachment;
#[cfg(test)]
mod attachment_tests;
pub mod composer_view;
pub mod config_menu;
pub mod context_meter;
#[cfg(test)]
mod context_meter_tests;
pub mod slash_menu;
#[cfg(test)]
mod slash_menu_tests;
pub mod upload;
