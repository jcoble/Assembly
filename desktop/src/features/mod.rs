//! One folder per feature, `features/<feature>/`, which never uses another feature's folder.

pub mod meter;
pub mod palette;
#[expect(
    dead_code,
    reason = "the session view's row views are drawn in a later step"
)]
pub mod session;
#[expect(
    dead_code,
    reason = "nothing in the window uses the sessions column until its pane registers"
)]
pub mod sessions;
pub mod shell;
