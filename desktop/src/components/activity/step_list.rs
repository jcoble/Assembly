//! A workflow's steps. A step with more to say drops down into it, one at a time.
use gpui_kit::assets::IconName;
use gpui_kit::component::label::Label;
use gpui_kit::component::list::ListItem;
use gpui_kit::component::progress::Progress;
use gpui_kit::component::{ActiveTheme as _, Icon, Sizable as _, h_flex, v_flex};
use gpui_kit::prelude::FluentBuilder as _;
use gpui_kit::*;

use super::chip::{StatusChip, Tone};
use crate::core::theme::text::{TextRole, TextRoleExt as _};
use crate::core::ui::row_drop_down::{GrowingBody, RowDropDowns};
use crate::core::ui::spinner::Spinner;
use crate::core::ui::three_line_row::{row_height, tabular};

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum StepMark {
    Done,
    Running,
    Upcoming,
    Failed,
    Waiting,
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub enum StepRight {
    None,
    Percent(u8),
    Elapsed(SharedString),
}

#[derive(Clone, Debug)]
pub struct StepView {
    /// Unique in its list.
    pub key: SharedString,
    pub mark: StepMark,
    pub label: SharedString,
    pub tool: Option<SharedString>,
    pub detail: Option<SharedString>,
    pub explanation: Option<SharedString>,
    pub pills: Vec<(SharedString, Tone)>,
    pub right: StepRight,
    /// This attempt and the most it can take.
    pub attempt: Option<(i64, i64)>,
    /// The scope it needs and whether it's been granted.
    pub scope: Option<(SharedString, bool)>,
    pub closed: bool,
}

impl StepView {
    fn has_more(&self) -> bool {
        self.detail.is_some() || self.explanation.is_some() || self.scope.is_some()
    }
}

/// One rule for every duration: 320 ms is "320ms", 12.4 s is "12.4s", and 148 s is "2m 28".
pub fn format_elapsed(ms: i64) -> SharedString {
    let ms = ms.max(0);
    match ms {
        0..1_000 => format!("{ms}ms"),
        1_000..60_000 => format!("{:.1}s", ms as f64 / 1000.),
        _ => format!("{}m {:02}", ms / 60_000, ms % 60_000 / 1000),
    }
    .into()
}

fn mark(mark: StepMark, cx: &App) -> AnyElement {
    let theme = cx.theme();
    let icon = |name: IconName, color: Hsla| {
        Icon::new(name)
            .xsmall()
            .text_color(color)
            .into_any_element()
    };
    match mark {
        StepMark::Done => icon(IconName::CircleCheck, theme.success),
        StepMark::Running => Spinner::new()
            .xsmall()
            .color(theme.muted_foreground)
            .into_any_element(),
        StepMark::Upcoming => icon(IconName::Dot, theme.muted_foreground),
        StepMark::Failed => icon(IconName::CircleX, theme.danger),
        StepMark::Waiting => icon(IconName::Dot, theme.warning),
    }
}

fn right(key: &SharedString, right: StepRight, cx: &App) -> Option<AnyElement> {
    let muted = cx.theme().muted_foreground;
    let number = |text: SharedString, cx: &App| {
        Label::new(text)
            .text_role(TextRole::Meta, cx)
            .font_features(tabular())
            .text_color(muted)
    };
    match right {
        StepRight::None => None,
        StepRight::Elapsed(elapsed) => Some(number(elapsed, cx).into_any_element()),
        StepRight::Percent(percent) => Some(
            h_flex()
                .gap_1()
                .child(
                    div().w_16().child(
                        Progress::new(SharedString::from(format!("progress:{key}")))
                            .value(f32::from(percent)),
                    ),
                )
                .child(number(format!("{percent}%").into(), cx))
                .into_any_element(),
        ),
    }
}

/// The step's whole text, at full length.
fn step_body(step: &StepView) -> impl Fn(&mut Window, &mut App) -> AnyElement + 'static {
    let (detail, explanation, scope, closed) = (
        step.detail.clone(),
        step.explanation.clone(),
        step.scope.clone(),
        step.closed,
    );
    move |_, cx| {
        let muted = cx.theme().muted_foreground;
        v_flex()
            .pl_6()
            .pr_2()
            .pb_2()
            .gap_1()
            .text_role(TextRole::Meta, cx)
            .when_some(detail.clone(), |body, detail| body.child(detail))
            .when_some(explanation.clone(), |body, explanation| {
                body.child(div().text_color(muted).child(explanation))
            })
            .when_some(scope.clone(), |body, (scope, granted)| {
                let granted = if granted { "granted" } else { "not granted" };
                body.child(
                    div()
                        .text_color(muted)
                        .child(format!("Scope: {scope} ({granted})")),
                )
            })
            .when(closed, |body| {
                body.child(div().text_color(muted).child("Closed"))
            })
            .into_any_element()
    }
}

