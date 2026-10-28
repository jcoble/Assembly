//! The expanded card's feed: one entry per event, newest first, with its time, what happened and the summary it
//! carried, in full. A report shows the state it moved the task to.

use chrono::{Local, TimeZone};
use gpui_kit::component::{ActiveTheme as _, h_flex, v_flex};
use gpui_kit::*;

use super::board_card::label;
use super::board_payload::BoardEvent;

pub fn feed(events: &[BoardEvent], cx: &App) -> Div {
    let muted = cx.theme().muted_foreground;
    v_flex().gap_0p5().children(events.iter().map(|event| {
        let time = (Local.timestamp_millis_opt(event.occurred_at).single())
            .map(|time| time.format("%-I:%M %p").to_string())
            .unwrap_or_default();
        let what = match event.kind.as_str() {
            "task_reported" => label(&event.task.state),
            "question_asked" => "Asked",
            "answer_recorded" => "Answered",
            "answer_delivered" => "Delivered",
            "answer_delivery_failed" => "Delivery failed",
            "answer_delivery_unknown" => "Delivery unknown",
            other => other,
        };
        h_flex()
            .items_start()
            .gap_2()
            .text_xs()
            .child(div().w(px(64.)).flex_none().text_color(muted).child(time))
            .child(
                div()
                    .w(px(104.))
                    .flex_none()
                    .text_color(muted)
                    .child(what.to_string()),
            )
            .child(div().flex_1().min_w_0().child(event.task.summary.clone()))
    }))
}
