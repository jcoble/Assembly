//! The rail's section and project headers: fold chevron, backend count, project machines and New session.

use crate::core::theme::text::{TextRole, TextRoleExt as _};
use crate::core::ui::access::Named as _;
use gpui_kit::assets::IconName;
use gpui_kit::component::button::{Button, ButtonVariants as _};
use gpui_kit::component::tooltip::Tooltip;
use gpui_kit::component::{ActiveTheme as _, Icon, Sizable as _, h_flex};
use gpui_kit::*;

/// A project's header line: room for its + button.
pub const PROJECT_HEIGHT: Pixels = px(30.);

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
        let theme = cx.theme();
        // A colour of the theme's chart set, the same for a project every time.
        let colours = [
            theme.chart_1,
            theme.chart_2,
            theme.chart_3,
            theme.chart_4,
            theme.chart_5,
        ];
        let sum = self.label.bytes().map(usize::from).sum::<usize>();
        let colour = colours[sum % colours.len()];
        let (muted, faint) = (theme.muted_foreground, theme.muted_foreground.opacity(0.65));
        h_flex()
            .w_full()
            .h(PROJECT_HEIGHT)
            .mt_1()
            .gap(px(6.))
            .pl(px(7.))
            .pr_1()
            .child(chevron(
                format!("fold:{}", self.key),
                self.folded,
                faint,
                self.on_toggle,
            ))
            .child(
                div()
                    .flex_none()
                    .w(px(20.))
                    .h(px(18.))
                    .rounded(px(5.))
                    .bg(colour.opacity(0.18))
                    .text_color(colour)
                    .text_role(TextRole::Meta, cx)
                    .font_weight(FontWeight::BOLD)
                    .flex()
                    .items_center()
                    .justify_center()
                    .child(badge_letters(&self.label)),
            )
            .child(
                div()
                    .flex_1()
                    .min_w_0()
                    .text_role(TextRole::Meta, cx)
                    .font_weight(FontWeight::SEMIBOLD)
                    .text_color(muted)
                    .truncate()
                    .child(self.label.to_uppercase()),
            )
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
                        .xsmall()
                        .text_color(muted),
                    )
            }))
            .child(count(self.count, faint, cx))
            .child(
                Button::new(SharedString::from(format!("new:{}", self.key)))
                    .named("New session in this project")
                    .ghost()
                    .xsmall()
                    .icon(IconName::Plus)
                    .tooltip("New session in this project")
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
        let theme = cx.theme();
        let (muted, faint) = (theme.muted_foreground, theme.muted_foreground.opacity(0.65));
        h_flex()
            .w_full()
            .h(PROJECT_HEIGHT)
            .pl(px(7.))
            .pr(px(14.))
            .gap(px(6.))
            .child(chevron(
                format!("fold:section:{}", self.key),
                self.folded,
                faint,
                self.on_toggle,
            ))
            .child(
                div()
                    .flex_1()
                    .text_role(TextRole::Meta, cx)
                    .font_weight(FontWeight::SEMIBOLD)
                    .text_color(muted)
                    .child(self.label),
            )
            .child(count(self.count, faint, cx))
    }
}

/// A header's fold chevron, the mock's 14 px faint one.
fn chevron(id: String, folded: bool, faint: Hsla, on_toggle: OnToggle) -> Button {
    Button::new(SharedString::from(id))
        .named(if folded {
            "Expand group"
        } else {
            "Collapse group"
        })
        .ghost()
        .with_size(px(18.))
        .size(px(20.))
        .text_color(faint)
        .icon(if folded {
            IconName::ChevronRight
        } else {
            IconName::ChevronDown
        })
        .on_click(on_toggle)
}

/// A header's count, at its right end.
fn count(count: usize, faint: Hsla, cx: &App) -> Div {
    div()
        .flex_none()
        .text_role(TextRole::Meta, cx)
        .font_weight(FontWeight::MEDIUM)
        .text_color(faint)
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
