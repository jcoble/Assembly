//! Small controlled inline editors composed from GPUI Kit's styled controls.
//! Call `init` once, subscribe to `SaveRequested`, and acknowledge that request only after the real save.
mod editor;
mod field;
mod form;
mod form_state;
mod kinds;
mod layout;
pub mod preview;
mod state;
pub use editor::ChoiceOption;
pub use layout::field_row;
pub use field::{FieldRule, FieldValue, InlineField, SaveMode, SaveRequested, init};
pub use form::{FormSaveRequested, InlineForm};
pub use form_state::{FormFailure, FormRule, FormValues};
pub use kinds::NumberFormat;
pub use state::{SaveFailure, SaveRequest};
