//! Row actions use the existing native menu/dialog primitives and the feature's one owning-root call.
use super::FilesPane;
use crate::core::{
    intents::open_git_history::OpenGitHistory,
    socket::{client::SocketClient, outcome::Outcome},
};
use crate::features::files::{
    document_store::DocumentKey,
    entry::FileOwnership,
    file_action_form::{ActionExpected, FileActionForm, Finished},
};
use gpui_kit::component::WindowExt as _;
use gpui_kit::component::menu::{PopupMenu, PopupMenuItem};
use gpui_kit::*;
use serde_json::json;

#[derive(Clone)]
struct Copied {
    key: DocumentKey,
    cut: bool,
    source: Option<crate::core::socket::row_source::ExpectedRowSource>,
}
struct FileClipboard(Copied);
impl Global for FileClipboard {}

impl FilesPane {
    pub(super) fn open_action(
        &mut self,
        kind: &'static str,
        path: String,
        destination: Option<String>,
        expected_owner: Option<DocumentKey>,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        let Some(session) = self.session.clone() else {
            return;
        };
        let form = cx.new(|cx| {
            FileActionForm::new(
                session,
                kind,
                path,
                destination,
                ActionExpected {
                    owner: expected_owner,
                    source: self.source.clone(),
                    checkout: self.owner.clone(),
                },
                window,
                cx,
            )
        });
        self.action_event = Some(cx.subscribe(&form, |pane, _, finished: &Finished, cx| {
            if finished.moved.as_ref().is_some_and(|key| {
                cx.try_global::<FileClipboard>()
                    .is_some_and(|clipboard| &clipboard.0.key == key)
            }) {
                cx.remove_global::<FileClipboard>();
            }
            if pane.session.as_deref() == Some(finished.session.as_str()) {
                pane.reload_kept(cx);
            }
        }));
        window.open_dialog(cx, move |dialog, _, cx| {
            let busy = form.read(cx).busy();
            dialog
                .title(form.read(cx).title())
                .close_button(!busy)
                .overlay_closable(!busy)
                .keyboard(!busy)
                .footer(Empty)
                .child(form.clone())
        });
    }

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

    fn mutation_item(
        label: &'static str,
        kind: &'static str,
        pane: WeakEntity<Self>,
        path: String,
        source: Option<crate::core::socket::row_source::ExpectedRowSource>,
    ) -> PopupMenuItem {
        PopupMenuItem::new(label).on_click(move |_, window, cx| {
            let _ = pane.update(cx, |pane, cx| {
                if pane.source != source {
                    pane.action_error = Some("The file menu belongs to an earlier source.".into());
                    cx.notify();
                    return;
                }
                pane.open_action(kind, path.clone(), None, None, window, cx)
            });
        })
    }
    fn metadata_item(
        label: &'static str,
        action: &'static str,
        pane: WeakEntity<Self>,
        path: String,
        source: Option<crate::core::socket::row_source::ExpectedRowSource>,
    ) -> PopupMenuItem {
        PopupMenuItem::new(label).on_click(move |_, _, cx| {
            let _ = pane.update(cx, |pane, cx| {
                if pane.source != source {
                    pane.action_error = Some("The file menu belongs to an earlier source.".into());
                    cx.notify();
                    return;
                }
                pane.metadata_action(path.clone(), action, cx)
            });
        })
    }
    pub(super) fn file_menu(
        mut menu: PopupMenu,
        pane: WeakEntity<Self>,
        session: String,
        path: String,
        folder: bool,
        source: Option<crate::core::socket::row_source::ExpectedRowSource>,
        cx: &App,
    ) -> PopupMenu {
        if folder {
            menu = menu
                .item(Self::mutation_item(
                    "New file",
                    "createFile",
                    pane.clone(),
                    path.clone(),
                    source.clone(),
                ))
                .item(Self::mutation_item(
                    "New folder",
                    "createFolder",
                    pane.clone(),
                    path.clone(),
                    source.clone(),
                ));
            if let Some(copied) = cx
                .try_global::<FileClipboard>()
                .map(|clipboard| clipboard.0.clone())
            {
                let target = format!(
                    "{}/{}",
                    path,
                    copied.key.path.rsplit('/').next().unwrap_or_default()
                );
                let pane = pane.clone();
                let paste_source = source.clone();
                menu = menu.item(PopupMenuItem::new("Paste").on_click(move |_, window, cx| {
                    let _ = pane.update(cx, |pane, cx| {
                        if pane.source != paste_source || copied.source != paste_source {
                            pane.action_error =
                                Some("The copied item or destination source changed.".into());
                            cx.notify();
                            return;
                        }
                        pane.open_action(
                            if copied.cut { "move" } else { "copy" },
                            copied.key.path.clone(),
                            Some(target.clone()),
                            Some(copied.key.clone()),
                            window,
                            cx,
                        )
                    });
                }));
            }
            menu = menu.separator();
        } else {
            let timeline_session = session.clone();
            let timeline_path = path.clone();
            let timeline_source = source.clone();
            menu = menu.item(
                PopupMenuItem::new("Open timeline").on_click(move |_, window, cx| {
                    crate::features::files::file_view::FileView::open_history(
                        timeline_session.clone(),
                        timeline_path.clone(),
                        timeline_source.clone(),
                        window,
                        cx,
                    )
                }),
            );
            let history_path = path.clone();
            let history_source = source.clone();
            menu = menu
                .item(PopupMenuItem::new("Git: View file history").on_click(
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
        menu = menu
            .item(Self::metadata_item(
                "Cut",
                "cut",
                pane.clone(),
                path.clone(),
                source.clone(),
            ))
            .item(Self::metadata_item(
                "Copy",
                "copy",
                pane.clone(),
                path.clone(),
                source.clone(),
            ))
            .item(Self::metadata_item(
                "Copy path",
                "copyPath",
                pane.clone(),
                path.clone(),
                source.clone(),
            ))
            .separator()
            .item(Self::mutation_item(
                "Rename",
                "rename",
                pane.clone(),
                path.clone(),
                source.clone(),
            ))
            .item(Self::mutation_item(
                "Move to Trash",
                "trash",
                pane.clone(),
                path.clone(),
                source.clone(),
            ))
            .separator()
            .item(Self::metadata_item(
                "Reveal in Finder",
                "reveal",
                pane.clone(),
                path,
                source.clone(),
            ));
        menu.item(
            PopupMenuItem::new("Toggle excluded files").on_click(move |_, _, cx| {
                let _ = pane.update(cx, |pane, cx| pane.toggle_excluded(cx));
            }),
        )
    }
}
