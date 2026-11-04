//! How the Tasks tab draws a row and its status and priority: the one table from Notion's words to the app's colours,
//! and a row's text from its data.

use std::rc::Rc;

use gpui_kit::assets::IconName;
use gpui_kit::component::button::{Button, ButtonCustomVariant, ButtonVariants as _};
use gpui_kit::component::list::ListItem;
use gpui_kit::component::popover::Popover;
use gpui_kit::component::{ActiveTheme as _, Icon, Sizable as _, h_flex, v_flex};
use gpui_kit::prelude::FluentBuilder as _;
use gpui_kit::*;

use crate::activity::{StatusChip, Tone};
use crate::core::theme::text::{TextRole, TextRoleExt as _};

use super::task_export::{NotionProjectOption, NotionQuery, NotionTaskRow};

/// A status's colour; the app's, not Notion's.
pub fn status_tone(status: &str) -> Tone {
    match status.trim().to_ascii_lowercase().as_str() {
        "done" | "complete" | "completed" => Tone::Success,
        "doing" | "in progress" | "working" => Tone::Info,
        "blocked" => Tone::Danger,
        "todo" | "to do" | "not started" | "backlog" => Tone::Warning,
        _ => Tone::Neutral,
    }
}

pub fn priority_tone(priority: &str) -> Tone {
    match priority.trim().to_ascii_lowercase().as_str() {
        "high" => Tone::Danger,
        "medium" => Tone::Warning,
        _ => Tone::Neutral,
    }
}

/// What a row's lines say. The project is left out when the list is already filtered to it.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct RowText {
    pub title: String,
    pub key: Option<String>,
    pub status: String,
    pub priority: Option<String>,
    pub project: Option<String>,
}

pub fn row_text(row: &NotionTaskRow, project_filtered: bool) -> RowText {
    let filled = |value: &str| (!value.is_empty()).then(|| value.to_owned());
    RowText {
        title: display_title(row),
        key: filled(&row.task_key),
        status: filled(&row.status).unwrap_or_else(|| "Unspecified".into()),
        priority: filled(&row.priority),
        project: if project_filtered {
            None
        } else {
            filled(&row.project_title)
        },
    }
}

/// The key has its own place on the row, so a title that starts with it doesn't say it twice.
pub fn display_title(row: &NotionTaskRow) -> String {
    let lead = format!("[{}]", row.task_key);
    match row.title.strip_prefix(&lead) {
        Some(rest) if !row.task_key.is_empty() => rest.trim_start().to_owned(),
        _ => row.title.clone(),
    }
}

pub fn task_row(
    index: usize,
    row: &NotionTaskRow,
    project_filtered: bool,
    on_open: impl Fn(&ClickEvent, &mut Window, &mut App) + 'static,
    cx: &App,
) -> impl IntoElement {
    let text = row_text(row, project_filtered);
    let muted = cx.theme().muted_foreground;
    let tone = status_tone(&row.status);
    let mark = if tone == Tone::Neutral {
        muted
    } else {
        tone.color(cx)
    };
    let mut meta = h_flex()
        .gap_1p5()
        .min_w_0()
        .items_center()
        .overflow_hidden();
    if let Some(key) = text.key {
        meta = meta.child(div().flex_shrink_0().text_color(muted).child(key));
    }
    meta = meta.child(StatusChip::new(text.status, tone));
    if let Some(priority) = text.priority {
        let tone = priority_tone(&priority);
        meta = meta.child(StatusChip::new(priority, tone));
    }
    if let Some(project) = text.project {
        meta = meta.child(div().min_w_0().truncate().text_color(muted).child(project));
    }
    ListItem::new(("task", index)).on_click(on_open).child(
        h_flex()
            .w_full()
            .min_w_0()
            .gap_3()
            .items_center()
            .child(
                div()
                    .flex_shrink_0()
                    .text_color(mark)
                    .child(Icon::new(IconName::File).size_4()),
            )
            .child(
                v_flex()
                    .flex_1()
                    .min_w_0()
                    .gap_0p5()
                    .child(
                        div()
                            .font_weight(FontWeight::MEDIUM)
                            .text_role(TextRole::Body, cx)
                            .line_clamp(2)
                            .child(text.title),
                    )
                    .child(div().text_role(TextRole::Meta, cx).child(meta)),
            ),
    )
}

