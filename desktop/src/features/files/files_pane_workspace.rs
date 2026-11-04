//! Restore only saved paths through the tree's existing bounded list pages; hidden panes do no IO.
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
    pub(crate) fn deactivate_workspace_tree(&mut self, cx: &mut Context<Self>) {
        self.generation = self.generation.wrapping_add(1);
        self.loads.clear();
        self.release_watch(cx);
        self.action_loading = None;
        self.action_error = None;
        self.workspace_key = None;
        self.workspace_checked = false;
        self.restoring_tree = None;
        self.session = None;
        self.source = None;
        self.owner = None;
        self.started = false;
        self.tree = FileTree::default();
        self.selected = None;
        self.revealing = None;
        cx.notify();
    }
    pub(crate) fn workspace_tree(&self, key: &WorkspaceKey) -> Option<TreeSnapshot> {
        if self.workspace_key.as_ref() != Some(key) {
            return None;
        }
        if let Some(restore) = &self.restoring_tree {
            let mut expanded = self.tree.expanded_paths();
            expanded.extend(restore.folders.iter().cloned());
            expanded.sort();
            expanded.dedup();
            return Some(TreeSnapshot {
                expanded_paths: expanded,
                selected_path: restore.selected.clone(),
                scroll: restore.scroll,
            });
        }
        Some(TreeSnapshot {
            expanded_paths: self.tree.expanded_paths(),
            selected_path: self.selected.clone(),
            scroll: f32::from(self.list.0.borrow().base_handle.offset().y),
        })
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
                    Outcome::Failed(error)=>{pane.tree.start();pane.tree.loaded("",Err(error.message));}
                    _=>{pane.tree.start();pane.tree.loaded("",Err("The owning checkout is unavailable or changed. The saved tree remains unchanged.".into()));}
                }cx.notify();
            });
        }));
        false
    }
    pub(super) fn continue_workspace_tree(&mut self, cx: &mut Context<Self>) {
        if !self.shown || self.tree.root_failed() {
            return;
        }
        let Some(mut restore) = self.restoring_tree.take() else {
            return;
        };
        while let Some(path) = restore.folders.front() {
            if self.tree.reveal_failed(path) {
                self.restoring_tree = Some(restore);
                return;
            }
            match self.tree.reveal(path) {
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
        if let Some(path) = &restore.selected {
            if self.tree.reveal_failed(path) {
                self.restoring_tree = Some(restore);
                return;
            }
            match self.tree.reveal_entry(path) {
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
                RevealStep::Missing => {}
            }
        }
        let offset = self.list.0.borrow().base_handle.offset();
        self.list
            .0
            .borrow()
            .base_handle
            .set_offset(point(offset.x, px(restore.scroll)));
    }
}
