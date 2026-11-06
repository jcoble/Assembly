//! `HistoryPane`'s render: the toolbar with its repository, branch and author pickers and filter box, and the timeline.
use super::*;
use crate::core::ui::access::Control as _;
use crate::core::ui::access::control_ids as ids;

/// One of the toolbar's pickers: its value on the trigger, "All…" and each choice in its menu.
fn picker(
    trigger: Button,
    label: String,
    all: &'static str,
    options: Vec<String>,
    current: Option<String>,
    pane: WeakEntity<HistoryPane>,
    pick: fn(&mut HistoryFilter, Option<String>),
) -> impl IntoElement + use<> {
    trigger
        .outline()
        .small()
        .dropdown_caret(true)
        .label(label)
        .dropdown_menu(move |menu, _, _| {
            std::iter::once(None)
                .chain(options.iter().cloned().map(Some))
                .fold(menu, |menu, value| {
                    let pane = pane.clone();
                    let checked = current == value;
                    menu.item(
                        PopupMenuItem::new(value.clone().unwrap_or_else(|| all.into()))
                            .checked(checked)
                            .on_click(move |_, _, cx| {
                                _ = pane.update(cx, |pane, cx| {
                                    pick(&mut pane.filter, value.clone());
                                    pane.apply_filter(cx);
                                });
                            }),
                    )
                })
        })
}
impl Render for HistoryPane {
    fn render(&mut self, _: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        let timeline = self.timeline.read(cx);
        let (loaded, shown, reading) = (
            timeline.history.commits.len(),
            timeline.shown_count(),
            timeline.is_reading(),
        );
        let branches = branch_options(&timeline.history.commits);
        let authors = author_options(&timeline.history.commits);
        let count = if self.filter.active() {
            format!("{shown} of {loaded}")
        } else if loaded > 0 {
            format!("{loaded} commits")
        } else {
            String::new()
        };
        let theme = cx.theme();
        let (muted, border, surface) = (theme.muted_foreground, theme.border, theme.secondary);
        let weak = cx.entity().downgrade();
        let branch_label = format!(
            "History: {}",
            self.filter.branch.as_deref().unwrap_or("all branches")
        );
        let toolbar = h_flex()
            .flex_none()
            .gap_2()
            .px(px(10.))
            .py(px(6.))
            .bg(surface)
            .border_b_1()
            .border_color(border)
            .child(checkout::repository_picker(
                self.folder_label(),
                self.checkouts
                    .as_ref()
                    .filter(|checkouts| checkouts.has_others())
                    .map(|checkouts| checkouts.repository_choices(self.own_branch.as_deref())),
                self.checkout.as_ref().map(|checkout| checkout.path.clone()),
                weak.clone(),
            ))
            .children(self.path.clone().map(|path| {
                let tip = path.clone();
                h_flex()
                    .gap_1()
                    .child(
                        div()
                            .id("history-path")
                            .max_w(px(260.))
                            .truncate()
                            .px(px(7.))
                            .py(px(3.))
                            .border_1()
                            .border_color(border)
                            .rounded(px(5.))
                            .text_role(TextRole::Body, cx)
                            .text_color(muted)
                            .tooltip(move |window, cx| Tooltip::new(tip.clone()).build(window, cx))
                            .child(path),
                    )
                    .child(
                        div().debug_selector(|| "history-path-clear".into()).child(
                            Button::new("history-path-clear")
                                .control(ids::history::PATH_CLEAR, "Show all repository history")
                                .ghost()
                                .compact()
                                .icon(Icon::new(IconName::X).size(px(14.)))
                                .tooltip("Show all repository history")
                                .on_click(cx.listener(|pane, _, _, cx| pane.set_path(None, cx))),
                        ),
                    )
            }))
            .child(picker(
                Button::new("history-branch").tooltip(
                    "Filters the history shown here. It does not switch the working directory.",
                ),
                branch_label,
                "All branches",
                branches,
                self.filter.branch.clone(),
                weak.clone(),
                |filter, value| filter.branch = value,
            ))
            .child(picker(
                Button::new("history-author"),
                self.filter
                    .author
                    .clone()
                    .unwrap_or_else(|| "All authors".into()),
                "All authors",
                authors,
                self.filter.author.clone(),
                weak,
                |filter, value| filter.author = value,
            ))
            .child(
                div()
                    .flex_1()
                    .min_w_0()
                    .max_w(px(320.))
                    .child(Input::new(&self.search).small()),
            )
            .child(
                div()
                    .flex_none()
                    .text_role(TextRole::Body, cx)
                    .text_color(muted)
                    .child(count),
            )
            .child(div().flex_1())
            .child(
                Button::new("history-refresh")
                    .control(ids::history::REFRESH, "Read the commit history again")
                    .ghost()
                    .compact()
                    .icon(Icon::new(IconName::RefreshCw).size(px(14.)))
                    .tooltip("Read the commit history again")
                    .disabled(reading)
                    .on_click(cx.listener(|pane, _, _, cx| {
                        pane.notice = None;
                        pane.read_status(cx);
                        pane.timeline
                            .update(cx, |timeline, cx| timeline.refresh(cx))
                    })),
            );
        v_flex()
            .track_focus(&self.focus)
            .size_full()
            .min_h_0()
            .child(toolbar)
            .children(self.notice.clone().map(|notice| {
                div()
                    .flex_none()
                    .px(px(10.))
                    .py_1()
                    .text_role(TextRole::Body, cx)
                    .text_color(muted)
                    .child(notice)
            }))
            .child(self.timeline.clone())
    }
}
