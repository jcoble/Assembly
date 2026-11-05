//! How a control gets its name in the accessibility tree, so `agent-desktop` and screen readers can find it: the
//! one place that sets an id, a role and a label. A feature never calls the kit's `accessibility_id` or `aria_label`
//! itself; it names a control with one of these, using an id from `control_ids`.
//!
//! - `Button::control(id, label)` and `Input::control(id, label)`: a button (an icon-only one is named by `label`)
//!   or a text field.
//! - `element.exposed(role, id, label)`: a div that stands for a control (a tab, a chip, a pill, a row).
//! - `element.selected_in_tree(..)`: the same, plus whether it's the picked one.
//! - `say(id, words)`: a line of visible text that a test or a script can read (a status, an empty state).
//!
//! Nothing here changes how anything is drawn: ids, roles and labels only reach assistive technology.

pub mod control_ids;

use gpui_kit::component::button::Button;
use gpui_kit::component::input::{Input, Textarea};
use gpui_kit::component::list::ListItem;
use gpui_kit::component::tab::Tab;
use gpui_kit::*;

/// Names a kit control in the tree. The kit gives a button its role (button or link) and a field its own.
pub trait Control: Sized {
    /// `id` is from `control_ids` (or built by one of its functions); `label` is what the control does or holds.
    fn control(self, id: impl Into<SharedString>, label: impl Into<SharedString>) -> Self;
}

impl Control for Button {
    fn control(self, id: impl Into<SharedString>, label: impl Into<SharedString>) -> Self {
        self.accessibility_id(id).accessibility_label(label)
    }
}

impl Control for Tab {
    /// The kit's tab is a tab already, selected by its bar; this gives it the id and the name.
    fn control(self, id: impl Into<SharedString>, label: impl Into<SharedString>) -> Self {
        self.accessibility_id(id).aria_label(label)
    }
}

impl Control for ListItem {
    /// A list row is a list item the way its list would make it, read as `label`.
    fn control(self, id: impl Into<SharedString>, label: impl Into<SharedString>) -> Self {
        self.role(Role::ListItem)
            .accessibility_id(id)
            .accessibility_label(label)
    }
}

impl Control for Textarea {
    fn control(self, id: impl Into<SharedString>, label: impl Into<SharedString>) -> Self {
        self.accessibility_id(id).aria_label(label)
    }
}

impl Control for Input {
    fn control(self, id: impl Into<SharedString>, label: impl Into<SharedString>) -> Self {
        self.accessibility_id(id).aria_label(label)
    }
}

/// Names a kit control that repeats (one per file, row or field) and so has no id of its own: by what it does.
pub trait Named: Sized {
    fn named(self, label: impl Into<SharedString>) -> Self;
}

impl Named for Button {
    fn named(self, label: impl Into<SharedString>) -> Self {
        self.accessibility_label(label)
    }
}

/// Names any element that has a GPUI id (a `Stateful` div, the kit's tab) in the tree, with a role.
pub trait ExposedElement: StatefulInteractiveElement + Sized {
    /// The element appears in the tree as `role`, named `label`, found by `id`.
    fn exposed(
        self,
        role: Role,
        id: impl Into<SharedString>,
        label: impl Into<SharedString>,
    ) -> Self {
        self.role(role).accessibility_id(id).aria_label(label)
    }

    /// As `exposed`, with whether it's the picked one of its row (a tab, a pill).
    fn selected_in_tree(
        self,
        role: Role,
        id: impl Into<SharedString>,
        label: impl Into<SharedString>,
        selected: bool,
    ) -> Self {
        self.exposed(role, id, label).aria_selected(selected)
    }

    /// A toggle (a filter value) that's `on` or off.
    fn toggle_in_tree(
        self,
        id: impl Into<SharedString>,
        label: impl Into<SharedString>,
        on: bool,
    ) -> Self {
        let state = if on { Toggled::True } else { Toggled::False };
        self.exposed(Role::Button, id, label).aria_toggled(state)
    }
}

impl<T: StatefulInteractiveElement> ExposedElement for T {}

/// A line of visible words that is also in the tree, as a label with these words: the way a status, an empty
/// state or an error becomes something a test or a script can read. The returned div takes the caller's styling.
pub fn say(id: impl Into<SharedString>, words: impl Into<SharedString>) -> Stateful<Div> {
    let (id, words) = (id.into(), words.into());
    div()
        .id(id.clone())
        .role(Role::Label)
        .accessibility_id(id)
        .aria_label(words.clone())
        .aria_value(words.clone())
        .child(words)
}

#[cfg(test)]
mod tests;
