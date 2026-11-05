//! The right-click menu and the clipboard, with the old panel's items; writes run in `files_pane_run.rs`.
use super::FilesPane;
use crate::core::socket::row_source::ExpectedRowSource;
use crate::core::{
    intents::open_git_history::OpenGitHistory,
    socket::{client::SocketClient, outcome::Outcome},
};
use crate::features::files::{
    document_store::DocumentKey, entry::FileOwnership, file_entry_bar::EntryKind,
};
use gpui_kit::component::ActiveTheme as _;
use gpui_kit::component::menu::{PopupMenu, PopupMenuItem};
use gpui_kit::component::tooltip::Tooltip;
use gpui_kit::prelude::FluentBuilder as _;
use gpui_kit::*;
use serde_json::json;

#[derive(Clone)]
pub(super) struct Copied {
    pub(super) key: DocumentKey,
    pub(super) cut: bool,
    pub(super) source: Option<ExpectedRowSource>,
}
pub(super) struct FileClipboard(pub(super) Copied);
/// What a menu item does to the pane.
type MenuAct = Box<dyn Fn(&mut FilesPane, &mut Window, &mut Context<FilesPane>)>;
impl Global for FileClipboard {}

impl FilesPane {
    /// The chip for a row on the clipboard: its word, when `path` in this folder is what was cut or copied.
    pub(in crate::features::files) fn clipboard_chip(
        &self,
        path: &str,
        cx: &App,
    ) -> Option<&'static str> {
        let copied = &cx.try_global::<FileClipboard>()?.0;
        let owner = self.owner.as_ref()?;
        (copied.source == self.source
            && copied.key.path == path
            && copied.key.machine == owner.machine
            && copied.key.checkout_root == owner.checkout_root)
            .then_some(if copied.cut { "cut" } else { "copy" })
    }

    pub(in crate::features::files) fn metadata_action(
        &mut self,
        path: String,
        action: &'static str,
        cx: &mut Context<Self>,
    ) {
        let (Some(session), Some(socket)) = (self.session.clone(), SocketClient::try_global(cx))
        else {
            return;
        };
        let generation = self.generation;
        let call = socket.read(cx).call::<FileOwnership>(
            "files",
            "owner",
            json!({"session":session,"expectedSource":self.source,"expectedOwner":self.owner}),
            None,
        );
        self.action_loading = Some(cx.spawn(async move |pane, cx| {
            let answer = call.await;
            let _ = pane.update(cx, |pane, cx| {
                if pane.generation != generation {
                    return;
                }
                pane.action_loading = None;
                match answer {
                    Outcome::Ok(owner) => {
                        let local = socket.read(cx).engine_machine().as_deref()
                            == Some(owner.machine.as_str());
                        let full = std::path::Path::new(&owner.checkout_root).join(&path);
                        match action {
                            "copy" | "cut" => cx.set_global(FileClipboard(Copied {
                                key: DocumentKey {
                                    machine: owner.machine,
                                    checkout_root: owner.checkout_root,
                                    path,
                                },
                                cut: action == "cut",
                                source: pane.source.clone(),
                            })),
                            "copyPath" => {
                                let text = if local {
                                    full.to_string_lossy().into_owned()
                                } else {
                                    format!("{}:{}", owner.machine, full.display())
                                };
                                cx.write_to_clipboard(ClipboardItem::new_string(text));
                            }
                            "reveal" if local => cx.reveal_path(&full),
                            "reveal" => {
                                pane.action_error = Some(
                                    "Reveal is available only for files owned by this desktop."
                                        .into(),
                                )
                            }
                            _ => {}
                        }
                    }
                    Outcome::Failed(error) => pane.action_error = Some(error.message),
                    Outcome::Aborted => {
                        pane.action_error = Some("The owning checkout could not be read.".into())
                    }
                }
                cx.notify();
            });
        }));
    }

    /// A menu item that runs `act` on the pane, or, with `blocked`, shows why it can't. `danger` colours it red.
    fn menu_item(
        label: &'static str,
        blocked: Option<String>,
        danger: bool,
        pane: WeakEntity<Self>,
        source: Option<ExpectedRowSource>,
        act: impl Fn(&mut Self, &mut Window, &mut Context<Self>) + 'static,
    ) -> PopupMenuItem {
        if blocked.is_none() && !danger {
            return PopupMenuItem::new(label)
                .on_click(move |_, window, cx| Self::on_menu(&pane, &source, &act, window, cx));
        }
        let tip: Option<SharedString> = blocked.clone().map(Into::into);
        let item = PopupMenuItem::element(move |_, cx| {
            let theme = cx.theme();
            let color = if tip.is_some() {
                theme.muted_foreground
            } else {
                theme.danger
            };
            div()
                .id(label)
                .text_color(color)
                .child(label)
                .when_some(tip.clone(), |item, tip| {
                    item.tooltip(move |window, cx| Tooltip::new(tip.clone()).build(window, cx))
                })
        });
        if blocked.is_some() {
            item.disabled(true)
        } else {
            item.on_click(move |_, window, cx| Self::on_menu(&pane, &source, &act, window, cx))
        }
    }

    fn on_menu(
        pane: &WeakEntity<Self>,
        source: &Option<ExpectedRowSource>,
        act: &impl Fn(&mut Self, &mut Window, &mut Context<Self>),
        window: &mut Window,
        cx: &mut App,
    ) {
        let _ = pane.update(cx, |pane, cx| {
            if &pane.source != source {
                pane.action_error = Some("The file menu belongs to an earlier source.".into());
                cx.notify();
                return;
            }
            act(pane, window, cx)
        });
    }

    /// The right-click menu, with the old panel's items. Writes are disabled with the reason in a read-only folder.
    pub(super) fn file_menu(
        menu: PopupMenu,
        pane: WeakEntity<Self>,
        session: String,
        path: String,
        folder: bool,
        source: Option<ExpectedRowSource>,
        cx: &App,
    ) -> PopupMenu {
        let Some(this) = pane.upgrade() else {
            return menu;
        };
        let this = this.read(cx);
        let blocked = this.write_block(cx);
        let local = this.owner.as_ref().is_some_and(|owner| {
            SocketClient::try_global(cx)
                .and_then(|socket| socket.read(cx).engine_machine())
                .as_deref()
                == Some(owner.machine.as_str())
        });
        let excluded = if this.include_excluded {
            "Hide Excluded Files"
        } else {
            "Show Excluded Files"
        };
        let pasteable = cx.has_global::<FileClipboard>();
        let item = |label, blocked: Option<String>, danger, act: MenuAct| {
            Self::menu_item(label, blocked, danger, pane.clone(), source.clone(), act)
        };
        let mut menu = menu.min_w(px(220.));
        if folder {
            let (new_file, new_folder, paste_into) = (path.clone(), path.clone(), path.clone());
            menu = menu
                .item(item(
                    "New File",
                    blocked.clone(),
                    false,
                    Box::new(move |pane, window, cx| {
                        pane.start_entry(EntryKind::NewFile, &new_file, window, cx)
                    }),
                ))
                .item(item(
                    "New Folder",
                    blocked.clone(),
                    false,
                    Box::new(move |pane, window, cx| {
                        pane.start_entry(EntryKind::NewFolder, &new_folder, window, cx)
                    }),
                ))
                .item(item(
                    "Paste",
                    blocked
                        .clone()
                        .or_else(|| (!pasteable).then(|| "Copy or cut a file first.".into())),
                    false,
                    Box::new(move |pane, window, cx| pane.paste_into(&paste_into, window, cx)),
                ))
                .separator();
        } else {
            let (timeline_path, history_path, history_source) =
                (path.clone(), path.clone(), source.clone());
            menu = menu
                .item(item(
                    "Open Timeline",
                    None,
                    false,
                    Box::new(move |pane, _, cx| pane.open_timeline(timeline_path.clone(), cx)),
                ))
                .item(PopupMenuItem::new("Git: View File History").on_click(
                    move |_, window, cx| {
                        window.dispatch_action(
                            Box::new(OpenGitHistory {
                                session_id: session.clone(),
                                path: Some(history_path.clone()),
                                expected_source: history_source.clone(),
                                checkout: None,
                            }),
                            cx,
                        )
                    },
                ))
                .separator();
        }
        let (cut, copy, copy_path, rename, delete, reveal) = (
            path.clone(),
            path.clone(),
            path.clone(),
            path.clone(),
            path.clone(),
            path,
        );
        menu.item(item(
            "Cut",
            blocked.clone(),
            false,
            Box::new(move |pane, _, cx| pane.metadata_action(cut.clone(), "cut", cx)),
        ))
        .item(item(
            "Copy",
            None,
            false,
            Box::new(move |pane, _, cx| pane.metadata_action(copy.clone(), "copy", cx)),
        ))
        .item(item(
            "Copy Path",
            None,
            false,
            Box::new(move |pane, _, cx| pane.metadata_action(copy_path.clone(), "copyPath", cx)),
        ))
        .separator()
        .item(item(
            "Rename",
            blocked.clone(),
            false,
            Box::new(move |pane, window, cx| {
                pane.start_entry(EntryKind::Rename, &rename, window, cx)
            }),
        ))
        .item(item(
            "Delete",
            blocked,
            true,
            Box::new(move |pane, window, cx| pane.delete_entry(delete.clone(), window, cx)),
        ))
        .separator()
        .item(item(
            "Reveal in Finder",
            (!local).then(|| "Reveal is available only for files owned by this desktop.".into()),
            false,
            Box::new(move |pane, _, cx| pane.metadata_action(reveal.clone(), "reveal", cx)),
        ))
        .item(item(
            excluded,
            None,
            false,
            Box::new(|pane, _, cx| pane.toggle_excluded(cx)),
        ))
    }
}
