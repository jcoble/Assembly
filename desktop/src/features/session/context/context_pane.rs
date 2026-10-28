//! `ContextPane` owns the drawer's Context tab: the files the selected session's tool calls edited, deleted or moved,
//! newest first, each with its lines added and removed, and the images its prompts carried (attachments_section.rs).
//! Replaces SessionContextPanel.svelte's Files touched and Attachments sections. Like it, the tab reads both
//! (`rows/files`, `rows/attachments`) when it's shown, and again when another session is picked while it's shown, and
//! doesn't follow the live rows: Chat already does, and nobody watches these lists while they change.

use std::rc::Rc;

use gpui_kit::component::dock::{BasePanel, Panel, PanelEvent, panel_handle, register_panel};
use gpui_kit::component::{ActiveTheme as _, h_flex, v_flex};
use gpui_kit::prelude::FluentBuilder as _;
use gpui_kit::*;
use serde::Deserialize;
use serde_json::json;

use crate::core::state::selected_session::SelectedSession;
use crate::core::state::sessions::Sessions;
use crate::features::session::client::session_client::{CallError, SessionClient};
use crate::features::session::client::socket_session_client::SocketSessionClient;
use crate::features::session::composer::attachment::Attachment;
use listing::Listing;

/// The layout's name for the tab (default_layout.rs).
const CONTEXT: &str = "Context";

/// One file, as `rows/files` sends it (RowsFeature.cs).
#[derive(Clone, Debug, Deserialize, PartialEq)]
pub struct FileTouched {
    pub path: String,
    pub added: i64,
    pub removed: i64,
}

/// One prompt's attachment lines, as `rows/attachments` sends them (RowsFeature.cs).
#[derive(Deserialize)]
struct PromptAttachments {
    lines: String,
}

pub struct ContextPane {
    focus: FocusHandle,
    client: Rc<dyn SessionClient>,
    /// The kit shows this tab; only then does a pick read.
    active: bool,
    files: Listing<FileTouched>,
    /// Each attached image's (path, name), newest prompt first.
    attachments: Listing<(String, String)>,
    /// Why the last image clicked didn't open.
    open_failed: Option<String>,
    _selected: Subscription,
    /// The reads going now; dropping it cancels them.
    _read: Option<Task<()>>,
    /// The image opening now.
    _open: Option<Task<()>>,
}

impl ContextPane {
    /// Registers the tab under the layout's Context name, after the placeholders, since the last registration wins.
    pub fn register(cx: &mut App) {
        register_panel(cx, CONTEXT, |_, _, cx| {
            panel_handle(cx.new(|cx| ContextPane::new(Rc::new(SocketSessionClient), cx)))
        });
    }

    pub fn new(client: Rc<dyn SessionClient>, cx: &mut Context<Self>) -> Self {
        Self {
            focus: cx.focus_handle(),
            client,
            active: false,
            files: Listing::Reading,
            attachments: Listing::Reading,
            open_failed: None,
            _selected: cx.observe(&SelectedSession::global(cx), |pane, _, cx| pane.read(cx)),
            _read: None,
            _open: None,
        }
    }

    /// Reads the selected session's files and attachments, if the tab is shown. A hidden tab drops the last session's
    /// lists, so the frame before it's shown again never draws them.
    fn read(&mut self, cx: &mut Context<Self>) {
        (self.files, self.attachments, self.open_failed) =
            (Listing::Reading, Listing::Reading, None);
        (self._read, self._open) = (None, None);
        cx.notify();
        if !self.active {
            return;
        }
        let Some(session) = SelectedSession::global(cx)
            .read(cx)
            .session_id()
            .map(String::from)
        else {
            return;
        };
        let args = json!({ "session": session });
        let files = self
            .client
            .call("rows", "files", args.clone(), Some("rows/files"), cx);
        let attachments =
            self.client
                .call("rows", "attachments", args, Some("rows/attachments"), cx);
        self._read = Some(cx.spawn(async move |pane, cx| {
            let files = listing(files.await);
            let attachments = listing::<PromptAttachments>(attachments.await);
            pane.update(cx, |pane, cx| {
                pane.files = files;
                pane.attachments = attachments.map(|prompts| {
                    prompts
                        .iter()
                        .flat_map(|prompt| Attachment::split_lines(&prompt.lines).1)
                        .map(|(path, name, _)| (path, name))
                        .collect()
                });
                cx.notify();
            })
            .ok();
        }));
    }
}

impl EventEmitter<PanelEvent> for ContextPane {}

impl Focusable for ContextPane {
    fn focus_handle(&self, _: &App) -> FocusHandle {
        self.focus.clone()
    }
}

impl BasePanel for ContextPane {
    fn panel_name(&self) -> &'static str {
        CONTEXT
    }

    /// A default layout pane: a permanent tab, with no close button.
    fn closable(&self, _: &App) -> bool {
        false
    }

    /// The kit tells the tab when it's shown (its pill picked, or the drawer opened on it) and when it's hidden.
    fn set_active(&mut self, active: bool, _: &mut Window, cx: &mut Context<Self>) {
        self.active = active;
        match active {
            true => self.read(cx),
            false => self._read = None,
        }
    }
}

