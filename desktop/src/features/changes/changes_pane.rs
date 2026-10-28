//! `ChangesPane` is the Changes tab beside Chat: one changed file's diff from `git/diff` (scm#13 to scm#17). There's one
//! tab for every file, as in the old app: the open diff intent brings it forward and shows the picked file. The read is
//! keyed, so picking another file stops the one on its way, and it's a task the pane holds, so closing the tab cancels
//! it. The tab is saved with the layout under the "Changes" pane's name, and comes back with its file read afresh.

use gpui_kit::component::button::{Button, ButtonVariants as _};
use gpui_kit::component::dock::{
    BasePanel, Panel, PanelEvent, PanelId, PanelInfo, PanelState, panel_handle, register_panel,
};
use gpui_kit::component::{ActiveTheme as _, Disableable as _, h_flex, v_flex};
use gpui_kit::prelude::FluentBuilder as _;
use gpui_kit::*;
use serde_json::{Value, json};

use super::diff_lines::Kind;
use super::file_diff::{FileDiff, Row, widest};
use crate::core::intents::open_diff::OpenDiff;
use crate::core::socket::client::SocketClient;
use crate::core::socket::outcome::Outcome;
use crate::core::ui::center_panes::CenterPanes;

/// The pane's name in the saved layout, and the one key its tab opens under.
pub const CHANGES: &str = "Changes";
const LINE: Pixels = px(20.);
const TEXT: Pixels = px(13.);

/// The open Changes tab, so the open diff intent shows its file there.
struct Open(WeakEntity<ChangesPane>);

impl Global for Open {}

/// The file the tab shows.
#[derive(Clone)]
struct Picked {
    session: String,
    path: String,
    original_path: Option<String>,
}

enum Shown {
    Reading,
    Read {
        diff: FileDiff,
        rows: Vec<Row>,
        widest: usize,
    },
    Failed(String),
}

pub struct ChangesPane {
    focus: FocusHandle,
    picked: Option<Picked>,
    shown: Shown,
    /// The read on its way; dropping it cancels the call.
    _read: Option<Task<()>>,
    list: UniformListScrollHandle,
}

impl ChangesPane {
    /// Restores the tab from the saved layout, and owns the open diff intent.
    pub fn register(cx: &mut App) {
        register_panel(cx, CHANGES, |build, _, cx| {
            let saved = match build.state().info.clone() {
                PanelInfo::Panel(saved) => saved,
                _ => Value::Null,
            };
            let text = |key: &str| saved[key].as_str().map(String::from);
            let picked = text("session")
                .zip(text("path"))
                .map(|(session, path)| Picked {
                    session,
                    path,
                    original_path: text("originalPath"),
                });
            let pane = cx.new(|cx| ChangesPane::new(picked, cx));
            CenterPanes::remember(CHANGES, PanelId::from(pane.entity_id()), cx);
            cx.set_global(Open(pane.downgrade()));
            panel_handle(pane)
        });
        cx.on_action(|intent: &OpenDiff, cx| {
            let picked = Picked {
                session: intent.session_id.clone(),
                path: intent.path.clone(),
                original_path: intent.original_path.clone(),
            };
            // A click runs this while its window is being updated, so the window is updated once that's done. A display
            // with no window manager has no active window, so the app's one window stands in.
            if let Some(window) = cx
                .active_window()
                .or_else(|| cx.windows().into_iter().next())
            {
                cx.defer(move |cx| {
                    _ = window.update(cx, |_, window, cx| Self::open(picked, window, cx))
                });
            }
        });
    }

    /// Brings the Changes tab forward, making it the first time, and shows `picked` in it.
    fn open(picked: Picked, window: &mut Window, cx: &mut App) {
        let open = cx.try_global::<Open>().and_then(|open| open.0.upgrade());
        if let Some(pane) = open {
            pane.update(cx, |pane, cx| pane.show(picked, cx));
            CenterPanes::open(CHANGES, window, cx, |_, _| pane);
            return;
        }
        CenterPanes::open(CHANGES, window, cx, |_, cx| {
            let pane = cx.new(|cx| ChangesPane::new(Some(picked), cx));
            cx.set_global(Open(pane.downgrade()));
            pane
        });
    }

    fn new(picked: Option<Picked>, cx: &mut Context<Self>) -> Self {
        let mut pane = ChangesPane {
            focus: cx.focus_handle(),
            picked: None,
            shown: Shown::Reading,
            _read: None,
            list: UniformListScrollHandle::new(),
        };
        if let Some(picked) = picked {
            pane.show(picked, cx);
        }
        pane
    }

    fn show(&mut self, picked: Picked, cx: &mut Context<Self>) {
        self.picked = Some(picked);
        self.shown = Shown::Reading;
        // Another file starts at its top.
        self.list = UniformListScrollHandle::new();
        self.read(cx);
    }

    fn read(&mut self, cx: &mut Context<Self>) {
        let Some(picked) = self.picked.clone() else {
            return;
        };
        let Some(socket) = SocketClient::try_global(cx) else {
            self.shown = Shown::Failed("The backend isn't running.".into());
            self._read = None;
            cx.notify();
            return;
        };
        let args = json!({ "session": picked.session, "path": picked.path, "originalPath": picked.original_path });
        // Keyed, so a newer read answers this one Aborted and cancels it on the backend.
        let call = socket
            .read(cx)
            .call::<FileDiff>("git", "diff", args, Some("git/diff"));
        self._read = Some(cx.spawn(async move |pane, cx| {
            let outcome = call.await;
            _ = pane.update(cx, |pane, cx| {
                pane.shown = match outcome {
                    Outcome::Ok(diff) => {
                        let rows = diff.rows();
                        Shown::Read {
                            widest: widest(&rows),
                            rows,
                            diff,
                        }
                    }
                    Outcome::Failed(failure) => Shown::Failed(failure.message),
                    // A newer read drops this task before its answer comes, so an abort here is the socket closing.
                    Outcome::Aborted => Shown::Failed("The connection dropped.".into()),
                };
                pane._read = None;
                cx.notify();
            });
        }));
        cx.notify();
    }

