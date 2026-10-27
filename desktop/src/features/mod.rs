//! One folder per feature, `features/<feature>/`, which never uses another feature's folder.

#[expect(
    dead_code,
    reason = "nothing in the window uses the session view until its pane registers"
)]
pub mod session;
#[expect(
    dead_code,
    reason = "nothing in the window uses the sessions column until its pane registers"
)]
pub mod sessions;