impl Panel for ContextPane {
    fn title(&mut self, _: &mut Window, _: &mut Context<Self>) -> impl IntoElement {
        CONTEXT
    }
}

impl Render for ContextPane {
    fn render(&mut self, _: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        let session = SelectedSession::global(cx)
            .read(cx)
            .session_id()
            .map(String::from);
        let picked = session.is_some();
        let remote = session.and_then(|session| machine_of(&session, cx));
        let theme = cx.theme();
        let (muted, success, danger) = (theme.muted_foreground, theme.success, theme.danger);
        let note = |id: &'static str, text: SharedString| {
            div()
                .debug_selector(move || id.into())
                .px_2()
                .text_color(muted)
                .child(text)
        };
        let body = match &self.files {
            _ if !picked => note(
                "context-note-pick",
                "Pick a session to see the files it has touched and the images attached to it."
                    .into(),
            ),
            Listing::Reading => note("context-note-reading", "Reading this session…".into()),
            Listing::Failed(why) => note(
                "context-note-failed",
                format!("Couldn't read files: {why}").into(),
            ),
            Listing::Read(files) if files.is_empty() => note(
                "context-note-empty",
                "No files yet. Files this session edits are listed here, newest first.".into(),
            ),
            Listing::Read(files) => v_flex().children(files.iter().zip(names(files)).map(
                |(file, (name, folder))| {
                    let selector = format!("touched-file-{}", file.path);
                    h_flex()
                        .debug_selector(move || selector)
                        .h(px(28.))
                        .px_2()
                        .gap_2()
                        .child(div().min_w_0().truncate().child(name.to_string()))
                        .child(
                            div()
                                .flex_1()
                                .min_w_0()
                                .truncate()
                                // A path's end says more than its start.
                                .text_ellipsis_start()
                                .text_color(muted)
                                .child(folder.to_string()),
                        )
                        .when(file.added > 0, |row| {
                            row.child(
                                div()
                                    .flex_none()
                                    .text_color(success)
                                    .child(format!("+{}", file.added)),
                            )
                        })
                        .when(file.removed > 0, |row| {
                            row.child(
                                div()
                                    .flex_none()
                                    .text_color(danger)
                                    .child(format!("-{}", file.removed)),
                            )
                        })
                },
            )),
        };
        let count = match &self.files {
            Listing::Read(files) if picked && !files.is_empty() => Some(files.len()),
            _ => None,
        };
        v_flex()
            .id("context-pane")
            .track_focus(&self.focus)
            .size_full()
            .overflow_y_scroll()
            .p_2()
            .gap_1()
            .text_size(px(13.))
            .child(
                h_flex()
                    .px_2()
                    .h(px(28.))
                    .gap_2()
                    .font_weight(FontWeight::SEMIBOLD)
                    .child("Files touched")
                    .when_some(count, |heading, count| {
                        heading.child(div().text_color(muted).child(count.to_string()))
                    }),
            )
            .child(body)
            .when(picked, |pane| {
                pane.child(div().h(px(8.)))
                    .child(self.attachments_section(remote, cx))
            })
    }
}

/// Answers a list, or why it couldn't be read.
fn listing<T: for<'de> Deserialize<'de>>(
    answer: Result<serde_json::Value, CallError>,
) -> Listing<T> {
    match answer.and_then(|list| {
        serde_json::from_value(list).map_err(|error| CallError::Failed(error.to_string()))
    }) {
        Ok(list) => Listing::Read(list),
        Err(error) => Listing::Failed(error.reason()),
    }
}

/// The machine a remote session runs on; `None` for one on this computer, or one the list hasn't sent.
fn machine_of(session: &str, cx: &App) -> Option<String> {
    Sessions::try_global(cx)?
        .read(cx)
        .items()
        .iter()
        .find(|item| item.session_id == session)?
        .machine
        .clone()
        .filter(|machine| !machine.is_empty())
}

/// Each file's name and its folder below the folder all the files share, which is usually the session's checkout: an
/// agent sends whole paths. A lone file shows no folder.
fn names(files: &[FileTouched]) -> Vec<(&str, &str)> {
    fn folder(path: &str) -> &str {
        path.rsplit_once('/').map_or("", |(folder, _)| folder)
    }
    let mut shared = files.first().map_or("", |file| folder(&file.path));
    for file in files {
        while !shared.is_empty()
            && !file
                .path
                .strip_prefix(shared)
                .is_some_and(|rest| rest.starts_with('/'))
        {
            shared = folder(shared);
        }
    }
    files
        .iter()
        .map(|file| {
            let rest = file.path[shared.len()..].trim_start_matches('/');
            let (folder, name) = rest.rsplit_once('/').unwrap_or(("", rest));
            (name, folder)
        })
        .collect()
}

#[path = "attachments_section.rs"]
mod attachments_section;
#[path = "listing.rs"]
mod listing;

#[cfg(test)]
#[path = "context_pane_tests.rs"]
mod tests;
