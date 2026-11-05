//! Restore only saved paths through the tree's existing bounded list pages; hidden panes do no IO.
use super::root::plain_failure;
use super::{FileTree, FilesPane};
use crate::core::socket::{client::SocketClient, outcome::Outcome};
use crate::core::state::workspace_panes::WorkspaceKey;
use crate::core::ui::center_panes::CenterPanes;
use crate::features::files::{
    entry::FileOwnership, file_tree::RevealStep, tree_codec::TreeSnapshot,
};
use gpui_kit::component::dock::{DockPlacement, PaneRef, PanelHandle};
use gpui_kit::*;
use serde_json::json;
use std::collections::VecDeque;
pub(super) struct TreeRestore {
    folders: VecDeque<String>,
    selected: Option<String>,
    scroll: f32,
}
impl FilesPane {
    pub(crate) fn workspace_views(cx: &App) -> Vec<Entity<Self>> {
        let Some(dock) = CenterPanes::dock(cx) else {
            return Vec::new();
        };
        let mut views = Vec::new();
        for placement in [
            DockPlacement::Left,
            DockPlacement::Center,
            DockPlacement::Right,
            DockPlacement::Bottom,
        ] {
            if let Some(tree) = dock.read(cx).layout(placement) {
                tree.root().walk(&mut |node| {
                    if let PaneRef::Tabs { panels, .. } = node.kind() {
                        for id in panels {
                            if let Some(panel) = dock.read(cx).panel(*id)
                                && let Some(handle) = PanelHandle::of(panel)
                                && let Ok(view) = handle.view().downcast::<Self>()
                            {
                                views.push(view);
                            }
                        }
                    }
                });
            }
        }
        views
    }
    /// The session has no recorded checkout, which doesn't mean it has no folder: `files/owner` says which it is.
    pub(crate) fn deactivate_workspace_tree(&mut self, cx: &mut Context<Self>) {
        self.take_subject(Self::subject(cx), cx);
    }
    pub(crate) fn workspace_tree(&self, key: &WorkspaceKey) -> Option<TreeSnapshot> {
        if self.workspace_key.as_ref() != Some(key) {
            return None;
        }
        Some(self.tree_snapshot())
    }
    /// The open folders, selection and scroll as they are, or as they will be once a restore underway finishes.
    fn tree_snapshot(&self) -> TreeSnapshot {
        if let Some(restore) = self
            .restoring_tree
            .as_ref()
            .filter(|_| self.building.is_none())
        {
            let mut expanded = self.tree.expanded_paths();
            expanded.extend(restore.folders.iter().cloned());
            expanded.sort();
            expanded.dedup();
            return TreeSnapshot {
                expanded_paths: expanded,
                selected_path: restore.selected.clone(),
                scroll: restore.scroll,
            };
        }
        TreeSnapshot {
            expanded_paths: self.tree.expanded_paths(),
            selected_path: self.selected.clone(),
            scroll: f32::from(self.list.0.borrow().base_handle.offset().y),
        }
    }
    /// Reads the folder again, keeping the open folders, selection and scroll. The tree on screen stays until the new
    /// one is ready, so a reload never flashes or collapses it. The lease stays: it's the same folder.
    pub(super) fn reload_kept(&mut self, cx: &mut Context<Self>) {
        if !self.shown {
            return;
        }
        if self.building.is_some() {
            self.reload_again = true;
            return;
        }
        // A root not listed yet, or refused: asking again is how a folder made since then appears.
        if !self.tree.root_loaded() {
            self.generation = self.generation.wrapping_add(1);
            self.loads.clear();
            self.observing = None;
            self.started = false;
            self.start(cx);
            cx.notify();
            return;
        }
        // This runs once the lease is back.
        if !self.started {
            self.reload_when_ready = true;
            self.start(cx);
            return;
        }
        let snapshot = self.tree_snapshot();
        self.generation = self.generation.wrapping_add(1);
        self.loads.clear();
        self.tree.cancel_loads();
        let mut next = FileTree::default();
        let root = next.start().to_string();
        self.building = Some(next);
        self.restoring_tree = Some(TreeRestore {
            folders: snapshot.expanded_paths.into(),
            selected: snapshot.selected_path,
            scroll: snapshot.scroll,
        });
        self.load(root, None, cx);
    }
    /// The listing moved under a page of the walk, so the walk starts again from the tree on screen.
    pub(super) fn restart_reload(&mut self, cx: &mut Context<Self>) {
        self.restoring_tree = None;
        self.drop_reload();
        self.loads.clear();
        self.reload_kept(cx);
    }
    pub(super) fn drop_reload(&mut self) {
        self.building = None;
        self.reload_again = false;
    }
    /// The tree a folder's answer belongs to.
    pub(super) fn walked_tree(&mut self) -> &mut FileTree {
        self.building.as_mut().unwrap_or(&mut self.tree)
    }
    fn finish_reload(&mut self, cx: &mut Context<Self>) {
        let Some(next) = self.building.take() else {
            return;
        };
        self.tree = next;
        self.continue_reveal(cx);
        if std::mem::take(&mut self.reload_again) {
            self.reload_kept(cx);
        }
        cx.notify();
    }
    pub(crate) fn restore_workspace_tree(
        &mut self,
        key: WorkspaceKey,
        snapshot: TreeSnapshot,
        cx: &mut Context<Self>,
    ) {
        // The single navigator has synchronously committed selection before any codec runs.
        self.follow(cx);
        if self.session.as_deref() != Some(key.session_id.as_str()) {
            return;
        }
        self.generation = self.generation.wrapping_add(1);
        self.loads.clear();
        self.release_watch(cx);
        self.workspace_key = Some(key);
        self.owner = None;
        self.workspace_checked = false;
        self.started = false;
        self.drop_reload();
        self.clear_root_failure();
        self.tree = FileTree::default();
        self.selected = None;
        self.revealing = None;
        self.restoring_tree = Some(TreeRestore {
            folders: snapshot.expanded_paths.into(),
            selected: snapshot.selected_path,
            scroll: snapshot.scroll,
        });
        self.start(cx);
        cx.notify();
    }
    pub(super) fn validate_workspace_owner(&mut self, cx: &mut Context<Self>) -> bool {
        if self.workspace_checked {
            return true;
        }
        if self.observing.is_some() {
            return false;
        }
        let (Some(socket), Some(session)) = (SocketClient::try_global(cx), self.session.clone())
        else {
            return false;
        };
        let key = self.workspace_key.clone();
        let source = self.source.clone();
        let expected = self.owner.clone().or_else(|| {
            key.as_ref().map(|key| FileOwnership {
                machine: key.machine.clone(),
                checkout_root: key.checkout_root.clone(),
                remote_session_id: key.session_id.clone(),
            })
        });
        let generation = self.generation;
        let call = socket.read(cx).call::<FileOwnership>(
            "files",
            "owner",
            json!({"session":session,
            "expectedSource":source,"expectedOwner":expected}),
            None,
        );
        self.observing=Some(cx.spawn(async move |pane,cx| {
            let answer=call.await;
            let _=pane.update(cx,|pane,cx| {
                if pane.generation!=generation||pane.workspace_key!=key||pane.source!=source||!pane.shown {return;}
                pane.observing=None;
                match answer {
                    Outcome::Ok(owner) if key.as_ref().is_none_or(|key|owner.machine==key.machine&&owner.checkout_root==key.checkout_root) => {
                        pane.owner=Some(owner);pane.workspace_checked=true;pane.start(cx);
                    }
                    Outcome::Failed(error)=>pane.fail_root(error,cx),
                    _=>pane.fail_root(plain_failure("The owning checkout is unavailable or changed. The saved tree remains unchanged."),cx),
                }cx.notify();
            });
        }));
        false
    }
    pub(super) fn continue_workspace_tree(&mut self, cx: &mut Context<Self>) {
        if !self.shown {
            return;
        }
        let reloading = self.building.is_some();
        if self.walked_tree().root_failed() {
            // A reload whose root is gone shows why, rather than rows that no longer exist.
            if let Some(failed) = self.building.take() {
                self.tree = failed;
                self.restoring_tree = None;
                self.reload_again = false;
            }
            return;
        }
        let Some(mut restore) = self.restoring_tree.take() else {
            return;
        };
        while let Some(path) = restore.folders.front() {
            // A saved tree waits on a failed folder; a reload moves on and shows the failure under it.
            if !reloading && self.tree.reveal_failed(path) {
                self.restoring_tree = Some(restore);
                return;
            }
            match self.walked_tree().reveal(path) {
                RevealStep::Load { dir, cursor } => {
                    self.restoring_tree = Some(restore);
                    self.load(dir, cursor, cx);
                    return;
                }
                RevealStep::Waiting => {
                    self.restoring_tree = Some(restore);
                    return;
                }
                RevealStep::Ready | RevealStep::Missing => {
                    restore.folders.pop_front();
                }
            }
        }
        // A reload follows the selection as it is now: it may have moved during the walk.
        let selected = if reloading {
            self.selected.clone()
        } else {
            restore.selected.clone()
        };
        if let Some(path) = &selected {
            if !reloading && self.tree.reveal_failed(path) {
                self.restoring_tree = Some(restore);
                return;
            }
            match self.walked_tree().reveal_entry(path) {
                RevealStep::Load { dir, cursor } => {
                    self.restoring_tree = Some(restore);
                    self.load(dir, cursor, cx);
                    return;
                }
                RevealStep::Waiting => {
                    self.restoring_tree = Some(restore);
                    return;
                }
                RevealStep::Ready => {
                    self.selected = Some(path.clone());
                }
                RevealStep::Missing => {
                    if reloading {
                        self.selected = None;
                    }
                }
            }
        }
        if reloading {
            self.finish_reload(cx);
            return;
        }
        let offset = self.list.0.borrow().base_handle.offset();
        self.list
            .0
            .borrow()
            .base_handle
            .set_offset(point(offset.x, px(restore.scroll)));
    }
}
