//! Tab bodies laid out over each other, so the stack is as tall as the tallest and doesn't change height when the tab
//! does. Only the selected one draws or takes clicks. A row whose cells each pull back over the one before: each is
//! measured at the stack's own width, so its text wraps as it will be drawn.

use gpui_kit::component::h_flex;
use gpui_kit::prelude::FluentBuilder as _;
use gpui_kit::*;

pub fn tab_stack(selected: usize, tabs: impl IntoIterator<Item = AnyElement>) -> Div {
    h_flex()
        .w_full()
        .items_start()
        .children(tabs.into_iter().enumerate().map(|(at, tab)| {
            div()
                .w_full()
                .flex_none()
                .when(at > 0, |cell| cell.ml(relative(-1.)))
                .when(at != selected, |cell| cell.invisible())
                .child(tab)
        }))
}

#[cfg(test)]
#[path = "tab_stack_tests.rs"]
mod tests;
