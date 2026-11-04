// Reuse production Settings dependencies without starting a backend or opening app data.
#![allow(dead_code, unused_imports)]
#[path = "../../src/core/assets.rs"]
pub mod assets;
#[path = "../../src/core/commands/mod.rs"]
pub mod commands;
#[path = "../../src/core/engine/mod.rs"]
pub mod engine;
#[path = "../../src/core/intents/mod.rs"]
pub mod intents;
#[path = "../../src/core/socket/mod.rs"]
pub mod socket;
#[path = "../../src/core/state/mod.rs"]
pub mod state;
#[path = "."]
pub mod ui {
    #[path = "."]
    pub mod ask {
        #[path = "../../src/core/ui/ask/payload.rs"]
        pub mod payload;
        #[path = "../../src/core/ui/ask/presentation.rs"]
        pub mod presentation;
    }
}
#[path = "theme.rs"]
pub mod theme;
