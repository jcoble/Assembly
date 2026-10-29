use crate::core::socket::update::Update;
use gpui_kit::assets::IconName;
use gpui_kit::component::button::{Button, ButtonCustomVariant, ButtonVariants as _};
use gpui_kit::component::menu::{DropdownMenu as _, PopupMenuItem};
use gpui_kit::component::popover::Popover;
use gpui_kit::component::{ActiveTheme as _, Sizable as _, h_flex, v_flex};
use gpui_kit::prelude::FluentBuilder as _;
use gpui_kit::*;
use serde::{Deserialize, Serialize};
use std::rc::Rc;

use super::sessions_pane::header_button;

pub const VIEW_KEY: &str = "rail.view";

#[derive(Clone, Copy, Debug, Deserialize, Serialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct GroupBy {
    pub status: bool,
    pub project: bool,
}

impl Default for GroupBy {
    fn default() -> Self {
        Self {
            status: false,
            project: true,
        }
    }
}

#[derive(Clone, Copy, Debug, Default, Deserialize, Serialize, PartialEq, Eq)]
#[serde(rename_all = "lowercase")]
pub enum Sort {
    #[default]
    Recent,
    Name,
}

#[derive(Clone, Copy, Debug, Default, Deserialize, Serialize, PartialEq, Eq)]
#[serde(rename_all = "lowercase")]
pub enum Dir {
    #[default]
    Desc,
    Asc,
}

#[derive(Clone, Copy, Debug, Default, Deserialize, Serialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct View {
    pub group_by: GroupBy,
    pub sort: Sort,
    pub dir: Dir,
}

impl View {
    pub fn with_sort(self, sort: Sort) -> Self {
        Self {
            sort,
            dir: match sort {
                Sort::Recent => Dir::Desc,
                Sort::Name => Dir::Asc,
            },
            ..self
        }
    }

    pub fn sort_wire(self) -> &'static str {
        match self.sort {
            Sort::Recent => "recent",
            Sort::Name => "name",
        }
    }

    pub fn dir_wire(self) -> &'static str {
        match self.dir {
            Dir::Desc => "desc",
            Dir::Asc => "asc",
        }
    }
}

pub fn saved_view(update: &Update) -> View {
    let Update::State(items) = update else {
        return View::default();
    };
    items
        .iter()
        .find(|(key, _)| key == VIEW_KEY)
        .and_then(|(_, item)| serde_json::from_value(item["value"].clone()).ok())
        .unwrap_or_default()
}

pub type OnView = Rc<dyn Fn(View, &mut Window, &mut App)>;

pub fn view_options(view: View, on_view: OnView, cx: &App) -> impl IntoElement {
    Popover::new("rail-view")
        .anchor(Anchor::TopRight)
        .trigger(
            header_button(Button::new("rail-view-button"), cx)
                .icon(gpui_kit::assets::IconName::ListFilter)
                .tooltip("View options"),
        )
        .content(move |_, _, cx| {
            let theme = cx.theme();
            // A chosen option is checked, in the accent.
            let chosen = ButtonCustomVariant::new(cx)
                .color(theme.caret.opacity(0.9))
                .foreground(theme.caret)
                .hover(theme.caret.opacity(0.2))
                .active(theme.caret.opacity(0.3));
            let toggle = |id: &'static str, label: &'static str, picked: bool, next: View| {
                let on_view = on_view.clone();
                Button::new(id)
                    .small()
                    .label(label)
                    .when(picked, |button| button.custom(chosen).icon(IconName::Check))
                    .when(!picked, |button| button.ghost())
                    .on_click(move |_, window, cx| on_view(next, window, cx))
            };
            let none = View {
                group_by: GroupBy {
                    status: false,
                    project: false,
                },
                ..view
            };
            let status = View {
                group_by: GroupBy {
                    status: !view.group_by.status,
                    ..view.group_by
                },
                ..view
            };
            let project = View {
                group_by: GroupBy {
                    project: !view.group_by.project,
                    ..view.group_by
                },
                ..view
            };
            let sort_name = match view.sort {
                Sort::Recent => "Recent activity",
                Sort::Name => "Name",
            };
            let direction = match (view.sort, view.dir) {
                (Sort::Recent, Dir::Desc) => "Newest first",
                (Sort::Recent, Dir::Asc) => "Oldest first",
                (Sort::Name, Dir::Asc) => "A to Z",
                (Sort::Name, Dir::Desc) => "Z to A",
            };
            let sort_view = view;
            let sort_change = on_view.clone();
            let dir_view = view;
            let dir_change = on_view.clone();
            v_flex()
                .w(px(250.))
                .p_2()
                .gap_2()
                .text_sm()
                .text_color(theme.foreground)
                .child("Group by")
                .child(
                    h_flex()
                        .gap_1()
                        .child(toggle(
                            "rail-group-none",
                            "None",
                            !view.group_by.status && !view.group_by.project,
                            none,
                        ))
                        .child(toggle(
                            "rail-group-status",
                            "Status",
                            view.group_by.status,
                            status,
                        ))
                        .child(toggle(
                            "rail-group-project",
                            "Project",
                            view.group_by.project,
                            project,
                        )),
                )
                .child("Sort")
                .child(
                    Button::new("rail-sort")
                        .secondary()
                        .small()
                        .label(sort_name)
                        .dropdown_caret(true)
                        .dropdown_menu(move |menu, _, _| {
                            [(Sort::Recent, "Recent activity"), (Sort::Name, "Name")]
                                .into_iter()
                                .fold(menu, |menu, (sort, label)| {
                                    let on_view = sort_change.clone();
                                    menu.item(
                                        PopupMenuItem::new(label)
                                            .checked(sort == sort_view.sort)
                                            .on_click(move |_, window, cx| {
                                                on_view(sort_view.with_sort(sort), window, cx)
                                            }),
                                    )
                                })
                        }),
                )
                .child("Direction")
                .child(
                    Button::new("rail-direction")
                        .secondary()
                        .small()
                        .label(direction)
                        .dropdown_caret(true)
                        .dropdown_menu(move |menu, _, _| {
                            let choices = match dir_view.sort {
                                Sort::Recent => {
                                    [(Dir::Desc, "Newest first"), (Dir::Asc, "Oldest first")]
                                }
                                Sort::Name => [(Dir::Asc, "A to Z"), (Dir::Desc, "Z to A")],
                            };
                            choices.into_iter().fold(menu, |menu, (dir, label)| {
                                let on_view = dir_change.clone();
                                menu.item(
                                    PopupMenuItem::new(label)
                                        .checked(dir == dir_view.dir)
                                        .on_click(move |_, window, cx| {
                                            on_view(View { dir, ..dir_view }, window, cx)
                                        }),
                                )
                            })
                        }),
                )
        })
}
