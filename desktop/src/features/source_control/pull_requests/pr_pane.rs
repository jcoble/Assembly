use crate::core::theme::text::{TextRole, TextRoleExt as _};
use super::{
    pr_detail::{PrBack, PrDetail, PrMerged},
    pr_owner::{PrOwner, ProjectChoice, holders, resolve},
    pr_queue::PrQueue,
    pr_search::PrState,
    pull_request::{PrTarget, PullRequestSummary},
};
use crate::core::{
    socket::row_source::ExpectedRowSource,
    intents::open_pull_requests::OpenPullRequests, state::selected_session::SelectedSession,
    ui::center_panes::CenterPanes,
};
use crate::core::socket::picture_client::PictureInterest;
use crate::features::projects::projects::{ProjectRecord, Projects};
use gpui_kit::component::dock::{
    BasePanel, Panel, PanelEvent, PanelId, PanelInfo, PanelState, panel_handle, register_panel,
};
use gpui_kit::component::input::{InputEvent, InputState};
use gpui_kit::*;
use serde_json::json;
use std::time::Duration;

#[path = "pr_view/list.rs"]
mod list;
#[path = "pr_view/opening_page.rs"]
mod opening_page;

gpui_kit::actions!(pull_requests, [ShowPullRequests]);
const NAME: &str = "Pull requests";
const KEY: &str = "pull-requests";
const MAX_SEARCH: usize = 150;
#[derive(Clone)]
struct Selected {
    repository: Option<String>,
    number: u64,
    /// Listed under "All local projects", so only this computer's copy can answer for it.
    listed_local: bool,
    row: Option<PullRequestSummary>,
    /// The tab the person is on; the page keeps it once it can read.
    on_changes: bool,
}
struct OpenPane(Option<WeakEntity<PrPane>>);
impl Global for OpenPane {}
pub struct PrPane {
    focus: FocusHandle,
    active: bool,
    _pictures: Option<PictureInterest>,
    session: Option<String>,
    /// The tab shows the selected session's project until the person picks one.
    follows_selection: bool,
    project: Option<String>,
    scope: Vec<String>,
    queue: Entity<PrQueue>,
    detail: Option<Entity<PrDetail>>,
    /// The pull request on screen before anyone can read it, while its owner is unknown.
    selected: Option<Selected>,
    /// "Link copied" on that page, until this ends.
    copied: Option<Task<()>>,
    /// Asking who owns a project's machine. Dropping the task forgets the answer.
    opening: Option<Task<()>>,
    /// A repository more than one project holds, waiting for the person to say which.
    choosing: Option<ProjectChoice>,
    search: Entity<InputState>,
    search_wait: Option<Task<()>>,
    projects: Option<Entity<Projects>>,
    failure: Option<String>,
    /// A pasted link to a repository Assembly doesn't have, and the words saying so.
    missing_link: Option<(String, String)>,
    _queue: Subscription,
    _detail: Option<[Subscription; 2]>,
    _projects: Option<Subscription>,
    _search: Subscription,
    _selection: Option<Subscription>,
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
        // An action runs while its window is being updated, so a second `cx.dispatch_action` here would be dropped.
        cx.on_action(|_: &ShowPullRequests, cx| {
            Self::open_when_free(
                OpenPullRequests {
                    session_id: None,
                    repo: None,
                    number: None,
                },
                cx,
            )
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
        cx.on_action(|intent: &OpenPullRequests, cx| Self::open_when_free(intent.clone(), cx));
    }
    fn open_when_free(intent: OpenPullRequests, cx: &mut App) {
        if let Some(window) = cx
            .active_window()
            .or_else(|| cx.windows().into_iter().next())
        {
            cx.defer(move |cx| {
                _ = window.update(cx, |_, window, cx| Self::open(intent, window, cx));
            });
        }
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
        let selected = SelectedSession::try_global(cx);
        let session = session.or_else(|| {
            let selected = selected.as_ref()?.read(cx);
            selected.session_id().map(String::from)
        });
        let queue = cx.new(|_| {
            let mut queue = PrQueue::new(session.clone(), vec![]);
            queue.state = PrState::All;
            queue
        });
        let source = selected_source(session.as_deref(), cx);
        queue.update(cx, |queue, cx| queue.set_source(source, cx));
        let queue_heard = cx.observe(&queue, |_, _, cx| cx.notify());
        let search = cx.new(|cx| {
            InputState::new(window, cx).placeholder("Search title, branch, author or #number")
        });
        let search_heard =
            cx.subscribe(&search, |pane: &mut Self, input, event: &InputEvent, cx| {
                let text = input.read(cx).value().to_string();
                match event {
                    InputEvent::PressEnter { .. } => pane.search(text, cx),
                    InputEvent::Change => pane.search_after_pause(text, cx),
                    _ => {}
                }
            });
        let selection_heard = selected.as_ref().map(|selected| {
            cx.observe(selected, |pane: &mut Self, selected, cx| {
                let session = selected.read(cx).session_id().map(String::from);
                if pane.follows_selection && session.is_some() && session != pane.session {
                    pane.session = session;
                    pane.show_session(cx);
                }
            })
        });
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
            _pictures: PictureInterest::hold(cx),
            session,
            follows_selection: true,
            project: None,
            scope: Vec::new(),
            queue,
            detail: None,
            selected: None,
            copied: None,
            opening: None,
            choosing: None,
            search,
            search_wait: None,
            projects,
            failure: None,
            missing_link: None,
            _queue: queue_heard,
            _detail: None,
            _projects: project_heard,
            _search: search_heard,
            _selection: selection_heard,
        };
        pane.sync_scope(cx);
        pane
    }
    /// A new queue for a new place, keeping the person's filters.
    fn replace_queue(&mut self, cx: &mut Context<Self>) {
        let current = self.queue.read(cx);
        let (state, scope, search) = (current.state, current.scope, current.search.clone());
        let session = self.session.clone();
        let queue = cx.new(|_| {
            let mut queue = PrQueue::new(session, vec![]);
            queue.state = state;
            queue.scope = scope;
            queue.search = search;
            queue
        });
        let source = selected_source(self.session.as_deref(), cx);
        queue.update(cx, |queue, cx| queue.set_source(source, cx));
        self._queue = cx.observe(&queue, |_, _, cx| cx.notify());
        self.queue = queue;
        self.scope.clear();
    }
    fn show_session(&mut self, cx: &mut Context<Self>) {
        self.close_detail(cx);
        self.project = None;
        self.replace_queue(cx);
        self.sync_scope(cx);
        cx.notify();
    }
    fn cancel_open(&mut self, cx: &mut Context<Self>) {
        self.opening = None;
        self.choosing = None;
        self.copied = None;
        if self.selected.take().is_some() {
            self.failure = None;
        }
        cx.notify();
    }
    fn close_detail(&mut self, cx: &mut Context<Self>) {
        self.cancel_open(cx);
        self.detail = None;
        self._detail = None;
        cx.notify();
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
            .filter(|project| match &self.project {
                Some(selected) => selected == &project.id,
                None => project.machine == "local",
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
        if let Some(session) = intent.session_id {
            self.follows_selection = true;
            if self.session.as_ref() != Some(&session) {
                self.session = Some(session);
                self.show_session(cx);
            }
        }
        self.sync_scope(cx);
        if let Some(number) = intent.number {
            self.select_pr(intent.repo, number, cx);
        }
        cx.notify();
    }
    /// Typing searches after a short pause; a link waits for Enter.
    fn search_after_pause(&mut self, text: String, cx: &mut Context<Self>) {
        self.search_wait = None;
        if text.trim().starts_with("https://") {
            return;
        }
        self.search_wait = Some(cx.spawn(async move |pane, cx| {
            cx.background_executor()
                .timer(Duration::from_millis(350))
                .await;
            _ = pane.update(cx, |pane, cx| {
                pane.search_wait = None;
                pane.search(text, cx);
            });
        }));
    }
    fn search(&mut self, text: String, cx: &mut Context<Self>) {
        self.search_wait = None;
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
            self.missing_link = None;
            match target {
                Ok(target) => self.select_pr(Some(target.repository), target.number, cx),
                Err(error) => match PrTarget::parse_url(&text) {
                    Ok(target) => {
                        self.missing_link = Some((
                            format!(
                                "github.com/{} is not a project in Assembly, so #{} cannot open here. Add the repository as a project, or open it on GitHub.",
                                target.repository, target.number
                            ),
                            text.trim().to_string(),
                        ));
                    }
                    Err(_) => self.failure = Some(error),
                },
            }
        } else {
            self.failure = None;
            self.missing_link = None;
            let text: String = text.chars().take(MAX_SEARCH).collect();
            self.queue
                .update(cx, |queue, cx| queue.set_search(text, cx));
        }
        cx.notify();
    }
    fn select_pr(&mut self, repository: Option<String>, number: u64, cx: &mut Context<Self>) {
        let wanted =
            Selected { repository, number, listed_local: false, row: None, on_changes: false };
        self.open_pr(wanted, cx);
    }
    fn select_row(&mut self, row: PullRequestSummary, cx: &mut Context<Self>) {
        let wanted = Selected {
            repository: Some(row.repository.clone()),
            number: row.number,
            listed_local: self.project.is_none(),
            row: Some(row),
            on_changes: false,
        };
        self.open_pr(wanted, cx);
    }
    /// Asks the same question again after a machine didn't answer.
    fn retry_open(&mut self, cx: &mut Context<Self>) {
        if let Some(wanted) = self.selected.clone() {
            self.open_pr(wanted, cx);
        }
    }
    fn open_pr(&mut self, wanted: Selected, cx: &mut Context<Self>) {
        self.cancel_open(cx);
        let Selected { repository, number, listed_local, row, on_changes } = wanted.clone();
        self.failure = None;
        if let Some(id) = self.session.clone() {
            let source = selected_source(Some(&id), cx);
            let owner = PrOwner::Session { id, source };
            self.open_detail(owner, repository, number, row, on_changes, cx);
            return;
        }
        if let Some(owner) = self.same_project(repository.as_deref(), cx) {
            self.open_detail(owner, repository, number, row, on_changes, cx);
            return;
        }
        self.close_detail(cx);
        let rows = self.projects.as_ref().map(|projects| projects.read(cx).rows());
        let found = holders(
            rows.unwrap_or_default(),
            self.project.as_deref(),
            repository.as_deref(),
            listed_local,
        );
        match found {
            Err(error) => self.failure = Some(error),
            Ok(mut projects) if projects.len() == 1 => {
                self.selected = Some(wanted);
                let project = projects.remove(0);
                self.resolve_project(&project, repository, number, cx);
            }
            Ok(projects) => {
                self.selected = Some(wanted);
                self.choosing = Some(ProjectChoice {
                    projects,
                    repository,
                    number,
                })
            }
        }
        cx.notify();
    }
    /// A pull request open from a project keeps answering for that repository, so moving along its stack asks nothing.
    fn same_project(&self, repository: Option<&str>, cx: &App) -> Option<PrOwner> {
        let detail = self.detail.as_ref()?.read(cx);
        let shown = detail.detail.as_ref()?;
        let owner = detail.owner();
        (matches!(owner, PrOwner::Project { .. })
            && repository.is_none_or(|repository| repository.eq_ignore_ascii_case(&shown.repository)))
        .then(|| owner.clone())
    }
    fn resolve_project(
        &mut self,
        project: &ProjectRecord,
        repository: Option<String>,
        number: u64,
        cx: &mut Context<Self>,
    ) {
        let resolving = resolve(project, cx);
        let task = cx.spawn(async move |pane, cx| {
            let owner = resolving.await;
            _ = pane.update(cx, |pane, cx| {
                pane.opening = None;
                match owner {
                    Ok(owner) => {
                        let shown = pane.selected.take();
                        let on_changes = shown.as_ref().is_some_and(|shown| shown.on_changes);
                        let row = shown.and_then(|shown| shown.row);
                        pane.open_detail(owner, repository, number, row, on_changes, cx)
                    }
                    Err(error) => {
                        pane.failure = Some(error);
                        cx.notify();
                    }
                }
            });
        });
        self.opening = Some(task);
    }
    /// The person said which project; the same source question follows.
    fn choose_project(&mut self, project_id: &str, cx: &mut Context<Self>) {
        let Some(choice) = self.choosing.take() else {
            return;
        };
        let Some(project) = choice.projects.iter().find(|project| project.id == project_id) else {
            return;
        };
        self.resolve_project(project, choice.repository, choice.number, cx);
        cx.notify();
    }
    fn open_detail(
        &mut self,
        owner: PrOwner,
        repository: Option<String>,
        number: u64,
        row: Option<PullRequestSummary>,
        on_changes: bool,
        cx: &mut Context<Self>,
    ) {
        let active = self.active;
        let detail = cx.new(|cx| {
            let mut detail = PrDetail::new(owner, repository, number, active, cx);
            detail.show_row(row, on_changes);
            detail
        });
        self._detail = Some([
            cx.subscribe(&detail, |pane: &mut Self, _, _: &PrBack, cx| {
                pane.close_detail(cx)
            }),
            cx.subscribe(&detail, |pane: &mut Self, _, merged: &PrMerged, cx| {
                pane.queue
                    .update(cx, |queue, cx| queue.merged(&merged.repository, merged.number, cx))
            }),
        ]);
        self.detail = Some(detail);
        cx.notify();
    }
    fn select_project(&mut self, project: Option<String>, cx: &mut Context<Self>) {
        self.session = None;
        self.follows_selection = false;
        self.project = project;
        self.failure = None;
        self.close_detail(cx);
        self.replace_queue(cx);
        self.sync_scope(cx);
        cx.notify();
    }
}
/// The source the selection read this session through, when it is the selected one.
fn selected_source(session: Option<&str>, cx: &App) -> Option<ExpectedRowSource> {
    let selected = SelectedSession::try_global(cx)?;
    let selected = selected.read(cx);
    (session.is_some() && selected.session_id() == session)
        .then(|| selected.source().cloned())
        .flatten()
}
#[cfg(test)]
#[path = "pr_view/pane_tests.rs"]
mod tests;
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
        self._pictures = active.then(|| PictureInterest::hold(cx)).flatten();
        self.sync_scope(cx);
        if let Some(detail) = &self.detail {
            detail.update(cx, |detail, cx| detail.set_active(active, cx));
        }
    }
    fn on_removed(&mut self, _: &mut Window, cx: &mut Context<Self>) {
        self.active = false;
        self._pictures = None;
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
        let page = match (self.detail.clone(), self.selected.clone()) {
            (Some(detail), _) => detail.into_any_element(),
            (None, Some(selected)) => self.opening_page(&selected, cx),
            (None, None) => self.list_page(cx).into_any_element(),
        };
        div()
            .track_focus(&self.focus)
            .size_full()
            .min_h_0()
            .child(page)
    }
}
