//! The expanded card's run rows, in the order the agent reported them: each row's text, its tool and detail, its
//! mark and, on the right, its percentage while running or its elapsed time once started. A future row is dimmed
//! with its future wording. A state this slice doesn't draw yet shows its text and the state's word. The footer
//! counts the done rows over all rows.

use chrono::DateTime;
use gpui_kit::assets::IconName as Glyph;
use gpui_kit::component::progress::Progress;
use gpui_kit::component::{ActiveTheme as _, Icon, Sizable as _, h_flex, v_flex};
use gpui_kit::prelude::FluentBuilder as _;
use gpui_kit::*;

use super::board_payload::BoardStep;

/// A row's mark.
#[derive(Clone, Debug, PartialEq)]
pub enum Mark {
    Future,
    Running,
    Done,
    /// Any other state, by its plain word.
    Word(&'static str),
}

/// One row as drawn.
#[derive(Clone, Debug, PartialEq)]
pub struct Row {
    pub text: String,
    pub tool: Option<String>,
    pub detail: Option<String>,
    pub mark: Mark,
    /// The running row's percentage, which also fills its bar.
    pub progress: Option<i32>,
    /// The right-hand value: the running row's percentage, else the elapsed time once started.
    pub value: Option<String>,
}

pub fn rows(steps: &[BoardStep], now: Option<i64>) -> Vec<Row> {
    steps.iter().map(|step| row(step, now)).collect()
}

fn row(step: &BoardStep, now: Option<i64>) -> Row {
    let mark = match step.state.as_str() {
        "future" => Mark::Future,
        "running" => Mark::Running,
        "done" => Mark::Done,
        "failed" => Mark::Word("Failed"),
        "retried" => Mark::Word("Retried"),
        "waiting" => Mark::Word("Waiting"),
        "skipped" => Mark::Word("Skipped"),
        _ => Mark::Word("Closed"),
    };
    let progress = step.progress.filter(|_| mark == Mark::Running);
    let elapsed = || {
        let started = ms(step.started_at.as_deref())?;
        Some(clock(ms(step.ended_at.as_deref()).or(now)? - started))
    };
    let value = match (&mark, progress) {
        (_, Some(percent)) => Some(format!("{percent}%")),
        (Mark::Running | Mark::Done, None) => elapsed(),
        _ => None,
    };
    Row {
        text: step.text.clone(),
        tool: step.tool.clone(),
        detail: step.detail.clone(),
        mark,
        progress,
        value,
    }
}

/// A recorded time in Unix ms.
fn ms(at: Option<&str>) -> Option<i64> {
    Some(DateTime::parse_from_rfc3339(at?).ok()?.timestamp_millis())
}

/// Elapsed time as the recordings show it: "0:12", "2:28", "1:04:09".
fn clock(ms: i64) -> String {
    let seconds = ms.max(0) / 1000;
    let (hours, minutes, seconds) = (seconds / 3600, seconds / 60 % 60, seconds % 60);
    match hours {
        0 => format!("{minutes}:{seconds:02}"),
        _ => format!("{hours}:{minutes:02}:{seconds:02}"),
    }
}

/// "3 of 9 done", or `None` for a task without rows.
pub fn footer(steps: &[BoardStep]) -> Option<String> {
    let done = steps.iter().filter(|step| step.state == "done").count();
    (!steps.is_empty()).then(|| format!("{done} of {} done", steps.len()))
}

/// The rows and their footer, or `None` for a task without rows, which draws as before.
pub fn draw(steps: &[BoardStep], now: Option<i64>, cx: &App) -> Option<Div> {
    let footer = footer(steps)?;
    let theme = cx.theme();
    let (muted, border) = (theme.muted_foreground, theme.border);
    let (success, info) = (theme.success, theme.info);
    let mono = theme.mono_font_family.clone();
    let list = rows(steps, now).into_iter().enumerate().map(|(ix, row)| {
        let (glyph, color) = match row.mark {
            Mark::Done => (Glyph::CircleCheck, success),
            Mark::Running => (Glyph::LoaderCircle, info),
            Mark::Future | Mark::Word(_) => (Glyph::Circle, muted),
        };
        let word = match row.mark {
            Mark::Word(word) => Some(word),
            _ => None,
        };
        let line = h_flex()
            .gap_2()
            .text_sm()
            .child(Icon::new(glyph).size(px(14.)).text_color(color))
            .children(row.tool.map(|tool| {
                div()
                    .flex_none()
                    .px_1()
                    .rounded(px(4.))
                    .border_1()
                    .border_color(border)
                    .text_xs()
                    .font_family(mono.clone())
                    .text_color(muted)
                    .child(tool)
            }))
            .child(div().flex_none().max_w_2_3().truncate().child(row.text))
            .children(word.map(|word| div().flex_none().text_xs().text_color(muted).child(word)))
            .child(
                div()
                    .flex_1()
                    .min_w_0()
                    .truncate()
                    .text_xs()
                    .font_family(mono.clone())
                    .text_color(muted)
                    .children(row.detail),
            )
            .children(
                row.value
                    .map(|value| div().flex_none().text_xs().text_color(muted).child(value)),
            );
        v_flex()
            .id(ElementId::Integer(ix as u64))
            .gap_1()
            .py_0p5()
            .when(row.mark == Mark::Future, |row| row.opacity(0.5))
            .child(line)
            .children(row.progress.map(|percent| {
                Progress::new(ElementId::Integer(ix as u64))
                    .xsmall()
                    .color(info)
                    .value(percent as f32)
            }))
    });
    Some(
        v_flex()
            .gap_1()
            .children(list)
            .child(div().pt_1().text_xs().text_color(muted).child(footer)),
    )
}

#[cfg(test)]
#[path = "board_rows_tests.rs"]
mod tests;
