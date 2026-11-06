//! The pull request pane's search: typing searches after a short pause, a link waits for Enter.
use super::*;
use crate::core::navigation::web_links::web_address;
use crate::core::socket::{client::SocketClient, connection_state::ConnectionState};

/// A followed link held until the pushed projects say whether a project holds its repository.
pub(super) struct WaitingLink {
    target: PrTarget,
    /// Hears the connection end, which answers the link with the projects known by then.
    _ended: Option<Subscription>,
}

impl PrPane {
    /// A pull request named by a link, pasted here or followed from anywhere, as the old `openLink` did. A link to the
    /// followed session's own repository (its list shows it) opens through the session, checked against the backend's
    /// actual origin. Any other opens through the project holding its repository, or the tab says no project does and
    /// offers the page on GitHub.
    pub(super) fn open_linked(&mut self, target: PrTarget, cx: &mut Context<Self>) {
        self.missing_link = None;
        self.waiting_link = None;
        let listed = self.queue.read(cx).page.items.iter();
        let sessions = self.session.is_some()
            && listed
                .map(|pr| &pr.repository)
                .any(|repository| repository.eq_ignore_ascii_case(&target.repository));
        if sessions {
            return self.select_pr(Some(target.repository), target.number, cx);
        }
        if !self.projects_answered(cx) {
            return self.wait_for_projects(target, cx);
        }
        self.open_through_project(target, cx);
    }
    /// Whether the projects say which repositories Assembly has: a list read answered, or there is no backend to ask.
    fn projects_answered(&self, cx: &App) -> bool {
        let projects = self.projects.as_ref();
        projects.is_none_or(|projects| projects.read(cx).answered())
    }
    /// Holds the link until the pushed projects answer, or the connection ends first.
    fn wait_for_projects(&mut self, target: PrTarget, cx: &mut Context<Self>) {
        let ended = SocketClient::try_global(cx).map(|socket| {
            cx.subscribe(&socket, |pane: &mut Self, _, state, cx| {
                if matches!(
                    state,
                    ConnectionState::Disconnected | ConnectionState::Refused { .. }
                ) {
                    pane.route_waiting_link(true, cx);
                    cx.notify();
                }
            })
        });
        self.waiting_link = Some(WaitingLink {
            target,
            _ended: ended,
        });
    }
    /// The waiting link, routed once the projects have answered, or with what is known when the connection `ended`.
    pub(super) fn route_waiting_link(&mut self, ended: bool, cx: &mut Context<Self>) {
        if self.waiting_link.is_none() || !(ended || self.projects_answered(cx)) {
            return;
        }
        if let Some(waiting) = self.waiting_link.take() {
            self.open_through_project(waiting.target, cx);
        }
    }
    fn open_through_project(&mut self, target: PrTarget, cx: &mut Context<Self>) {
        match target.clone().among(&self.project_repositories(cx)) {
            Some(found) => {
                self.follow_to_holder(&found.repository, cx);
                self.select_in_project(found.repository, found.number, cx)
            }
            None => {
                self.missing_link = Some((
                    format!(
                        "github.com/{} is not a project in Assembly, so #{} cannot open here. Add the repository as a project, or open it on GitHub.",
                        target.repository, target.number
                    ),
                    target.web_page(),
                ));
            }
        }
    }
    /// A tab set to a project that doesn't hold `repository` moves to the one that does, or to all local projects when
    /// several do, so the project that answers is the link's, whatever the tab showed.
    fn follow_to_holder(&mut self, repository: &str, cx: &mut Context<Self>) {
        let Some(chosen) = self.project.clone() else {
            return;
        };
        let Some(projects) = &self.projects else {
            return;
        };
        let rows = projects.read(cx).rows();
        if holders(rows, Some(&chosen), Some(repository), false).is_ok() {
            return;
        }
        let mut holding = holders(rows, None, Some(repository), false).unwrap_or_default();
        let next = (holding.len() == 1).then(|| holding.remove(0).id);
        self.select_project(next, cx);
    }
    fn project_repositories(&self, cx: &App) -> Vec<String> {
        let Some(projects) = &self.projects else {
            return Vec::new();
        };
        let rows = projects.read(cx).rows();
        rows.iter()
            .filter(|project| project.removed_at.is_none())
            .filter_map(|project| project.github_repository().map(String::from))
            .collect()
    }
    /// Typing searches after a short pause; a link waits for Enter.
    pub(super) fn search_after_pause(&mut self, text: String, cx: &mut Context<Self>) {
        self.search_wait = None;
        if web_address(&text).is_some() {
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
    pub(super) fn search(&mut self, text: String, cx: &mut Context<Self>) {
        self.search_wait = None;
        if web_address(&text).is_some() {
            match PrTarget::parse_url(&text) {
                Ok(target) => self.open_linked(target, cx),
                Err(error) => {
                    self.missing_link = None;
                    self.failure = Some(error);
                }
            }
        } else {
            self.failure = None;
            self.missing_link = None;
            self.waiting_link = None;
            let text: String = text.chars().take(MAX_SEARCH).collect();
            self.queue
                .update(cx, |queue, cx| queue.set_search(text, cx));
        }
        cx.notify();
    }
}
