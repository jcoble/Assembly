//! One pull request: its read, its review draft and the page bar over Summary and Changes.
use super::super::git_operation::GitOperation;
use super::{
    merge_dialog::MergeRequest,
    post_confirm::PostRequest,
    pr_activity::change_totals,
    pr_files::PrFiles,
    pr_owner::PrOwner,
    pr_parts::loading_line,
    pr_writes::{PrWrites, WriteDone},
    pull_request::{PullRequestDetail, PullRequestSummary},
    review_draft::{ReviewDraft, ReviewIdentity},
};
use crate::core::theme::text::{TextRole, TextRoleExt as _};
use crate::core::ui::access::control_ids as ids;
use crate::core::ui::access::{Control as _, Named as _};
use crate::core::{
    intents::open_url::OpenInBrowser,
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
#[path = "pr_view/merge_card.rs"]
mod merge_card;
#[path = "pr_view/side_column.rs"]
mod side_column;
#[path = "pr_view/summary.rs"]
mod summary;
#[path = "pr_view/threads.rs"]
mod threads;

#[derive(Clone, Copy, PartialEq)]
enum Page {
    Summary,
    Changes,
}
/// The person asked to go back to the list.
pub struct PrBack;
/// This pull request was merged, so an open list no longer has it.
pub struct PrMerged {
    pub repository: String,
    pub number: u64,
}
struct ReplyTarget {
    id: u64,
    author: String,
    path: String,
    line: Option<usize>,
}
pub struct PrDetail {
    owner: PrOwner,
    repository: Option<String>,
    number: u64,
    active: bool,
    loading: bool,
    needs_refresh: bool,
    revision: u64,
    /// The list row this was opened from, standing in until the read arrives.
    row: Option<PullRequestSummary>,
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
    /// The head a generated draft was written against; a new head makes it wrong.
    drafted_head: Option<String>,
    writes: Entity<PrWrites>,
    merge_method: &'static str,
    posted: Option<String>,
    merged: Option<String>,
    copied: Option<Task<()>>,
    page: Page,
    mode: DiffMode,
    width: Pixels,
    main_scroll: ScrollHandle,
    side_scroll: ScrollHandle,
    threads: Entity<threads::PrThreads>,
    read: Option<Task<()>>,
    _review: Option<Subscription>,
    _drafter: Subscription,
    _writes: [Subscription; 2],
    _threads: Subscription,
}
impl EventEmitter<PrBack> for PrDetail {}
impl EventEmitter<PrMerged> for PrDetail {}
impl PrDetail {
    /// What the person already saw of this pull request before its read: its row, and which tab they were on.
    pub fn show_row(&mut self, row: Option<PullRequestSummary>, on_changes: bool) {
        self.row = row;
        if on_changes {
            self.page = Page::Changes;
        }
    }
    #[cfg(test)]
    pub fn on_changes(&self) -> bool {
        self.page == Page::Changes
    }
    #[cfg(test)]
    pub fn row(&self) -> Option<&PullRequestSummary> {
        self.row.as_ref()
    }
    pub fn owner(&self) -> &PrOwner {
        &self.owner
    }
    pub fn new(
        owner: PrOwner,
        repository: Option<String>,
        number: u64,
        active: bool,
        cx: &mut Context<Self>,
    ) -> Self {
        let drafter = cx.new(|_| GitOperation::default());
        let drafter_heard = cx.observe(&drafter, |view: &mut Self, _, cx| view.draft_changed(cx));
        let writes = cx.new(|cx| PrWrites::new(owner.clone(), cx));
        let writes_heard = [
            cx.observe(&writes, |_, _, cx| cx.notify()),
            cx.subscribe(&writes, |view: &mut Self, _, done: &WriteDone, cx| {
                view.write_done(done, cx)
            }),
        ];
        let threads = cx.new(|cx| threads::PrThreads::new(&owner, repository.clone(), number, cx));
        let threads_heard = cx.observe(&threads, |_, _, cx| cx.notify());
        let mut detail = Self {
            owner,
            repository,
            number,
            active,
            loading: false,
            needs_refresh: true,
            revision: 0,
            row: None,
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
            drafted_head: None,
            writes,
            merge_method: "merge",
            posted: None,
            merged: None,
            copied: None,
            page: Page::Summary,
            mode: DiffMode::Unified,
            width: px(0.),
            main_scroll: ScrollHandle::new(),
            side_scroll: ScrollHandle::new(),
            threads,
            read: None,
            _review: None,
            _drafter: drafter_heard,
            _writes: writes_heard,
            _threads: threads_heard,
        };
        if active {
            detail.refresh(cx);
            detail
                .threads
                .update(cx, |threads, cx| threads.set_active(true, cx));
        }
        detail
    }
    pub fn set_active(&mut self, active: bool, cx: &mut Context<Self>) {
        if self.active == active {
            return;
        }
        self.active = active;
        self.threads
            .update(cx, |threads, cx| threads.set_active(active, cx));
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
        // Only a read that began after the write ended can show what the write did.
        let writes_settled = !self.writes.read(cx).busy(cx);
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
            self.owner.read_args(json!({"number":self.number})),
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
                            view.threads.update(cx,|threads,cx|threads.set_repository(detail.repository.clone(),cx));
                            view.writes.update(cx,|writes,_|writes.show_read(detail.repository.clone(),detail.local_root.clone()));
                            if view.review.is_none(){
                                let review=cx.new(|_|ReviewDraft::new(ReviewIdentity{repository:detail.repository.clone(),number:detail.number,head:detail.head_sha.clone(),base:detail.base_sha.clone()}));
                                view._review=Some(cx.observe(&review,|_,_,cx|cx.notify()));view.review=Some(review);
                            }
                            if let Some(files)=&view.files{files.update(cx,|files,cx|files.replace_detail(&detail,cx));}
                            view.detail=Some(detail);view.page_read(writes_settled,cx);view.sync_files(cx);
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
        if self.files.is_none()
            && let (Some(detail), Some(review)) = (&self.detail, &self.review)
        {
            let owner = self.owner.clone();
            let review = review.clone();
            let mode = self.mode;
            self.files = Some(cx.new(|cx| {
                let mut files = PrFiles::new(owner, detail, review, cx);
                files.set_mode(mode, cx);
                files
            }));
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
        let (success, danger, well) = (cx.theme().success, cx.theme().danger, cx.theme().muted);
        let pill = || h_flex().items_center().rounded_full().bg(well);
        let totals = self
            .detail
            .as_ref()
            .map(|detail| change_totals(&detail.files));
        let on_changes = self.page == Page::Changes;
        let url = (self.detail.as_ref().map(|detail| detail.url.clone()))
            .or_else(|| self.row.as_ref().map(|row| row.url.clone()))
            .filter(|url| !url.is_empty());
        let posting = self.writes.read(cx).posting(cx);
        h_flex()
            .px_3()
            .py_2()
            .gap_2()
            .border_b_1()
            .border_color(border)
            .child(
                Button::new("pr-back")
                    .control(ids::pr::BACK, "All pull requests")
                    .ghost()
                    .small()
                    .icon(IconName::ChevronLeft)
                    .tooltip("All pull requests")
                    .on_click(cx.listener(|_, _, _, cx| cx.emit(PrBack))),
            )
            .child(
                pill()
                    .gap(px(2.))
                    .p(px(3.))
                    .child(
                        Button::new("pr-page-summary")
                            .ghost()
                            .small()
                            .rounded(ButtonRounded::Size(px(999.)))
                            .label("Summary")
                            .selected(!on_changes)
                            .on_click(
                                cx.listener(|view, _, _, cx| view.select_page(Page::Summary, cx)),
                            ),
                    )
                    .child(
                        Button::new("pr-page-changes")
                            .named("Changes")
                            .ghost()
                            .small()
                            .rounded(ButtonRounded::Size(px(999.)))
                            .selected(on_changes)
                            .on_click(
                                cx.listener(|view, _, _, cx| view.select_page(Page::Changes, cx)),
                            )
                            .child(h_flex().gap_1().child("Changes").children(totals.map(
                                |(added, removed)| {
                                    h_flex()
                                        .gap_1()
                                        .child(div().text_color(success).child(format!("+{added}")))
                                        .child(
                                            div().text_color(danger).child(format!("−{removed}")),
                                        )
                                },
                            ))),
                    ),
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
                pill()
                    .gap(px(2.))
                    .p(px(2.))
                    .child(
                        Button::new("pr-copy-link")
                            .named(if self.copied.is_some() {
                                "Link copied"
                            } else {
                                "Copy link"
                            })
                            .ghost()
                            .small()
                            .rounded(ButtonRounded::Size(px(999.)))
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
                            .control(ids::pr::REFRESH, "Refresh this pull request")
                            .ghost()
                            .small()
                            .rounded(ButtonRounded::Size(px(999.)))
                            .icon(IconName::RefreshCw)
                            .tooltip("Refresh this pull request")
                            .disabled(self.loading || posting)
                            .on_click(cx.listener(|view, _, _, cx| view.refresh(cx))),
                    )
                    .child(
                        Button::new("pr-open-web")
                            .control(ids::pr::OPEN_WEB, "Open on GitHub")
                            .ghost()
                            .small()
                            .rounded(ButtonRounded::Size(px(999.)))
                            .icon(IconName::ExternalLink)
                            .tooltip("Open on GitHub")
                            .disabled(url.is_none())
                            .on_click(move |_, window, cx| {
                                if let Some(url) = &url {
                                    window.dispatch_action(
                                        Box::new(OpenInBrowser { url: url.clone() }),
                                        cx,
                                    );
                                }
                            }),
                    ),
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
                        .px_4()
                        .py_2()
                        .text_role(TextRole::Body, cx)
                        .text_color(muted)
                        .child("Showing the first 100 files. Open GitHub to see the rest."),
                )
            })
            .when(lines > 0, |page| {
                page.child(
                    h_flex()
                        .px_4()
                        .py_2()
                        .gap_1()
                        .text_role(TextRole::Body, cx)
                        .text_color(muted)
                        .child(if lines == 1 {
                            "1 line comment in your review. Post it from".to_string()
                        } else {
                            format!("{lines} line comments in your review. Post them from")
                        })
                        .child(
                            h_flex()
                                .child(
                                    Button::new("pr-post-from-summary")
                                        .link()
                                        .small()
                                        .label("Summary")
                                        .on_click(cx.listener(|view, _, _, cx| {
                                            view.select_page(Page::Summary, cx)
                                        })),
                                )
                                .child("."),
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
        let post_error = self.writes.read(cx).post_error(cx);
        let page = match (&detail, self.page) {
            (Some(detail), Page::Changes) => self.changes_page(detail, cx),
            (None, Page::Changes) => v_flex()
                .flex_1()
                .min_h_0()
                .p_3()
                .when(self.loading, |page| page.child(loading_line(cx)))
                .into_any_element(),
            (detail, _) => self.summary_page(detail.as_ref(), window, cx),
        };
        v_flex()
            .size_full()
            .min_h_0()
            .child(self.page_bar(cx))
            .children(
                self.failure
                    .clone()
                    .into_iter()
                    .chain(post_error)
                    .map(|error| {
                        div()
                            .px_3()
                            .py_1()
                            .text_role(TextRole::Body, cx)
                            .text_color(danger)
                            .child(error)
                    }),
            )
            .child(page)
    }
}
#[cfg(test)]
#[path = "pr_detail_tests.rs"]
mod tests;
