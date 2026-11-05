//! The pull request pane's search: typing searches after a short pause, a link waits for Enter.
use super::*;

impl PrPane {
    /// A pull request named by a link, pasted here or followed from anywhere, as the old `openLink` did. A link to the
    /// followed session's own repository (its list shows it) opens through the session, checked against the backend's
    /// actual origin. Any other opens through the project holding its repository, or the tab says no project does and
    /// offers the page on GitHub.
    pub(super) fn open_linked(&mut self, target: PrTarget, cx: &mut Context<Self>) {
        self.missing_link = None;
        let listed = self.queue.read(cx).page.items.iter();
        let sessions = self.session.is_some()
            && listed
                .map(|pr| &pr.repository)
                .any(|repository| repository.eq_ignore_ascii_case(&target.repository));
        if sessions {
            return self.select_pr(Some(target.repository), target.number, cx);
        }
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
    pub(super) fn search(&mut self, text: String, cx: &mut Context<Self>) {
        self.search_wait = None;
        if text.trim().starts_with("https://") {
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
            let text: String = text.chars().take(MAX_SEARCH).collect();
            self.queue
                .update(cx, |queue, cx| queue.set_search(text, cx));
        }
        cx.notify();
    }
}
