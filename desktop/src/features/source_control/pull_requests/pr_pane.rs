use crate::core::theme::text::{TextRole, TextRoleExt as _};
use super::{
    pr_detail::PrDetail,
    pr_queue::{PrQueue, QueueState},
    pull_request::PrTarget,
};
use crate::core::{
    intents::open_pull_requests::OpenPullRequests,
    ui::{
        center_panes::CenterPanes,
        filter_pills::{FilterGroup, FilterOption, FilterPills, FilterPillsChanged},
    },
};
use crate::features::projects::projects::Projects;
use gpui_kit::component::button::{Button, ButtonVariants as _};
use gpui_kit::component::dock::{
    BasePanel, Panel, PanelEvent, PanelId, PanelInfo, PanelState, panel_handle, register_panel,
};
use gpui_kit::component::input::{Input, InputEvent, InputState};
use gpui_kit::component::menu::{DropdownMenu as _, PopupMenuItem};
use gpui_kit::component::{ActiveTheme as _, Disableable as _, Sizable as _, h_flex, v_flex};
use gpui_kit::prelude::FluentBuilder as _;
use gpui_kit::*;
use serde_json::json;

gpui_kit::actions!(pull_requests, [ShowPullRequests]);
const NAME: &str = "Pull requests";
const KEY: &str = "pull-requests";
struct OpenPane(Option<WeakEntity<PrPane>>);
impl Global for OpenPane {}
pub struct PrPane {
    focus: FocusHandle,
    active: bool,
    session: Option<String>,
    project: Option<String>,
    scope: Vec<String>,
    queue: Entity<PrQueue>,
    detail: Option<Entity<PrDetail>>,
    search: Entity<InputState>,
    filters: Entity<FilterPills>,
    projects: Option<Entity<Projects>>,
    failure: Option<String>,
    _queue: Subscription,
    _projects: Option<Subscription>,
    _search: Subscription,
    _filters: Subscription,
}
impl PrPane {
    pub fn workspace_key(state: &PanelState, session: &str) -> Result<String, String> {
        let invalid = || "Invalid saved Pull requests target.".to_string();
        if state.panel_name.as_str() != NAME {
            return Err(invalid());
        }
        let PanelInfo::Panel(info) = &state.info else {
            return Err(invalid());
        };
        if !info.is_object() {
            return Err(invalid());
        }
        if let Some(value) = info.get("session").filter(|value| !value.is_null()) {
            if value.as_str() != Some(session) {
                return Err(invalid());
            }
        }
        Ok(KEY.into())
    }
    pub fn register(cx: &mut App) {
        cx.set_global(OpenPane(None));
        crate::core::commands::command_registry::CommandRegistry::add(
            NAME,
            Box::new(ShowPullRequests),
            cx,
        );
        cx.on_action(|_: &ShowPullRequests, cx| {
            cx.dispatch_action(&OpenPullRequests {
                session_id: None,
                repo: None,
                number: None,
            })
        });
        register_panel(cx, NAME, |build, window, cx| {
            let saved = match build.state().info.clone() {
                PanelInfo::Panel(saved) => saved,
                _ => serde_json::Value::Null,
            };
            let session = saved["session"].as_str().map(String::from);
            let pane = cx.new(|cx| Self::new(session, window, cx));
            Self::remember(&pane, cx);
            panel_handle(pane)
        });
        cx.on_action(|intent: &OpenPullRequests, cx| {
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
    fn remember(pane: &Entity<Self>, cx: &mut App) {
        CenterPanes::remember(KEY, PanelId::from(pane.entity_id()), cx);
        cx.global_mut::<OpenPane>().0 = Some(pane.downgrade());
    }
    fn open(intent: OpenPullRequests, window: &mut Window, cx: &mut App) {
        if let Some(pane) = cx
            .global::<OpenPane>()
            .0
            .as_ref()
            .and_then(WeakEntity::upgrade)
        {
            pane.update(cx, |pane, cx| pane.apply_intent(intent, cx));
            CenterPanes::open(KEY, window, cx, |_, _| pane);
        } else {
            CenterPanes::open(KEY, window, cx, |window, cx| {
                let pane = cx.new(|cx| Self::new(intent.session_id.clone(), window, cx));
                pane.update(cx, |pane, cx| pane.apply_intent(intent, cx));
                Self::remember(&pane, cx);
                pane
            });
        }
    }
    fn new(session: Option<String>, window: &mut Window, cx: &mut Context<Self>) -> Self {
        let queue = cx.new(|_| PrQueue::new(session.clone(), vec![]));
        let queue_heard = cx.observe(&queue, |_, _, cx| cx.notify());
        let search = cx.new(|cx| {
            InputState::new(window, cx).placeholder("Search or paste a PR link, then Enter")
        });
        let search_heard =
            cx.subscribe(&search, |pane: &mut Self, input, event: &InputEvent, cx| {
                if matches!(event, InputEvent::PressEnter { .. }) {
                    pane.search(input.read(cx).value().to_string(), cx);
                }
            });
        let mut group = FilterGroup::new(
            "State",
            [QueueState::Open, QueueState::Mine, QueueState::Review]
                .into_iter()
                .map(|state| FilterOption {
                    value: state.wire().into(),
                    label: state.label().into(),
                })
                .collect(),
        );
        group.picked = vec!["open".into()];
        let filters = cx.new(|cx| FilterPills::new(vec![group], cx));
        let filter_heard = cx.subscribe(
            &filters,
            |pane: &mut Self, pills, _: &FilterPillsChanged, cx| {
                let previous = pane.queue.read(cx).state.wire();
                let picked = pills.read(cx).state.groups[0].picked.clone();
                let selected = picked
                    .iter()
                    .find(|value| value.as_str() != previous)
                    .or_else(|| picked.first())
                    .map_or("open", String::as_str)
                    .to_string();
                pills.update(cx, |pills, cx| {
                    pills.state.groups[0].picked = vec![selected.clone()];
                    cx.notify();
                });
                let state = match selected.as_str() {
                    "mine" => QueueState::Mine,
                    "review" => QueueState::Review,
                    _ => QueueState::Open,
                };
                pane.queue
                    .update(cx, |queue, cx| queue.select_state(state, cx));
            },
        );
        let projects = Projects::get(cx);
        let project_heard = projects.as_ref().map(|projects| {
            cx.observe(projects, |pane: &mut Self, _, cx| {
                pane.sync_scope(cx);
                cx.notify();
            })
        });
        let mut pane = Self {
            focus: cx.focus_handle(),
            active: true,
            session,
            project: None,
            scope: Vec::new(),
            queue,
            detail: None,
            search,
            filters,
            projects,
            failure: None,
            _queue: queue_heard,
            _projects: project_heard,
            _search: search_heard,
            _filters: filter_heard,
        };
        pane.sync_scope(cx);
        pane
    }
    fn sync_scope(&mut self, cx: &mut Context<Self>) {
        if self.session.is_some() {
            self.queue
                .update(cx, |queue, cx| queue.set_active(self.active, cx));
            return;
        }
        let Some(projects) = &self.projects else {
            return;
        };
        let projects = projects.read(cx);
        let mut repositories: Vec<String> = projects
            .rows()
            .iter()
            .filter(|project| {
                self.project
                    .as_ref()
                    .is_none_or(|selected| selected == &project.id)
            })
            .filter_map(|project| project.github_repository().map(String::from))
            .collect();
        repositories.sort();
        repositories.dedup_by(|a, b| a.eq_ignore_ascii_case(b));
        let incomplete =
            self.project.is_none() && (projects.next.is_some() || repositories.len() > 32);
        if incomplete {
            self.failure = Some(
                "Choose a project, or load more projects before searching all repositories.".into(),
            );
            self.queue
                .update(cx, |queue, cx| queue.set_active(false, cx));
            return;
        }
        if repositories != self.scope {
            self.scope = repositories.clone();
            self.queue
                .update(cx, |queue, cx| queue.set_repositories(repositories, cx));
        }
        self.queue
            .update(cx, |queue, cx| queue.set_active(self.active, cx));
    }
    fn apply_intent(&mut self, intent: OpenPullRequests, cx: &mut Context<Self>) {
        if self.session != intent.session_id {
            self.session = intent.session_id;
            self.detail = None;
            let queue = cx.new(|_| PrQueue::new(self.session.clone(), vec![]));
            self._queue = cx.observe(&queue, |_, _, cx| cx.notify());
            self.queue = queue;
            self.scope.clear();
        }
        self.sync_scope(cx);
        if let Some(number) = intent.number {
            self.select_pr(intent.repo, number, cx);
        }
        cx.notify();
    }
    fn search(&mut self, text: String, cx: &mut Context<Self>) {
        if text.trim().starts_with("https://") {
            let mut known = self.scope.clone();
            known.extend(
                self.queue
                    .read(cx)
                    .page
                    .items
                    .iter()
                    .map(|pr| pr.repository.clone()),
            );
            if let Some(projects) = &self.projects {
                known.extend(
                    projects
                        .read(cx)
                        .rows()
                        .iter()
                        .filter_map(|project| project.github_repository().map(String::from)),
                );
            }
            let target = if self.session.is_some() {
                PrTarget::parse_url(&text)
            } else {
                PrTarget::from_url(&text, &known)
            };
            // A session target is checked against the backend's actual origin before any detail is displayed.
            match target {
                Ok(target) => self.select_pr(Some(target.repository), target.number, cx),
                Err(error) => self.failure = Some(error),
            }
        } else {
            self.failure = None;
            self.queue
                .update(cx, |queue, cx| queue.set_search(text, cx));
        }
        cx.notify();
    }
    fn select_pr(&mut self, repository: Option<String>, number: u64, cx: &mut Context<Self>) {
        let Some(session) = self.session.clone() else {
            // Project-only target admission needs the actual owning-host bridge; never reuse another session's root.
            self.failure = Some(
                "Open this project's checkout in Source control to view this pull request.".into(),
            );
            cx.notify();
            return;
        };
        self.failure = None;
        self.detail =
            Some(cx.new(|cx| PrDetail::new(session, repository, number, self.active, cx)));
        cx.notify();
    }
    fn select_project(&mut self, project: Option<String>, cx: &mut Context<Self>) {
        self.session = None;
        self.project = project;
        self.detail = None;
        self.failure = None;
        let queue = cx.new(|_| PrQueue::new(None, vec![]));
        self._queue = cx.observe(&queue, |_, _, cx| cx.notify());
        self.queue = queue;
        self.scope.clear();
        self.sync_scope(cx);
        cx.notify();
    }
}
impl EventEmitter<PanelEvent> for PrPane {}
impl Focusable for PrPane {
    fn focus_handle(&self, _: &App) -> FocusHandle {
        self.focus.clone()
    }
}
impl BasePanel for PrPane {
    fn panel_name(&self) -> &'static str {
        NAME
    }
    fn set_active(&mut self, active: bool, _: &mut Window, cx: &mut Context<Self>) {
        self.active = active;
        self.sync_scope(cx);
        if let Some(detail) = &self.detail {
            detail.update(cx, |detail, cx| detail.set_active(active, cx));
        }
    }
    fn on_removed(&mut self, _: &mut Window, cx: &mut Context<Self>) {
        self.active = false;
        self.queue
            .update(cx, |queue, cx| queue.set_active(false, cx));
        if let Some(detail) = &self.detail {
            detail.update(cx, |detail, cx| detail.set_active(false, cx));
        }
    }
    fn dump(&self, _: &App) -> PanelState {
        PanelState {
            panel_name: NAME.into(),
            children: Vec::new(),
            info: PanelInfo::panel(json!({"session":self.session})),
        }
    }
}
impl Panel for PrPane {
    fn title(&mut self, _: &mut Window, _: &mut Context<Self>) -> impl IntoElement {
        NAME
    }
}
impl Render for PrPane {
    fn render(&mut self, _: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        let danger = cx.theme().danger;
        let projects = self
            .projects
            .as_ref()
            .map(|projects| projects.read(cx).rows().to_vec())
            .unwrap_or_default();
        let label = if self.session.is_some() {
            "Selected checkout".to_string()
        } else {
            self.project
                .as_ref()
                .and_then(|id| projects.iter().find(|project| project.id == *id))
                .map_or("All projects".to_string(), |project| project.title.clone())
        };
        let target = cx.entity().downgrade();
        let all = target.clone();
        let picker = Button::new("pr-project")
            .ghost()
            .compact()
            .label(label)
            .dropdown_menu(move |menu, _, _| {
                let menu = menu.item(PopupMenuItem::new("All projects").on_click({
                    let all = all.clone();
                    move |_, _, cx| {
                        _ = all.update(cx, |pane, cx| pane.select_project(None, cx));
                    }
                }));
                projects
                    .iter()
                    .filter(|project| project.github_repository().is_some())
                    .fold(menu, |menu, project| {
                        let id = project.id.clone();
                        let target = target.clone();
                        menu.item(
                            PopupMenuItem::new(format!("{} · {}", project.title, project.machine))
                                .on_click(move |_, _, cx| {
                                    _ = target.update(cx, |pane, cx| {
                                        pane.select_project(Some(id.clone()), cx)
                                    });
                                }),
                        )
                    })
            });
        let queue = self.queue.read(cx);
        let count = queue.page.items.len();
        let more = queue.page.cursor.is_some();
        let loading = queue.loading;
        let total = queue.page.total;
        let queue_error = queue.failure.clone();
        let projects_loading = self
            .projects
            .as_ref()
            .is_some_and(|projects| projects.read(cx).loading);
        let projects_error = self
            .projects
            .as_ref()
            .and_then(|projects| projects.read(cx).failure.clone());
        let mut left =
            v_flex()
                .w(px(320.))
                .min_h_0()
                .gap_1()
                .child(picker)
                .child(Input::new(&self.search).small())
                .child(self.filters.clone())
                .child(
                    h_flex()
                        .gap_2()
                        .child(format!("{total} pull requests"))
                        .child(
                            Button::new("pr-queue-refresh")
                                .ghost()
                                .compact()
                                .label("Refresh")
                                .disabled(loading)
                                .on_click(cx.listener(|pane, _, _, cx| {
                                    pane.queue.update(cx, |queue, cx| queue.refresh(cx))
                                })),
                        ),
                )
                .children(
                    self.failure
                        .clone()
                        .or(queue_error)
                        .or(projects_error)
                        .map(|error| div().px_2().text_role(TextRole::Body, cx).text_color(danger).child(error)),
                )
                .when(loading || projects_loading, |left| {
                    left.child("Reading pull requests…")
                })
                .child(
                    uniform_list(
                        "pr-queue-items",
                        count,
                        cx.processor(|pane, range: std::ops::Range<usize>, _, cx| {
                            let items = pane
                                .queue
                                .read(cx)
                                .page
                                .items
                                .iter()
                                .skip(range.start)
                                .take(range.len())
                                .cloned()
                                .collect::<Vec<_>>();
                            items
                                .into_iter()
                                .map(|pr| {
                                    Button::new(SharedString::from(format!(
                                        "queue:{}:{}",
                                        pr.repository, pr.number
                                    )))
                                    .ghost()
                                    .label(format!(
                                        "{} #{} · {}{}",
                                        pr.repository,
                                        pr.number,
                                        pr.title,
                                        if pr.is_draft { " · Draft" } else { "" }
                                    ))
                                    .on_click(cx.listener(move |pane, _, _, cx| {
                                        pane.select_pr(Some(pr.repository.clone()), pr.number, cx)
                                    }))
                                })
                                .collect()
                        }),
                    )
                    .flex_1()
                    .min_h_0(),
                )
                .when(more, |left| {
                    left.child(
                        Button::new("pr-more")
                            .ghost()
                            .label("Load more")
                            .disabled(loading)
                            .on_click(cx.listener(|pane, _, _, cx| {
                                pane.queue.update(cx, |queue, cx| queue.load_more(cx))
                            })),
                    )
                });
        if let Some(projects) = &self.projects {
            if projects.read(cx).next.is_some() {
                let projects = projects.clone();
                left = left.child(
                    Button::new("pr-more-projects")
                        .ghost()
                        .label("Load more projects")
                        .disabled(projects_loading)
                        .on_click(move |_, _, cx| {
                            projects.update(cx, |projects, cx| projects.load_more(cx))
                        }),
                );
            }
        }
        h_flex()
            .track_focus(&self.focus)
            .size_full()
            .min_h_0()
            .child(left)
            .child(
                div()
                    .flex_1()
                    .min_w_0()
                    .size_full()
                    .children(self.detail.clone()),
            )
    }
}
