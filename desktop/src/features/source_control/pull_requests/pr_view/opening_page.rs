//! A pull request's page before it can be read: the row's words at once, and "Loading" while its project's machine
//! says who owns it, or the question of which project, when more than one holds it.
use super::super::pr_parts::{PrHeading, loading_line};
use super::{PrPane, Selected};
use crate::core::intents::open_url::OpenUrl;
use crate::core::theme::text::{TextRole, TextRoleExt as _};
use crate::core::ui::access::control_ids as ids;
use crate::core::ui::access::{Control as _, Named as _};
use gpui_kit::assets::IconName;
use gpui_kit::component::button::{Button, ButtonGroup, ButtonRounded, ButtonVariants as _};
use gpui_kit::component::{
    ActiveTheme as _, Disableable as _, Selectable as _, Sizable as _, h_flex, v_flex,
};
use gpui_kit::prelude::FluentBuilder as _;
use gpui_kit::*;
use std::time::Duration;

fn project_place(title: &str, machine: &str) -> String {
    if machine.is_empty() || machine == "local" {
        format!("{title} · This computer")
    } else {
        format!("{title} · {machine}")
    }
}

impl PrPane {
    pub(super) fn show_tab(&mut self, on_changes: bool, cx: &mut Context<Self>) {
        if let Some(selected) = &mut self.selected {
            selected.on_changes = on_changes;
        }
        cx.notify();
    }
    fn copy_row_link(&mut self, url: String, cx: &mut Context<Self>) {
        cx.write_to_clipboard(ClipboardItem::new_string(url));
        self.copied = Some(cx.spawn(async move |pane, cx| {
            cx.background_executor().timer(Duration::from_secs(2)).await;
            _ = pane.update(cx, |pane, cx| {
                pane.copied = None;
                cx.notify();
            });
        }));
        cx.notify();
    }
    pub(super) fn opening_page(&self, selected: &Selected, cx: &mut Context<Self>) -> AnyElement {
        let row = selected.row.as_ref();
        let url = row.map(|row| row.url.clone()).filter(|url| !url.is_empty());
        let (copied, opened) = (url.clone(), url.clone());
        let on_changes = selected.on_changes;
        let waiting = self.opening.is_some() || self.choosing.is_some();
        let bar = h_flex()
            .px_3()
            .py_2()
            .gap_2()
            .border_b_1()
            .border_color(cx.theme().border)
            .child(
                Button::new("pr-back")
                    .control(ids::pr::BACK, "All pull requests")
                    .ghost()
                    .small()
                    .icon(IconName::ChevronLeft)
                    .tooltip("All pull requests")
                    .on_click(cx.listener(|pane, _, _, cx| pane.close_detail(cx))),
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
                            .label("Changes")
                            .selected(on_changes),
                    )
                    .on_click(cx.listener(|pane, picked: &Vec<usize>, _, cx| {
                        pane.show_tab(picked.first() == Some(&1), cx)
                    })),
            )
            .child(div().flex_1())
            .child(
                Button::new("pr-copy-link")
                    .named(if self.copied.is_some() {
                        "Link copied"
                    } else {
                        "Copy link"
                    })
                    .ghost()
                    .small()
                    .icon(IconName::Link)
                    .tooltip(if self.copied.is_some() {
                        "Link copied"
                    } else {
                        "Copy link"
                    })
                    .disabled(url.is_none())
                    .on_click(cx.listener(move |pane, _, _, cx| {
                        if let Some(url) = copied.clone() {
                            pane.copy_row_link(url, cx);
                        }
                    })),
            )
            .child(
                Button::new("pr-refresh")
                    .control(ids::pr::REFRESH, "Refresh this pull request")
                    .ghost()
                    .small()
                    .icon(IconName::RefreshCw)
                    .tooltip("Refresh this pull request")
                    .disabled(waiting)
                    .on_click(cx.listener(|pane, _, _, cx| pane.retry_open(cx))),
            )
            .child(
                Button::new("pr-open-web")
                    .control(ids::pr::OPEN_WEB, "Open on GitHub")
                    .ghost()
                    .small()
                    .icon(IconName::ExternalLink)
                    .tooltip("Open on GitHub")
                    .disabled(url.is_none())
                    .on_click(move |_, window, cx| {
                        if let Some(url) = &opened {
                            window.dispatch_action(Box::new(OpenUrl { url: url.clone() }), cx);
                        }
                    }),
            );
        let title = match row {
            Some(row) => PrHeading::of_row(row).view(cx),
            None => v_flex().child(
                div()
                    .text_role(TextRole::Heading, cx)
                    .font_weight(FontWeight::SEMIBOLD)
                    .child(format!("#{}", selected.number)),
            ),
        };
        let danger = cx.theme().danger;
        let question = self.choosing.as_ref().map(|choice| {
            let what = choice.repository.as_deref().unwrap_or("This pull request");
            let picks: Vec<_> = (choice.projects.iter())
                .map(|project| {
                    let id = project.id.clone();
                    Button::new(SharedString::from(format!("pr-open-from:{id}")))
                        .outline()
                        .small()
                        .label(project_place(&project.title, &project.machine))
                        .tooltip(project.root_path.clone())
                        .on_click(cx.listener(move |pane, _, _, cx| pane.choose_project(&id, cx)))
                })
                .collect();
            v_flex()
                .gap_2()
                .text_role(TextRole::Body, cx)
                .child(format!("{what} is in more than one project. Open it from:"))
                .child(h_flex().gap_2().flex_wrap().children(picks))
        });
        v_flex()
            .size_full()
            .min_h_0()
            .child(bar)
            .children(self.failure.clone().map(|error| {
                div()
                    .px_3()
                    .py_1()
                    .text_role(TextRole::Body, cx)
                    .text_color(danger)
                    .child(error)
            }))
            .child(
                v_flex()
                    .gap_3()
                    .p_5()
                    .when(!on_changes, |page| page.child(title))
                    .when(self.opening.is_some(), |page| page.child(loading_line(cx)))
                    .children(question),
            )
            .into_any_element()
    }
}
