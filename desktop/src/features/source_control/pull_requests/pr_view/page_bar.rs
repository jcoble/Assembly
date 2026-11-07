//! The bar over a pull request: back to the list, the Summary, Checks and Changes pages, the diff mode on Changes, and
//! the link, refresh and GitHub buttons.
use super::super::{pr_activity::check_tone, pull_request::CheckTone};
use super::*;

impl PrDetail {
    pub(super) fn page_bar(&self, cx: &mut Context<Self>) -> impl IntoElement {
        let border = cx.theme().border;
        let (success, danger, well) = (cx.theme().success, cx.theme().danger, cx.theme().muted);
        let pill = || h_flex().items_center().rounded_full().bg(well);
        let totals = self
            .detail
            .as_ref()
            .map(|detail| change_totals(&detail.files));
        let on_changes = self.page == Page::Changes;
        let failing = (self.detail.as_ref())
            .map(|detail| {
                detail
                    .checks
                    .iter()
                    .filter(|check| check_tone(check) == CheckTone::Failing)
                    .count()
            })
            .filter(|failing| *failing > 0);
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
                            .selected(self.page == Page::Summary)
                            .on_click(
                                cx.listener(|view, _, _, cx| view.select_page(Page::Summary, cx)),
                            ),
                    )
                    .child(
                        Button::new("pr-page-checks")
                            .named("Checks")
                            .ghost()
                            .small()
                            .rounded(ButtonRounded::Size(px(999.)))
                            .selected(self.page == Page::Checks)
                            .on_click(
                                cx.listener(|view, _, _, cx| view.select_page(Page::Checks, cx)),
                            )
                            .child(h_flex().gap_1().child("Checks").children(failing.map(
                                |failing| {
                                    div().text_color(danger).child(format!("{failing} failing"))
                                },
                            ))),
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
}
