//! The one header row: the branch menu at the left, how many files changed, and the more-actions menu at the right.
use super::SourceControlPane;
use crate::core::ui::access::control_ids as ids;
use crate::core::ui::access::{Control as _, Named as _};
use crate::core::ui::reasoned_item::reasoned_item;
use crate::features::source_control::actions_menu::{MenuAction, PublishRow, READ_ONLY_SCOPE};
use crate::features::source_control::git_status::GitStatus;
use gpui_kit::assets::IconName;
use gpui_kit::component::button::{Button, ButtonVariants as _};
use gpui_kit::component::menu::{DropdownMenu as _, PopupMenu, PopupMenuItem};
use gpui_kit::component::popover::Popover;
use gpui_kit::component::tag::Tag;
use gpui_kit::component::{ActiveTheme as _, Icon, Sizable as _, h_flex};
use gpui_kit::*;

impl SourceControlPane {
    pub(super) fn render_header(&self, cx: &mut Context<Self>) -> impl IntoElement + use<> {
        let status = self.shown_status(cx);
        let mut row = h_flex()
            .flex_none()
            .min_h(px(32.))
            .px_2()
            .pt(px(6.))
            .pb_2()
            .gap_1();
        row = match &status {
            Some(status) => row.child(self.branch_trigger(status, cx)),
            None => row.child(div().flex_1()),
        };
        row.children(status.map(|status| {
            Tag::secondary()
                .xsmall()
                .rounded_full()
                .child(status.total_files.to_string())
        }))
        .child(self.actions_button(cx))
    }
    fn branch_trigger(
        &self,
        status: &GitStatus,
        cx: &mut Context<Self>,
    ) -> impl IntoElement + use<> {
        let theme = cx.theme();
        let (muted, warning, success) = (theme.muted_foreground, theme.warning, theme.success);
        let (ahead, behind) = (status.ahead, status.behind);
        let menu = self.branch_menu.clone();
        let read_only = self.read_only(cx);
        Popover::new("git-branch-menu")
            .anchor(Anchor::TopLeft)
            .p_0()
            .open(self.branch_open)
            .on_open_change(cx.listener(|pane, open: &bool, _, cx| pane.set_branch_open(*open, cx)))
            .trigger_style(StyleRefinement::default().flex_1().min_w_0())
            .trigger(
                Button::new("git-branch")
                    .named(format!("Branch: {}", status.branch_title()))
                    .ghost()
                    .compact()
                    .w_full()
                    .tooltip(if read_only {
                        READ_ONLY_SCOPE.to_string()
                    } else {
                        status.branch_title()
                    })
                    .child(
                        h_flex()
                            .w_full()
                            .min_w_0()
                            .gap_1()
                            .child(
                                Icon::new(IconName::GitBranch)
                                    .size(px(12.))
                                    .text_color(muted),
                            )
                            .child(
                                div()
                                    .flex_1()
                                    .min_w_0()
                                    .truncate()
                                    .child(status.branch_name()),
                            )
                            .children(
                                (behind > 0)
                                    .then(|| div().text_color(warning).child(format!("↓{behind}"))),
                            )
                            .children(
                                (ahead > 0)
                                    .then(|| div().text_color(success).child(format!("↑{ahead}"))),
                            )
                            .child(
                                Icon::new(IconName::ChevronDown)
                                    .size(px(12.))
                                    .text_color(muted),
                            ),
                    ),
            )
            .content(move |_, _, _| menu.clone())
    }
    fn actions_button(&self, cx: &mut Context<Self>) -> impl IntoElement + use<> {
        let pane = cx.entity().downgrade();
        Button::new("git-actions")
            .control(ids::git::ACTIONS, "More source-control actions")
            .ghost()
            .compact()
            .rounded(px(999.))
            .icon(Icon::new(IconName::Ellipsis).size(px(16.)))
            .tooltip("More source-control actions")
            .dropdown_menu_with_anchor(Anchor::TopRight, move |menu: PopupMenu, window, cx| {
                let Some(entity) = pane.upgrade() else {
                    return menu;
                };
                let (rows, publish) = entity.read(cx).menu_rows(cx);
                let mut menu = menu.min_w(px(260.)).max_w(px(288.));
                for row in rows {
                    if row.separated {
                        menu = menu.separator();
                    }
                    let action = row.action;
                    let weak = pane.clone();
                    menu = menu.item(reasoned_item(row.label, row.reason, move |window, cx| {
                        _ = weak.update(cx, |pane, cx| pane.menu_action(action, window, cx));
                    }));
                    if action == MenuAction::Push {
                        menu = publish_row(menu, publish.clone(), pane.clone(), window, cx);
                    }
                }
                menu
            })
    }
}
fn publish_row(
    menu: PopupMenu,
    publish: PublishRow,
    pane: WeakEntity<SourceControlPane>,
    window: &mut Window,
    cx: &mut Context<PopupMenu>,
) -> PopupMenu {
    match publish {
        PublishRow::Hidden => menu,
        PublishRow::Blocked(reason) => {
            menu.item(reasoned_item("Publish branch", Some(reason), |_, _| {}))
        }
        PublishRow::One(remote) => menu.item(reasoned_item(
            format!("Publish to {remote}"),
            None,
            move |_, cx| {
                let remote = remote.clone();
                _ = pane.update(cx, |pane, cx| pane.start_sync("publish", Some(remote), cx));
            },
        )),
        PublishRow::Many(remotes) => {
            menu.submenu("Publish to", window, cx, move |mut menu, _, _| {
                for remote in &remotes {
                    let weak = pane.clone();
                    let chosen = remote.clone();
                    menu = menu.item(PopupMenuItem::new(remote.clone()).on_click(
                        move |_, _, cx| {
                            let chosen = chosen.clone();
                            _ = weak.update(cx, |pane, cx| {
                                pane.start_sync("publish", Some(chosen), cx)
                            });
                        },
                    ));
                }
                menu
            })
        }
    }
}
