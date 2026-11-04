//! Small controlled inline editors composed from GPUI Kit's styled controls.
//! Call `init` once, subscribe to `SaveRequested`, and acknowledge that request only after the real save.
mod editor;
mod field;
mod group_state;
mod grouped;
mod kinds;
pub mod preview;
mod state;
pub use editor::ChoiceOption;
pub use field::{FieldRule, FieldValue, InlineField, SaveMode, SaveRequested, init};
pub use kinds::NumberFormat;
pub use state::{SaveFailure, SaveRequest};
