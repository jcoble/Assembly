use super::super::git_operation::GitOperation;
use super::{pr_detail::PrDetail, pull_request::PullRequestDetail};
use crate::core::intents::open_url::OpenUrl;
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
    detail: WeakEntity<PrDetail>,
    operation: Entity<GitOperation>,
    submitted: bool,
    merged: Option<String>,
    failure: Option<String>,
    _operation: Subscription,
}
pub fn open(
    session: String,
    detail: PullRequestDetail,
    owner: WeakEntity<PrDetail>,
    window: &mut Window,
    cx: &mut App,
) {
    if !MergeRequest::available(&detail.state, detail.is_draft, &detail.mergeable) {
        return;
    }
    let request = MergeRequest {
        number: detail.number,
        head: detail.head_sha,
        base: detail.base_sha,
        head_branch: detail.head_branch,
        base_branch: detail.base_branch,
        method: String::new(),
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
                        view.merged = Some(url.into());
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
            detail: owner,
            operation,
            submitted: false,
            merged: None,
            failure: None,
            _operation: heard,
        }
    });
    window.open_dialog(cx, move |dialog, _, _| {
        dialog
            .title("Confirm pull request merge")
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
    fn render(&mut self, _: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        let busy = self.operation.read(cx).busy;
        let danger = cx.theme().danger;
        let current = self.is_current(cx);
        let mut methods = h_flex().gap_1();
        for (method, label) in [
            ("merge", "Merge commit"),
            ("squash", "Squash"),
            ("rebase", "Rebase"),
        ] {
            methods = methods.child(
                Button::new(method)
                    .ghost()
                    .label(label)
                    .disabled(self.submitted || self.request.method == method)
                    .on_click(cx.listener(move |view, _, _, cx| {
                        view.request.method = method.into();
                        cx.notify();
                    })),
            );
        }
        let mut root = v_flex()
            .gap_2()
            .child(format!(
                "#{} · {} → {}",
                self.request.number, self.request.head_branch, self.request.base_branch
            ))
            .child(format!("Head: {}", self.request.head))
            .child(format!("Base: {}", self.request.base))
            .child(methods)
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
            );
        if !current && !self.submitted {
            root = root.child(
                "Refresh before merging: the pull request changed or is no longer mergeable.",
            );
        }
        if self
            .operation
            .read(cx)
            .status
            .as_ref()
            .is_some_and(|status| status.state == "uncertain")
        {
            root = root.child("This may have posted. Refresh before trying again.");
        }
        if let Some(url) = self.merged.clone() {
            root = root.child(
                Button::new("merged-pr-link")
                    .ghost()
                    .label("Merged · Open on GitHub")
                    .on_click(move |_, _, cx| cx.dispatch_action(&OpenUrl { url: url.clone() })),
            );
        }
        root.child(
            h_flex()
                .gap_2()
                .child(
                    Button::new("merge-close")
                        .ghost()
                        .label("Close")
                        .on_click(|_, window, cx| window.close_dialog(cx)),
                )
                .child(
                    Button::new("merge-refresh")
                        .ghost()
                        .label("Refresh pull request")
                        .on_click(cx.listener(|view, _, window, cx| {
                            if let Some(detail) = view.detail.upgrade() {
                                detail.update(cx, |detail, cx| detail.refresh(cx));
                            }
                            window.close_dialog(cx);
                        })),
                )
                .child(
                    Button::new("merge-confirm")
                        .primary()
                        .label(if busy { "Merging…" } else { "Confirm merge" })
                        .disabled(self.submitted || !current || self.request.args().is_err())
                        .on_click(cx.listener(|view, _, _, cx| view.confirm(cx))),
                ),
        )
    }
}
#[cfg(test)]
#[path = "merge_dialog_tests.rs"]
mod tests;
