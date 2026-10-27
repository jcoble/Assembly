//! The composer under the timeline: the draft, send, Stop, the context ring, the agent's menus and its "/" commands.
//! Attached images have their models and tests here but aren't drawn yet.

pub mod agent_config;
#[expect(dead_code, reason = "nothing attaches images yet (g0-session#24)")]
pub mod attachment;
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
#[cfg_attr(
    not(test),
    expect(dead_code, reason = "nothing attaches images yet (g0-session#24)")
)]
pub mod upload;