/// What the view menu changes.
#[derive(Clone, Debug, PartialEq, Eq)]
pub enum ViewChange {
    Sort(&'static str),
    Direction(&'static str),
    Project(Option<String>),
}

pub type OnViewChange = Rc<dyn Fn(ViewChange, &mut Window, &mut App)>;

pub fn direction_label(sort: &str, direction: &str) -> &'static str {
    match (sort, direction) {
        ("number", "asc") => "Lowest first",
        ("number", _) => "Highest first",
        (_, "asc") => "A to Z",
        _ => "Z to A",
    }
}

/// The header's pill: the shown project, opening the sort, direction and project choices.
pub fn view_menu(
    query: &NotionQuery,
    projects: &[NotionProjectOption],
    on_change: OnViewChange,
) -> impl IntoElement {
    let shown = query
        .project_id
        .as_ref()
        .and_then(|id| projects.iter().find(|project| project.id == *id))
        .map_or("All projects".to_owned(), |project| project.title.clone());
    let (query, projects) = (query.clone(), projects.to_vec());
    Popover::new("tasks-view")
        .anchor(Anchor::TopLeft)
        .trigger(
            Button::new("tasks-view-button")
                .small()
                .ghost()
                .icon(IconName::ChevronDown)
                .label(shown)
                .tooltip("View options"),
        )
        .content(move |_, _, cx| {
            let theme = cx.theme();
            let chosen = ButtonCustomVariant::new(cx)
                .color(theme.caret.opacity(0.9))
                .foreground(theme.caret)
                .hover(theme.caret.opacity(0.2))
                .active(theme.caret.opacity(0.3));
            let on_change = on_change.clone();
            let toggle =
                move |id: SharedString, label: String, picked: bool, change: ViewChange| {
                    let on_change = on_change.clone();
                    Button::new(id)
                        .small()
                        .label(label)
                        .when(picked, |button| button.custom(chosen).icon(IconName::Check))
                        .when(!picked, |button| button.ghost())
                        .on_click(move |_, window, cx| on_change(change.clone(), window, cx))
                };
            let heading = |text: &'static str| {
                div()
                    .text_role(TextRole::Meta, cx)
                    .text_color(cx.theme().muted_foreground)
                    .child(text)
            };
            let mut project_list = v_flex()
                .id("tasks-view-projects")
                .max_h(px(220.))
                .overflow_y_scroll()
                .items_start()
                .child(toggle(
                    "project-all".into(),
                    "All projects".into(),
                    query.project_id.is_none(),
                    ViewChange::Project(None),
                ));
            for project in &projects {
                project_list = project_list.child(toggle(
                    format!("project-{}", project.id).into(),
                    project.title.clone(),
                    query.project_id.as_deref() == Some(project.id.as_str()),
                    ViewChange::Project(Some(project.id.clone())),
                ));
            }
            let sort = |value: &'static str, label: &str| {
                toggle(
                    format!("sort-{value}").into(),
                    label.to_owned(),
                    query.sort == value,
                    ViewChange::Sort(value),
                )
            };
            let direction = |value: &'static str| {
                toggle(
                    format!("direction-{value}").into(),
                    direction_label(&query.sort, value).to_owned(),
                    query.direction == value,
                    ViewChange::Direction(value),
                )
            };
            v_flex()
                .gap_2()
                .w(px(260.))
                .child(heading("Sort"))
                .child(
                    h_flex()
                        .gap_1()
                        .child(sort("number", "Task number"))
                        .child(sort("title", "Title")),
                )
                .child(heading("Direction"))
                .child(
                    h_flex()
                        .gap_1()
                        .child(direction("desc"))
                        .child(direction("asc")),
                )
                .child(heading("Project"))
                .child(project_list)
        })
}

#[cfg(test)]
mod tests {
    use super::{
        NotionTaskRow, Tone, direction_label, display_title, priority_tone, row_text, status_tone,
    };

    fn row(title: &str, key: &str) -> NotionTaskRow {
        NotionTaskRow {
            source_id: "s".into(),
            page_id: "p".into(),
            title: title.into(),
            key_number: 1,
            task_key: key.into(),
            project_id: "x".into(),
            project_title: "Assembly".into(),
            status: "To Do".into(),
            priority: String::new(),
            assignee: String::new(),
            due: None,
            url: String::new(),
            fetched_ms: 0,
        }
    }

    #[test]
    fn a_title_that_leads_with_its_key_does_not_say_it_twice() {
        assert_eq!(display_title(&row("[TSK-1] Fix it", "TSK-1")), "Fix it");
        assert_eq!(
            display_title(&row("[Assembly] Fix it", "TSK-1")),
            "[Assembly] Fix it"
        );
        assert_eq!(display_title(&row("[TSK-1] Fix it", "")), "[TSK-1] Fix it");
    }

    #[test]
    fn the_project_is_hidden_when_the_list_is_filtered_to_it() {
        assert_eq!(
            row_text(&row("t", "k"), false).project.as_deref(),
            Some("Assembly")
        );
        assert_eq!(row_text(&row("t", "k"), true).project, None);
    }

    #[test]
    fn a_task_with_no_status_or_priority_says_so_only_for_status() {
        let mut bare = row("t", "");
        bare.status.clear();
        let text = row_text(&bare, false);
        assert_eq!(text.status, "Unspecified");
        assert_eq!((text.key, text.priority), (None, None));
    }

    #[test]
    fn tones_come_from_the_one_table() {
        assert_eq!(status_tone(" DONE "), Tone::Success);
        assert_eq!(status_tone("Doing"), Tone::Info);
        assert_eq!(status_tone("To Do"), Tone::Warning);
        assert_eq!(status_tone("Blocked"), Tone::Danger);
        assert_eq!(status_tone("Other"), Tone::Neutral);
        assert_eq!(priority_tone("High"), Tone::Danger);
        assert_eq!(priority_tone("Low"), Tone::Neutral);
    }

    #[test]
    fn the_direction_reads_for_its_sort() {
        assert_eq!(direction_label("number", "desc"), "Highest first");
        assert_eq!(direction_label("title", "asc"), "A to Z");
    }
}
