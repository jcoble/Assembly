//! One desktop workspace coordinator and its explicit draft-loss guard.
pub mod drafts_guard;
pub mod workspace_coordinator;

mod legacy_migration;

mod center_tabs;

mod center_tabs_capture;

mod legacy_layout;

mod top_tabs_codec;

mod drawer_codec;
/// Called once after the concrete Chat/Changes/History/PR panel factories register, before any window selection.
pub fn init(cx:&mut gpui_kit::App)->Result<(),String> {
    crate::features::session::view::session_pane::SessionPane::register_workspace(cx)?;
    crate::core::state::workspace_panes::WorkspacePanes::register(Box::new(top_tabs_codec::TopTabsCodec),cx)?;
    crate::core::state::workspace_panes::WorkspacePanes::register(Box::new(drawer_codec::DrawerCodec),cx)?;
    workspace_coordinator::WorkspaceCoordinator::init(cx);
    Ok(())
}
