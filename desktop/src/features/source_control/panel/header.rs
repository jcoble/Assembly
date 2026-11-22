//! The drawer's head, as the look draws it: "Source control" with how many files changed and the more-actions menu,
//! then the branch card holding the branch menu, upstream counts, and a second line naming its machine, base branch
//! and age. Called by the pane's render.
use super::SourceControlPane;
use crate::core::state::viewed_project::{LOCAL_MACHINE, ViewedProject};
use crate::core::theme::look::Look;
use crate::core::theme::metrics::{CORNER_TILE, SPACE};
use crate::core::theme::text::{TextRole, TextRoleExt as _};
use crate::core::ui::access::control_ids as ids;
use crate::core::ui::access::{Control as _, Named as _};
use crate::core::ui::reasoned_item::reasoned_item;
use crate::features::source_control::actions_menu::{MenuAction, PublishRow, READ_ONLY_SCOPE};
use crate::features::source_control::git_labels::branch_age;
use crate::features::source_control::git_status::GitStatus;
use crate::features::source_control::section_heading::count_chip;
use gpui_kit::assets::IconName;
use gpui_kit::component::button::{Button, ButtonVariants as _};
use gpui_kit::component::group_box::GroupBox;
use gpui_kit::component::menu::{DropdownMenu as _, PopupMenu, PopupMenuItem};
use gpui_kit::component::popover::Popover;
use gpui_kit::component::{Icon, h_flex, v_flex};
use gpui_kit::*;

/// The card's inside sides: with the branch button's own padding, its icon sits the look's 13 in.
const CARD_PADDING_X: Pixels = px(8.);
const BRANCH_ICON: Pixels = px(16.);
const CHEVRON_ICON: Pixels = px(12.);
const MACHINE_ICON: Pixels = px(12.);

impl SourceControlPane {
    /// The look's drawer head: the "Source control" title on a line with how many files changed and the more-actions
    /// menu, then the branch card.
    pub(super) fn render_header(&self, cx: &mut Context<Self>) -> impl IntoElement + use<> {
        let status = self.shown_status(cx);
        let look = Look::of(cx);
        let title = h_flex()
            .gap_1()
            .my(SPACE[3])
            .pl(SPACE[0])
            .child(
                div()
                    .flex_1()
                    .text_role(TextRole::Title, cx)
                    .font_weight(FontWeight::SEMIBOLD)
                    .text_color(look.text_1)
                    .child("Source control"),
            )
            .children(
                status
                    .as_ref()
                    .map(|status| count_chip(status.total_files.to_string(), cx)),
            )
            .child(self.actions_button(cx));
        v_flex()
            .flex_none()
            .px(SPACE[5])
            .pb(SPACE[1])
            .child(title)
            .children(status.map(|status| self.branch_card(&status, cx)))
    }
    /// The look's `.gcard`: the kit's group box on the card fill, holding the branch menu, upstream counts, and a
    /// second line naming the machine and (when off the default branch) its base branch and age.
    fn branch_card(&self, status: &GitStatus, cx: &mut Context<Self>) -> GroupBox {
        let look = Look::of(cx);
        let (ahead, behind) = (status.ahead, status.behind);
        let (remote, machine) = self.machine_label(cx);
        // On the base branch itself, detached, or when no base is found, the second line names only the machine.
        let subtitle = match (&status.base_branch, status.branch_created_at, self.now) {
            (Some(base), Some(created), Some(now)) => {
                format!("{machine} · from {base}, {}", branch_age(now, created))
            }
            (Some(base), _, _) => format!("{machine} · from {base}"),
            (None, _, _) => machine,
        };
        let surface = StyleRefinement::default()
            .px(CARD_PADDING_X)
            .py(SPACE[1])
            .rounded(CORNER_TILE)
            .bg(look.card)
            .border_1()
            .border_color(look.line_chip);
        GroupBox::new()
            .id("git-branch-card")
            .content_style(surface)
            .child(
                v_flex()
                    .gap(SPACE[0])
                    .child(
                        h_flex()
                            .gap(SPACE[3])
                            .child(self.branch_trigger(status, cx))
                            .child(div().flex_1())
                            .children((behind > 0).then(|| {
                                div()
                                    .text_role(TextRole::Meta, cx)
                                    .text_color(look.needs_you)
                                    .child(format!("{behind} behind"))
                            }))
                            .children((ahead > 0).then(|| {
                                div()
                                    .text_role(TextRole::Meta, cx)
                                    .text_color(look.text_2)
                                    .child(format!("{ahead} ahead"))
                            })),
                    )
                    .child(
                        h_flex()
                            .gap(SPACE[1])
                            .pl(SPACE[1])
                            .pb(SPACE[0])
                            .min_w_0()
                            .text_role(TextRole::Meta, cx)
                            .text_color(look.text_3)
                            .child(
                                Icon::new(if remote {
                                    IconName::Server
                                } else {
                                    IconName::Laptop
                                })
                                .size(MACHINE_ICON)
                                .flex_none(),
                            )
                            .child(div().min_w_0().truncate().child(subtitle)),
                    ),
            )
    }
    /// Whether the displayed folder is on a remote machine, and its display name ("This Mac" for local).
    fn machine_label(&self, cx: &App) -> (bool, String) {
        if let Some(viewed) = ViewedProject::viewed(cx) {
            return if viewed.machine == LOCAL_MACHINE {
                (false, "This Mac".into())
            } else {
                (true, viewed.machine)
            };
        }
        match self
            .read
            .read(cx)
            .source()
            .and_then(|source| source.route_machine.as_deref())
            .filter(|name| !name.is_empty())
        {
            Some(name) => (true, name.to_string()),
            None => (false, "This Mac".into()),
        }
    }
    /// The branch's name with its menu: the branch icon, the name at 600, a chevron.
    fn branch_trigger(
        &self,
        status: &GitStatus,
        cx: &mut Context<Self>,
    ) -> impl IntoElement + use<> {
        let look = Look::of(cx);
        let menu = self.branch_menu.clone();
        let read_only = self.read_only(cx);
        Popover::new("git-branch-menu")
            .anchor(Anchor::TopLeft)
            .p_0()
            .open(self.branch_open)
            .on_open_change(cx.listener(|pane, open: &bool, _, cx| pane.set_branch_open(*open, cx)))
            .trigger_style(StyleRefinement::default().min_w_0())
            .trigger(
                Button::new("git-branch")
                    .named(format!("Branch: {}", status.branch_title()))
                    .ghost()
                    .compact()
                    .tooltip(if read_only {
                        READ_ONLY_SCOPE.to_string()
                    } else {
                        status.branch_title()
                    })
                    .child(
                        h_flex()
                            .min_w_0()
                            .gap(SPACE[3])
                            .text_color(look.text_1)
                            .child(Icon::new(IconName::GitBranch).size(BRANCH_ICON))
                            .child(
                                div()
                                    .min_w_0()
                                    .truncate()
                                    .font_weight(FontWeight::SEMIBOLD)
                                    .child(status.branch_name()),
                            )
                            .child(
                                Icon::new(IconName::ChevronDown)
                                    .size(CHEVRON_ICON)
                                    .text_color(look.text_3),
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
                // The kit opens a submenu leftward only when this width wouldn't fit to the right, so it is wider than any row.
                let mut menu = menu.min_w(px(260.)).max_w(px(420.));
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
