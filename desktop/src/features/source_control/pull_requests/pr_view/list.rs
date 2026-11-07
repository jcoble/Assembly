//! The Pull requests list: a heading, the filters, a note when something's wrong, and one row per pull request.
use super::super::{
    pr_activity::{pr_state, repo_name},
    pr_parts::{age_label, avatar, state_icon, tone_color},
    pr_search::{PrScope, PrState},
    pull_request::{CheckTone, PullRequestSummary},
};
use super::*;
use crate::activity::{StatusChip, Tone};
use crate::core::intents::open_url::OpenInBrowser;
use crate::core::theme::text::{TextRole, TextRoleExt as _};
use crate::core::ui::access::Control as _;
use crate::core::ui::access::control_ids as ids;
use gpui_kit::assets::IconName;
use gpui_kit::component::button::{Button, ButtonRounded, ButtonVariants as _};
use gpui_kit::component::input::Input;
use gpui_kit::component::list::ListItem;
use gpui_kit::component::menu::{DropdownMenu as _, PopupMenuItem};
use gpui_kit::component::spinner::Spinner;
use gpui_kit::component::tooltip::Tooltip;
use gpui_kit::component::{
    ActiveTheme as _, Disableable as _, Icon, Selectable as _, Sizable as _, h_flex, v_flex,
};
use gpui_kit::prelude::FluentBuilder as _;

