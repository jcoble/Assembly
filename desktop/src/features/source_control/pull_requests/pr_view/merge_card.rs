//! The Summary page's merge card: each thing that stands in the way with the move that clears it, the button and the
//! method, and how the merge went. Conflicts open in place to the regions Git can't merge.
use super::super::{
    merge_dialog, merge_dialog::MergeRequest, pr_activity::merge_method_label, pr_writes::PrAct,
};
use super::lookups::{Looked, PrLookups};
use super::readiness::{Fact, Move, Standing, readiness, resolve_prompt};
use super::summary::quiet;
use super::*;
use crate::core::theme::text::TextRole;
use crate::core::ui::row_drop_down::GrowingBody;
use crate::core::ui::spinner::Spinner;
use crate::core::ui::three_line_row::row_height;
use gpui_kit::component::Icon;
use gpui_kit::component::menu::{DropdownMenu as _, PopupMenuItem};

const CONFLICTS: &str = "conflicts";

fn conflicts_body(
    lookups: &Entity<PrLookups>,
    target: &WeakEntity<PrDetail>,
    shown: &PullRequestDetail,
    cx: &App,
) -> AnyElement {
    let theme = cx.theme();
    let (danger, success, accent, muted, mono, border) = (
        theme.danger,
        theme.success,
        theme.info,
        theme.muted_foreground,
        theme.mono_font_family.clone(),
        theme.border,
    );
    let column = v_flex().gap_3().px_4().pb_3();
    match lookups.read(cx).conflicts(cx) {
        None | Some(Looked::Reading) => column
            .child(
                h_flex()
                    .gap_2()
                    .text_role(TextRole::Body, cx)
                    .text_color(muted)
                    .child(Spinner::new().small())
                    .child("Merging the branches to find the conflicts…"),
            )
            .into_any_element(),
        Some(Looked::Failed(error)) => column
            .child(
                div()
                    .text_role(TextRole::Body, cx)
                    .text_color(danger)
                    .child(error),
            )
            .into_any_element(),
        Some(Looked::Found(conflicts)) if conflicts.files.is_empty() => column
            .child(quiet(
                "These commits merge cleanly now. Refresh to see what GitHub says.",
                cx,
            ))
            .into_any_element(),
        Some(Looked::Found(conflicts)) => {
            let prompt = resolve_prompt(shown, &conflicts);
            let target = target.clone();
            let url = format!("{}/conflicts", shown.url);
            let count = conflicts.files.len();
            column
                .child(quiet(
                    format!(
                        "{count} {} can't merge on their own:",
                        if count == 1 { "file" } else { "files" }
                    ),
                    cx,
                ))
                .children(conflicts.files.iter().map(|file| {
                    v_flex()
                        .gap_1()
                        .child(
                            div()
                                .text_role(TextRole::Body, cx)
                                .font_family(mono.clone())
                                .font_weight(FontWeight::MEDIUM)
                                .child(file.path.clone()),
                        )
                        .children(file.regions.iter().map(|region| {
                            v_flex()
                                .p_2()
                                .rounded(px(6.))
                                .border_1()
                                .border_color(border)
                                .font_family(mono.clone())
                                .text_role(TextRole::Meta, cx)
                                .children(region.lines().map(|line| {
                                    let color = if line.starts_with("<<<<<<<") {
                                        Some(success)
                                    } else if line.starts_with(">>>>>>>") {
                                        Some(accent)
                                    } else if line.starts_with("=======") {
                                        Some(muted)
                                    } else {
                                        None
                                    };
                                    div()
                                        .whitespace_nowrap()
                                        .when_some(color, |line, color| line.text_color(color))
                                        .child(if line.is_empty() {
                                            " ".to_string()
                                        } else {
                                            line.to_string()
                                        })
                                }))
                        }))
                        .when(file.cut || file.regions.is_empty(), |file| {
                            file.child(quiet("More of this file conflicts than is shown here.", cx))
                        })
                }))
                .child(
                    h_flex()
                        .gap_2()
                        .child(
                            Button::new("pr-conflicts-ask")
                                .small()
                                .icon(IconName::Bot)
                                .label("Ask an agent to resolve them")
                                .on_click(move |_, window, cx| {
                                    _ = target.update(cx, |view, cx| {
                                        view.ask_agent(prompt.clone(), window, cx)
                                    });
                                }),
                        )
                        .child(
                            Button::new("pr-conflicts-web")
                                .ghost()
                                .small()
                                .icon(IconName::ExternalLink)
                                .label("Resolve on GitHub")
                                .on_click(move |_, window, cx| {
                                    window.dispatch_action(
                                        Box::new(OpenInBrowser { url: url.clone() }),
                                        cx,
                                    )
                                }),
                        ),
                )
                .into_any_element()
        }
    }
}

