//! One session, open: its rows folded into what the timeline draws, the timeline itself (a virtual list), the
//! header, the status line and the composer. Replaces app/'s Conversation.svelte, rows/, session/, chat/ and
//! composer/. Rows and calls go through `client::SessionClient` until the socket client (lane g0-socket) lands.

pub mod client;
pub mod composer;
pub mod model;
pub mod rows;
pub mod view;
