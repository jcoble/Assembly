//! The file history card under the tree, from the old FileHistoryPane.svelte: it starts closed, follows whichever
//! changed last of the selection and the file on screen in the editor, and reads only while open and shown.
use super::header::tool;
use super::{FileView, FilesPane, TREE_LINE};
use crate::core::intents::open_git_history::OpenGitHistory;
use crate::core::state::active_file::ActiveFile;
use crate::core::theme::text::{TextRole, TextRoleExt as _};
use crate::core::ui::access::ExposedElement as _;
use crate::core::ui::access::control_ids::files as ids;
use crate::core::ui::row_drop_down::{GrowingBody, RowDropDownEvent, RowDropDowns};
use crate::features::files::file_history::HistoryFile;
use crate::features::files::history_target::Picked;
use gpui_kit::assets::IconName;
use gpui_kit::component::tooltip::Tooltip;
use gpui_kit::component::{ActiveTheme as _, Icon, h_flex, v_flex};
use gpui_kit::prelude::FluentBuilder as _;
use gpui_kit::*;

/// The card's key in its drop-down.
const CARD: &str = "files-history";

impl FilesPane {
    /// What the card hears: the editor's file, and its own opening and closing.
    pub(super) fn history_heard(
        drops: &Entity<RowDropDowns>,
        cx: &mut Context<Self>,
    ) -> [Subscription; 3] {
        let active = ActiveFile::global(cx);
        [
            cx.observe(&active, |pane, _, cx| pane.follow_active_file(cx)),
            cx.subscribe(drops, |pane, _, event: &RowDropDownEvent, cx| {
                if !matches!(event, RowDropDownEvent::Moved(_)) {
                    pane.sync_history(cx);
                }
            }),
            cx.observe(drops, |_, _, cx| cx.notify()),
        ]
    }

    pub(in crate::features::files) fn history_open(&self, cx: &App) -> bool {
        self.history_drops.read(cx).is_open(&CARD.into())
    }

    /// The editor's file changed: the card follows it when it's this tree's.
    pub(super) fn follow_active_file(&mut self, cx: &mut Context<Self>) {
        let picked = match ActiveFile::global(cx).read(cx).file() {
            None => Picked::Nothing,
            Some(file)
                if self.session.as_deref() == Some(file.session.as_str())
                    && self.source == file.source =>
            {
                Picked::File(file.path.clone())
            }
            Some(_) => Picked::Elsewhere,
        };
        self.history_target.editor(picked);
        self.sync_history(cx);
    }

    /// Brings the card up to date with the selection, the editor's file, and whether it's open and shown.
    pub(super) fn sync_history(&mut self, cx: &mut Context<Self>) {
        let picked = match self.selected.as_deref() {
            None => Picked::Nothing,
            Some(path) => match self.tree.entry_state(path) {
                Some((true, _)) => Picked::Folder,
                Some((false, _)) => Picked::File(path.to_string()),
                // Not in the tree yet: a reload will show it, and this runs again then.
                None => Picked::Nothing,
            },
        };
        self.history_target.selected(picked);
        let file = self
            .session
            .clone()
            .zip(self.history_target.current().map(str::to_string))
            .map(|(session, path)| HistoryFile {
                session,
                path,
                source: self.source.clone(),
            });
        let active = self.shown && self.history_open(cx);
        self.history.update(cx, |list, cx| {
            if !active {
                list.set_active(false, cx);
            }
            list.show(file, cx);
            if active {
                list.set_active(true, cx);
            }
        });
        cx.notify();
    }

    pub(in crate::features::files) fn toggle_history(&mut self, cx: &mut Context<Self>) {
        let list = self.history.clone();
        self.history_drops.update(cx, |drops, cx| {
            drops.toggle(CARD.into(), move |_, _| list.clone().into_any_element(), cx)
        });
    }

    /// Open Timeline: selects the file, opens the card and shows the file's history.
    pub(in crate::features::files) fn open_timeline(
        &mut self,
        path: String,
        cx: &mut Context<Self>,
    ) {
        // The selection is the latest change even when it was already this file.
        self.history_target.selected(Picked::Nothing);
        self.selected = Some(path);
        if !self.history_open(cx) {
            self.toggle_history(cx);
        }
        self.sync_history(cx);
    }

