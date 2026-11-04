use super::commit_timeline::{CommitTimeline, uncommitted_of};
use super::git_status::GitStatus;
use super::history_filter::{HistoryFilter, author_options, branch_options};
use crate::core::socket::{client::SocketClient, outcome::Outcome};
use crate::core::state::sessions::Sessions;
use crate::core::{intents::open_git_history::OpenGitHistory, ui::center_panes::CenterPanes};
use gpui_kit::assets::IconName;
use gpui_kit::component::button::{Button, ButtonVariants as _};
use gpui_kit::component::dock::{
    BasePanel, Panel, PanelEvent, PanelId, PanelInfo, PanelState, panel_handle, register_panel,
};
use gpui_kit::component::input::{Input, InputEvent, InputState};
use gpui_kit::component::menu::{DropdownMenu as _, PopupMenuItem};
use gpui_kit::component::tooltip::Tooltip;
use gpui_kit::component::{ActiveTheme as _, Disableable as _, Icon, Sizable as _, h_flex, v_flex};
use gpui_kit::*;
use serde_json::json;
use std::collections::HashMap;

const HISTORY: &str = "Git history";
struct OpenHistory(HashMap<String, WeakEntity<HistoryPane>>);
impl Global for OpenHistory {}
pub struct HistoryPane {
    focus: FocusHandle,
    session: String,
    path: Option<String>,
    expected_source: Option<crate::core::socket::row_source::ExpectedRowSource>,
    timeline: Entity<CommitTimeline>,
    search: Entity<InputState>,
    filter: HistoryFilter,
    /// The working copy behind the toolbar's folder label and the uncommitted row; re-read when shown or refreshed.
    status: Option<GitStatus>,
    status_read: Option<Task<()>>,
    active: bool,
    /// This session's state when last heard, so the end of a turn can be told from any other change.
    turn_state: Option<String>,
    _heard: Vec<Subscription>,
}
fn key(session: &str) -> String {
    format!("git-history:{session}")
}
gpui_kit::actions!(git_history, [ShowGitHistory]);
impl HistoryPane {
    pub fn workspace_key(state: &PanelState, session: &str) -> Result<String, String> {
        let invalid = || "Invalid saved Git history target.".to_string();
        if state.panel_name.as_str() != HISTORY {
            return Err(invalid());
        }
        let PanelInfo::Panel(info) = &state.info else {
            return Err(invalid());
        };
        if !info.is_object() || info["session"].as_str() != Some(session) {
            return Err(invalid());
        }
        if let Some(value) = info.get("path").filter(|value| !value.is_null()) {
            if value
                .as_str()
                .is_none_or(|path| path.is_empty() || path.contains('\0'))
            {
                return Err(invalid());
            }
        }
        if let Some(source) = info.get("expectedSource").filter(|source| !source.is_null()) {
            if serde_json::from_value::<crate::core::socket::row_source::ExpectedRowSource>(source.clone()).is_err() {
                return Err(invalid());
            }
        }
        Ok(key(session))
    }

