use crate::core::theme::text::{TextRole, TextRoleExt as _};
use super::{
    pr_detail::PrDetail,
    review_draft::{LineComment, ReviewDraft, ReviewIdentity},
};
use gpui_kit::component::button::{Button, ButtonVariants as _};
use gpui_kit::component::{ActiveTheme as _, Disableable as _, WindowExt as _, h_flex, v_flex};
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
    pub fn is_reply(&self) -> bool {
        matches!(self.action, Action::Reply(_))
    }
    fn title(&self) -> &'static str {
        match self.action {
            Action::Review(_) => "Post this review?",
            Action::Reply(_) => "Post this reply?",
            Action::Comment => "Post this comment?",
        }
    }
    pub(super) fn action(&self) -> &'static str {
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
/// What will be posted, and the go-ahead. Confirming closes this; the page shows the post running and how it went.
pub struct PostConfirm {
    request: PostRequest,
    detail: WeakEntity<PrDetail>,
    failure: Option<String>,
}
pub fn open(request: PostRequest, detail: WeakEntity<PrDetail>, window: &mut Window, cx: &mut App) {
    let title = request.title();
    let confirm = cx.new(|_| PostConfirm {
        request,
        detail,
        failure: None,
    });
    window.open_dialog(cx, move |dialog, _, _| {
        dialog.title(title).footer(Empty).child(confirm.clone())
    });
}
impl PostConfirm {
    fn current(&self, cx: &App) -> Option<ReviewIdentity> {
        self.detail.upgrade()?.read(cx).current_identity()
    }
    fn free(&self, cx: &App) -> bool {
        self.detail
            .upgrade()
            .is_some_and(|detail| detail.read(cx).writes_free(cx))
    }
    fn confirm(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        let request = self.request.clone();
        let started = self
            .detail
            .update(cx, |detail, cx| detail.start_post(&request, cx));
        match started {
            Ok(Err(error)) => {
                self.failure = Some(error);
                cx.notify();
            }
            _ => window.close_dialog(cx),
        }
    }
    fn refresh(&self, window: &mut Window, cx: &mut App) {
        _ = self.detail.update(cx, |detail, cx| detail.refresh(cx));
        window.close_dialog(cx);
    }
}
impl Render for PostConfirm {
    fn render(&mut self, _window: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        let theme = cx.theme();
        let (danger, muted, border, radius) = (
            theme.danger,
            theme.muted_foreground,
            theme.border,
            theme.radius,
        );
        let current = self.current(cx);
        let can_post = self.free(cx)
            && current
                .as_ref()
                .is_some_and(|current| self.request.args(current).is_ok());
        let identity = &self.request.identity;
        let is_comment = matches!(self.request.action, Action::Comment);
        let head: String = identity.head.chars().take(10).collect();
        v_flex()
            .gap_2()
            .child(div().font_weight(FontWeight::SEMIBOLD).child(format!(
                "{} #{} · head {head}",
                identity.repository, identity.number
            )))
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
                    .text_role(TextRole::Body, cx)
                    .child(
                        div()
                            .flex_none()
                            .font_weight(FontWeight::SEMIBOLD)
                            .child(format!("{}:{}", comment.path, comment.line)),
                    )
                    .child(comment.body.clone())
            }))
            .child(div().text_role(TextRole::Body, cx).text_color(muted).child(if is_comment {
                "GitHub will notify participants."
            } else {
                "GitHub will notify participants. Assembly will check the PR head again before posting."
            }))
            .children(
                self.failure
                    .clone()
                    .map(|error| div().text_color(danger).child(error)),
            )
            .children(current.is_none().then(|| {
                div()
                    .text_role(TextRole::Body, cx)
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
                    .children(current.is_none().then(|| {
                        Button::new("post-refresh")
                            .ghost()
                            .label("Refresh this pull request")
                            .on_click(cx.listener(|view, _, window, cx| view.refresh(window, cx)))
                    }))
                    .child(
                        Button::new("post-confirm")
                            .primary()
                            .label(if self.request.is_reply() {
                                "Post reply to GitHub"
                            } else {
                                "Post to GitHub"
                            })
                            .disabled(!can_post)
                            .on_click(cx.listener(|view, _, window, cx| view.confirm(window, cx))),
                    ),
            )
    }
}
#[cfg(test)]
#[path = "post_confirm_tests.rs"]
mod tests;
