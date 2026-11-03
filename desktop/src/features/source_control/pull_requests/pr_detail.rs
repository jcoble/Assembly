use crate::core::theme::text::{TextRole, TextRoleExt as _};
use super::{
    pr_files::PrFiles,
    pull_request::PullRequestDetail,
    review_draft::{ReviewDraft, ReviewIdentity},
};
use crate::core::{
    intents::open_url::OpenUrl,
    socket::{client::SocketClient, outcome::Outcome},
    ui::markdown::view::MarkdownView,
};
use gpui_kit::component::button::{Button, ButtonVariants as _};
use gpui_kit::component::input::{InputEvent, Textarea, TextareaState};
use gpui_kit::component::{ActiveTheme as _, Disableable as _, h_flex, v_flex};
use gpui_kit::prelude::FluentBuilder as _;
use gpui_kit::*;
use serde_json::json;

#[derive(Clone, Copy, PartialEq)]
enum Tab {
    Conversation,
    Checks,
    Files,
}
pub struct PrDetail {
    session: String,
    repository: Option<String>,
    number: u64,
    active: bool,
    loading: bool,
    needs_refresh: bool,
    revision: u64,
    pub detail: Option<PullRequestDetail>,
    pub failure: Option<String>,
    review: Option<Entity<ReviewDraft>>,
    files: Option<Entity<PrFiles>>,
    review_input: Option<(Entity<TextareaState>, Subscription)>,
    tab: Tab,
    read: Option<Task<()>>,
    _review: Option<Subscription>,
}
impl PrDetail {
    pub fn new(
        session: String,
        repository: Option<String>,
        number: u64,
        active: bool,
        cx: &mut Context<Self>,
    ) -> Self {
        let mut detail = Self {
            session,
            repository,
            number,
            active,
            loading: false,
            needs_refresh: true,
            revision: 0,
            detail: None,
            failure: None,
            review: None,
            files: None,
            review_input: None,
            tab: Tab::Conversation,
            read: None,
            _review: None,
        };
        if active {
            detail.refresh(cx);
        }
        detail
    }
    pub fn set_active(&mut self, active: bool, cx: &mut Context<Self>) {
        if self.active == active {
            return;
        }
        self.active = active;
        if !active {
            self.needs_refresh |= self.loading;
            self.loading = false;
            self.read = None;
            self.revision += 1;
        } else if self.needs_refresh {
            self.refresh(cx);
        }
        if let Some(files) = &self.files {
            files.update(cx, |files, cx| {
                files.set_active(active && self.tab == Tab::Files, cx)
            });
        }
        cx.notify();
    }
    pub fn refresh(&mut self, cx: &mut Context<Self>) {
        self.needs_refresh = true;
        if !self.active {
            return;
        }
        self.read = None;
        self.revision += 1;
        let revision = self.revision;
        self.loading = true;
        self.failure = None;
        let Some(socket) = SocketClient::try_bulk(cx) else {
            self.loading = false;
            self.failure = Some("The bulk connection isn't running.".into());
            cx.notify();
            return;
        };
        let call = socket.read(cx).call::<PullRequestDetail>(
            "git",
            "pr",
            json!({"session":self.session,"number":self.number}),
            None,
        );
        self.read=Some(cx.spawn(async move|view,cx|{
            let answer=call.await;
            _=view.update(cx,|view,cx|{
                if view.revision!=revision||!view.active{return;}view.loading=false;view.read=None;
                match answer {
                    Outcome::Ok(detail)=>{
                        if detail.number!=view.number||view.repository.as_ref().is_some_and(|repo|!repo.eq_ignore_ascii_case(&detail.repository)) {
                            view.failure=Some("This pull request is not in the selected checkout's repository.".into());
                        }else if detail.head_sha.len()!=40||detail.base_sha.len()!=40||!detail.head_sha.bytes().all(|byte|byte.is_ascii_hexdigit())||!detail.base_sha.bytes().all(|byte|byte.is_ascii_hexdigit()) {
                            view.failure=Some("GitHub did not confirm this pull request's revisions. Refresh before trying again.".into());
                        }else {
                            view.repository=Some(detail.repository.clone());view.needs_refresh=false;
                            if view.review.is_none(){
                                let review=cx.new(|_|ReviewDraft::new(ReviewIdentity{repository:detail.repository.clone(),number:detail.number,head:detail.head_sha.clone(),base:detail.base_sha.clone()}));
                                view._review=Some(cx.observe(&review,|_,_,cx|cx.notify()));view.review=Some(review);
                            }
                            if let Some(files)=&view.files{files.update(cx,|files,cx|files.replace_detail(&detail,cx));}
                            view.detail=Some(detail);view.sync_files(cx);
                        }
                    },Outcome::Failed(error)=>view.failure=Some(error.message),Outcome::Aborted=>view.failure=Some("The pull request read was cancelled.".into()),
                }cx.notify();
            });
        }));
        cx.notify();
    }
    pub fn current_identity(&self) -> Option<ReviewIdentity> {
        if self.loading || self.needs_refresh {
            return None;
        }
        let detail = self.detail.as_ref()?;
        if detail.state != "OPEN" {
            return None;
        }
        Some(ReviewIdentity {
            repository: detail.repository.clone(),
            number: detail.number,
            head: detail.head_sha.clone(),
            base: detail.base_sha.clone(),
        })
    }
    fn ensure_review_input(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        if self.review_input.is_some() {
            return;
        }
        let Some(review) = self.review.clone() else {
            return;
        };
        let body = review.read(cx).body.clone();
        let input = cx.new(|cx| {
            TextareaState::new(window, cx)
                .default_value(body)
                .placeholder("Overall review comment")
                .auto_grow(3, 8)
        });
        let heard = cx.subscribe(&input, move |_, input, event: &InputEvent, cx| {
            if matches!(event, InputEvent::Change) {
                let body = input.read(cx).value().to_string();
                review.update(cx, |review, cx| {
                    review.body = body;
                    cx.notify();
                });
            }
        });
        self.review_input = Some((input, heard));
    }
    fn preview_review(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        let request = self
            .current_identity()
            .ok_or_else(|| "Refresh first. Your review draft is kept.".to_string())
            .and_then(|identity| {
                self.review
                    .as_ref()
                    .ok_or_else(|| "Start a review first.".to_string())
                    .and_then(|review| {
                        super::post_confirm::PostRequest::review(review.read(cx), &identity)
                    })
            });
        match request {
            Ok(request) => super::post_confirm::open(
                self.session.clone(),
                request,
                cx.entity().downgrade(),
                window,
                cx,
            ),
            Err(error) => {
                self.failure = Some(error);
                cx.notify();
            }
        }
    }
    fn sync_files(&mut self, cx: &mut Context<Self>) {
        if self.tab != Tab::Files {
            return;
        }
        if self.files.is_none() {
            if let (Some(detail), Some(review)) = (&self.detail, &self.review) {
                let session = self.session.clone();
                let review = review.clone();
                self.files = Some(cx.new(|cx| PrFiles::new(session, detail, review, cx)));
            }
        }
        if let Some(files) = &self.files {
            files.update(cx, |files, cx| files.set_active(self.active, cx));
        }
    }
    fn select_tab(&mut self, tab: Tab, cx: &mut Context<Self>) {
        self.tab = tab;
        if let Some(files) = &self.files {
            files.update(cx, |files, cx| {
                files.set_active(self.active && tab == Tab::Files, cx)
            });
        }
        self.sync_files(cx);
        cx.notify();
    }
}
impl Render for PrDetail {
    fn render(&mut self, window: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        self.ensure_review_input(window, cx);
        let danger = cx.theme().danger;
        let mut root = v_flex()
            .size_full()
            .min_h_0()
            .gap_2()
            .child(
                Button::new("pr-refresh")
                    .ghost()
                    .compact()
                    .label(if self.loading {
                        "Refreshing…"
                    } else {
                        "Refresh"
                    })
                    .disabled(self.loading)
                    .on_click(cx.listener(|view, _, _, cx| view.refresh(cx))),
            )
            .children(
                self.failure
                    .clone()
                    .map(|error| div().text_role(TextRole::Body, cx).text_color(danger).child(error)),
            );
        let Some(detail) = self.detail.clone() else {
            return root.into_any_element();
        };
        let url = detail.url.clone();
        root = root
            .child(
                h_flex()
                    .gap_2()
                    .child(
                        div()
                            .flex_1()
                            .child(format!("#{} {}", detail.number, detail.title)),
                    )
                    .child(
                        Button::new("pr-open-web")
                            .ghost()
                            .compact()
                            .label("Open on GitHub")
                            .on_click(move |_, _, cx| {
                                cx.dispatch_action(&OpenUrl { url: url.clone() })
                            }),
                    ),
            )
            .child(div().text_role(TextRole::Body, cx).child(format!(
                "{} · {} → {}{}",
                detail.state,
                detail.head_branch,
                detail.base_branch,
                if detail.is_draft { " · Draft" } else { "" }
            )))
            .child(
                div()
                    .text_role(TextRole::Body, cx)
                    .child(format!("{} / {}", detail.head_sha, detail.base_sha)),
            );
        let can_post = self.current_identity().is_some();
        let mut actions = h_flex().gap_2().child(
            Button::new("pr-comment")
                .ghost()
                .compact()
                .label("Comment")
                .disabled(!can_post)
                .on_click(cx.listener(|view, _, window, cx| {
                    if let Some(identity) = view.current_identity() {
                        super::post_composer::open(
                            view.session.clone(),
                            identity,
                            None,
                            cx.entity().downgrade(),
                            window,
                            cx,
                        );
                    }
                })),
        );
        if super::merge_dialog::MergeRequest::available(
            &detail.state,
            detail.is_draft,
            &detail.mergeable,
        ) {
            actions = actions.child(
                Button::new("pr-merge")
                    .ghost()
                    .compact()
                    .label("Merge…")
                    .disabled(!can_post)
                    .on_click(cx.listener(|view, _, window, cx| {
                        if let Some(detail) = view.detail.clone() {
                            super::merge_dialog::open(
                                view.session.clone(),
                                detail,
                                cx.entity().downgrade(),
                                window,
                                cx,
                            );
                        }
                    })),
            );
        }
        root = root.child(actions);
        if let Some((input, _)) = &self.review_input {
            let reviewable = self.current_identity().is_some_and(|identity| {
                self.review
                    .as_ref()
                    .is_some_and(|review| review.read(cx).can_post(&identity))
            });
            root = root.child(Textarea::new(input)).child(
                Button::new("pr-review-preview")
                    .ghost()
                    .label("Preview review")
                    .disabled(!reviewable)
                    .on_click(cx.listener(|view, _, window, cx| view.preview_review(window, cx))),
            );
            if self.current_identity().is_some_and(|identity| {
                self.review
                    .as_ref()
                    .is_some_and(|review| review.read(cx).identity != identity)
            }) {
                root=root.child("The pull request changed. Your review draft is kept at its original head; inspect it before posting.");
            }
        }
        let mut tabs = h_flex().gap_1();
        for (label, tab) in [
            ("Conversation", Tab::Conversation),
            ("Checks", Tab::Checks),
            ("Files changed", Tab::Files),
        ] {
            tabs = tabs.child(
                Button::new(label)
                    .ghost()
                    .compact()
                    .label(label)
                    .disabled(self.tab == tab)
                    .on_click(cx.listener(move |view, _, _, cx| view.select_tab(tab, cx))),
            );
        }
        root = root.child(tabs);
        match self.tab {
            Tab::Files => {
                if let Some(files) = &self.files {
                    root = root.child(files.clone());
                }
                if detail.more_files {
                    root=root.child("Only the first 100 files are listed. Open the pull request on GitHub to see all files.");
                }
            }
            Tab::Checks => {
                root = root.children(detail.checks.into_iter().map(|check| {
                    let url = check.url.clone();
                    h_flex()
                        .gap_2()
                        .child(format!(
                            "{} · {} {}",
                            check.name, check.status, check.conclusion
                        ))
                        .child(
                            Button::new(SharedString::from(format!("check:{}", check.name)))
                                .ghost()
                                .compact()
                                .label("Open")
                                .disabled(url.is_empty())
                                .on_click(move |_, _, cx| {
                                    cx.dispatch_action(&OpenUrl { url: url.clone() })
                                }),
                        )
                }));
            }
            Tab::Conversation => {
                let mut conversation = v_flex().gap_2().child(MarkdownView {
                    id: "pr-body".into(),
                    blocks: detail.body_blocks.clone(),
                    session: None,
                    expected_source: None,
                });
                for (index, event) in detail.timeline.iter().enumerate() {
                    let blocks = if event.kind == "comment" {
                        detail
                            .comments
                            .iter()
                            .find(|comment| Some(&comment.id) == event.comment_id.as_ref())
                            .map(|comment| comment.body_blocks.clone())
                    } else if event.kind == "review" {
                        detail
                            .reviews
                            .iter()
                            .find(|review| {
                                review.author == event.author
                                    && review.body == event.body
                                    && review.submitted_at == event.created_at
                            })
                            .map(|review| review.body_blocks.clone())
                    } else {
                        None
                    };
                    let mut row = v_flex().gap_1().child(format!(
                        "{} · {} · {}",
                        event.author, event.kind, event.created_at
                    ));
                    row = if let Some(blocks) = blocks {
                        row.child(MarkdownView {
                            id: SharedString::from(format!("pr-timeline:{index}")),
                            blocks,
                            session: None,
                            expected_source: None,
                        })
                    } else {
                        row.child(event.body.clone())
                    };
                    if event.kind == "comment" {
                        if let Some(comment) = detail
                            .comments
                            .iter()
                            .find(|comment| Some(&comment.id) == event.comment_id.as_ref())
                        {
                            if comment.path.is_some() && comment.reply_to_id.is_none() {
                                if let Ok(id) = comment.id.parse::<u64>() {
                                    row =
                                        row.child(
                                            Button::new(SharedString::from(format!(
                                                "reply:{}",
                                                comment.id
                                            )))
                                            .ghost()
                                            .compact()
                                            .label("Reply")
                                            .disabled(!can_post)
                                            .on_click(cx.listener(move |view, _, window, cx| {
                                                if let Some(identity) = view.current_identity() {
                                                    super::post_composer::open(
                                                        view.session.clone(),
                                                        identity,
                                                        Some(id),
                                                        cx.entity().downgrade(),
                                                        window,
                                                        cx,
                                                    );
                                                }
                                            })),
                                        );
                                }
                            }
                        }
                    }
                    conversation = conversation.child(row);
                }
                root = root.child(
                    div()
                        .id("pr-conversation")
                        .flex_1()
                        .min_h_0()
                        .overflow_y_scroll()
                        .child(conversation),
                );
            }
        }
        root.into_any_element()
    }
}
#[cfg(test)]
#[path = "pr_detail_tests.rs"]
mod tests;
