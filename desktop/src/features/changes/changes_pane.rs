//! One owned diff response per pick. The kit retains viewer state until a newer response replaces it.
use super::{branch_diff::BranchDiff, file_diff::FileDiff};
use crate::core::intents::{
    open_branch_diff::OpenBranchDiff, open_commit_diff::OpenCommitDiff, open_diff::OpenDiff,
};
use crate::core::socket::{client::SocketClient, outcome::Outcome};
use crate::core::state::preferences::Preferences;
use crate::core::ui::{
    center_panes::CenterPanes,
    diff::{SuppliedPatch, supplied_patch},
};
use gpui_kit::component::button::{Button, ButtonVariants as _};
use gpui_kit::component::diff::{Diff, DiffMode, DiffState};
use gpui_kit::component::dock::{
    BasePanel, Panel, PanelEvent, PanelId, PanelInfo, PanelState, panel_handle, register_panel,
};
use gpui_kit::component::input::{Editor, EditorState};
use gpui_kit::component::{ActiveTheme as _, Disableable as _, h_flex, v_flex};
use gpui_kit::*;
use serde_json::{Value, json};

pub const CHANGES: &str = "Changes";
const KEY: &str = "git/diff";
struct Open(WeakEntity<ChangesPane>);
impl Global for Open {}