fn step_row(
    list: &SharedString,
    step: StepView,
    drops: &Entity<RowDropDowns>,
    cx: &mut App,
) -> AnyElement {
    let muted = cx.theme().muted_foreground;
    let radius = cx.theme().radius;
    let key = SharedString::from(format!("{list}:{}", step.key));
    let open = drops.read(cx).is_open(&key);
    let has_more = step.has_more();
    let line = |text: SharedString, cx: &App| {
        Label::new(text)
            .w_full()
            .truncate()
            .text_role(TextRole::Meta, cx)
            .text_color(muted)
    };
    let top = h_flex()
        .gap_2()
        .child(mark(step.mark, cx))
        .child(
            Label::new(step.label.clone())
                .flex_1()
                .min_w_0()
                .truncate()
                .text_role(TextRole::Body, cx)
                .when(step.closed || step.mark == StepMark::Upcoming, |label| {
                    label.text_color(muted)
                }),
        )
        .children(step.tool.clone().map(|tool| {
            div()
                .flex_none()
                .text_role(TextRole::Meta, cx)
                .text_color(muted)
                .child(tool)
        }))
        .children(
            step.pills
                .iter()
                .map(|(label, tone)| StatusChip::new(label.clone(), *tone)),
        )
        .when_some(step.attempt, |row, (attempt, most)| {
            row.child(
                div()
                    .flex_none()
                    .text_role(TextRole::Meta, cx)
                    .text_color(muted)
                    .child(format!("Attempt {attempt}/{most}")),
            )
        })
        .when_some(
            step.scope.clone().filter(|(_, granted)| !granted),
            |row, _| row.child(StatusChip::new("Needs scope", Tone::Warning)),
        )
        .children(right(&key, step.right.clone(), cx))
        .when(has_more, |row| {
            row.child(
                Icon::new(if open {
                    IconName::ChevronDown
                } else {
                    IconName::ChevronRight
                })
                .xsmall()
                .text_color(muted),
            )
        });
    let item = ListItem::new(key.clone())
        .w_full()
        .px_2()
        .py_1()
        .rounded(radius)
        .child(
            v_flex()
                .w_0()
                .min_w_full()
                .gap_0p5()
                .child(top)
                .when(!open, |column| {
                    column
                        .when_some(step.detail.clone(), |column, detail| {
                            column.child(line(detail, cx))
                        })
                        .when_some(step.explanation.clone(), |column, explanation| {
                            column.child(line(explanation, cx))
                        })
                }),
        )
        .when(has_more, |item| {
            let (drops, key, body) = (drops.clone(), key.clone(), step_body(&step));
            let body = std::rc::Rc::new(body);
            item.on_click(move |_, _, cx| {
                let body = body.clone();
                drops.update(cx, |drops, cx| {
                    drops.toggle(key.clone(), move |window, cx| body(window, cx), cx)
                });
            })
        });
    v_flex()
        .w_full()
        .child(item)
        .child(GrowingBody::new(key, drops.clone(), row_height(cx)).max_rows(None))
        .into_any_element()
}

/// Draws the rows with no gap between them, since a shut row's body has to take up nothing.
pub fn step_list(
    key: impl Into<SharedString>,
    steps: Vec<StepView>,
    drops: &Entity<RowDropDowns>,
    cx: &mut App,
) -> AnyElement {
    let key = key.into();
    v_flex()
        .w_full()
        .children(
            steps
                .into_iter()
                .map(|step| step_row(&key, step, drops, cx)),
        )
        .into_any_element()
}

#[cfg(test)]
#[path = "step_list_tests.rs"]
mod tests;