    pub fn register(cx: &mut App) {
        cx.set_global(OpenHistory(HashMap::new()));
        crate::core::commands::command_registry::CommandRegistry::add(
            "Git history",
            Box::new(ShowGitHistory),
            cx,
        );
        cx.on_action(|_: &ShowGitHistory, cx| {
            let selected = crate::core::state::selected_session::SelectedSession::global(cx);
            let selected = selected.read(cx);
            let session = selected.session_id().map(String::from);
            let expected_source = selected.source().cloned();
            if let Some(session_id) = session {
                if let Some(window) = cx
                    .active_window()
                    .or_else(|| cx.windows().into_iter().next())
                {
                    cx.defer(move |cx| {
                        _ = window.update(cx, |_, window, cx| {
                            Self::open(
                                OpenGitHistory {
                                    session_id,
                                    expected_source,
                                    path: None,
                                },
                                window,
                                cx,
                            )
                        });
                    });
                }
            }
        });

        register_panel(cx, HISTORY, |build, window, cx| {
            let saved = match build.state().info.clone() {
                PanelInfo::Panel(saved) => saved,
                _ => serde_json::Value::Null,
            };
            let session = saved["session"].as_str().unwrap_or_default().to_string();
            let path = saved["path"].as_str().map(String::from);
            let source = saved.get("expectedSource").cloned().and_then(|value| serde_json::from_value(value).ok());
            let pane = cx.new(|cx| Self::new(session.clone(), path, source, window, cx));
            Self::remember(&session, &pane, cx);
            panel_handle(pane)
        });
        cx.on_action(|intent: &OpenGitHistory, cx| {
            let intent = intent.clone();
            if let Some(window) = cx
                .active_window()
                .or_else(|| cx.windows().into_iter().next())
            {
                cx.defer(move |cx| {
                    _ = window.update(cx, |_, window, cx| Self::open(intent, window, cx));
                });
            }
        });
    }
    fn remember(session: &str, pane: &Entity<Self>, cx: &mut App) {
        CenterPanes::remember(key(session), PanelId::from(pane.entity_id()), cx);
        cx.global_mut::<OpenHistory>()
            .0
            .insert(session.into(), pane.downgrade());
    }
    fn open(intent: OpenGitHistory, window: &mut Window, cx: &mut App) {
        let existing = cx
            .global::<OpenHistory>()
            .0
            .get(&intent.session_id)
            .and_then(WeakEntity::upgrade);
        if let Some(pane) = existing {
            pane.update(cx, |pane, cx| {
                pane.path = intent.path.clone();
                pane.expected_source = intent.expected_source.clone();
                pane.timeline.update(cx, |timeline, cx| timeline.set_target(intent.path, intent.expected_source, cx));
                cx.notify();
            });
            CenterPanes::open(key(&intent.session_id), window, cx, |_, _| pane);
        } else {
            CenterPanes::open(key(&intent.session_id), window, cx, |window, cx| {
                let session = intent.session_id;
                let pane = cx.new(|cx| Self::new(session.clone(), intent.path, intent.expected_source, window, cx));
                Self::remember(&session, &pane, cx);
                pane
            });
        }
    }
    fn new(
        session: String,
        path: Option<String>,
        expected_source: Option<crate::core::socket::row_source::ExpectedRowSource>,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) -> Self {
        let timeline = cx.new(|cx| CommitTimeline::new(session.clone(), path.clone(), expected_source.clone(), true, cx));
        let search = cx.new(|cx| InputState::new(window, cx).placeholder("Search message, hash or author"));
        let mut heard = vec![
            cx.observe(&timeline, |_, _, cx| cx.notify()),
            cx.subscribe(&search, |pane, _, event: &InputEvent, cx| {
                if matches!(event, InputEvent::Change) {
                    pane.filter.text = pane.search.read(cx).value().to_string();
                    pane.apply_filter(cx);
                }
            }),
        ];
        if let Some(sessions) = Sessions::try_global(cx) {
            heard.push(cx.observe(&sessions, |pane, _, cx| pane.sessions_changed(cx)));
        }
        let mut pane = Self {
            focus: cx.focus_handle(),
            session,
            path,
            expected_source,
            timeline,
            search,
            filter: HistoryFilter::default(),
            status: None,
            status_read: None,
            active: true,
            turn_state: None,
            _heard: heard,
        };
        pane.turn_state = pane.session_state(cx);
        pane.read_status(cx);
        pane
    }
    fn session_state(&self, cx: &App) -> Option<String> {
        let sessions = Sessions::try_global(cx)?;
        let sessions = sessions.read(cx);
        let item = sessions.items().iter().find(|item| item.session_id == self.session)?;
        Some(item.state.clone())
    }
    /// A turn that ends has usually changed the working copy, and the uncommitted row has to say so.
    fn sessions_changed(&mut self, cx: &mut Context<Self>) {
        let state = self.session_state(cx);
        let ended = self.turn_state.as_deref() == Some("running")
            && state.as_deref().is_some_and(|state| state != "running");
        self.turn_state = state;
        if ended && self.active {
            self.read_status(cx);
        }
    }
    fn read_status(&mut self, cx: &mut Context<Self>) {
        let Some(socket) = SocketClient::try_global(cx) else {
            return;
        };
        let call = socket.read(cx).call::<GitStatus>(
            "git",
            "status",
            json!({"session":self.session,"expectedSource":self.expected_source}),
            Some(&format!("git/status/{}", cx.entity_id())),
        );
        self.status_read = Some(cx.spawn(async move |pane, cx| {
            let outcome = call.await;
            _ = pane.update(cx, |pane, cx| {
                pane.status_read = None;
                let prior = pane.status.take().and_then(|status| status.head);
                pane.status = match outcome {
                    Outcome::Ok(status) => Some(status),
                    _ => None,
                };
                let head = pane.status.as_ref().and_then(|status| status.head.clone());
                let moved = prior.is_some() && head.is_some() && prior != head;
                let uncommitted = pane.status.as_ref().and_then(uncommitted_of);
                pane.timeline.update(cx, |timeline, cx| {
                    timeline.set_uncommitted(uncommitted, cx);
                    if moved {
                        timeline.refresh(cx);
                    }
                });
                cx.notify();
            });
        }));
    }
    /// What the folder picker's trigger reads: this session's own folder, the only one its history can be read for.
    fn folder_label(&self) -> String {
        let Some(status) = &self.status else {
            return "No repository".into();
        };
        let folder = status
            .checkout_root
            .as_deref()
            .map(|root| root.trim_end_matches('/').rsplit('/').next().unwrap_or(root))
            .unwrap_or("This folder");
        match &status.branch {
            Some(branch) => format!("{folder} ({branch})"),
            None => folder.to_string(),
        }
    }
    fn set_path(&mut self, path: Option<String>, cx: &mut Context<Self>) {
        self.path = path.clone();
        self.timeline
            .update(cx, |timeline, cx| timeline.set_path(path, cx));
        cx.notify();
    }
    fn apply_filter(&mut self, cx: &mut Context<Self>) {
        let filter = self.filter.clone();
        self.timeline
            .update(cx, |timeline, cx| timeline.set_filter(filter, cx));
        cx.notify();
    }
}
impl EventEmitter<PanelEvent> for HistoryPane {}
impl Focusable for HistoryPane {
    fn focus_handle(&self, _: &App) -> FocusHandle {
        self.focus.clone()
    }
}
impl BasePanel for HistoryPane {
    fn panel_name(&self) -> &'static str {
        HISTORY
    }
    fn set_active(&mut self, active: bool, _: &mut Window, cx: &mut Context<Self>) {
        self.active = active;
        self.timeline
            .update(cx, |timeline, cx| timeline.set_active(active, cx));
        if active {
            self.read_status(cx);
        } else {
            self.status_read = None;
        }
    }
    fn on_removed(&mut self, _: &mut Window, cx: &mut Context<Self>) {
        self.status_read = None;
        self.timeline
            .update(cx, |timeline, cx| timeline.set_active(false, cx));
    }
    fn dump(&self, _: &App) -> PanelState {
        PanelState {
            panel_name: HISTORY.into(),
            children: Vec::new(),
            info: PanelInfo::panel(json!({"session":self.session,"path":self.path,"expectedSource":self.expected_source})),
        }
    }
}
impl Panel for HistoryPane {
    fn title(&mut self, _: &mut Window, _: &mut Context<Self>) -> impl IntoElement {
        HISTORY
    }
}
/// One of the toolbar's pickers: its value on the trigger, "All…" and each choice in its menu.
fn picker(
    trigger: Button,
    label: String,
    all: &'static str,
    options: Vec<String>,
    current: Option<String>,
    pane: WeakEntity<HistoryPane>,
    pick: fn(&mut HistoryFilter, Option<String>),
) -> impl IntoElement + use<> {
    trigger
        .outline()
        .small()
        .dropdown_caret(true)
        .label(label)
        .dropdown_menu(move |menu, _, _| {
            std::iter::once(None)
                .chain(options.iter().cloned().map(Some))
                .fold(menu, |menu, value| {
                    let pane = pane.clone();
                    let checked = current == value;
                    menu.item(
                        PopupMenuItem::new(value.clone().unwrap_or_else(|| all.into()))
                            .checked(checked)
                            .on_click(move |_, _, cx| {
                                _ = pane.update(cx, |pane, cx| {
                                    pick(&mut pane.filter, value.clone());
                                    pane.apply_filter(cx);
                                });
                            }),
                    )
                })
        })
}
impl Render for HistoryPane {
    fn render(&mut self, _: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        let timeline = self.timeline.read(cx);
        let (loaded, shown, reading) =
            (timeline.history.commits.len(), timeline.shown_count(), timeline.is_reading());
        let branches = branch_options(&timeline.history.commits);
        let authors = author_options(&timeline.history.commits);
        let count = if self.filter.active() {
            format!("{shown} of {loaded}")
        } else if loaded > 0 {
            format!("{loaded} commits")
        } else {
            String::new()
        };
        let theme = cx.theme();
        let (muted, border, surface) = (theme.muted_foreground, theme.border, theme.secondary);
        let weak = cx.entity().downgrade();
        let branch_label = format!(
            "History: {}",
            self.filter.branch.as_deref().unwrap_or("all branches")
        );
        let toolbar = h_flex()
            .flex_none()
            .gap_2()
            .px(px(10.))
            .py(px(6.))
            .bg(surface)
            .border_b_1()
            .border_color(border)
            .child(
                Button::new("history-folder")
                    .outline()
                    .small()
                    .label(self.folder_label())
                    .disabled(true),
            )
            .children(self.path.clone().map(|path| {
                let tip = path.clone();
                h_flex()
                    .gap_1()
                    .child(
                        div()
                            .id("history-path")
                            .max_w(px(260.))
                            .truncate()
                            .px(px(7.))
                            .py(px(3.))
                            .border_1()
                            .border_color(border)
                            .rounded(px(5.))
                            .text_sm()
                            .text_color(muted)
                            .tooltip(move |window, cx| Tooltip::new(tip.clone()).build(window, cx))
                            .child(path),
                    )
                    .child(
                        div().debug_selector(|| "history-path-clear".into()).child(
                            Button::new("history-path-clear")
                                .ghost()
                                .compact()
                                .icon(Icon::new(IconName::X).size(px(14.)))
                                .tooltip("Show all repository history")
                                .on_click(cx.listener(|pane, _, _, cx| pane.set_path(None, cx))),
                        ),
                    )
            }))
            .child(picker(
                Button::new("history-branch")
                    .tooltip("Filters the history shown here. It does not switch the working directory."),
                branch_label,
                "All branches",
                branches,
                self.filter.branch.clone(),
                weak.clone(),
                |filter, value| filter.branch = value,
            ))
            .child(picker(
                Button::new("history-author"),
                self.filter.author.clone().unwrap_or_else(|| "All authors".into()),
                "All authors",
                authors,
                self.filter.author.clone(),
                weak,
                |filter, value| filter.author = value,
            ))
            .child(div().flex_1().min_w_0().max_w(px(320.)).child(Input::new(&self.search).small()))
            .child(div().flex_none().text_sm().text_color(muted).child(count))
            .child(div().flex_1())
            .child(
                Button::new("history-refresh")
                    .ghost()
                    .compact()
                    .icon(Icon::new(IconName::RefreshCw).size(px(14.)))
                    .tooltip("Read the commit history again")
                    .disabled(reading)
                    .on_click(cx.listener(|pane, _, _, cx| {
                        pane.read_status(cx);
                        pane.timeline.update(cx, |timeline, cx| timeline.refresh(cx))
                    })),
            );
        v_flex()
            .track_focus(&self.focus)
            .size_full()
            .min_h_0()
            .child(toolbar)
            .child(self.timeline.clone())
    }
}
#[cfg(test)]
#[path = "history_pane_tests.rs"]
mod tests;
