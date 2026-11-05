//! Open another project: the bar above the Files and Git tabs, from plain display values. It says which project is
//! being viewed, and opens the popover that picks one. Replaces OpenOtherProject.svelte and the path helpers in
//! projects.ts. Old wording is kept word for word.

use std::rc::Rc;

use gpui_kit::assets::IconName;
use gpui_kit::component::button::{Button, ButtonVariants as _};
use gpui_kit::component::input::{Input, InputState};
use gpui_kit::component::list::ListItem;
use gpui_kit::component::popover::Popover;
use gpui_kit::component::scroll::ScrollableElement as _;
use gpui_kit::component::tooltip::Tooltip;
use gpui_kit::component::{ActiveTheme as _, Icon, Sizable as _, h_flex, v_flex};
use gpui_kit::prelude::FluentBuilder as _;
use gpui_kit::*;

use crate::core::theme::text::{TextRole, TextRoleExt as _};
use crate::core::ui::access::Control as _;
use crate::core::ui::access::control_ids::open_project as ids;

pub const TITLE: &str = "Open another project";
const DESCRIPTION: &str = "Look at or edit another project's files without starting a session. Git is read-only while another session works there, so its checkout is never disturbed.";
pub const FILTER_PLACEHOLDER: &str = "Filter projects";
const NO_MATCH: &str = "No project matches.";
const POPOVER_WIDTH: Pixels = px(480.);
const LIST_HEIGHT: Pixels = px(400.);
const WORKTREE_INDENT: Pixels = px(24.);
const GLYPH: Pixels = px(14.);
/// How many characters of a path a row shows before cutting its middle.
const PATH_CHARS: usize = 46;

/// One place to open: a project, or a worktree under it.
#[derive(Clone, Debug, PartialEq)]
pub struct ProjectOption {
    pub project_id: String,
    pub machine: String,
    pub label: String,
    pub folder: String,
    pub worktree: bool,
}

/// One machine's projects.
#[derive(Clone, Debug, PartialEq)]
pub struct ProjectGroup {
    pub machine: String,
    pub local: bool,
    pub label: String,
    pub options: Vec<ProjectOption>,
}

/// A home folder shown as `~`.
pub fn short_path(path: &str) -> String {
    let mut parts = path.splitn(4, '/');
    match (parts.next(), parts.next(), parts.next(), parts.next()) {
        (Some(""), Some("home" | "Users"), Some(user), rest) if !user.is_empty() => match rest {
            Some(rest) => format!("~/{rest}"),
            None => "~".to_string(),
        },
        _ => path.to_string(),
    }
}

/// A long path with its middle cut, keeping the last folder whole.
pub fn middle_truncate_path(path: &str, max: usize) -> String {
    let length = path.chars().count();
    if length <= max {
        return path.to_string();
    }
    let last = path.rfind('/').map_or(path, |at| &path[at..]);
    let keep = max as isize - last.chars().count() as isize - 1;
    if keep > 0 {
        let start: String = path.chars().take(keep as usize).collect();
        format!("{start}…{last}")
    } else {
        format!("…{last}")
    }
}

/// The groups with only what matches: any word of the machine, the name or the path, ignoring case.
pub fn filtered(groups: &[ProjectGroup], filter: &str) -> Vec<ProjectGroup> {
    let filter = filter.trim().to_lowercase();
    groups
        .iter()
        .filter_map(|group| {
            let options: Vec<_> = group
                .options
                .iter()
                .filter(|option| {
                    format!("{} {} {}", group.label, option.label, option.folder)
                        .to_lowercase()
                        .contains(&filter)
                })
                .cloned()
                .collect();
            (!options.is_empty()).then(|| ProjectGroup {
                options,
                ..group.clone()
            })
        })
        .collect()
}

type Picked = Rc<dyn Fn(&ProjectOption, &mut Window, &mut App)>;
type Pressed = Rc<dyn Fn(&mut Window, &mut App)>;
type OpenChanged = Rc<dyn Fn(bool, &mut Window, &mut App)>;
type KeyPressed = Rc<dyn Fn(&KeyDownEvent, &mut Window, &mut App)>;

