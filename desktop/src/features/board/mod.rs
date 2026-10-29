//! The Build Board: the cards agents report through `board_update`, read from the backend's `board/*` calls
//! (docs/behavior/board.md). Replaces the old app's Board popover with a center pane.

pub mod board_card;
pub mod board_feed;
pub mod board_pane;
pub mod board_payload;
pub mod board_question;
pub mod board_state;

gpui_kit::actions!(board, [OpenBoard]);
