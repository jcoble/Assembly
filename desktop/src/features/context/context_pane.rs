//! `ContextPane` owns the drawer's Context tab: the files the selected session's tool calls edited, deleted or moved,
//! newest first, each with its lines added and removed. Replaces SessionContextPanel.svelte's Files touched section.
//! Like it, the tab reads them (`rows/files`) when it's shown, and again when another session is picked while it's
//! shown, and doesn't follow the live rows: Chat already does, and nobody watches this list while it changes.

use std::rc::Rc;

use gpui_kit::component::dock::{BasePanel, Panel, PanelEvent, panel_handle, register_panel};
use gpui_kit::component::{ActiveTheme as _, h_flex, v_flex};
use gpui_kit::prelude::FluentBuilder as _;
use gpui_kit::*;
use serde::Deserialize;
use serde_json::json;

use crate::core::state::selected_session::SelectedSession;
use crate::features::session::client::session_client::{CallError, SessionClient};
use crate::features::session::client::socket_session_client::SocketSessionClient;
use crate::features::shell::default_layout::CONTEXT;

/// One file, as `rows/files` sends it (RowsFeature.cs).
#[derive(Clone, Debug, Deserialize, PartialEq)]
pub struct FileTouched {
    pub path: String,
    pub added: i64,
    pub removed: i64,
}

#[derive(Clone, Debug, PartialEq)]
enum Files {
    Reading,
    Read(Vec<FileTouched>),
    /// Why the read failed.
    Failed(String),
}

pub struct ContextPane {
    focus: FocusHandle,
    client: Rc<dyn SessionClient>,
    /// The kit shows this tab; only then does a pick read.
    active: bool,
    files: Files,
    _selected: Subscription,
    /// The read going now; dropping it cancels it.
    _read: Option<Task<()>>,
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
            files: Files::Reading,
            _selected: cx.observe(&SelectedSession::global(cx), |pane, _, cx| pane.read(cx)),
            _read: None,
        }
    }

    /// Reads the selected session's files, if the tab is shown.
    fn read(&mut self, cx: &mut Context<Self>) {
        if !self.active {
            return;
        }
        self.files = Files::Reading;
        self._read = None;
        cx.notify();
        let Some(session) = SelectedSession::global(cx)
            .read(cx)
            .session_id()
            .map(String::from)
        else {
            return;
        };
        let call = self.client.call(
            "rows",
            "files",
            json!({ "session": session }),
            Some("rows/files"),
            cx,
        );
        self._read = Some(cx.spawn(async move |pane, cx| {
            let answer = call.await.and_then(|files| {
                serde_json::from_value(files).map_err(|error| CallError::Failed(error.to_string()))
            });
            pane.update(cx, |pane, cx| {
                pane.files = match answer {
                    Ok(files) => Files::Read(files),
                    Err(CallError::Aborted) => return,
                    Err(error) => Files::Failed(error.reason()),
                };
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
        let picked = SelectedSession::global(cx).read(cx).session_id().is_some();
        let theme = cx.theme();
        let (muted, success, danger, accent) = (
            theme.muted_foreground,
            theme.success,
            theme.danger,
            theme.accent,
        );
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
                "Pick a session to see the files it has touched.".into(),
            ),
            Files::Reading => note("context-note-reading", "Reading this session…".into()),
            Files::Failed(why) => note(
                "context-note-failed",
                format!("Couldn't read files: {why}").into(),
            ),
            Files::Read(files) if files.is_empty() => note(
                "context-note-empty",
                "No files yet. Files this session edits are listed here, newest first.".into(),
            ),
            Files::Read(files) => v_flex().children(files.iter().map(|file| {
                let (folder, name) = file.path.rsplit_once('/').unwrap_or(("", &file.path));
                let selector = format!("touched-file-{}", file.path);
                h_flex()
                    .debug_selector(move || selector)
                    .h(px(28.))
                    .px_2()
                    .gap_2()
                    .rounded(px(6.))
                    .hover(|row| row.bg(accent))
                    .child(div().min_w_0().truncate().child(name.to_string()))
                    .child(
                        div()
                            .flex_1()
                            .min_w_0()
                            .truncate()
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
            })),
        };
        let count = match &self.files {
            Files::Read(files) if picked && !files.is_empty() => Some(files.len()),
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
    }
}

#[cfg(test)]
#[path = "context_pane_tests.rs"]
mod tests;