    fn history_path(&self) -> Option<(String, String)> {
        self.session
            .clone()
            .zip(self.history_target.current().map(str::to_string))
    }

    /// Go to File: opens the file as a preview and shows it in the tree.
    fn go_to_file(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        let Some((session, path)) = self.history_path() else {
            return;
        };
        FileView::open_at(
            session,
            path.clone(),
            (None, None),
            false,
            self.source.clone(),
            window,
            cx,
        );
        self.revealing = Some((path, false));
        self.continue_reveal(cx);
    }

    fn open_large(&self, window: &mut Window, cx: &mut Context<Self>) {
        let Some((session, path)) = self.history_path() else {
            return;
        };
        window.dispatch_action(
            Box::new(OpenGitHistory {
                session_id: session,
                expected_source: self.source.clone(),
                path: Some(path),
                checkout: None,
            }),
            cx,
        );
    }

    pub(super) fn history_card(&self, cx: &mut Context<Self>) -> AnyElement {
        let open = self.history_open(cx);
        let target = self.history_target.current().map(str::to_string);
        let theme = cx.theme();
        let (muted, hover, card, radius) = (
            theme.muted_foreground,
            theme.accent,
            theme.muted,
            theme.radius,
        );
        let name = target.as_ref().map(|path| {
            (
                path.rsplit('/').next().unwrap_or(path).to_string(),
                path.clone(),
            )
        });
        let trigger = h_flex()
            .id(ids::HISTORY)
            .exposed(Role::Button, ids::HISTORY, "File history")
            .flex_1()
            .min_w_0()
            .gap_2()
            .px_3()
            .py_2()
            .cursor_pointer()
            .text_role(TextRole::Body, cx)
            .hover(|trigger| trigger.bg(hover))
            .child(
                Icon::new(if open {
                    IconName::ChevronDown
                } else {
                    IconName::ChevronRight
                })
                .size(px(12.))
                .text_color(muted),
            )
            .child(
                Icon::new(IconName::RotateCcwClock)
                    .size(px(14.))
                    .text_color(muted),
            )
            .child(
                div()
                    .flex_none()
                    .whitespace_nowrap()
                    .font_weight(FontWeight::SEMIBOLD)
                    .child("File history"),
            )
            .when_some(name, |trigger, (name, path)| {
                trigger.child(
                    div()
                        .id("files-history-file")
                        .min_w_0()
                        .overflow_hidden()
                        .text_ellipsis()
                        .whitespace_nowrap()
                        .text_color(muted)
                        .child(name)
                        .tooltip(move |window, cx| Tooltip::new(path.clone()).build(window, cx)),
                )
            })
            .on_click(cx.listener(|pane, _, _, cx| pane.toggle_history(cx)));
        let actions = (open && target.is_some()).then(|| {
            h_flex()
                .flex_none()
                .pr_1()
                .child(
                    tool(ids::HISTORY_GO, IconName::FileUp, "Go to File")
                        .on_click(cx.listener(|pane, _, window, cx| pane.go_to_file(window, cx))),
                )
                .child(
                    tool(
                        ids::HISTORY_REFRESH,
                        IconName::RefreshCw,
                        "Refresh file history",
                    )
                    .on_click(cx.listener(|pane, _, _, cx| {
                        pane.history.update(cx, |list, cx| list.refresh(cx))
                    })),
                )
                .child(
                    tool(
                        ids::HISTORY_LARGE,
                        IconName::Maximize2,
                        "Open file history in the large view",
                    )
                    .on_click(cx.listener(|pane, _, window, cx| pane.open_large(window, cx))),
                )
        });
        v_flex()
            .flex_none()
            .m_2()
            .rounded(radius)
            .bg(card)
            .overflow_hidden()
            .child(
                h_flex()
                    .min_h(px(30.))
                    .items_center()
                    .child(trigger)
                    .children(actions),
            )
            .child(
                GrowingBody::new(CARD.into(), self.history_drops.clone(), TREE_LINE).max_rows(None),
            )
            .into_any_element()
    }
}
