//! The Build Board: the cards agents report through `board_update`, read from the backend's `board/*` calls
//! (docs/behavior/board.md), shown in the top bar's dropdown as in the old app.

pub mod board_card;
pub mod board_commands;
pub mod board_feed;
pub mod board_pane;
pub mod board_payload;
pub mod board_queries;
pub mod board_question;
pub mod board_state;

gpui_kit::actions!(board, [OpenBoard]);
