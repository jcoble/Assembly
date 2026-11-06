//! What a session is doing, on a three-line row. The list draws `GrowingBody` under it, as it does for any row.
use std::rc::Rc;

use gpui_kit::component::ActiveTheme as _;
use gpui_kit::component::list::ListItem;
use gpui_kit::*;

use super::needs_card::CardBody;
use crate::core::ui::row_drop_down::RowDropDowns;
use crate::core::ui::three_line_row::{RowArrow, ThreeLineRow};

pub struct SessionRowValues {
    /// Unique in its list.
    pub key: SharedString,
    pub lead: AnyElement,
    pub title: SharedString,
    pub chips: Vec<AnyElement>,
    pub task: Option<SharedString>,
    pub pr: Option<SharedString>,
    pub worktree: Option<SharedString>,
    pub project: Option<SharedString>,
    pub step: Option<SharedString>,
    pub time: Option<SharedString>,
    /// What it opens into.
    pub body: CardBody,
}

/// A project and its folder are often the same word, which reads once.
fn joined(parts: [&Option<SharedString>; 2]) -> SharedString {
    let mut words: Vec<&str> = parts
        .into_iter()
        .flatten()
        .map(SharedString::as_ref)
        .collect();
    words.dedup();
    words.join(" · ").into()
}

pub fn session_row(values: SessionRowValues, drops: &Entity<RowDropDowns>, cx: &App) -> ListItem {
    let muted = cx.theme().muted_foreground;
    let open = drops.read(cx).is_open(&values.key);
    let toggle: Rc<dyn Fn(&mut App)> = {
        let (drops, key, body) = (drops.clone(), values.key.clone(), values.body);
        Rc::new(move |cx| {
            let (key, body) = (key.clone(), body.clone());
            drops.update(cx, |drops, cx| {
                drops.toggle(
                    key,
                    move |window, cx| {
                        div()
                            .px_3()
                            .pb_2()
                            .child(body(window, cx))
                            .into_any_element()
                    },
                    cx,
                )
            });
        })
    };
    let on_click = toggle.clone();
    let second = joined([&values.task, &values.pr]);
    let third = joined([&values.project, &values.worktree]);
    let third = match &values.step {
        Some(step) if third.is_empty() => step.clone(),
        Some(step) => format!("{third} · {step}").into(),
        None => third,
    };
    ThreeLineRow {
        key: values.key,
        lead: values.lead,
        title: values.title,
        title_color: cx.theme().foreground,
        time: values.time.map(|time| (time, muted)),
        arrow: Some(RowArrow {
            open,
            on_toggle: toggle,
        }),
        icons: values.chips,
        spelled_out: second.clone(),
        second,
        third,
        dot: false,
    }
    .item(cx)
    .on_click(move |_, _, cx| on_click(cx))
}

#[cfg(test)]
#[path = "session_row_tests.rs"]
mod tests;
