//! The rail's section and project headers, as the look's group header (`.gh`): fold chevron, name, the backend's
//! count, then for a project its machines and a "+ New" chip.

use crate::components::chip_button::chip_button;
use crate::core::theme::look::Look;
use crate::core::theme::metrics::{CORNER_TAG, GROUP_HEIGHT, SPACE};
use crate::core::theme::text::{TextRole, TextRoleExt as _};
use crate::core::ui::access::Named as _;
use crate::core::ui::three_line_row::tabular;
use gpui_kit::assets::IconName;
use gpui_kit::component::button::{Button, ButtonVariants as _};
use gpui_kit::component::tooltip::Tooltip;
use gpui_kit::component::{Icon, Sizable as _, h_flex};
use gpui_kit::*;

/// The fold chevron's button.
const CHEVRON_SIZE: Pixels = px(20.);
/// The machine icons, the chevron's and the + New chip's glyphs.
const MACHINE_ICON: Pixels = px(13.);
/// The "+ New" chip, the mock's `.gh .new`.
const NEW_HEIGHT: Pixels = px(24.);
const NEW_PADDING_LEFT: Pixels = px(7.);

type OnNew = Box<dyn Fn(&ClickEvent, &mut Window, &mut App)>;
type OnToggle = Box<dyn Fn(&ClickEvent, &mut Window, &mut App)>;

#[derive(IntoElement)]
pub struct ProjectHeader {
    label: String,
    /// The row's key. One repo can head a project row in several sections, and ids must differ between them.
    key: String,
    machines: Vec<String>,
    count: usize,
    folded: bool,
    on_toggle: OnToggle,
    on_new: OnNew,
}

impl ProjectHeader {
    pub fn new(
        label: String,
        key: String,
        machines: Vec<String>,
        count: usize,
        folded: bool,
        on_toggle: impl Fn(&ClickEvent, &mut Window, &mut App) + 'static,
        on_new: impl Fn(&ClickEvent, &mut Window, &mut App) + 'static,
    ) -> Self {
        ProjectHeader {
            label,
            key,
            machines,
            count,
            folded,
            on_toggle: Box::new(on_toggle),
            on_new: Box::new(on_new),
        }
    }
}

impl RenderOnce for ProjectHeader {
    fn render(self, _window: &mut Window, cx: &mut App) -> impl IntoElement {
        let look = Look::of(cx);
        let new_id = SharedString::from(format!("new:{}", self.key));
        header_row()
            .child(chevron(
                format!("fold:{}", self.key),
                self.folded,
                &look,
                self.on_toggle,
            ))
            .child(label(self.label, &look, cx))
            .child(count(self.count, &look, cx))
            // Which machines it's on, as icons that say so on hover.
            .children(self.machines.into_iter().map(|machine| {
                let local = machine == "local";
                let name: SharedString = if local {
                    "This Mac".into()
                } else {
                    machine.into()
                };
                div()
                    .id(name.clone())
                    .flex_none()
                    .tooltip(move |window, cx| Tooltip::new(name.clone()).build(window, cx))
                    .child(
                        Icon::new(if local {
                            IconName::Laptop
                        } else {
                            IconName::Server
                        })
                        .size(MACHINE_ICON)
                        .text_color(look.text_3),
                    )
            }))
            .child(div().flex_1())
            // "+ New": a chip, the mock's `.gh .new`.
            .child(
                chip_button(new_id, false, cx)
                    .named("New session in this project")
                    .h(NEW_HEIGHT)
                    .pl(NEW_PADDING_LEFT)
                    .pr(SPACE[4])
                    .tooltip("New session in this project")
                    .child(
                        h_flex()
                            .gap(SPACE[1])
                            .text_role(TextRole::Meta, cx)
                            .font_weight(FontWeight::MEDIUM)
                            .child(Icon::new(IconName::Plus).size(MACHINE_ICON))
                            .child("New"),
                    )
                    .on_click(self.on_new),
            )
    }
}

#[derive(IntoElement)]
pub struct SectionHeader {
    key: String,
    label: String,
    count: usize,
    folded: bool,
    on_toggle: OnToggle,
}

impl SectionHeader {
    pub fn new(
        key: String,
        label: String,
        count: usize,
        folded: bool,
        on_toggle: impl Fn(&ClickEvent, &mut Window, &mut App) + 'static,
    ) -> Self {
        Self {
            key,
            label,
            count,
            folded,
            on_toggle: Box::new(on_toggle),
        }
    }
}

impl RenderOnce for SectionHeader {
    fn render(self, _window: &mut Window, cx: &mut App) -> impl IntoElement {
        let look = Look::of(cx);
        header_row()
            .child(chevron(
                format!("fold:section:{}", self.key),
                self.folded,
                &look,
                self.on_toggle,
            ))
            .child(label(self.label, &look, cx))
            .child(count(self.count, &look, cx))
    }
}

/// A group header's line, the mock's `.gh`: 32 high, its pieces 6 apart.
fn header_row() -> Div {
    h_flex()
        .w_full()
        .h(GROUP_HEIGHT)
        .pl(SPACE[2])
        .pr(SPACE[1])
        .gap(SPACE[2])
}

/// A group's name, in Meta 600 text-2, giving way as the column narrows.
fn label(label: String, look: &Look, cx: &App) -> Div {
    div()
        .min_w_0()
        .text_role(TextRole::Meta, cx)
        .font_weight(FontWeight::SEMIBOLD)
        .text_color(look.text_2)
        .truncate()
        .child(label)
}

/// A header's fold chevron, in text-3.
fn chevron(id: String, folded: bool, look: &Look, on_toggle: OnToggle) -> Button {
    Button::new(SharedString::from(id))
        .named(if folded {
            "Expand group"
        } else {
            "Collapse group"
        })
        .ghost()
        .with_size(MACHINE_ICON)
        .size(CHEVRON_SIZE)
        .rounded(CORNER_TAG)
        .text_color(look.text_3)
        .icon(if folded {
            IconName::ChevronRight
        } else {
            IconName::ChevronDown
        })
        .on_click(on_toggle)
}

/// A header's count, right after its name.
fn count(count: usize, look: &Look, cx: &App) -> Div {
    div()
        .flex_none()
        .text_role(TextRole::Meta, cx)
        .font_weight(FontWeight::MEDIUM)
        .font_features(tabular())
        .text_color(look.text_3)
        .child(count.to_string())
}
