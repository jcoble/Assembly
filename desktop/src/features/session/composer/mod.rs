//! The composer under the timeline: the draft, send, Stop and the context ring. The "/" menu, the agent's config menus
//! and attached images have their models and tests here but aren't drawn yet.

#[expect(
    dead_code,
    reason = "the agent's menus aren't drawn yet (g0-session#27)"
)]
pub mod agent_config;
#[expect(dead_code, reason = "nothing attaches images yet (g0-session#24)")]
pub mod attachment;
#[cfg(test)]
mod attachment_tests;
pub mod composer_view;
#[expect(
    dead_code,
    reason = "the agent's menus aren't drawn yet (g0-session#27)"
)]
pub mod config_menu;
pub mod context_meter;
#[cfg(test)]
mod context_meter_tests;
pub mod context_ring;
#[cfg_attr(
    not(test),
    expect(dead_code, reason = "the slash menu isn't drawn yet (g0-session#26)")
)]
pub mod slash_menu;
#[cfg(test)]
mod slash_menu_tests;
#[cfg_attr(
    not(test),
    expect(dead_code, reason = "nothing attaches images yet (g0-session#24)")
)]
pub mod upload;
