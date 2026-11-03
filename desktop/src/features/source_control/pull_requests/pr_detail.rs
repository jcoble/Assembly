//! One pull request: its read, its review draft and the page bar over Summary and Changes.
use super::super::git_operation::GitOperation;
use super::{
    pr_activity::change_totals,
    pr_files::PrFiles,
    pull_request::PullRequestDetail,
    review_draft::{ReviewDraft, ReviewIdentity},
};
use crate::core::{
    intents::open_url::OpenUrl,
    socket::{client::SocketClient, outcome::Outcome},
};
use gpui_kit::assets::IconName;
use gpui_kit::component::button::{Button, ButtonGroup, ButtonRounded, ButtonVariants as _};
use gpui_kit::component::diff::DiffMode;
use gpui_kit::component::input::TextareaState;
use gpui_kit::component::{
    ActiveTheme as _, Disableable as _, Selectable as _, Sizable as _, h_flex, v_flex,
};
use gpui_kit::prelude::FluentBuilder as _;
use gpui_kit::*;
use serde_json::json;
use std::time::Duration;

#[path = "pr_view/composer.rs"]
mod composer;
#[path = "pr_view/summary.rs"]
mod summary;

#[derive(Clone, Copy, PartialEq)]
enum Page {
    Summary,
    Changes,
}
/// The person asked to go back to the list.
pub struct PrBack;
struct ReplyTarget {
    id: u64,
    author: String,
    path: String,
    line: Option<usize>,
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
    /// Text the next render puts in the comment box; setting an input needs a window.
    pending_body: Option<String>,
    /// Counts edits to the comment box, so a generated draft can't land on newer words.
    edits: u64,
    reply: Option<ReplyTarget>,
    reply_input: Option<Entity<TextareaState>>,
    drafter: Entity<GitOperation>,
    drafting_at: Option<(ReviewIdentity, u64)>,
    merge_method: &'static str,
    posted: Option<String>,
    merged: Option<String>,
    copied: Option<Task<()>>,
    page: Page,
    mode: DiffMode,
    width: Pixels,
    read: Option<Task<()>>,
    _review: Option<Subscription>,
    _drafter: Subscription,
}
impl EventEmitter<PrBack> for PrDetail {}
impl PrDetail {
    pub fn new(
        session: String,
        repository: Option<String>,
        number: u64,
        active: bool,
        cx: &mut Context<Self>,
    ) -> Self {
        let drafter = cx.new(|_| GitOperation::default());
        let drafter_heard = cx.observe(&drafter, |view: &mut Self, _, cx| view.draft_changed(cx));
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
            pending_body: None,
            edits: 0,
            reply: None,
            reply_input: None,
            drafter,
            drafting_at: None,
            merge_method: "merge",
            posted: None,
            merged: None,
            copied: None,
            page: Page::Summary,
            mode: DiffMode::Unified,
            width: px(0.),
            read: None,
            _review: None,
            _drafter: drafter_heard,
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
                files.set_active(active && self.page == Page::Changes, cx)
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
    fn sync_files(&mut self, cx: &mut Context<Self>) {
        if self.page != Page::Changes {
            return;
        }
        if self.files.is_none() {
            if let (Some(detail), Some(review)) = (&self.detail, &self.review) {
                let session = self.session.clone();
                let review = review.clone();
                let mode = self.mode;
                self.files = Some(cx.new(|cx| {
                    let mut files = PrFiles::new(session, detail, review, cx);
                    files.set_mode(mode, cx);
                    files
                }));
            }
        }
        if let Some(files) = &self.files {
            files.update(cx, |files, cx| files.set_active(self.active, cx));
        }
    }
    fn select_page(&mut self, page: Page, cx: &mut Context<Self>) {
        self.page = page;
        if let Some(files) = &self.files {
            files.update(cx, |files, cx| {
                files.set_active(self.active && page == Page::Changes, cx)
            });
        }
        self.sync_files(cx);
        cx.notify();
    }
    fn select_mode(&mut self, mode: DiffMode, cx: &mut Context<Self>) {
        self.mode = mode;
        if let Some(files) = &self.files {
            files.update(cx, |files, cx| files.set_mode(mode, cx));
        }
        cx.notify();
    }
    fn copy_link(&mut self, cx: &mut Context<Self>) {
        let Some(url) = self.detail.as_ref().map(|detail| detail.url.clone()) else {
            return;
        };
        cx.write_to_clipboard(ClipboardItem::new_string(url));
        self.copied = Some(cx.spawn(async move |view, cx| {
            cx.background_executor().timer(Duration::from_secs(2)).await;
            _ = view.update(cx, |view, cx| {
                view.copied = None;
                cx.notify();
            });
        }));
        cx.notify();
    }
    fn page_bar(&self, cx: &mut Context<Self>) -> impl IntoElement {
        let border = cx.theme().border;
        let (success, danger) = (cx.theme().success, cx.theme().danger);
        let (added, removed) = self
            .detail
            .as_ref()
            .map_or((0, 0), |detail| change_totals(&detail.files));
        let on_changes = self.page == Page::Changes;
        let url = self.detail.as_ref().map(|detail| detail.url.clone());
        let posting = self.drafter.read(cx).busy;
        h_flex()
            .px_3()
            .py_2()
            .gap_2()
            .border_b_1()
            .border_color(border)
            .child(
                Button::new("pr-back")
                    .ghost()
                    .small()
                    .icon(IconName::ChevronLeft)
                    .tooltip("All pull requests")
                    .on_click(cx.listener(|_, _, _, cx| cx.emit(PrBack))),
            )
            .child(
                ButtonGroup::new("pr-page")
                    .compact()
                    .outline()
                    .small()
                    .child(
                        Button::new("pr-page-summary")
                            .rounded(ButtonRounded::Size(px(999.)))
                            .label("Summary")
                            .selected(!on_changes),
                    )
                    .child(
                        Button::new("pr-page-changes")
                            .rounded(ButtonRounded::Size(px(999.)))
                            .selected(on_changes)
                            .child(
                                h_flex()
                                    .gap_1()
                                    .child("Changes")
                                    .child(div().text_color(success).child(format!("+{added}")))
                                    .child(div().text_color(danger).child(format!("−{removed}"))),
                            ),
                    )
                    .on_click(cx.listener(|view, picked: &Vec<usize>, _, cx| {
                        let page = if picked.first() == Some(&1) {
                            Page::Changes
                        } else {
                            Page::Summary
                        };
                        view.select_page(page, cx);
                    })),
            )
            .child(div().flex_1())
            .when(on_changes, |bar| {
                bar.child(
                    ButtonGroup::new("pr-mode")
                        .compact()
                        .outline()
                        .small()
                        .child(
                            Button::new("pr-mode-unified")
                                .label("Unified")
                                .selected(self.mode == DiffMode::Unified),
                        )
                        .child(
                            Button::new("pr-mode-split")
                                .label("Side by side")
                                .selected(self.mode == DiffMode::Split),
                        )
                        .on_click(cx.listener(|view, picked: &Vec<usize>, _, cx| {
                            let mode = if picked.first() == Some(&1) {
                                DiffMode::Split
                            } else {
                                DiffMode::Unified
                            };
                            view.select_mode(mode, cx);
                        })),
                )
            })
            .child(
                Button::new("pr-copy-link")
                    .ghost()
                    .small()
                    .icon(IconName::Link)
                    .tooltip(if self.copied.is_some() {
                        "Link copied"
                    } else {
                        "Copy link"
                    })
                    .disabled(url.is_none())
                    .on_click(cx.listener(|view, _, _, cx| view.copy_link(cx))),
            )
            .child(
                Button::new("pr-refresh")
                    .ghost()
                    .small()
                    .icon(IconName::RefreshCw)
                    .tooltip("Refresh this pull request")
                    .disabled(self.loading || posting)
                    .on_click(cx.listener(|view, _, _, cx| view.refresh(cx))),
            )
            .child(
                Button::new("pr-open-web")
                    .ghost()
                    .small()
                    .icon(IconName::ExternalLink)
                    .tooltip("Open on GitHub")
                    .disabled(url.is_none())
                    .on_click(move |_, _, cx| {
                        if let Some(url) = &url {
                            cx.dispatch_action(&OpenUrl { url: url.clone() });
                        }
                    }),
            )
    }
    fn changes_page(&self, detail: &PullRequestDetail, cx: &mut Context<Self>) -> AnyElement {
        let muted = cx.theme().muted_foreground;
        let lines = self
            .review
            .as_ref()
            .map_or(0, |review| review.read(cx).comments.len());
        v_flex()
            .flex_1()
            .min_h_0()
            .when(detail.more_files, |page| {
                page.child(
                    div()
                        .px_3()
                        .py_1()
                        .text_sm()
                        .text_color(muted)
                        .child("Showing the first 100 files. Open GitHub to see the rest."),
                )
            })
            .when(lines > 0, |page| {
                page.child(
                    h_flex()
                        .px_3()
                        .py_1()
                        .gap_1()
                        .text_sm()
                        .text_color(muted)
                        .child(if lines == 1 {
                            "1 line comment in your review.".to_string()
                        } else {
                            format!("{lines} line comments in your review.")
                        })
                        .child(
                            Button::new("pr-post-from-summary")
                                .link()
                                .small()
                                .label(if lines == 1 {
                                    "Post it from Summary."
                                } else {
                                    "Post them from Summary."
                                })
                                .on_click(cx.listener(|view, _, _, cx| {
                                    view.select_page(Page::Summary, cx)
                                })),
                        ),
                )
            })
            .children(self.files.clone())
            .into_any_element()
    }
}
impl Render for PrDetail {
    fn render(&mut self, window: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        self.sync_composer(window, cx);
        let danger = cx.theme().danger;
        let detail = self.detail.clone();
        let page = match (&detail, self.page) {
            (Some(detail), Page::Changes) => self.changes_page(detail, cx),
            (detail, _) => self.summary_page(detail.as_ref(), cx),
        };
        v_flex()
            .size_full()
            .min_h_0()
            .child(self.page_bar(cx))
            .children(self.failure.clone().map(|error| {
                div()
                    .px_3()
                    .py_1()
                    .text_sm()
                    .text_color(danger)
                    .child(error)
            }))
            .child(page)
    }
}
#[cfg(test)]
#[path = "pr_detail_tests.rs"]
mod tests;
