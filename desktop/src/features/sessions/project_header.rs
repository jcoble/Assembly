//! The rail's section and project headers, as the look's group header (`.gh`): fold chevron, name, the backend's
//! count, then for a project its machines and a "+ New" pill.

use crate::core::theme::look::Look;
use crate::core::theme::metrics::{CORNER_PILL, CORNER_TAG, GROUP_HEIGHT, SPACE};
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
/// The project's letter mark, as a provider mark.
const MARK_SIZE: Pixels = px(20.);
/// The machine icons, the chevron's and the + New pill's glyphs.
const MACHINE_ICON: Pixels = px(13.);
/// The "+ New" pill, the mock's `.gh .new`.
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

/// The badge's letters, from the label's last part ("owner/repo" names the repo): the first letters of its first two
/// words ("EdiPlatform" and "rental-management" are two words each), or a one-word name's first two letters.
pub fn badge_letters(label: &str) -> String {
    let name = label.rsplit('/').next().unwrap_or(label);
    let mut words = Vec::new();
    let mut last: Option<char> = None;
    for (at, letter) in name.char_indices() {
        let starts = letter.is_alphanumeric()
            && match last {
                None => true,
                Some(last) => {
                    !last.is_alphanumeric() || (last.is_lowercase() && letter.is_uppercase())
                }
            };
        if starts {
            words.push(at);
        }
        last = Some(letter);
    }
    let letters: String = match words[..] {
        [first, second, ..] => [first, second]
            .iter()
            .filter_map(|at| name[*at..].chars().next())
            .collect(),
        [first] => name[first..]
            .chars()
            .filter(|letter| letter.is_alphanumeric())
            .take(2)
            .collect(),
        [] => String::new(),
    };
    letters.to_uppercase()
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
            // The project's two letters on a neutral mark, as a provider mark is drawn.
            .child(
                div()
                    .flex_none()
                    .size(MARK_SIZE)
                    .rounded(CORNER_TAG)
                    .bg(look.card_raised)
                    .border_1()
                    .border_color(look.hairline_strong)
                    .text_color(look.text_2)
                    .text_role(TextRole::Meta, cx)
                    .font_weight(FontWeight::BOLD)
                    .flex()
                    .items_center()
                    .justify_center()
                    .child(badge_letters(&self.label)),
            )
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
            // "+ New": an outlined pill, the mock's `.gh .new`.
            .child(
                h_flex()
                    .id(new_id)
                    .role(Role::Button)
                    .aria_label("New session in this project")
                    .flex_none()
                    .h(NEW_HEIGHT)
                    .pl(NEW_PADDING_LEFT)
                    .pr(SPACE[4])
                    .gap(SPACE[1])
                    .rounded(CORNER_PILL)
                    .border_1()
                    .border_color(look.line_chip)
                    .text_color(look.text_2)
                    .text_role(TextRole::Meta, cx)
                    .font_weight(FontWeight::MEDIUM)
                    .cursor_pointer()
                    .hover(move |pill| pill.bg(look.fill_hover))
                    .tooltip(|window, cx| {
                        Tooltip::new("New session in this project").build(window, cx)
                    })
                    .child(Icon::new(IconName::Plus).size(MACHINE_ICON))
                    .child("New")
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

#[cfg(test)]
mod tests {
    use super::badge_letters;

    #[test]
    fn a_badge_takes_two_letters_from_the_repo_name() {
        assert_eq!(badge_letters("jcoble/Assembly"), "AS");
        assert_eq!(badge_letters("EdiPlatform"), "EP");
        assert_eq!(badge_letters("rental-management"), "RM");
        assert_eq!(badge_letters("x"), "X");
        assert_eq!(badge_letters(""), "");
    }
}
