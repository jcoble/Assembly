//! The pull request pane's search: typing searches after a short pause, a link waits for Enter.
use super::*;

impl PrPane {
    /// A pull request named by a link, pasted here or followed from anywhere. A session's link is checked against the
    /// backend's actual origin before any detail is displayed; otherwise the repository must be a project here, or the
    /// tab says so and offers the page on GitHub.
    pub(super) fn open_linked(&mut self, target: PrTarget, cx: &mut Context<Self>) {
        self.missing_link = None;
        if self.session.is_some() {
            return self.select_pr(Some(target.repository), target.number, cx);
        }
        match target.clone().among(&self.known_repositories(cx)) {
            Some(target) => self.select_pr(Some(target.repository), target.number, cx),
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
    fn known_repositories(&self, cx: &App) -> Vec<String> {
        let mut known = self.scope.clone();
        let listed = self.queue.read(cx).page.items.iter();
        known.extend(listed.map(|pr| pr.repository.clone()));
        if let Some(projects) = &self.projects {
            let rows = projects.read(cx).rows();
            known.extend(
                rows.iter()
                    .filter_map(|project| project.github_repository().map(String::from)),
            );
        }
        known
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
