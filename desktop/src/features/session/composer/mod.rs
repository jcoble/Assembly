//! The composer under the timeline: the draft, attached images, send, Stop, the context ring, the agent's menus and its
//! "/" commands.

pub mod agent_config;
pub mod attachment;
pub mod attachment_chip;
#[cfg(test)]
mod attachment_tests;
pub mod composer_view;
pub mod config_menu;
pub mod context_meter;
#[cfg(test)]
mod context_meter_tests;
pub mod context_ring;
pub mod slash_list;
pub mod slash_menu;
#[cfg(test)]
mod slash_menu_tests;
pub mod upload;
