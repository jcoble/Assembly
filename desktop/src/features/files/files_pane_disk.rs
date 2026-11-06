//! Changes made on disk outside the app reach the tree. While the pane holds a lease it follows `filesChanged` for
//! it, and reads the tree again when a folder it shows changed. The state a subscribe or a reconnect sends first isn't
//! a change, so it reads nothing.
use super::FilesPane;
use crate::core::socket::{client::SocketClient, update::Update};
use gpui_kit::*;

impl FilesPane {
    /// Follows the lease's changes; dropping `watching` with the lease ends it.
    pub(super) fn watch_disk(&mut self, cx: &mut Context<Self>) {
        let (Some(lease), Some(socket)) = (self.lease.clone(), SocketClient::try_global(cx)) else {
            return;
        };
        let mut subscription = socket.read(cx).subscribe("filesChanged", Some(&lease));
        self.watching = Some(cx.spawn(async move |pane, cx| {
            while let Ok(update) = subscription.updates.recv().await {
                let Update::Change { item, .. } = update else {
                    continue;
                };
                let folders: Vec<String> = item["folders"]
                    .as_array()
                    .into_iter()
                    .flatten()
                    .filter_map(|folder| folder.as_str().map(String::from))
                    .collect();
                if pane
                    .update(cx, |pane, cx| pane.disk_changed(&folders, cx))
                    .is_err()
                {
                    return;
                }
            }
        }));
    }

    /// No folders means the backend lost track, so it reads again anyway.
    fn disk_changed(&mut self, folders: &[String], cx: &mut Context<Self>) {
        if folders.is_empty() || folders.iter().any(|folder| self.tree.shows(folder)) {
            self.reload_kept(cx);
        }
    }
}
