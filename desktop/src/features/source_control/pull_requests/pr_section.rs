//! The panel's Open pull requests section: it reads only once opened, and each row opens the pull request. Replaces
//! PullRequestList.svelte.
use super::super::git_action::GitWrites;
use super::super::git_target::GitTarget;
use super::super::section_heading::section_heading;
use super::pull_request::{CheckTone, PullRequestSummary};
use super::{create_pr_dialog, pr_queue::PrQueue};
use crate::core::intents::{open_pull_requests::OpenPullRequests, open_url::OpenUrl};
use gpui_kit::assets::IconName;
use gpui_kit::component::button::{Button, ButtonVariants as _};
use gpui_kit::component::list::ListItem;
use gpui_kit::component::{ActiveTheme as _, Disableable as _, Icon, h_flex, v_flex};
use gpui_kit::prelude::FluentBuilder as _;
use gpui_kit::*;

/// The line under the heading once the section has answered.
pub fn summary_line(count: usize) -> String {
    match count {
        0 => "No open pull requests.".into(),
        1 => "1 open pull request".into(),
        count => format!("{count} open pull requests"),
    }
}
pub struct PrSection {
    target: GitTarget,
    branch: String,
    writes: GitWrites,
    expanded: bool,
    active: bool,
    queue: Entity<PrQueue>,
    _heard: Vec<Subscription>,
}
impl PrSection {
    pub fn new(target: GitTarget, writes: GitWrites, active: bool, cx: &mut Context<Self>) -> Self {
        let queue = cx.new(|_| PrQueue::new(Some(target.session.clone()), vec![]));
        let source = target.source.clone();
        queue.update(cx, |queue, cx| queue.set_source(source, cx));
        let heard = vec![
            cx.observe(&queue, |_, _, cx| cx.notify()),
            cx.observe(&writes.operation, |_, _, cx| cx.notify()),
            cx.observe(&writes.action, |_, _, cx| cx.notify()),
        ];
        Self {
            target,
            branch: String::new(),
            writes,
            expanded: false,
            active,
            queue,
            _heard: heard,
        }
    }
    pub fn set_active(&mut self, active: bool, cx: &mut Context<Self>) {
        self.active = active;
        let wanted = active && self.expanded;
        self.queue
            .update(cx, |queue, cx| queue.set_active(wanted, cx));
    }
    pub fn set_target(
        &mut self,
        target: GitTarget,
        branch: Option<String>,
        cx: &mut Context<Self>,
    ) {
        let source = target.source.clone();
        self.target = target;
        self.branch = branch.unwrap_or_default();
        self.queue
            .update(cx, |queue, cx| queue.set_source(source, cx));
    }
    fn toggle(&mut self, cx: &mut Context<Self>) {
        self.expanded = !self.expanded;
        self.set_active(self.active, cx);
        cx.notify();
    }
    fn row(&self, pr: PullRequestSummary, cx: &App) -> AnyElement {
        let theme = cx.theme();
        let muted = theme.muted_foreground;
        let tone = |tone: CheckTone| match tone {
            CheckTone::Passing => theme.success,
            CheckTone::Failing => theme.danger,
            CheckTone::Pending => theme.warning,
            CheckTone::Quiet => muted,
        };
        let check = pr.check_line().map(|(text, check)| {
            div()
                .flex_none()
                .text_sm()
                .text_color(tone(check))
                .child(text)
        });
        let second = if pr.is_draft {
            format!("Draft · {}", pr.head_branch)
        } else {
            pr.head_branch.clone()
        };
        let id = format!("{}:{}", pr.repository, pr.number);
        let session = self.target.session.clone();
        let url = pr.url.clone();
        let (repository, number, title) = (pr.repository.clone(), pr.number, pr.title.clone());
        ListItem::new(SharedString::from(format!("session-pr:{id}")))
            .px_2()
            .on_click(move |_, window, cx| {
                window.dispatch_action(
                    Box::new(OpenPullRequests {
                        session_id: Some(session.clone()),
                        repo: Some(repository.clone()),
                        number: Some(number),
                    }),
                    cx,
                )
            })
            .child(
                h_flex()
                    .w_full()
                    .gap_2()
                    .child(
                        div()
                            .flex_none()
                            .text_sm()
                            .text_color(muted)
                            .child(format!("#{number}")),
                    )
                    .child(
                        v_flex()
                            .flex_1()
                            .min_w_0()
                            .child(div().truncate().text_sm().child(title))
                            .child(div().truncate().text_sm().text_color(muted).child(second)),
                    )
                    .children(check)
                    .child(
                        Button::new(SharedString::from(format!("session-pr-open:{id}")))
                            .ghost()
                            .compact()
                            .icon(Icon::new(IconName::ExternalLink).size(px(14.)))
                            .tooltip("Open on GitHub")
                            .on_click(move |_, window, cx| {
                                cx.stop_propagation();
                                window.dispatch_action(Box::new(OpenUrl { url: url.clone() }), cx)
                            }),
                    ),
            )
            .into_any_element()
    }
}
impl Render for PrSection {
    fn render(&mut self, _: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        let (danger, muted) = (cx.theme().danger, cx.theme().muted_foreground);
        let queue = self.queue.read(cx);
        let loading = queue.loading;
        let items = queue.page.items.clone();
        let total = queue.page.total.max(items.len());
        let failure = queue.failure.clone();
        let expanded = self.expanded;
        let busy = self.writes.busy(cx);
        let actions = if expanded {
            vec![
                Button::new("pr-section-refresh")
                    .ghost()
                    .compact()
                    .icon(Icon::new(IconName::RefreshCw).size(px(14.)))
                    .tooltip("Refresh")
                    .disabled(loading)
                    .on_click(cx.listener(|section, _, _, cx| {
                        section.queue.update(cx, |queue, cx| queue.refresh(cx))
                    }))
                    .into_any_element(),
            ]
        } else {
            Vec::new()
        };
        let toggle = cx.listener(|section: &mut Self, _: &ClickEvent, _, cx| section.toggle(cx));
        let rows = expanded.then(|| {
            items
                .into_iter()
                .map(|pr| self.row(pr, cx))
                .collect::<Vec<_>>()
        });
        v_flex()
            .gap_1()
            .child(section_heading(
                "pr-section-toggle",
                "Open pull requests",
                None,
                expanded,
                toggle,
                actions,
                cx,
            ))
            .when(expanded, |section| {
                section
                    .when(loading, |section| {
                        section.child(
                            div()
                                .px_2()
                                .text_sm()
                                .text_color(muted)
                                .child("Reading open pull requests…"),
                        )
                    })
                    .when_some(failure.clone(), |section, failure| {
                        section.child(
                            v_flex()
                                .px_2()
                                .gap_1()
                                .items_start()
                                .child(div().text_sm().text_color(danger).child(failure))
                                .child(
                                    Button::new("pr-section-retry")
                                        .ghost()
                                        .compact()
                                        .label("Try again")
                                        .on_click(cx.listener(|section, _, _, cx| {
                                            section.queue.update(cx, |queue, cx| queue.refresh(cx))
                                        })),
                                ),
                        )
                    })
                    .when(!loading && failure.is_none(), |section| {
                        section.child(
                            div()
                                .px_2()
                                .text_sm()
                                .text_color(muted)
                                .child(summary_line(total)),
                        )
                    })
            })
            .children(rows.into_iter().flatten())
            .when(expanded, |section| {
                section.child(
                    div().px_2().child(
                        Button::new("pr-section-create")
                            .ghost()
                            .compact()
                            .label("Create pull request…")
                            .disabled(busy)
                            .on_click(cx.listener(|section, _, window, cx| {
                                create_pr_dialog::open(
                                    section.target.clone(),
                                    section.writes.clone(),
                                    section.branch.clone(),
                                    window,
                                    cx,
                                )
                            })),
                    ),
                )
            })
    }
}
