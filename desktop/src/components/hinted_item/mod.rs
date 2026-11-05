//! A menu row that says more when the pointer rests on it: its hint, or, when it can't run, why not.
//! The row is drawn under its id and its hover text under `<id>-hint: <text>`; those debug selectors exist for tests.

use gpui_kit::component::menu::PopupMenuItem;
use gpui_kit::component::tooltip::Tooltip;
use gpui_kit::prelude::FluentBuilder as _;
use gpui_kit::*;

#[cfg(test)]
mod tests;

type OnClick = Box<dyn Fn(&mut Window, &mut App)>;

pub struct HintedItem {
    id: SharedString,
    label: SharedString,
    hint: Option<SharedString>,
    disabled_reason: Option<SharedString>,
    on_click: Option<OnClick>,
}

impl HintedItem {
    pub fn new(id: impl Into<SharedString>, label: impl Into<SharedString>) -> Self {
        Self {
            id: id.into(),
            label: label.into(),
            hint: None,
            disabled_reason: None,
            on_click: None,
        }
    }

    /// What the row does, shown while the pointer rests on it.
    pub fn hint(mut self, hint: impl Into<SharedString>) -> Self {
        self.hint = Some(hint.into());
        self
    }

    /// Why the row can't run: it stays visible, does nothing when clicked, and says this instead of its hint.
    pub fn disabled_reason(mut self, reason: Option<SharedString>) -> Self {
        self.disabled_reason = reason;
        self
    }

    pub fn on_click(mut self, on_click: impl Fn(&mut Window, &mut App) + 'static) -> Self {
        self.on_click = Some(Box::new(on_click));
        self
    }
}

impl From<HintedItem> for PopupMenuItem {
    fn from(item: HintedItem) -> Self {
        let HintedItem {
            id,
            label,
            hint,
            disabled_reason,
            on_click,
        } = item;
        let disabled = disabled_reason.is_some();
        let hover_text = disabled_reason.or(hint);
        PopupMenuItem::element(move |_, _| {
            let selector = id.to_string();
            div()
                .id(id.clone())
                .w_full()
                .debug_selector(move || selector)
                .child(label.clone())
                .when_some(hover_text.clone(), |row, text| {
                    let selector = format!("{id}-hint: {text}");
                    row.tooltip(move |window, cx| {
                        let (text, selector) = (text.clone(), selector.clone());
                        Tooltip::element(move |_, _| {
                            let selector = selector.clone();
                            div().debug_selector(move || selector).child(text.clone())
                        })
                        .build(window, cx)
                    })
                })
        })
        .disabled(disabled)
        .when_some(on_click, |item, on_click| {
            item.on_click(move |_, window, cx| on_click(window, cx))
        })
    }
}
