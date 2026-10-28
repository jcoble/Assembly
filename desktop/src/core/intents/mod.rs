//! Intents: asks to open something, sent from anywhere and handled by the one feature that owns that kind of thing.
//! Each is a kit action with its data, dispatched with `window.dispatch_action` and handled by its owner's global
//! `cx.on_action`. The kit runs only the newest global handler for an action, so the owner is the only one that
//! registers it. Replaces the old app's openFileBus.ts and workbenchNavigation.ts.

pub mod open_diff;
pub mod open_file;
pub mod open_session;
pub mod open_settings;
pub mod open_url;