    fn row(row: &Row, cx: &App) -> AnyElement {
        let theme = cx.theme();
        let number = |n: Option<u32>| {
            div()
                .w(px(44.))
                .flex_none()
                .pr_2()
                .text_right()
                .text_color(theme.muted_foreground)
                .child(n.map(|n| n.to_string()).unwrap_or_default())
        };
        let line = match row {
            Row::Label(label) => {
                return div()
                    .h(LINE)
                    .px_3()
                    .font_weight(FontWeight::MEDIUM)
                    .text_color(theme.muted_foreground)
                    .child(*label)
                    .into_any_element();
            }
            Row::Line(line) => line,
        };
        let (marker, color, fill) = match line.kind {
            Kind::Added => ("+", Some(theme.success), Some(theme.success.opacity(0.12))),
            Kind::Removed => ("-", Some(theme.danger), Some(theme.danger.opacity(0.12))),
            Kind::Run | Kind::Note => ("", Some(theme.muted_foreground), None),
            Kind::Same => (" ", None, None),
        };
        // The font is set on the row, not only on the list, so measuring the widest row on its own counts the same
        // characters at the same width.
        h_flex()
            .h(LINE)
            .min_w_full()
            .font_family(theme.mono_font_family.clone())
            .text_size(TEXT)
            .when_some(fill, |row, fill| row.bg(fill))
            .child(number(line.old))
            .child(number(line.new))
            .child(
                div()
                    .w(px(16.))
                    .flex_none()
                    .when_some(color, |m, c| m.text_color(c))
                    .child(marker),
            )
            .child(
                div()
                    .whitespace_nowrap()
                    .when_some(color, |text, color| text.text_color(color))
                    .child(SharedString::from(line.text.clone())),
            )
            .into_any_element()
    }
}

impl EventEmitter<PanelEvent> for ChangesPane {}

impl Focusable for ChangesPane {
    fn focus_handle(&self, _: &App) -> FocusHandle {
        self.focus.clone()
    }
}

impl BasePanel for ChangesPane {
    fn panel_name(&self) -> &'static str {
        CHANGES
    }

    fn on_removed(&mut self, _: &mut Window, _: &mut Context<Self>) {
        self._read = None;
    }

    fn dump(&self, _: &App) -> PanelState {
        let info = match &self.picked {
            Some(picked) => json!({ "session": picked.session, "path": picked.path,
                "originalPath": picked.original_path }),
            None => Value::Null,
        };
        PanelState {
            panel_name: CHANGES.into(),
            children: Vec::new(),
            info: PanelInfo::panel(info),
        }
    }
}

impl Panel for ChangesPane {
    fn title(&mut self, _: &mut Window, _: &mut Context<Self>) -> impl IntoElement {
        CHANGES
    }
}

impl Render for ChangesPane {
    fn render(&mut self, _: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        let theme = cx.theme();
        let (muted, danger, mono) = (
            theme.muted_foreground,
            theme.danger,
            theme.mono_font_family.clone(),
        );
        let note = |text: String| div().px_3().py_2().text_sm().child(text);
        let reading = matches!(self.shown, Shown::Reading) && self.picked.is_some();
        let header = h_flex()
            .px_3()
            .py_1()
            .gap_2()
            .justify_between()
            .child(
                div()
                    .min_w_0()
                    .truncate()
                    .text_sm()
                    .text_color(muted)
                    .child(match &self.picked {
                        Some(picked) => match &picked.original_path {
                            Some(from) => format!("{from} → {}", picked.path),
                            None => picked.path.clone(),
                        },
                        None => "Pick a changed file in the Source control tab.".into(),
                    }),
            )
            .child(
                Button::new("changes-refresh")
                    .ghost()
                    .compact()
                    .label(if reading { "Reading…" } else { "Refresh" })
                    .disabled(reading || self.picked.is_none())
                    .on_click(cx.listener(|pane, _, _, cx| {
                        pane.shown = Shown::Reading;
                        pane.read(cx)
                    })),
            );
        let pane = v_flex().track_focus(&self.focus).size_full().child(header);
        match &self.shown {
            _ if self.picked.is_none() => pane,
            Shown::Reading => pane.child(note("Reading the diff…".into()).text_color(muted)),
            Shown::Failed(message) => pane.child(note(message.clone()).text_color(danger)),
            Shown::Read { diff, rows, widest } => pane
                .when(!rows.is_empty(), |pane| {
                    pane.child(
                        uniform_list(
                            "changes-lines",
                            rows.len(),
                            cx.processor(move |pane, range: std::ops::Range<usize>, _, cx| {
                                let Shown::Read { rows, .. } = &pane.shown else {
                                    return Vec::new();
                                };
                                range.map(|ix| ChangesPane::row(&rows[ix], cx)).collect()
                            }),
                        )
                        .track_scroll(&self.list)
                        .with_width_from_item(Some(*widest))
                        .with_horizontal_sizing_behavior(
                            ListHorizontalSizingBehavior::Unconstrained,
                        )
                        .flex_1()
                        .font_family(mono)
                        .text_size(TEXT),
                    )
                })
                .children(
                    diff.notes(rows)
                        .into_iter()
                        .map(|text| note(text.into()).text_color(muted)),
                ),
        }
    }
}

#[cfg(test)]
#[path = "changes_pane_tests.rs"]
mod tests;