#[derive(Clone, Debug, PartialEq)]
enum Picked {
    File {
        session: String,
        path: String,
        original_path: Option<String>,
    },
    Branch {
        session: String,
    },
    Commit {
        session: String,
        sha: String,
        path: String,
        title: String,
    },
}
struct PatchView {
    label: &'static str,
    content: PatchContent,
}
enum PatchContent {
    Unified(Entity<DiffState>),
    Combined {
        text: String,
        editor: Option<Entity<EditorState>>,
    },
}
enum Shown {
    Reading,
    Read {
        views: Vec<PatchView>,
        notes: Vec<&'static str>,
        base: Option<String>,
    },
    Failed(String),
}
impl Shown {
    fn patches(
        parts: Vec<(&'static str, &str)>,
        truncated: bool,
        notes: Vec<&'static str>,
        base: Option<String>,
        mode: DiffMode,
        cx: &mut App,
    ) -> Self {
        let mut views = Vec::new();
        for (label, text) in parts {
            match supplied_patch(text, truncated) {
                Ok(SuppliedPatch::Unified(files)) if !files.is_empty() => views.push(PatchView {
                    label,
                    content: PatchContent::Unified(
                        cx.new(|cx| DiffState::new(files, cx).with_mode(mode)),
                    ),
                }),
                Ok(SuppliedPatch::Combined(text)) => views.push(PatchView {
                    label,
                    content: PatchContent::Combined { text, editor: None },
                }),
                Ok(_) => {}
                Err(error) => return Self::Failed(error),
            }
        }
        Self::Read { views, notes, base }
    }
    fn file(diff: FileDiff, mode: DiffMode, cx: &mut App) -> Self {
        Self::patches(diff.parts(), diff.truncated, diff.notes(), None, mode, cx)
    }
}

pub struct ChangesPane {
    focus: FocusHandle,
    picked: Option<Picked>,
    shown: Shown,
    mode: DiffMode,
    _read: Option<Task<()>>,
    _preferences: Option<Subscription>,
}
impl ChangesPane {
    /// Validates saved target metadata before a workspace coordinator rebuilds this registered pane.
    /// Folder containment remains the existing backend SessionPaths authority.
    pub fn workspace_key(state: &PanelState, session: &str) -> Result<String, String> {
        let invalid = || "Invalid saved Changes target.".to_string();
        if state.panel_name.as_str() != CHANGES {
            return Err(invalid());
        }
        let PanelInfo::Panel(info) = &state.info else {
            return Err(invalid());
        };
        if info.is_null() {
            return Ok(CHANGES.into());
        }
        if !info.is_object() || info["session"].as_str() != Some(session) {
            return Err(invalid());
        }
        for field in ["path", "originalPath"] {
            if let Some(value) = info.get(field).filter(|value| !value.is_null()) {
                if value
                    .as_str()
                    .is_none_or(|path| path.is_empty() || path.contains('\0'))
                {
                    return Err(invalid());
                }
            }
        }
        if let Some(split) = info.get("split") {
            if !split.is_boolean() {
                return Err(invalid());
            }
        }
        if let Some(sha) = info.get("sha") {
            if sha.as_str().is_none_or(|sha| {
                sha.len() != 40 || !sha.bytes().all(|byte| byte.is_ascii_hexdigit())
            }) || info["path"].as_str().is_none()
            {
                return Err(invalid());
            }
        }
        Ok(CHANGES.into())
    }
    pub fn register(cx: &mut App) {
        register_panel(cx, CHANGES, |build, _, cx| {
            let saved = match build.state().info.clone() {
                PanelInfo::Panel(saved) => saved,
                _ => Value::Null,
            };
            let text = |key: &str| saved[key].as_str().map(String::from);
            let picked = text("session").map(|session| match (text("sha"), text("path")) {
                (Some(sha), Some(path)) => Picked::Commit {
                    session,
                    sha,
                    path,
                    title: text("title").unwrap_or_default(),
                },
                (_, Some(path)) => Picked::File {
                    session,
                    path,
                    original_path: text("originalPath"),
                },
                _ => Picked::Branch { session },
            });
            let pane = cx.new(|cx| {
                let mut pane = ChangesPane::new(None, cx);
                if saved["split"] == true {
                    pane.mode = DiffMode::Split;
                }
                if let Some(picked) = picked {
                    pane.show(picked, cx);
                }
                pane
            });
            CenterPanes::remember(CHANGES, PanelId::from(pane.entity_id()), cx);
            cx.set_global(Open(pane.downgrade()));
            panel_handle(pane)
        });
        cx.on_action(|intent: &OpenDiff, cx| {
            Self::open_later(
                Picked::File {
                    session: intent.session_id.clone(),
                    path: intent.path.clone(),
                    original_path: intent.original_path.clone(),
                },
                cx,
            )
        });
        cx.on_action(|intent: &OpenBranchDiff, cx| {
            Self::open_later(
                Picked::Branch {
                    session: intent.session_id.clone(),
                },
                cx,
            )
        });
        cx.on_action(|intent: &OpenCommitDiff, cx| {
            Self::open_later(
                Picked::Commit {
                    session: intent.session_id.clone(),
                    sha: intent.sha.clone(),
                    path: intent.path.clone(),
                    title: intent.title.clone(),
                },
                cx,
            )
        });
    }
    fn open_later(picked: Picked, cx: &mut App) {
        if let Some(window) = cx
            .active_window()
            .or_else(|| cx.windows().into_iter().next())
        {
            cx.defer(move |cx| {
                _ = window.update(cx, |_, window, cx| Self::open(picked, window, cx));
            });
        }
    }
    fn open(picked: Picked, window: &mut Window, cx: &mut App) {
        if let Some(pane) = cx.try_global::<Open>().and_then(|open| open.0.upgrade()) {
            pane.update(cx, |pane, cx| pane.show(picked, cx));
            CenterPanes::open(CHANGES, window, cx, |_, _| pane);
        } else {
            CenterPanes::open(CHANGES, window, cx, |_, cx| {
                let pane = cx.new(|cx| ChangesPane::new(Some(picked), cx));
                cx.set_global(Open(pane.downgrade()));
                pane
            });
        }
    }
    fn new(picked: Option<Picked>, cx: &mut Context<Self>) -> Self {
        let mut pane = Self {
            focus: cx.focus_handle(),
            picked: None,
            shown: Shown::Reading,
            mode: DiffMode::Unified,
            _read: None,
            _preferences: Preferences::try_global(cx)
                .map(|preferences| cx.observe(&preferences, |_, _, cx| cx.notify())),
        };
        if let Some(picked) = picked {
            pane.show(picked, cx);
        }
        pane
    }
    fn show(&mut self, picked: Picked, cx: &mut Context<Self>) {
        self.picked = Some(picked);
        self.shown = Shown::Reading;
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
        let socket = socket.read(cx);
        self._read = Some(match picked {
            Picked::File {
                session,
                path,
                original_path,
            } => {
                let call = socket.call::<FileDiff>(
                    "git",
                    "diff",
                    json!({"session":session,"path":path,"originalPath":original_path}),
                    Some(KEY),
                );
                cx.spawn(async move |pane, cx| Self::answered(pane, call.await, Shown::file, cx))
            }
            Picked::Commit {
                session, sha, path, ..
            } => {
                let call = socket.call::<FileDiff>(
                    "git",
                    "commitDiff",
                    json!({"session":session,"sha":sha,"path":path}),
                    Some(KEY),
                );
                cx.spawn(async move |pane, cx| {
                    Self::answered(
                        pane,
                        call.await,
                        |diff, mode, cx| {
                            Shown::patches(
                                vec![("Commit", &diff.unstaged)],
                                diff.truncated,
                                diff.notes(),
                                None,
                                mode,
                                cx,
                            )
                        },
                        cx,
                    )
                })
            }
            Picked::Branch { session } => {
                let call = socket.call::<BranchDiff>(
                    "git",
                    "branchDiff",
                    json!({"session":session}),
                    Some(KEY),
                );
                cx.spawn(async move |pane, cx| {
                    Self::answered(
                        pane,
                        call.await,
                        |branch, mode, cx| {
                            Shown::patches(
                                vec![("Branch", &branch.diff)],
                                branch.truncated,
                                branch.notes(),
                                Some(branch.base.clone()),
                                mode,
                                cx,
                            )
                        },
                        cx,
                    )
                })
            }
        });
        cx.notify();
    }
    fn answered<T>(
        pane: WeakEntity<Self>,
        outcome: Outcome<T>,
        shown: impl FnOnce(T, DiffMode, &mut App) -> Shown,
        cx: &mut AsyncApp,
    ) {
        _ = pane.update(cx, |pane, cx| {
            pane.shown = match outcome {
                Outcome::Ok(answer) => shown(answer, pane.mode, cx),
                Outcome::Failed(failure) => Shown::Failed(failure.message),
                Outcome::Aborted => Shown::Failed("The connection dropped.".into()),
            };
            pane._read = None;
            cx.notify();
        });
    }
    fn set_mode(&mut self, mode: DiffMode, cx: &mut Context<Self>) {
        self.mode = mode;
        if let Shown::Read { views, .. } = &self.shown {
            for view in views {
                if let PatchContent::Unified(state) = &view.content {
                    state.update(cx, |state, cx| state.set_mode(mode, cx));
                }
            }
        }
        cx.notify();
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
        let mut info = match &self.picked {
            Some(Picked::File {
                session,
                path,
                original_path,
            }) => json!({"session":session,"path":path,"originalPath":original_path}),
            Some(Picked::Commit {
                session,
                sha,
                path,
                title,
            }) => json!({"session":session,"sha":sha,"path":path,"title":title}),
            Some(Picked::Branch { session }) => json!({"session":session}),
            None => Value::Null,
        };
        if info.is_object() {
            info["split"] = json!(self.mode == DiffMode::Split);
        }
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
    fn render(&mut self, window: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        let (muted, danger) = (cx.theme().muted_foreground, cx.theme().danger);
        let (font, size, _) = Preferences::editor_text(cx);
        let reading = matches!(self.shown, Shown::Reading) && self.picked.is_some();
        let title = match (&self.picked, &self.shown) {
            (
                Some(Picked::File {
                    path,
                    original_path: Some(from),
                    ..
                }),
                _,
            ) => format!("{from} → {path}"),
            (Some(Picked::File { path, .. }), _) => path.clone(),
            (Some(Picked::Commit { title, path, .. }), _) => format!("{title} — {path}"),
            (
                Some(Picked::Branch { .. }),
                Shown::Read {
                    base: Some(base), ..
                },
            ) => format!("Branch vs {base}"),
            (Some(Picked::Branch { .. }), _) => "Branch".into(),
            (None, _) => "Pick a changed file in the Source control tab.".into(),
        };
        let header = h_flex()
            .px_3()
            .py_1()
            .gap_2()
            .child(
                div()
                    .flex_1()
                    .min_w_0()
                    .truncate()
                    .text_sm()
                    .text_color(muted)
                    .child(title),
            )
            .child(
                Button::new("diff-unified")
                    .ghost()
                    .compact()
                    .label("Unified")
                    .disabled(self.mode == DiffMode::Unified)
                    .on_click(cx.listener(|pane, _, _, cx| pane.set_mode(DiffMode::Unified, cx))),
            )
            .child(
                Button::new("diff-split")
                    .ghost()
                    .compact()
                    .label("Split")
                    .disabled(self.mode == DiffMode::Split)
                    .on_click(cx.listener(|pane, _, _, cx| pane.set_mode(DiffMode::Split, cx))),
            )
            .child(
                Button::new("changes-refresh")
                    .ghost()
                    .compact()
                    .label(if reading { "Reading…" } else { "Refresh" })
                    .disabled(reading || self.picked.is_none())
                    .on_click(cx.listener(|pane, _, _, cx| {
                        pane.shown = Shown::Reading;
                        pane.read(cx);
                    })),
            );
        let pane = v_flex()
            .track_focus(&self.focus)
            .size_full()
            .min_h_0()
            .child(header);
        let note = |text: String| div().px_3().py_2().text_sm().child(text);
        match &mut self.shown {
            _ if self.picked.is_none() => pane,
            Shown::Reading => pane.child(note("Reading the diff…".into()).text_color(muted)),
            Shown::Failed(message) => pane.child(note(message.clone()).text_color(danger)),
            Shown::Read { views, notes, .. } => pane
                .children(views.iter_mut().map(|view| {
                    let body = match &mut view.content {
                        PatchContent::Unified(state) => Diff::new(state).into_any_element(),
                        PatchContent::Combined { text, editor } => {
                            let state = editor.get_or_insert_with(|| {
                                cx.new(|cx| {
                                    EditorState::new(window, cx)
                                        .default_value(text.clone())
                                        .line_number(false)
                                        .soft_wrap(false)
                                })
                            });
                            v_flex()
                                .size_full()
                                .min_h_0()
                                .child(div().px_3().text_xs().text_color(muted).child(
                                    "Combined conflict diff — shown exactly as Git returned it.",
                                ))
                                .child(
                                    Editor::new(state)
                                        .readonly(true)
                                        .appearance(false)
                                        .bordered(false)
                                        .h_full(),
                                )
                                .into_any_element()
                        }
                    };
                    v_flex()
                        .flex_1()
                        .min_h_0()
                        .child(div().px_3().text_xs().text_color(muted).child(view.label))
                        .child(
                            div()
                                .flex_1()
                                .min_h_0()
                                .font_family(font.clone())
                                .text_size(size)
                                .child(body),
                        )
                }))
                .children(
                    notes
                        .iter()
                        .map(|text| note((*text).into()).text_color(muted)),
                ),
        }
    }
}
#[cfg(test)]
#[path = "changes_pane_tests.rs"]
mod tests;
