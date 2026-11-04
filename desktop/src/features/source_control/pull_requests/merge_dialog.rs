use super::{
    pr_activity::merge_method_label, pr_detail::PrDetail, pull_request::PullRequestDetail,
};
use gpui_kit::component::button::{Button, ButtonVariants as _};
use gpui_kit::component::{
    ActiveTheme as _, Disableable as _, Sizable as _, WindowExt as _, h_flex, v_flex,
};
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
    pub fn current(&self, detail: &PullRequestDetail) -> bool {
        Self::available(&detail.state, detail.is_draft, &detail.mergeable)
            && self.number == detail.number
            && self.head == detail.head_sha
            && self.base == detail.base_sha
    }
}
/// What will be merged, and the go-ahead. Confirming closes this; the merge card shows it running and how it went.
struct MergeDialog {
    request: MergeRequest,
    repository: String,
    title: String,
    has_draft: bool,
    detail: WeakEntity<PrDetail>,
    failure: Option<String>,
}
pub fn open(
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
    let request = MergeRequest {
        number: detail.number,
        head: detail.head_sha,
        base: detail.base_sha,
        head_branch: detail.head_branch,
        base_branch: detail.base_branch,
        method: method.into(),
    };
    let form = cx.new(|_| MergeDialog {
        request,
        repository: detail.repository,
        title: detail.title,
        has_draft,
        detail: owner,
        failure: None,
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
                && owner.writes_free(cx)
                && owner
                    .detail
                    .as_ref()
                    .is_some_and(|detail| self.request.current(detail))
        })
    }
    fn confirm(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        if !self.is_current(cx) {
            self.failure = Some("This pull request changed. Refresh before merging.".into());
            cx.notify();
            return;
        }
        let request = self.request.clone();
        let started = self
            .detail
            .update(cx, |detail, cx| detail.start_merge(&request, cx));
        match started {
            Ok(Err(error)) => {
                self.failure = Some(error);
                cx.notify();
            }
            _ => window.close_dialog(cx),
        }
    }
}
impl Render for MergeDialog {
    fn render(&mut self, _window: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        let theme = cx.theme();
        let (danger, muted, mono) = (
            theme.danger,
            theme.muted_foreground,
            theme.mono_font_family.clone(),
        );
        let current = self.is_current(cx);
        let short = |sha: &str| sha.chars().take(10).collect::<String>();
        v_flex()
            .gap_2()
            .child(
                h_flex()
                    .flex_wrap()
                    .gap_1()
                    .text_sm()
                    .text_color(muted)
                    .child(
                        div()
                            .font_weight(FontWeight::SEMIBOLD)
                            .child(format!("{} #{}", self.repository, self.request.number)),
                    )
                    .child(format!("· {}", self.title)),
            )
            .child(
                h_flex()
                    .flex_wrap()
                    .gap_1()
                    .text_sm()
                    .text_color(muted)
                    .child(
                        div()
                            .text_xs()
                            .font_family(mono.clone())
                            .child(self.request.head_branch.clone()),
                    )
                    .child("into")
                    .child(
                        div()
                            .text_xs()
                            .font_family(mono)
                            .child(self.request.base_branch.clone()),
                    )
                    .child(format!(
                        "using {}.",
                        merge_method_label(&self.request.method)
                    )),
            )
            .child(div().text_sm().text_color(muted).child(format!(
                "Head {} · base {}",
                short(&self.request.head),
                short(&self.request.base)
            )))
            .children(self.has_draft.then(|| {
                div()
                    .text_sm()
                    .text_color(muted)
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
            .children((!current).then(|| {
                div().text_sm().child(
                    "Refresh before merging: the pull request changed or is no longer mergeable.",
                )
            }))
            .child(
                h_flex()
                    .mt_1()
                    .gap_2()
                    .justify_end()
                    .child(
                        Button::new("merge-close")
                            .ghost()
                            .small()
                            .label("Cancel")
                            .on_click(|_, window, cx| window.close_dialog(cx)),
                    )
                    .children((!current).then(|| {
                        Button::new("merge-refresh")
                            .ghost()
                            .small()
                            .label("Refresh this pull request")
                            .on_click(cx.listener(|view, _, window, cx| {
                                _ = view.detail.update(cx, |detail, cx| detail.refresh(cx));
                                window.close_dialog(cx);
                            }))
                    }))
                    .child(
                        Button::new("merge-confirm")
                            .primary()
                            .small()
                            .label("Merge on GitHub")
                            .disabled(!current || self.request.args().is_err())
                            .on_click(cx.listener(|view, _, window, cx| view.confirm(window, cx))),
                    ),
            )
    }
}
#[cfg(test)]
#[path = "merge_dialog_tests.rs"]
mod tests;