impl PrDetail {
    pub(super) fn merge_card(
        &self,
        detail: &PullRequestDetail,
        cx: &mut Context<Self>,
    ) -> impl IntoElement {
        let theme = cx.theme();
        let (surface, danger, radius, border) =
            (theme.secondary, theme.danger, theme.radius_lg, theme.border);
        let writes = self.writes.read(cx);
        let merging = writes.merging(cx);
        let merge_error = writes.merge_error(cx);
        let merge_uncertain = writes.merge_uncertain(cx);
        let writes_free = writes.free(cx);
        let acting = writes.acting(cx);
        let act_free = writes_free && self.current_identity().is_some();
        let facts: Vec<_> = readiness(detail)
            .into_iter()
            .map(|fact| self.fact_row(fact, border, cx))
            .collect();
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
            .children(facts)
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
                    })
                    .when(!detail.is_draft, |row| {
                        let converting = acting == Some(PrAct::BackToDraft);
                        row.child(div().flex_1()).child(
                            Button::new("pr-to-draft")
                                .ghost()
                                .small()
                                .label(if converting {
                                    PrAct::BackToDraft.doing()
                                } else {
                                    "Convert to draft"
                                })
                                .loading(converting)
                                .disabled(!act_free)
                                .on_click(cx.listener(|view, _, _, cx| {
                                    view.act(PrAct::BackToDraft, json!({"ready":false}), cx)
                                })),
                        )
                    }),
            )
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
            .children(merge_error.map(|error| {
                div()
                    .px_4()
                    .pb_3()
                    .text_role(TextRole::Body, cx)
                    .text_color(danger)
                    .child(error)
            }))
    }
    fn fact_row(&self, fact: Fact, border: Hsla, cx: &mut Context<Self>) -> AnyElement {
        let theme = cx.theme();
        let (success, warning, danger, muted) = (
            theme.success,
            theme.warning,
            theme.danger,
            theme.muted_foreground,
        );
        let mark = match fact.standing {
            Standing::Good => Icon::new(IconName::CircleCheck)
                .size(px(16.))
                .text_color(success)
                .into_any_element(),
            Standing::Bad => Icon::new(IconName::CircleX)
                .size(px(16.))
                .text_color(danger)
                .into_any_element(),
            Standing::Waiting => Icon::new(IconName::CircleAlert)
                .size(px(16.))
                .text_color(warning)
                .into_any_element(),
            Standing::Quiet => div()
                .size(px(16.))
                .flex()
                .items_center()
                .justify_center()
                .child(div().size(px(6.)).rounded_full().bg(muted))
                .into_any_element(),
        };
        let writes = self.writes.read(cx);
        let acting = writes.acting(cx);
        let free = writes.free(cx) && self.current_identity().is_some();
        let conflicts_open = self.drops.read(cx).is_open(&CONFLICTS.into());
        let button = fact.action.map(|action| {
            let (label, act) = match action {
                Move::Ready => ("Ready for review", Some(PrAct::Ready)),
                Move::UpdateBranch => ("Update branch", Some(PrAct::UpdateBranch)),
                Move::SeeChecks => ("See checks", None),
                Move::ShowConflicts => (
                    if conflicts_open {
                        "Hide conflicts"
                    } else {
                        "Show conflicts"
                    },
                    None,
                ),
            };
            let running = act.is_some() && act == acting;
            Button::new(SharedString::from(format!("pr-fact:{action:?}")))
                .outline()
                .small()
                .label(if running {
                    act.map_or(label, PrAct::doing)
                } else {
                    label
                })
                .loading(running)
                .disabled(act.is_some() && !free)
                .on_click(cx.listener(move |view, _, _, cx| match action {
                    Move::Ready => view.act(PrAct::Ready, json!({"ready":true}), cx),
                    Move::UpdateBranch => view.act(PrAct::UpdateBranch, json!({}), cx),
                    Move::SeeChecks => view.select_page(Page::Checks, cx),
                    Move::ShowConflicts => view.toggle_conflicts(cx),
                }))
        });
        let shows_conflicts = fact.action == Some(Move::ShowConflicts);
        v_flex()
            .border_b_1()
            .border_color(border)
            .child(
                h_flex()
                    .gap_3()
                    .px_4()
                    .py_3()
                    .child(div().flex_none().child(mark))
                    .child(
                        v_flex()
                            .flex_1()
                            .min_w_0()
                            .child(div().font_weight(FontWeight::MEDIUM).child(fact.title))
                            .children(
                                fact.detail
                                    .map(|detail| div().text_color(muted).child(detail)),
                            ),
                    )
                    .children(button),
            )
            .when(shows_conflicts, |row| {
                row.child(
                    GrowingBody::new(CONFLICTS.into(), self.drops.clone(), row_height(cx))
                        .max_rows(None),
                )
            })
            .into_any_element()
    }
    fn toggle_conflicts(&mut self, cx: &mut Context<Self>) {
        let Some(detail) = self.detail.clone() else {
            return;
        };
        let number = self.number;
        self.lookups
            .update(cx, |lookups, cx| lookups.read_conflicts(number, cx));
        let (lookups, target) = (self.lookups.clone(), cx.entity().downgrade());
        self.drops.update(cx, |drops, cx| {
            drops.toggle(
                CONFLICTS.into(),
                move |_, cx| conflicts_body(&lookups, &target, &detail, cx),
                cx,
            )
        });
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
