//! Time comes from the backend's `clock` topic, never a timer in the window (plan 5.8). This folder holds the topic's
//! subscriber and, once a save needs one, the one named debounce. Nothing here ticks on its own.

pub mod backend_clock;
pub mod tick;
