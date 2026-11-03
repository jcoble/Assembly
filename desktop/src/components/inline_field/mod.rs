//! Small controlled inline editors composed from GPUI Kit's styled controls.
//! Call `init` once, subscribe to `SaveRequested`, and acknowledge that request only after the real save.
mod editor;
mod field;
mod group_state;
mod grouped;
pub mod preview;
mod state;
pub use editor::ChoiceOption;
pub use field::{FieldValue, InlineField, SaveRequested, init};
pub use state::SaveRequest;
