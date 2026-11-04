//! The pull request pane's search: typing searches after a short pause, a link waits for Enter.
use super::*;

impl PrPane {
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
}
