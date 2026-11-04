//! `ContextPane` owns the drawer's Context tab: the files the selected session's tool calls edited, deleted or moved,
//! newest first, each with its lines added and removed, and the images its prompts carried (attachments_section.rs).
//! Replaces SessionContextPanel.svelte's Files touched and Attachments sections. Like it, the tab reads both
//! (`rows/files`, `rows/attachments`) when it's shown, and again when another session is picked while it's shown, and
//! doesn't follow the live rows: Chat already does, and nobody watches these lists while they change.

use crate::core::theme::text::{TextRole, TextRoleExt as _};
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

#[derive(Clone,PartialEq)]
struct ContextScope {
    session:String,key:Option<crate::core::engine::SessionKey>,source:Option<crate::core::socket::row_source::ExpectedRowSource>,valid:bool,
}
pub struct ContextPane {
    context_scope:Option<ContextScope>,read_serial:u64,
    focus: FocusHandle,
    client: Rc<dyn SessionClient>,
    /// The kit shows this tab; only then does a pick read.
    active: bool,
    files: Listing<FileTouched>,
    /// Each attached image's (path, name), newest prompt first.
    attachments: Listing<(String, String)>,
    /// The machine that has a remote session's images, from the same read.
    machine: Option<String>,
    /// The read left out older prompts' images.
    more: bool,
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
            context_scope:None,read_serial:0,
            focus: cx.focus_handle(),
            client,
            active: false,
            files: Listing::Reading,
            attachments: Listing::Reading,
            machine: None,
            more: false,
            open_failed: None,
            _selected: cx.observe(&SelectedSession::global(cx), |pane, _, cx| pane.read(cx)),
            _read: None,
            _open: None,
        }
    }

    /// Reads the selected session's files and attachments, if the tab is shown. A hidden tab drops the last session's
    /// lists, so the frame before it's shown again never draws them.
    fn read(&mut self, cx: &mut Context<Self>) {
        self.read_serial+=1;let serial=self.read_serial;self.context_scope=None;
        (
            self.files,
            self.attachments,
            self.machine,
            self.more,
            self.open_failed,
        ) = (Listing::Reading, Listing::Reading, None, false, None);
        (self._read, self._open) = (None, None);
        cx.notify();
        if !self.active {
            return;
        }
        let selected=SelectedSession::global(cx);
        let selected=selected.read(cx);
        let Some(session)=selected.session_id().map(str::to_owned)else{return;};
        let scope=ContextScope{session:session.clone(),key:selected.key().cloned(),source:selected.source().cloned(),valid:true};
        if (scope.key.is_some()&&scope.source.is_none())||scope.source.as_ref().is_some_and(|source|scope.key.as_ref().is_none_or(|key|key.machine!=source.machine||key.id!=session)) {
            self.files=Listing::Failed("The selected session source is unavailable.".into());self.attachments=Listing::Failed("The selected session source is unavailable.".into());return;
        }
        self.context_scope=Some(scope.clone());
        let mut args=json!({"session":session});
        if let Some(source)=&scope.source{args["expectedSource"]=json!(source);}
        let files = self
            .client
            .call("rows", "files", args.clone(), Some("rows/files"), cx);
        let attachments =
            self.client
                .call("rows", "attachments", args, Some("rows/attachments"), cx);
        self._read = Some(cx.spawn(async move |pane, cx| {
            let files = files.await;
            let attachments = attachments.await;
            let refused=matches!(&files,Err(CallError::Refused{code,..}) if code=="source_changed")||matches!(&attachments,Err(CallError::Refused{code,..}) if code=="source_changed");
            pane.update(cx, |pane, cx| {
                if pane.read_serial!=serial||pane.context_scope.as_ref()!=Some(&scope){return;}
                if refused {
                    if let Some(held)=pane.context_scope.as_mut(){held.valid=false;}
                    if let Some(source)=&scope.source{crate::features::session::view::session_pane::SessionPane::refuse_image_source(&scope.session,source,"The Context session source changed.".into(),cx);}
                }
                pane.files = touched(files);
                (pane.attachments, pane.machine, pane.more) = match attachments {
                    Ok(answer) => attached(&answer),
                    Err(error) => (Listing::Failed(error.reason()), None, false),
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
            false => {self._read=None;self._open=None;self.context_scope=None;self.read_serial+=1;}
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
            .text_role(TextRole::Body, cx)
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
                    .child(self.attachments_section(cx))
            })
    }
}

/// `rows/files`' answer, or why it couldn't be read.
fn touched(answer: Result<serde_json::Value, CallError>) -> Listing<FileTouched> {
    match answer.and_then(|list| {
        serde_json::from_value(list).map_err(|error| CallError::Failed(error.to_string()))
    }) {
        Ok(list) => Listing::Read(list),
        Err(error) => Listing::Failed(error.reason()),
    }
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
use attachments_section::attached;
#[path = "listing.rs"]
mod listing;

#[cfg(test)]
#[path = "context_pane_tests.rs"]
mod tests;