#[derive(IntoElement)]
pub struct OpenProjectBar {
    /// "Viewing <name> on <machine>", or nothing while the session's own folder is shown.
    pub viewing: Option<String>,
    pub read_only_reason: Option<String>,
    pub can_start_here: bool,
    pub open: bool,
    pub filter: Entity<InputState>,
    pub groups: Vec<ProjectGroup>,
    /// Whether the machine's registry has another page to load.
    pub more: bool,
    pub on_more: Pressed,
    /// Which option of the flattened list Enter picks.
    pub highlighted: usize,
    pub list_scroll: ScrollHandle,
    pub on_open_change: OpenChanged,
    pub on_key: KeyPressed,
    pub on_pick: Picked,
    pub on_back: Pressed,
    pub on_new_session: Pressed,
}

impl OpenProjectBar {
    fn row(
        option: &ProjectOption,
        local: bool,
        at: usize,
        highlighted: bool,
        on_pick: Picked,
        muted: Hsla,
        cx: &App,
    ) -> ListItem {
        let tip = SharedString::from(option.folder.clone());
        let picked = option.clone();
        ListItem::new(SharedString::from(format!("open-project-option-{at}")))
            .selected(highlighted)
            .on_click(move |_, window, cx| on_pick(&picked, window, cx))
            .child(
                h_flex()
                    .items_start()
                    .gap_2()
                    .when(!option.worktree, |row| {
                        row.child(
                            Icon::new(if local {
                                IconName::Laptop
                            } else {
                                IconName::Cloud
                            })
                            .size(GLYPH)
                            .mt(px(2.)),
                        )
                    })
                    .child(
                        v_flex()
                            .flex_1()
                            .min_w_0()
                            .when(option.worktree, |rows| rows.pl(WORKTREE_INDENT))
                            .child(
                                div()
                                    .text_role(TextRole::Body, cx)
                                    .child(option.label.clone()),
                            )
                            .child(
                                div()
                                    .id(SharedString::from(format!("open-project-path-{at}")))
                                    .text_role(TextRole::Body, cx)
                                    .text_color(muted)
                                    .truncate()
                                    .tooltip(move |window, cx| {
                                        Tooltip::new(tip.clone()).build(window, cx)
                                    })
                                    .child(middle_truncate_path(
                                        &short_path(&option.folder),
                                        PATH_CHARS,
                                    )),
                            ),
                    ),
            )
    }

    fn popover_body(self, window_cx: &App) -> Div {
        let theme = window_cx.theme();
        let (muted, border) = (theme.muted_foreground, theme.border);
        let mut at = 0;
        let mut rows: Vec<AnyElement> = Vec::new();
        for group in &self.groups {
            rows.push(
                div()
                    .px_2()
                    .py_1()
                    .text_role(TextRole::Meta, window_cx)
                    .text_color(muted)
                    .child(group.label.clone())
                    .into_any_element(),
            );
            for option in &group.options {
                rows.push(
                    Self::row(
                        option,
                        group.local,
                        at,
                        at == self.highlighted,
                        self.on_pick.clone(),
                        muted,
                        window_cx,
                    )
                    .into_any_element(),
                );
                at += 1;
            }
        }
        let on_key = self.on_key.clone();
        v_flex()
            .w(POPOVER_WIDTH)
            .on_key_down(move |event, window, cx| on_key(event, window, cx))
            .child(
                v_flex()
                    .gap_1()
                    .px_3()
                    .pt_3()
                    .pb_2()
                    .child(
                        div()
                            .text_role(TextRole::Body, window_cx)
                            .font_weight(FontWeight::MEDIUM)
                            .child(TITLE),
                    )
                    .child(
                        div()
                            .text_role(TextRole::Body, window_cx)
                            .text_color(muted)
                            .child(DESCRIPTION),
                    ),
            )
            .child(
                div().border_t_1().border_color(border).p_1().child(
                    Input::new(&self.filter)
                        .appearance(false)
                        .prefix(Icon::new(IconName::Search).size(GLYPH)),
                ),
            )
            .child(
                div()
                    .border_t_1()
                    .border_color(border)
                    .child(if rows.is_empty() {
                        div()
                            .p_3()
                            .text_role(TextRole::Body, window_cx)
                            .text_color(muted)
                            .child(NO_MATCH)
                            .into_any_element()
                    } else {
                        div()
                            .relative()
                            .max_h(LIST_HEIGHT)
                            .child(
                                v_flex()
                                    .id("open-project-list")
                                    .max_h(LIST_HEIGHT)
                                    .p_1()
                                    .overflow_y_scroll()
                                    .track_scroll(&self.list_scroll)
                                    .children(rows)
                                    .when(self.more, |list| {
                                        let on_more = self.on_more.clone();
                                        list.child(
                                            Button::new("open-project-load-more")
                                                .control(ids::LOAD_MORE, "Load more")
                                                .ghost()
                                                .compact()
                                                .label("Load more")
                                                .on_click(move |_, window, cx| on_more(window, cx)),
                                        )
                                    }),
                            )
                            .vertical_scrollbar(&self.list_scroll)
                            .into_any_element()
                    }),
            )
    }
}

