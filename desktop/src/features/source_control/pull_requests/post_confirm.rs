use super::super::git_operation::GitOperation;
use super::{
    pr_detail::PrDetail,
    review_draft::{LineComment, ReviewDraft, ReviewIdentity},
};
use gpui_kit::component::button::{Button, ButtonVariants as _};
use gpui_kit::component::{ActiveTheme as _, Disableable as _, WindowExt as _, h_flex, v_flex};
use gpui_kit::gpui_base::StyledExt as _;
use gpui_kit::*;
use serde_json::{Value, json};

#[derive(Clone)]
enum Action {
    Review(Vec<LineComment>),
    Reply(u64),
    Comment,
}
#[derive(Clone)]
pub struct PostRequest {
    identity: ReviewIdentity,
    body: String,
    action: Action,
}
impl PostRequest {
    pub fn review(draft: &ReviewDraft, current: &ReviewIdentity) -> Result<Self, String> {
        if !draft.can_post(current) {
            return Err("Refresh first, and check every review comment before posting.".into());
        }
        Ok(Self {
            identity: draft.identity.clone(),
            body: draft.body.trim().into(),
            action: Action::Review(
                draft
                    .comments
                    .iter()
                    .map(|comment| {
                        let mut target = comment.target.clone();
                        target.body = target.body.trim().into();
                        target
                    })
                    .collect(),
            ),
        })
    }
    pub fn comment(identity: ReviewIdentity, body: String) -> Result<Self, String> {
        Self::text(identity, body, Action::Comment, 20_000)
    }
    pub fn reply(identity: ReviewIdentity, id: u64, body: String) -> Result<Self, String> {
        if id == 0 {
            return Err("Refresh this review thread before replying.".into());
        }
        Self::text(identity, body, Action::Reply(id), 10_000)
    }
    fn text(
        identity: ReviewIdentity,
        body: String,
        action: Action,
        limit: usize,
    ) -> Result<Self, String> {
        if body.trim().is_empty() || body.len() > limit {
            return Err("Enter a comment within the size limit.".into());
        }
        Ok(Self {
            identity,
            body: body.trim().into(),
            action,
        })
    }
    pub fn body(&self) -> &str {
        &self.body
    }
    pub fn comments(&self) -> &[LineComment] {
        match &self.action {
            Action::Review(comments) => comments,
            _ => &[],
        }
    }
    fn title(&self) -> &'static str {
        match self.action {
            Action::Review(_) => "Post this review?",
            Action::Reply(_) => "Post this reply?",
            Action::Comment => "Post this comment?",
        }
    }
    fn action(&self) -> &'static str {
        match self.action {
            Action::Review(_) => "review",
            Action::Reply(_) => "reply",
            Action::Comment => "comment",
        }
    }
    pub fn args(&self, current: &ReviewIdentity) -> Result<Value, String> {
        if self.identity != *current {
            return Err(
                "This pull request changed. Refresh before posting. Your draft is kept.".into(),
            );
        }
        let mut args = json!({"number":self.identity.number,"expectedHead":self.identity.head,"body":self.body});
        match &self.action {
            Action::Review(comments) => args["comments"] = json!(comments),
            Action::Reply(id) => args["commentId"] = json!(id),
            Action::Comment => {}
        }
        Ok(args)
    }
}
pub struct PostConfirm {
    session: String,
    request: PostRequest,
    detail: Option<WeakEntity<PrDetail>>,
    operation: Entity<GitOperation>,
    submitted: bool,
    closing: bool,
    failure: Option<String>,
    _operation: Subscription,
}
pub fn open(
    session: String,
    request: PostRequest,
    detail: WeakEntity<PrDetail>,
    window: &mut Window,
    cx: &mut App,
) {
    let title = request.title();
    let confirm = cx.new(|cx| PostConfirm::new(session, request, Some(detail), cx));
    window.open_dialog(cx, move |dialog, _, _| {
        dialog.title(title).footer(Empty).child(confirm.clone())
    });
}
impl PostConfirm {
    pub fn new(
        session: String,
        request: PostRequest,
        detail: Option<WeakEntity<PrDetail>>,
        cx: &mut Context<Self>,
    ) -> Self {
        let operation = cx.new(|_| GitOperation::default());
        let heard = cx.observe(&operation, |view: &mut Self, _, cx| {
            if let Some(status) = &view.operation.read(cx).status {
                if status.state == "succeeded" {
                    let url = status
                        .result
                        .as_ref()
                        .and_then(Value::as_str)
                        .filter(|value| {
                            url::Url::parse(value).is_ok_and(|url| {
                                url.scheme() == "https" && url.host_str() == Some("github.com")
                            })
                        });
                    if let Some(url) = url {
                        let url = url.to_string();
                        let was_reply = matches!(view.request.action, Action::Reply(_));
                        if let Some(detail) = view.detail.clone() {
                            _ = detail
                                .update(cx, |detail, cx| detail.post_finished(was_reply, url, cx));
                        }
                        view.closing = true;
                    } else {
                        view.failure =
                            Some("The post was not confirmed. Refresh before trying again.".into());
                    }
                }
            }
            cx.notify();
        });
        Self {
            session,
            request,
            detail,
            operation,
            submitted: false,
            closing: false,
            failure: None,
            _operation: heard,
        }
    }
    pub fn confirm(&mut self, current: &ReviewIdentity, cx: &mut Context<Self>) {
        if self.submitted {
            return;
        }
        match self.request.args(current) {
            Ok(args) => {
                self.submitted = true;
                self.failure = None;
                let session = self.session.clone();
                let action = self.request.action();
                self.operation.update(cx, |operation, cx| {
                    operation.start(action, Some(session), args, cx)
                });
            }
            Err(error) => self.failure = Some(error),
        }
        cx.notify();
    }
    pub fn uncertain(&self, cx: &App) -> bool {
        self.operation
            .read(cx)
            .status
            .as_ref()
            .is_some_and(|status| status.state == "uncertain")
    }
    fn current(&self, cx: &App) -> Option<ReviewIdentity> {
        self.detail.as_ref()?.upgrade()?.read(cx).current_identity()
    }
    fn refresh(&self, window: &mut Window, cx: &mut App) {
        if let Some(detail) = self.detail.as_ref().and_then(WeakEntity::upgrade) {
            detail.update(cx, |detail, cx| detail.refresh(cx));
        }
        window.close_dialog(cx);
    }
}
impl Render for PostConfirm {
    fn render(&mut self, window: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        if self.closing {
            self.closing = false;
            cx.defer_in(window, |_, window, cx| window.close_dialog(cx));
        }
        let busy = self.operation.read(cx).busy;
        let theme = cx.theme();
        let (danger, muted, border, radius) = (
            theme.danger,
            theme.muted_foreground,
            theme.border,
            theme.radius,
        );
        let current = self.current(cx);
        let can_post = !self.submitted
            && current
                .as_ref()
                .is_some_and(|current| self.request.args(current).is_ok());
        let uncertain = self.uncertain(cx);
        let identity = &self.request.identity;
        let is_comment = matches!(self.request.action, Action::Comment);
        let head: String = identity.head.chars().take(10).collect();
        v_flex()
            .gap_2()
            .child(
                div()
                    .font_semibold()
                    .child(format!("{} #{} · head {head}", identity.repository, identity.number)),
            )
            .children(match self.request.action {
                Action::Reply(id) => Some(format!("Reply to review comment {id}")),
                _ => None,
            })
            .child(
                div()
                    .id("post-body")
                    .max_h(px(240.))
                    .overflow_y_scroll()
                    .p_2()
                    .rounded(radius)
                    .border_1()
                    .border_color(border)
                    .child(self.request.body.clone()),
            )
            .children(self.request.comments().iter().map(|comment| {
                h_flex()
                    .gap_2()
                    .text_sm()
                    .child(
                        div()
                            .flex_none()
                            .font_semibold()
                            .child(format!("{}:{}", comment.path, comment.line)),
                    )
                    .child(comment.body.clone())
            }))
            .child(div().text_sm().text_color(muted).child(if is_comment {
                "GitHub will notify participants."
            } else {
                "GitHub will notify participants. Assembly will check the PR head again before posting."
            }))
            .children(
                self.failure
                    .clone()
                    .map(|error| div().text_color(danger).child(error)),
            )
            .children(
                self.operation
                    .read(cx)
                    .error
                    .clone()
                    .map(|error| div().text_color(danger).child(error)),
            )
            .children(uncertain.then(|| {
                div()
                    .text_sm()
                    .child("This may have posted. Refresh before trying again.")
            }))
            .children((current.is_none() && !self.submitted).then(|| {
                div()
                    .text_sm()
                    .child("Refresh the pull request before posting. Your draft is kept.")
            }))
            .child(
                h_flex()
                    .gap_2()
                    .justify_end()
                    .child(
                        Button::new("post-close")
                            .ghost()
                            .label("Cancel")
                            .on_click(|_, window, cx| window.close_dialog(cx)),
                    )
                    .children((uncertain || current.is_none()).then(|| {
                        Button::new("post-refresh")
                            .ghost()
                            .label("Refresh this pull request")
                            .on_click(cx.listener(|view, _, window, cx| view.refresh(window, cx)))
                    }))
                    .child(
                        Button::new("post-confirm")
                            .primary()
                            .label(if busy {
                                "Posting…"
                            } else if matches!(self.request.action, Action::Reply(_)) {
                                "Post reply to GitHub"
                            } else {
                                "Post to GitHub"
                            })
                            .disabled(!can_post)
                            .on_click(cx.listener(|view, _, _, cx| {
                                if let Some(current) = view.current(cx) {
                                    view.confirm(&current, cx);
                                }
                            })),
                    ),
            )
    }
}
#[cfg(test)]
#[path = "post_confirm_tests.rs"]
mod tests;
