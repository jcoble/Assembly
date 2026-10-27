//! What the session pane draws: the header, the timeline (a virtual list of entries), each kind of row, and the
//! plan card above the composer. Rows are `RenderOnce` and read their item and its `Kept`; their buttons send a
//! `SessionAction` to the pane, and only the pane and the timeline own state.

pub mod ask_row;
pub mod grow;
pub mod header;
pub mod item_row;
pub mod lightbox;
pub mod markdown_view;
pub mod message_meta;
pub mod plan_card;
pub mod sent_message;
pub mod session_action;
pub mod session_pane;
pub mod subagent_card;
pub mod task_card;
pub mod timeline;
pub mod tool_row;
pub mod turn_fold_row;