impl RenderOnce for OpenProjectBar {
    fn render(self, _: &mut Window, cx: &mut App) -> impl IntoElement {
        let theme = cx.theme();
        let (muted, secondary) = (theme.muted_foreground, theme.secondary);
        let viewing = self.viewing.clone();
        let reason = self.read_only_reason.clone();
        let can_start_here = self.can_start_here;
        let on_open_change = self.on_open_change.clone();
        let (on_back, on_new_session) = (self.on_back.clone(), self.on_new_session.clone());
        let open = self.open;
        let label = viewing.clone().unwrap_or_else(|| TITLE.to_string());
        let body = Rc::new(std::cell::RefCell::new(Some(self)));
        v_flex()
            .flex_none()
            .min_w_0()
            .mx_3()
            .mt_1()
            .mb_2()
            .gap_1()
            .when(viewing.is_some(), |bar| {
                bar.rounded_lg().bg(secondary).p_1()
            })
            .child(
                h_flex().child(
                    Popover::new("open-project")
                        .anchor(Anchor::TopLeft)
                        .p_0()
                        .open(open)
                        .on_open_change(move |open, window, cx| on_open_change(*open, window, cx))
                        .trigger_style(StyleRefinement::default().min_w_0())
                        .trigger(
                            Button::new("open-project-trigger")
                                .control(ids::TRIGGER, TITLE)
                                .ghost()
                                .small()
                                .icon(
                                    Icon::new(IconName::FolderSearch)
                                        .size(GLYPH)
                                        .text_color(muted),
                                )
                                .label(label),
                        )
                        .content(move |_, _, cx| match body.borrow_mut().take() {
                            Some(bar) => bar.popover_body(cx).into_any_element(),
                            None => Empty.into_any_element(),
                        }),
                ),
            )
            .when_some(
                reason.clone().filter(|_| viewing.is_some()),
                |bar, reason| {
                    bar.child(
                        h_flex()
                            .items_start()
                            .gap_2()
                            .px_2()
                            .text_role(TextRole::Body, cx)
                            .text_color(muted)
                            .child(Icon::new(IconName::Lock).size(GLYPH).mt(px(2.)))
                            .child(div().min_w_0().child(reason)),
                    )
                },
            )
            .when(viewing.is_some(), |bar| {
                bar.child(
                    h_flex()
                        .flex_wrap()
                        .justify_end()
                        .gap_1()
                        .when(reason.is_some() && can_start_here, |row| {
                            row.child(
                                Button::new("open-project-new-session")
                                    .control(ids::NEW_SESSION, "New session here")
                                    .outline()
                                    .small()
                                    .label("New session here")
                                    .on_click(move |_, window, cx| on_new_session(window, cx)),
                            )
                        })
                        .child(
                            Button::new("open-project-back")
                                .control(ids::BACK, "Back to session")
                                .ghost()
                                .small()
                                .label("Back to session")
                                .on_click(move |_, window, cx| on_back(window, cx)),
                        ),
                )
            })
    }
}

#[cfg(test)]
#[path = "open_project_tests.rs"]
mod tests;
