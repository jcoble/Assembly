//! The Summary page's merge card: what stands in the way, the button and the method, and how the merge went.
use crate::core::theme::text::TextRole;
use super::super::{
    merge_dialog,
    merge_dialog::MergeRequest,
    pr_activity::{checks_fact, merge_method_label, mergeable_fact},
};
use super::summary::quiet;
use super::*;
use gpui_kit::component::menu::{DropdownMenu as _, PopupMenuItem};

impl PrDetail {
    pub(super) fn merge_card(
        &self,
        detail: &PullRequestDetail,
        cx: &mut Context<Self>,
    ) -> impl IntoElement {
        let theme = cx.theme();
        let (surface, success, muted, danger, radius, border) = (
            theme.secondary,
            theme.success,
            theme.muted_foreground,
            theme.danger,
            theme.radius_lg,
            theme.border,
        );
        let writes = self.writes.read(cx);
        let merging = writes.merging(cx);
        let merge_error = writes.merge_error(cx);
        let merge_uncertain = writes.merge_uncertain(cx);
        let writes_free = writes.free(cx);
        let fact = |settled: bool, text: String| {
            h_flex()
                .gap_3()
                .px_4()
                .py_3()
                .text_role(TextRole::Body, cx)
                .border_b_1()
                .border_color(border)
                .child(
                    div()
                        .flex_none()
                        .text_role(TextRole::Title, cx)
                        .text_color(if settled { success } else { muted })
                        .child(if settled { "✓" } else { "•" }),
                )
                .child(text)
        };
        let (checks_settled, checks) = checks_fact(&detail.checks);
        let (merge_settled, conflicts) = mergeable_fact(&detail.mergeable);
        let can_merge = MergeRequest::available(&detail.state, detail.is_draft, &detail.mergeable)
            && writes_free
            && self.current_identity().is_some();
        let method = self.merge_method;
        let target = cx.entity().downgrade();
        v_flex()
            .text_role(TextRole::Body, cx)
            .rounded(radius)
            .bg(surface)
            .overflow_hidden()
            .child(fact(checks_settled, checks))
            .child(fact(merge_settled, conflicts.to_string()))
            .child(
                h_flex()
                    .flex_wrap()
                    .gap_2()
                    .px_4()
                    .py_3()
                    .child(
                        Button::new("pr-merge")
                            .small()
                            .label(if merging {
                                "Merging…"
                            } else {
                                "Merge pull request"
                            })
                            .loading(merging)
                            .disabled(!can_merge)
                            .on_click(
                                cx.listener(|view, _, window, cx| view.preview_merge(window, cx)),
                            ),
                    )
                    .child("using")
                    .child(
                        Button::new("pr-merge-method")
                            .outline()
                            .small()
                            .dropdown_caret(true)
                            .label(merge_method_label(method))
                            .disabled(merging)
                            .dropdown_menu(move |menu, _, _| {
                                ["merge", "squash", "rebase"].into_iter().fold(
                                    menu,
                                    |menu, choice| {
                                        let target = target.clone();
                                        menu.item(
                                            PopupMenuItem::new(merge_method_label(choice))
                                                .checked(choice == method)
                                                .on_click(move |_, _, cx| {
                                                    _ = target.update(cx, |view, cx| {
                                                        view.merge_method = choice;
                                                        cx.notify();
                                                    });
                                                }),
                                        )
                                    },
                                )
                            }),
                    )
                    .when(merging, |row| {
                        row.child(
                            Button::new("pr-merge-cancel")
                                .ghost()
                                .small()
                                .label("Cancel")
                                .on_click(cx.listener(|view, _, _, cx| {
                                    view.writes.update(cx, |writes, cx| writes.cancel(cx))
                                })),
                        )
                    }),
            )
            .when(detail.is_draft, |card| {
                card.child(
                    quiet(
                        "Mark this draft ready for review on GitHub before merging.",
                        cx,
                    )
                    .px_4()
                    .pb_3(),
                )
            })
            .when(merge_uncertain, |card| {
                card.child(
                    quiet(
                        "Refresh this PR to check whether the merge completed before trying again.",
                        cx,
                    )
                    .px_4()
                    .pb_3(),
                )
            })
            .children(merge_error.map(|error| div().px_4().pb_3().text_role(TextRole::Body, cx).text_color(danger).child(error)))
    }
    fn preview_merge(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        let Some(detail) = self.detail.clone() else {
            return;
        };
        let has_draft = self.review.as_ref().is_some_and(|review| {
            let review = review.read(cx);
            !review.body.trim().is_empty() || !review.comments.is_empty()
        });
        merge_dialog::open(
            detail,
            self.merge_method,
            has_draft,
            cx.entity().downgrade(),
            window,
            cx,
        );
    }
}
