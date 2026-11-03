use crate::core::theme::text::{TextRole, TextRoleExt as _};
use super::{create_pr_dialog, pr_queue::PrQueue};
use crate::core::intents::open_pull_requests::OpenPullRequests;
use gpui_kit::component::button::{Button, ButtonVariants as _};
use gpui_kit::component::{ActiveTheme as _, Disableable as _, h_flex, v_flex};
use gpui_kit::prelude::FluentBuilder as _;
use gpui_kit::*;

pub struct PrSection {
    session: String,
    expanded: bool,
    active: bool,
    queue: Entity<PrQueue>,
    _heard: Subscription,
}
impl PrSection {
    pub fn new(session: String, active: bool, cx: &mut Context<Self>) -> Self {
        let queue = cx.new(|_| PrQueue::new(Some(session.clone()), vec![]));
        let heard = cx.observe(&queue, |_, _, cx| cx.notify());
        Self {
            session,
            expanded: false,
            active,
            queue,
            _heard: heard,
        }
    }
    pub fn set_active(&mut self, active: bool, cx: &mut Context<Self>) {
        self.active = active;
        self.queue.update(cx, |queue, cx| {
            queue.set_active(active && self.expanded, cx)
        });
    }
    fn toggle(&mut self, cx: &mut Context<Self>) {
        self.expanded = !self.expanded;
        let active = self.active && self.expanded;
        self.queue
            .update(cx, |queue, cx| queue.set_active(active, cx));
        cx.notify();
    }
}
impl Render for PrSection {
    fn render(&mut self, _: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        let danger = cx.theme().danger;
        let queue = self.queue.read(cx);
        let loading = queue.loading;
        let items = queue.page.items.clone();
        let failure = queue.failure.clone();
        let expanded = self.expanded;
        let session = self.session.clone();
        let create_session = session.clone();
        v_flex()
            .gap_1()
            .child(
                h_flex()
                    .gap_2()
                    .px_2()
                    .child(
                        Button::new("pr-section-toggle")
                            .ghost()
                            .compact()
                            .label(if expanded {
                                "▾ Pull requests"
                            } else {
                                "▸ Pull requests"
                            })
                            .on_click(cx.listener(|section, _, _, cx| section.toggle(cx))),
                    )
                    .child(
                        Button::new("pr-section-create")
                            .ghost()
                            .compact()
                            .label("Create pull request…")
                            .on_click(move |_, window, cx| {
                                create_pr_dialog::open(create_session.clone(), window, cx)
                            }),
                    ),
            )
            .when(expanded, move |section| {
                section
                    .child(
                        Button::new("pr-section-refresh")
                            .ghost()
                            .compact()
                            .label("Refresh")
                            .disabled(loading)
                            .on_click(cx.listener(|section, _, _, cx| {
                                section.queue.update(cx, |queue, cx| queue.refresh(cx))
                            })),
                    )
                    .children(
                        failure.clone().map(|message| {
                            div().px_2().text_role(TextRole::Body, cx).text_color(danger).child(message)
                        }),
                    )
                    .when(loading, |section| {
                        section.child(div().px_2().text_role(TextRole::Body, cx).child("Reading pull requests…"))
                    })
                    .when(
                        !loading && failure.is_none() && items.is_empty(),
                        |section| {
                            section.child(div().px_2().text_role(TextRole::Body, cx).child("No open pull requests."))
                        },
                    )
                    .children(items.into_iter().map(move |pr| {
                        let session = session.clone();
                        Button::new(SharedString::from(format!(
                            "session-pr:{}:{}",
                            pr.repository, pr.number
                        )))
                        .ghost()
                        .label(format!(
                            "#{} {}{}",
                            pr.number,
                            pr.title,
                            if pr.is_draft { " · Draft" } else { "" }
                        ))
                        .on_click(move |_, _, cx| {
                            cx.dispatch_action(&OpenPullRequests {
                                session_id: Some(session.clone()),
                                repo: Some(pr.repository.clone()),
                                number: Some(pr.number),
                            })
                        })
                    }))
            })
    }
}
