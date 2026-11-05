//! The buttons above the tree, in the old panel's order and with its words.
use super::FilesPane;
use crate::core::state::selected_session::SelectedSession;
use crate::core::state::viewed_project::ViewedProject;
use crate::core::ui::access::Control as _;
use crate::core::ui::access::control_ids::files as ids;
use crate::features::files::file_entry_bar::EntryKind;
use crate::features::files::panel_message::READ_ONLY;
use gpui_kit::assets::IconName;
use gpui_kit::component::button::{Button, ButtonVariants as _};
use gpui_kit::component::{Disableable as _, Icon, Sizable as _, h_flex};
use gpui_kit::*;

/// An icon button whose tooltip and accessible name are what it does.
pub(super) fn tool(id: &'static str, icon: IconName, tip: impl Into<SharedString>) -> Button {
    let tip = tip.into();
    Button::new(id)
        .control(id, tip.clone())
        .ghost()
        .xsmall()
        .icon(Icon::new(icon).size(px(14.)))
        .tooltip(tip)
}

impl FilesPane {
    #[cfg(test)]
    pub(in crate::features::files) fn action_idle(&self) -> bool {
        self.action_loading.is_none()
    }

    /// Why this folder can't be changed from here, if it can't. A viewed project can be changed unless a session
    /// works in it.
    pub(in crate::features::files) fn read_only_reason(&self, cx: &App) -> Option<String> {
        ViewedProject::read_only_reason(cx).or_else(|| {
            (ViewedProject::viewed(cx).is_none()
                && SelectedSession::global(cx).read(cx).read_only())
            .then(|| READ_ONLY.to_string())
        })
    }

    pub(in crate::features::files) fn can_create(&self, cx: &App) -> bool {
        self.tree.root_loaded() && self.read_only_reason(cx).is_none()
    }

    pub(in crate::features::files) fn toggle_sort(&mut self, cx: &mut Context<Self>) {
        self.descending = !self.descending;
        self.reload_kept(cx);
    }

    pub(in crate::features::files) fn toggle_excluded(&mut self, cx: &mut Context<Self>) {
        self.include_excluded = !self.include_excluded;
        self.sync_filter(cx);
        self.reload_kept(cx);
    }

    /// Asks again, saying so at once rather than leaving an old failure up.
    pub(in crate::features::files) fn retry(&mut self, cx: &mut Context<Self>) {
        self.clear_root_failure();
        self.reload_kept(cx);
        cx.notify();
    }

    /// Ends whatever listing is running. The rows it has stay; a root it never heard about stays not loaded.
    pub(in crate::features::files) fn stop_listing(&mut self, cx: &mut Context<Self>) {
        self.generation = self.generation.wrapping_add(1);
        self.loads.clear();
        self.observing = None;
        self.tree.cancel_loads();
        self.restoring_tree = None;
        self.revealing = None;
        self.drop_reload();
        if !self.tree.root_loaded() {
            self.started = true;
        }
        cx.notify();
    }

    pub(super) fn header(&self, cx: &mut Context<Self>) -> Div {
        let reason = self.read_only_reason(cx);
        let creatable = self.can_create(cx);
        let tip =
            |what: &'static str| -> SharedString { reason.clone().map_or(what.into(), Into::into) };
        h_flex()
            .px_2()
            .pb_1()
            .gap_0p5()
            .flex_none()
            .child(
                tool(ids::NEW_FILE, IconName::FilePlus, tip("New file"))
                    .disabled(!creatable)
                    .on_click(cx.listener(|pane, _, window, cx| {
                        pane.start_entry(EntryKind::NewFile, "", window, cx)
                    })),
            )
            .child(
                tool(ids::NEW_FOLDER, IconName::FolderPlus, tip("New folder"))
                    .disabled(!creatable)
                    .on_click(cx.listener(|pane, _, window, cx| {
                        pane.start_entry(EntryKind::NewFolder, "", window, cx)
                    })),
            )
            .child(
                if self.descending {
                    tool(
                        ids::SORT,
                        IconName::ArrowUpZA,
                        "Sorted Z to A. Click to sort A to Z.",
                    )
                } else {
                    tool(
                        ids::SORT,
                        IconName::ArrowDownAZ,
                        "Sorted A to Z. Click to sort Z to A.",
                    )
                }
                .on_click(cx.listener(|pane, _, _, cx| pane.toggle_sort(cx))),
            )
            .child(
                if self.include_excluded {
                    tool(ids::EXCLUDED, IconName::EyeOff, "Hide excluded files")
                } else {
                    tool(ids::EXCLUDED, IconName::Eye, "Show excluded files")
                }
                .on_click(cx.listener(|pane, _, _, cx| pane.toggle_excluded(cx))),
            )
            .child(if self.is_loading() {
                tool(ids::STOP, IconName::Square, "Stop listing files")
                    .on_click(cx.listener(|pane, _, _, cx| pane.stop_listing(cx)))
            } else {
                tool(
                    ids::REFRESH,
                    IconName::RefreshCw,
                    "List this project's files again",
                )
                .on_click(cx.listener(|pane, _, _, cx| pane.retry(cx)))
            })
    }
}
