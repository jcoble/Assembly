//! The generic pieces activity is drawn from. They take plain display values, so any feature can map its data onto them.
mod chip;
mod section;
pub use chip::{LinkChip, StatusChip, Tone};
pub use section::ActivitySection;
