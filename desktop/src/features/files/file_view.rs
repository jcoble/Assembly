//! `FileView` shows one file of a session's folder in a center tab, read-only, with line numbers (`files/read`). It reads
//! the first part when first shown and the next part as the list scrolls near the end of what it holds (`FileText`).
//! The part being read is a task the view holds, so closing the tab drops it and cancels its call. Plain text only: the
//! kit's highlighter needs gpui-component's `tree-sitter` or `syntect` feature, which this build doesn't turn on. The tab
//! is saved with the layout under the "File" pane's name and comes back read afresh.

use gpui_kit::component::dock::{
    BasePanel, Panel, PanelEvent, PanelId, PanelInfo, PanelState, panel_handle, register_panel,
};
use gpui_kit::component::{ActiveTheme as _, h_flex, v_flex};
use gpui_kit::prelude::FluentBuilder as _;
use gpui_kit::*;
use serde_json::{Value, json};

use super::entry::FileChunk;
use super::file_text::FileText;
use crate::core::socket::client::SocketClient;
use crate::core::socket::outcome::Outcome;
use crate::core::ui::center_panes::CenterPanes;

#[cfg(test)]
#[path = "file_view_tests.rs"]
mod tests;

/// The pane's name in the saved layout.
pub const FILE: &str = "File";
const LINE: Pixels = px(20.);
const TEXT: Pixels = px(13.);

pub struct FileView {
    focus: FocusHandle,
    pub(super) session: String,
    pub(super) path: String,
    pub(super) text: FileText,
    /// The part being read; dropping it cancels its call.
    pub(super) loading: Option<Task<()>>,
    started: bool,
    list: UniformListScrollHandle,
}

impl FileView {
    /// Restores a file tab from the saved layout.
    pub fn register(cx: &mut App) {
        register_panel(cx, FILE, |build, _, cx| {
            let saved = match build.state().info.clone() {
                PanelInfo::Panel(saved) => saved,
                _ => Value::Null,
            };
            let text = |key: &str| saved[key].as_str().unwrap_or_default().to_string();
            let (session, path) = (text("session"), text("path"));
            let view = cx.new(|cx| FileView::new(session.clone(), path.clone(), cx));
            CenterPanes::remember(key(&session, &path), PanelId::from(view.entity_id()), cx);
            panel_handle(view)
        });
    }

    /// Opens the session's file at `path` in the center, or brings its tab forward.
    pub fn open(session: String, path: String, window: &mut Window, cx: &mut App) {
        CenterPanes::open(key(&session, &path), window, cx, |_, cx| {
            cx.new(|cx| FileView::new(session, path, cx))
        });
    }

    pub fn new(session: String, path: String, cx: &mut Context<Self>) -> Self {
        FileView {
            focus: cx.focus_handle(),
            session,
            path,
            text: FileText::default(),
            loading: None,
            started: false,
            list: UniformListScrollHandle::new(),
        }
    }

    pub(super) fn load_part(&mut self, offset: u64, cx: &mut Context<Self>) {
        let Some(socket) = SocketClient::try_global(cx) else {
            self.text
                .loaded(offset, Err("The backend isn't running.".into()));
            return;
        };
        let args = json!({ "session": self.session, "path": self.path, "offset": offset });
        let call = socket
            .read(cx)
            .call::<FileChunk>("files", "read", args, None);
        self.loading = Some(cx.spawn(async move |view, cx| {
            let outcome = call.await;
            let _ = view.update(cx, |view, cx| {
                view.loading = None;
                let answer = match outcome {
                    Outcome::Ok(chunk) => Ok(chunk),
                    Outcome::Failed(failure) => Err(failure.message),
                    Outcome::Aborted => {
                        Err("The connection dropped; open the file again to retry.".into())
                    }
                };
                if let Some(again) = view.text.loaded(offset, answer) {
                    view.load_part(again, cx);
                }
                cx.notify();
            });
        }));
    }

    fn line(&self, ix: usize, gutter: Pixels, muted: Hsla) -> AnyElement {
        h_flex()
            .h(LINE)
            .child(
                div()
                    .w(gutter)
                    .flex_none()
                    .pr_3()
                    .text_right()
                    .text_color(muted)
                    .child((ix + 1).to_string()),
            )
            .child(
                div()
                    .whitespace_nowrap()
                    .child(SharedString::from(self.text.lines[ix].clone())),
            )
            .into_any_element()
    }
}

/// The key a file's tab opens under, so the same file opens once.
fn key(session: &str, path: &str) -> String {
    format!("file:{session}:{path}")
}

impl EventEmitter<PanelEvent> for FileView {}

impl Focusable for FileView {
    fn focus_handle(&self, _: &App) -> FocusHandle {
        self.focus.clone()
    }
}

impl BasePanel for FileView {
    fn panel_name(&self) -> &'static str {
        FILE
    }

    /// Reads the first part the first time the tab is shown.
    fn set_active(&mut self, active: bool, _: &mut Window, cx: &mut Context<Self>) {
        if active && !self.started {
            self.started = true;
            let offset = self.text.start();
            self.load_part(offset, cx);
            cx.notify();
        }
    }

    fn on_removed(&mut self, _: &mut Window, _: &mut Context<Self>) {
        self.loading = None;
    }

    fn dump(&self, _: &App) -> PanelState {
        PanelState {
            panel_name: FILE.into(),
            children: Vec::new(),
            info: PanelInfo::panel(json!({ "session": self.session, "path": self.path })),
        }
    }
}

impl Panel for FileView {
    fn title(&mut self, _: &mut Window, _: &mut Context<Self>) -> impl IntoElement {
        SharedString::from(
            self.path
                .rsplit('/')
                .next()
                .unwrap_or(&self.path)
                .to_string(),
        )
    }
}

impl Render for FileView {
    fn render(&mut self, _: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        let theme = cx.theme();
        let (muted, danger, mono) = (
            theme.muted_foreground,
            theme.danger,
            theme.mono_font_family.clone(),
        );
        let note = |text: String| div().px_3().py_2().text_sm().child(text);
        let status = if let Some(error) = &self.text.error {
            Some(note(error.clone()).text_color(danger))
        } else if self.text.binary {
            Some(note("This file isn't text, so it isn't shown.".into()).text_color(muted))
        } else if self.text.lines.is_empty() && self.text.is_loading() {
            Some(note("Loading…".into()).text_color(muted))
        } else {
            None
        };
        let digits = self.text.line_count.max(1).to_string().len() as f32;
        let gutter = px(digits * 8. + 20.);
        v_flex()
            .track_focus(&self.focus)
            .size_full()
            .child(
                div()
                    .px_3()
                    .py_1()
                    .text_sm()
                    .text_color(muted)
                    .child(self.path.clone()),
            )
            .children(status)
            .when(!self.text.lines.is_empty(), |view| {
                view.child(
                    uniform_list(
                        "file-lines",
                        self.text.lines.len(),
                        cx.processor(move |view, range: std::ops::Range<usize>, _, cx| {
                            // A one-row range is the list measuring its widest row, not what's shown.
                            if range.len() > 1
                                && let Some(offset) = view.text.more(range.end)
                            {
                                view.load_part(offset, cx);
                            }
                            let muted = cx.theme().muted_foreground;
                            range.map(|ix| view.line(ix, gutter, muted)).collect()
                        }),
                    )
                    .track_scroll(&self.list)
                    .with_width_from_item(Some(self.text.widest))
                    .with_horizontal_sizing_behavior(ListHorizontalSizingBehavior::Unconstrained)
                    .flex_1()
                    .font_family(mono)
                    .text_size(TEXT),
                )
            })
    }
}
