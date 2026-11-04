//! A workflow run on a three-line row: its steps' progress, and whatever the caller wants it to open into.
use std::rc::Rc;

use gpui_kit::component::progress::Progress;
use gpui_kit::component::{ActiveTheme as _, v_flex};
use gpui_kit::*;

use super::chip::{StatusChip, Tone};
use super::needs_card::CardBody;
use crate::core::ui::row_drop_down::{GrowingBody, RowDropDowns};
use crate::core::ui::three_line_row::{RowArrow, ThreeLineRow, row_height};

#[derive(IntoElement)]
pub struct RunRow {
    /// Unique in its list.
    pub key: SharedString,
    pub title: SharedString,
    pub task_key: Option<SharedString>,
    pub step_line: SharedString,
    pub done: usize,
    pub total: usize,
    pub tone: Tone,
    pub pill: Option<AnyElement>,
    /// A step list or a whole run card.
    pub body: CardBody,
    pub drops: Entity<RowDropDowns>,
}

fn percent(done: usize, total: usize) -> f32 {
    if total == 0 {
        return 0.;
    }
    done as f32 / total as f32 * 100.
}

/// The body keeps the row's padding, so a caller's body is only its content.
fn padded(body: CardBody) -> impl Fn(&mut Window, &mut App) -> AnyElement + 'static {
    move |window, cx| {
        div()
            .px_3()
            .pb_2()
            .child(body(window, cx))
            .into_any_element()
    }
}

impl RunRow {
    /// Opens the row if it isn't already.
    pub fn open(key: SharedString, drops: &Entity<RowDropDowns>, body: CardBody, cx: &mut App) {
        drops.update(cx, |drops, cx| {
            if !drops.is_open(&key) {
                drops.toggle(key, padded(body), cx);
            }
        });
    }
}

impl RenderOnce for RunRow {
    fn render(self, _: &mut Window, cx: &mut App) -> impl IntoElement {
        let open = self.drops.read(cx).is_open(&self.key);
        let color = self.tone.color(cx);
        let toggle: Rc<dyn Fn(&mut App)> = {
            let (drops, key, body) = (self.drops.clone(), self.key.clone(), self.body.clone());
            Rc::new(move |cx| {
                let (key, body) = (key.clone(), body.clone());
                drops.update(cx, |drops, cx| drops.toggle(key, padded(body), cx));
            })
        };
        let icons = self
            .task_key
            .map(|task| StatusChip::new(task, Tone::Neutral).into_any_element())
            .into_iter()
            .chain(self.pill)
            .collect();
        let on_click = toggle.clone();
        let item = ThreeLineRow {
            key: self.key.clone(),
            lead: div()
                .size_2()
                .flex_none()
                .rounded_full()
                .bg(color)
                .into_any_element(),
            title: self.title,
            title_color: cx.theme().foreground,
            time: None,
            arrow: Some(RowArrow {
                open,
                on_toggle: toggle,
            }),
            icons,
            second: self.step_line.clone(),
            spelled_out: self.step_line,
            third: format!("{} of {} steps", self.done, self.total).into(),
            dot: false,
        }
        .item(cx)
        .on_click(move |_, _, cx| on_click(cx));
        let bar = div().px_3().pb_1().child(
            Progress::new(SharedString::from(format!("run-progress:{}", self.key)))
                .value(percent(self.done, self.total))
                .color(color),
        );
        v_flex()
            .w_full()
            .child(item)
            .child(bar)
            .child(GrowingBody::new(self.key, self.drops, row_height(cx)).max_rows(None))
    }
}

#[cfg(test)]
#[path = "run_row_tests.rs"]
mod tests;