impl PrPane {
    fn project_label(&self, cx: &App) -> String {
        let projects = self.projects.as_ref().map(|projects| projects.read(cx));
        let chosen = self
            .project
            .as_ref()
            .and_then(|id| projects?.rows().iter().find(|project| project.id == *id));
        if let Some(project) = chosen {
            return project_name(&project.title, &project.machine);
        }
        if self.session.is_none() {
            return "All local projects".into();
        }
        // A session's project shows once its pull requests say which repository it is.
        let first = self.queue.read(cx).page.items.first();
        let found = first.and_then(|pr| {
            projects?.rows().iter().find(|project| {
                project
                    .github_repository()
                    .is_some_and(|repo| repo.eq_ignore_ascii_case(&pr.repository))
            })
        });
        found.map_or("This session's project".into(), |project| {
            project_name(&project.title, &project.machine)
        })
    }
    fn project_picker(&self, cx: &mut Context<Self>) -> impl IntoElement {
        let label = self.project_label(cx);
        let projects = self
            .projects
            .as_ref()
            .map(|projects| projects.read(cx).rows().to_vec())
            .unwrap_or_default();
        let target = cx.entity().downgrade();
        let chosen = self.project.clone();
        let everywhere = self.session.is_none() && chosen.is_none();
        Button::new("pr-project")
            .outline()
            .small()
            .dropdown_caret(true)
            .label(label)
            .dropdown_menu(move |menu, _, _| {
                let all = target.clone();
                let menu = menu.item(
                    PopupMenuItem::new("All local projects")
                        .checked(everywhere)
                        .on_click(move |_, _, cx| {
                            _ = all.update(cx, |pane, cx| pane.select_project(None, cx));
                        }),
                );
                projects
                    .iter()
                    .filter(|project| project.github_repository().is_some())
                    .fold(menu, |menu, project| {
                        let id = project.id.clone();
                        let target = target.clone();
                        menu.item(
                            PopupMenuItem::new(project_name(&project.title, &project.machine))
                                .checked(chosen.as_ref() == Some(&project.id))
                                .on_click(move |_, _, cx| {
                                    _ = target.update(cx, |pane, cx| {
                                        pane.select_project(Some(id.clone()), cx)
                                    });
                                }),
                        )
                    })
            })
    }
    fn filters(&self, cx: &mut Context<Self>) -> impl IntoElement {
        let queue = self.queue.read(cx);
        let (state, scope) = (queue.state, queue.scope);
        let target = cx.entity().downgrade();
        h_flex()
            .gap_2()
            .flex_wrap()
            .child(
                div()
                    .flex_1()
                    .min_w(px(260.))
                    .max_w(px(420.))
                    .child(Input::new(&self.search).small()),
            )
            .child(
                h_flex()
                    .gap(px(2.))
                    .p(px(3.))
                    .rounded_full()
                    .bg(cx.theme().muted)
                    .children(PrState::ALL.into_iter().map(|option| {
                        Button::new(option.label())
                            .ghost()
                            .small()
                            .rounded(ButtonRounded::Size(px(999.)))
                            .label(option.label())
                            .selected(option == state)
                            .on_click(cx.listener(move |pane, _, _, cx| {
                                pane.queue
                                    .update(cx, |queue, cx| queue.select_state(option, cx))
                            }))
                    })),
            )
            .child(
                Button::new("pr-scope")
                    .outline()
                    .small()
                    .dropdown_caret(true)
                    .label(scope.label())
                    .dropdown_menu(move |menu, _, _| {
                        PrScope::ALL.into_iter().fold(menu, |menu, option| {
                            let target = target.clone();
                            menu.item(
                                PopupMenuItem::new(option.label())
                                    .checked(option == scope)
                                    .on_click(move |_, _, cx| {
                                        _ = target.update(cx, |pane, cx| {
                                            pane.queue.update(cx, |queue, cx| {
                                                queue.select_scope(option, cx)
                                            })
                                        });
                                    }),
                            )
                        })
                    }),
            )
            .child(self.project_picker(cx))
    }
    fn row(&self, pr: PullRequestSummary, cx: &mut Context<Self>) -> impl IntoElement {
        let theme = cx.theme();
        let (border, muted) = (theme.border, theme.muted_foreground);
        let color = tone_color(pr_state(&pr.state, pr.is_draft).1, cx);
        let key = format!("{}:{}", pr.repository, pr.number);
        let opened = pr.clone();
        ListItem::new(SharedString::from(format!("pr-row:{key}")))
            .w_full()
            .px_3()
            .py_2()
            .border_b_1()
            .border_color(border)
            .on_click(cx.listener(move |pane, _, _, cx| pane.select_row(opened.clone(), cx)))
            .child(
                h_flex()
                    .w_full()
                    .gap_3()
                    .child(
                        Icon::new(state_icon(&pr.state, pr.is_draft))
                            .size(px(15.))
                            .text_color(color),
                    )
                    .child(
                        v_flex()
                            .flex_1()
                            .min_w_0()
                            .child(div().truncate().child(pr.title.clone()))
                            .child(
                                h_flex()
                                    .gap_1()
                                    .min_w_0()
                                    .text_role(TextRole::Body, cx)
                                    .text_color(muted)
                                    .child(format!(
                                        "{} #{} ·",
                                        repo_name(&pr.repository),
                                        pr.number
                                    ))
                                    .child(div().truncate().child(pr.head_branch.clone()))
                                    .child(Icon::new(IconName::ArrowRight).size(px(11.)))
                                    .child(div().truncate().child(pr.base_branch.clone())),
                            ),
                    )
                    .child(
                        h_flex()
                            .flex_none()
                            .gap_2()
                            .text_role(TextRole::Body, cx)
                            .text_color(muted)
                            .children(row_marks(&pr, cx))
                            .children(avatar(&pr.author))
                            .child(div().w(px(60.)).flex().justify_end().children(age_label(
                                format!("pr-row-age:{key}").into(),
                                &pr.updated_at,
                                " ago",
                            ))),
                    ),
            )
    }
    fn more_row(&self, cx: &mut Context<Self>) -> impl IntoElement {
        let queue = self.queue.read(cx);
        let (loading, shown, total) = (queue.loading, queue.page.items.len(), queue.page.total);
        div().px_3().py_2().child(
            Button::new("pr-more")
                .ghost()
                .small()
                .label(if loading {
                    "Loading…".to_string()
                } else {
                    format!("Load more · {shown} of {total}")
                })
                .loading(loading)
                .disabled(loading)
                .on_click(cx.listener(|pane, _, _, cx| {
                    pane.queue.update(cx, |queue, cx| queue.load_more(cx))
                })),
        )
    }
    pub(super) fn list_page(&self, cx: &mut Context<Self>) -> impl IntoElement {
        let theme = cx.theme();
        let (danger, muted) = (theme.danger, theme.muted_foreground);
        let queue = self.queue.read(cx);
        let count = queue.page.items.len();
        let more = queue.page.cursor.is_some();
        let loading = queue.loading;
        let error = self.failure.clone().or(queue.failure.clone());
        let projects_loading = self
            .projects
            .as_ref()
            .is_some_and(|projects| projects.read(cx).loading);
        let projects_error = self
            .projects
            .as_ref()
            .and_then(|projects| projects.read(cx).failure.clone());
        let more_projects = self
            .projects
            .as_ref()
            .filter(|projects| projects.read(cx).next.is_some())
            .cloned();
        let error = error.or(projects_error);
        let empty = count == 0 && !loading && !projects_loading && error.is_none();
        let missing = self.missing_link.clone();
        v_flex()
            .size_full()
            .min_h_0()
            .gap_3()
            .pt_5()
            .px_6()
            .child(
                h_flex()
                    .gap_2()
                    .justify_between()
                    .child(
                        div()
                            .text_role(TextRole::Heading, cx)
                            .font_weight(FontWeight::SEMIBOLD)
                            .child("Pull requests"),
                    )
                    .child(
                        Button::new("pr-queue-refresh")
                            .control(ids::pr::QUEUE_REFRESH, "Refresh pull requests")
                            .ghost()
                            .small()
                            .icon(IconName::RefreshCw)
                            .tooltip("Refresh pull requests")
                            .disabled(loading)
                            .on_click(cx.listener(|pane, _, _, cx| {
                                pane.queue.update(cx, |queue, cx| queue.refresh(cx))
                            })),
                    ),
            )
            .child(self.filters(cx))
            .children(missing.map(|(words, url)| {
                h_flex()
                    .gap_2()
                    .text_role(TextRole::Body, cx)
                    .child(words)
                    .child(
                        div().debug_selector(|| "pr-missing-open".into()).child(
                            Button::new("pr-missing-open")
                                .ghost()
                                .small()
                                .label("Open on GitHub")
                                .on_click(move |_, window, cx| {
                                    window.dispatch_action(
                                        Box::new(OpenInBrowser { url: url.clone() }),
                                        cx,
                                    )
                                }),
                        ),
                    )
            }))
            .children(error.map(|error| {
                div()
                    .text_role(TextRole::Body, cx)
                    .text_color(danger)
                    .child(error)
            }))
            .when((loading || projects_loading) && count == 0, |page| {
                page.child(
                    h_flex()
                        .gap_2()
                        .text_role(TextRole::Body, cx)
                        .text_color(muted)
                        .child(Spinner::new().small())
                        .child("Loading pull requests…"),
                )
            })
            .when(empty, |page| {
                page.child(
                    div()
                        .text_role(TextRole::Body, cx)
                        .text_color(muted)
                        .child("No pull requests match these filters."),
                )
            })
            .child(
                uniform_list(
                    "pr-queue-items",
                    count + usize::from(more),
                    cx.processor(|pane, range: std::ops::Range<usize>, _, cx| {
                        range
                            .map(|index| {
                                let item = pane.queue.read(cx).page.items.get(index).cloned();
                                match item {
                                    Some(pr) => pane.row(pr, cx).into_any_element(),
                                    None => pane.more_row(cx).into_any_element(),
                                }
                            })
                            .collect()
                    }),
                )
                .w_full()
                .flex_1()
                .min_h_0(),
            )
            .children(more_projects.map(|projects| {
                div().pb_2().child(
                    Button::new("pr-more-projects")
                        .ghost()
                        .small()
                        .label("Load more projects")
                        .disabled(projects_loading)
                        .on_click(move |_, _, cx| {
                            projects.update(cx, |projects, cx| projects.load_more(cx))
                        }),
                )
            }))
    }
}
/// What a row says about review and checks: a chip for a decision that matters and a mark for the head's checks.
fn row_marks(pr: &PullRequestSummary, cx: &App) -> Vec<AnyElement> {
    let review = match pr.review_decision.as_deref() {
        Some("APPROVED") => Some(("Approved", Tone::Success)),
        Some("CHANGES_REQUESTED") => Some(("Changes requested", Tone::Danger)),
        _ => None,
    };
    let checks = pr.check_line().and_then(|(text, tone)| {
        let (icon, color) = match tone {
            CheckTone::Passing => (IconName::CircleCheck, cx.theme().success),
            CheckTone::Failing => (IconName::CircleX, cx.theme().danger),
            CheckTone::Pending => (IconName::Clock, cx.theme().warning),
            CheckTone::Quiet => return None,
        };
        let id = SharedString::from(format!("pr-row-checks:{}:{}", pr.repository, pr.number));
        Some(
            div()
                .id(id)
                .tooltip(move |window, cx| Tooltip::new(text.clone()).build(window, cx))
                .child(Icon::new(icon).size(px(14.)).text_color(color)),
        )
    });
    review
        .map(|(words, tone)| StatusChip::new(words, tone).into_any_element())
        .into_iter()
        .chain(checks.map(IntoElement::into_any_element))
        .collect()
}
fn project_name(title: &str, machine: &str) -> String {
    if machine.is_empty() || machine == "local" {
        title.to_string()
    } else {
        format!("{title} (remote)")
    }
}
