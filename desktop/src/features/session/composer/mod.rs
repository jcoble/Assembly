//! The composer under the timeline: the draft, attached images, one Send or Stop, the context gauge, the agent's settings
//! pills and its "/" commands. `capsule/` holds the box's own pieces.

pub mod agent_config;
#[path = "capsule/answer_panel.rs"]
pub mod answer_panel;
pub mod attachment;
#[path = "capsule/attachment_card.rs"]
pub mod attachment_card;
#[cfg(test)]
mod attachment_tests;
pub mod composer_view;
#[path = "capsule/config_labels.rs"]
pub mod config_labels;
#[path = "capsule/config_pills.rs"]
pub mod config_pills;
#[path = "capsule/context_gauge.rs"]
pub mod context_gauge;
pub mod context_meter;
#[cfg(test)]
mod context_meter_tests;
pub mod draft_config;
#[path = "capsule/shape.rs"]
pub mod shape;
pub mod slash_list;
pub mod slash_menu;
#[cfg(test)]
mod slash_menu_tests;
pub mod upload;
