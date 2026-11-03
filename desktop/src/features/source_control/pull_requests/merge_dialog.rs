use super::super::git_operation::GitOperation;
use super::{
    pr_activity::merge_method_label, pr_detail::PrDetail, pull_request::PullRequestDetail,
};
use gpui_kit::component::button::{Button, ButtonVariants as _};
use gpui_kit::component::{ActiveTheme as _, Disableable as _, WindowExt as _, h_flex, v_flex};
use gpui_kit::*;
use serde_json::{Value, json};

#[derive(Clone)]
pub struct MergeRequest {
    pub number: u64,
    pub head: String,
    pub base: String,
    pub head_branch: String,
    pub base_branch: String,
    pub method: String,
}
impl MergeRequest {
    pub fn available(state: &str, draft: bool, mergeable: &str) -> bool {
        state == "OPEN" && !draft && mergeable == "MERGEABLE"
    }
    pub fn args(&self) -> Result<Value, String> {
        let sha =
            |value: &str| value.len() == 40 && value.bytes().all(|byte| byte.is_ascii_hexdigit());
        if self.number == 0
            || !sha(&self.head)
            || !sha(&self.base)
            || !matches!(self.method.as_str(), "merge" | "squash" | "rebase")
        {
            return Err("Choose a merge method and refresh both revisions before merging.".into());
        }
        Ok(
            json!({"number":self.number,"method":self.method,"expectedHead":self.head,"expectedBase":self.base}),
        )
    }
    fn current(&self, detail: &PullRequestDetail) -> bool {
        Self::available(&detail.state, detail.is_draft, &detail.mergeable)
            && self.number == detail.number
            && self.head == detail.head_sha
            && self.base == detail.base_sha
    }
}
struct MergeDialog {
    session: String,
    request: MergeRequest,
    repository: String,
    title: String,
    has_draft: bool,
    closing: bool,
    detail: WeakEntity<PrDetail>,
    operation: Entity<GitOperation>,
    submitted: bool,
    failure: Option<String>,
    _operation: Subscription,
}
pub fn open(
    session: String,
    detail: PullRequestDetail,
    method: &str,
    has_draft: bool,
    owner: WeakEntity<PrDetail>,
    window: &mut Window,
    cx: &mut App,
) {
    if !MergeRequest::available(&detail.state, detail.is_draft, &detail.mergeable) {
        return;
    }
    let repository = detail.repository;
    let title = detail.title;
    let request = MergeRequest {
        number: detail.number,
        head: detail.head_sha,
        base: detail.base_sha,
        head_branch: detail.head_branch,
        base_branch: detail.base_branch,
        method: method.into(),
    };
    let form = cx.new(|cx| {
        let operation = cx.new(|_| GitOperation::default());
        let heard = cx.observe(&operation, |view: &mut MergeDialog, _, cx| {
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
                        let owner = view.detail.clone();
                        _ = owner.update(cx, |detail, cx| detail.merge_finished(url, cx));
                        view.closing = true;
                    } else {
                        view.failure = Some(
                            "The merge was not confirmed. Refresh before trying again.".into(),
                        );
                    }
                }
            }
            cx.notify();
        });
        MergeDialog {
            session,
            request,
            repository,
            title,
            has_draft,
            closing: false,
            detail: owner,
            operation,
            submitted: false,
            failure: None,
            _operation: heard,
        }
    });
    window.open_dialog(cx, move |dialog, _, _| {
        dialog
            .title("Merge this pull request?")
            .footer(Empty)
            .child(form.clone())
    });
}
impl MergeDialog {
    fn is_current(&self, cx: &App) -> bool {
        self.detail.upgrade().is_some_and(|owner| {
            let owner = owner.read(cx);
            owner.current_identity().is_some()
                && owner
                    .detail
                    .as_ref()
                    .is_some_and(|detail| self.request.current(detail))
        })
    }
    fn confirm(&mut self, cx: &mut Context<Self>) {
        if self.submitted {
            return;
        }
        if !self.is_current(cx) {
            self.failure = Some("This pull request changed. Refresh before merging.".into());
            cx.notify();
            return;
        }
        match self.request.args() {
            Ok(args) => {
                self.submitted = true;
                let session = self.session.clone();
                self.operation.update(cx, |operation, cx| {
                    operation.start("merge", Some(session), args, cx)
                });
            }
            Err(error) => self.failure = Some(error),
        }
        cx.notify();
    }
}
impl Render for MergeDialog {
    fn render(&mut self, window: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        if self.closing {
            self.closing = false;
            cx.defer_in(window, |_, window, cx| window.close_dialog(cx));
        }
        let busy = self.operation.read(cx).busy;
        let theme = cx.theme();
        let (danger, muted) = (theme.danger, theme.muted_foreground);
        let current = self.is_current(cx);
        let uncertain = self
            .operation
            .read(cx)
            .status
            .as_ref()
            .is_some_and(|status| status.state == "uncertain");
        let short = |sha: &str| sha.chars().take(10).collect::<String>();
        v_flex()
            .gap_2()
            .child(
                div()
                    .font_semibold()
                    .child(format!("{} #{} · {}", self.repository, self.request.number, self.title)),
            )
            .child(format!(
                "{} into {} using {}.",
                self.request.head_branch,
                self.request.base_branch,
                merge_method_label(&self.request.method)
            ))
            .child(div().text_sm().text_color(muted).child(format!(
                "Head {} · base {}",
                short(&self.request.head),
                short(&self.request.base)
            )))
            .children(self.has_draft.then(|| {
                div()
                    .text_sm()
                    .child("Your unposted comment will be cleared if the merge succeeds.")
            }))
            .child(div().text_sm().text_color(muted).child(
                "Assembly checks the PR again before asking GitHub to merge it. GitHub enforces the repository's merge rules.",
            ))
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
            .children((!current && !self.submitted).then(|| {
                div().text_sm().child(
                    "Refresh before merging: the pull request changed or is no longer mergeable.",
                )
            }))
            .children(uncertain.then(|| {
                div().text_sm().child(
                    "Refresh this PR to check whether the merge completed before trying again.",
                )
            }))
            .child(
                h_flex()
                    .gap_2()
                    .justify_end()
                    .child(
                        Button::new("merge-close")
                            .ghost()
                            .label("Cancel")
                            .on_click(|_, window, cx| window.close_dialog(cx)),
                    )
                    .children((uncertain || !current).then(|| {
                        Button::new("merge-refresh")
                            .ghost()
                            .label("Refresh this pull request")
                            .on_click(cx.listener(|view, _, window, cx| {
                                if let Some(detail) = view.detail.upgrade() {
                                    detail.update(cx, |detail, cx| detail.refresh(cx));
                                }
                                window.close_dialog(cx);
                            }))
                    }))
                    .child(
                        Button::new("merge-confirm")
                            .primary()
                            .label(if busy { "Merging…" } else { "Merge on GitHub" })
                            .disabled(self.submitted || !current || self.request.args().is_err())
                            .on_click(cx.listener(|view, _, _, cx| view.confirm(cx))),
                    ),
            )
    }
}
#[cfg(test)]
#[path = "merge_dialog_tests.rs"]
mod tests;
