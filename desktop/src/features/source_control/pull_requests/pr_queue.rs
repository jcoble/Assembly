use super::pull_request::PullRequestPage;
use crate::core::socket::{client::SocketClient, outcome::Outcome, row_source::ExpectedRowSource};
use gpui_kit::*;
use serde_json::json;

#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub enum QueueState {
    #[default]
    Open,
    Mine,
    Review,
}
impl QueueState {
    pub fn wire(self) -> &'static str {
        match self {
            Self::Open => "open",
            Self::Mine => "mine",
            Self::Review => "review",
        }
    }
    pub fn label(self) -> &'static str {
        match self {
            Self::Open => "Open",
            Self::Mine => "Mine",
            Self::Review => "Needs review",
        }
    }
}
/// One visible queue owns its pending request; hiding it cancels reads, and no timer refreshes it.
pub struct PrQueue {
    pub page: PullRequestPage,
    pub state: QueueState,
    pub search: String,
    pub failure: Option<String>,
    pub loading: bool,
    session: Option<String>,
    repositories: Vec<String>,
    expected_source: Option<ExpectedRowSource>,
    active: bool,
    loaded: bool,
    revision: u64,
    read: Option<Task<()>>,
}
impl PrQueue {
    pub fn new(session: Option<String>, repositories: Vec<String>) -> Self {
        Self {
            page: PullRequestPage::default(),
            state: QueueState::Open,
            search: String::new(),
            failure: None,
            loading: false,
            session,
            repositories,
            expected_source: None,
            active: false,
            loaded: false,
            revision: 0,
            read: None,
        }
    }
    pub fn set_active(&mut self, active: bool, cx: &mut Context<Self>) {
        if self.active == active {
            return;
        }
        self.active = active;
        if !active {
            self.read = None;
            self.loading = false;
            self.revision += 1;
        } else if !self.loaded {
            self.request(false, cx);
        }
        cx.notify();
    }
    pub fn select_state(&mut self, state: QueueState, cx: &mut Context<Self>) {
        if self.state != state {
            self.state = state;
            self.refresh(cx);
        }
    }
    pub fn set_search(&mut self, text: String, cx: &mut Context<Self>) {
        if self.search != text {
            self.search = text;
            self.refresh(cx);
        }
    }
    /// A session's queue reads the source its pane read, and refuses rather than answering from anywhere else.
    pub fn set_source(&mut self, source: Option<ExpectedRowSource>, cx: &mut Context<Self>) {
        if self.expected_source != source {
            self.expected_source = source;
            self.refresh(cx);
        }
    }
    pub fn set_repositories(&mut self, repositories: Vec<String>, cx: &mut Context<Self>) {
        if self.repositories != repositories {
            self.repositories = repositories;
            self.refresh(cx);
        }
    }
    pub fn refresh(&mut self, cx: &mut Context<Self>) {
        self.read = None;
        self.loading = false;
        self.loaded = false;
        self.revision += 1;
        self.page = PullRequestPage::default();
        if self.active {
            self.request(false, cx);
        }
        cx.notify();
    }
    pub fn load_more(&mut self, cx: &mut Context<Self>) {
        if self.page.cursor.is_some() {
            self.request(true, cx);
        }
    }
    fn request(&mut self, more: bool, cx: &mut Context<Self>) {
        if !self.active || self.loading {
            return;
        }
        let Some(socket) = SocketClient::try_global(cx) else {
            self.failure = Some("The backend isn't running.".into());
            cx.notify();
            return;
        };
        if self.session.is_none() && self.repositories.is_empty() {
            self.loaded = true;
            self.page = PullRequestPage::default();
            cx.notify();
            return;
        }
        self.loading = true;
        self.failure = None;
        self.revision += 1;
        let revision = self.revision;
        let mut args = json!({"session":self.session,"repositories":self.repositories,"state":self.state.wire(),"search":self.search,"cursor":if more{self.page.cursor.clone()}else{None}});
        if let Some(source) = &self.expected_source {
            args["expectedSource"] = json!(source);
        }
        let call = socket
            .read(cx)
            .call::<PullRequestPage>("git", "prs", args, None);
        self.read = Some(cx.spawn(async move |queue, cx| {
            let answer = call.await;
            _ = queue.update(cx, |queue, cx| {
                if queue.revision != revision || !queue.active {
                    return;
                }
                queue.loading = false;
                queue.read = None;
                match answer {
                    Outcome::Ok(page) => {
                        if more {
                            queue.page.append(page);
                        } else {
                            queue.page = page;
                        }
                        queue.loaded = true;
                    }
                    Outcome::Failed(error) => queue.failure = Some(error.message),
                    Outcome::Aborted => {
                        queue.failure = Some("The pull request read was cancelled.".into())
                    }
                }
                cx.notify();
            });
        }));
        cx.notify();
    }
}
#[cfg(test)]
#[path = "pr_queue_tests.rs"]
mod tests;
