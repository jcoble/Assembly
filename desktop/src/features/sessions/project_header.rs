//! The rail's section and project headers: fold chevron, backend count, project machines and New session.

use gpui_kit::assets::IconName;
use gpui_kit::component::button::{Button, ButtonVariants as _};
use gpui_kit::component::{ActiveTheme as _, Sizable as _, h_flex};
use gpui_kit::*;

/// A project's header line: room for its + button.
pub const PROJECT_HEIGHT: Pixels = px(30.);

type OnNew = Box<dyn Fn(&ClickEvent, &mut Window, &mut App)>;
type OnToggle = Box<dyn Fn(&ClickEvent, &mut Window, &mut App)>;

#[derive(IntoElement)]
pub struct ProjectHeader {
    label: String,
    repo_url: String,
    machines: Vec<String>,
    count: usize,
    folded: bool,
    on_toggle: OnToggle,
    on_new: OnNew,
}

impl ProjectHeader {
    pub fn new(
        label: String,
        repo_url: String,
        machines: Vec<String>,
        count: usize,
        folded: bool,
        on_toggle: impl Fn(&ClickEvent, &mut Window, &mut App) + 'static,
        on_new: impl Fn(&ClickEvent, &mut Window, &mut App) + 'static,
    ) -> Self {
        ProjectHeader {
            label,
            repo_url,
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
                format!("fold:project:{}", self.repo_url),
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
                    .text_size(px(9.5))
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
                    .text_size(px(11.5))
                    .font_weight(FontWeight::SEMIBOLD)
                    .text_color(muted)
                    .truncate()
                    .child(self.label.to_uppercase()),
            )
            // The mock's machine chips: 18 px round, on the pill fill.
            .children(self.machines.into_iter().map(|machine| {
                h_flex()
                    .flex_none()
                    .h(px(18.))
                    .px(px(7.))
                    .rounded_full()
                    .bg(theme.secondary)
                    .text_size(px(10.5))
                    .font_weight(FontWeight::MEDIUM)
                    .text_color(muted)
                    .child(if machine == "local" {
                        "This Mac".to_owned()
                    } else {
                        machine
                    })
            }))
            .child(count(self.count, faint))
            .child(
                Button::new(SharedString::from(format!("project:{}", self.repo_url)))
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
                    .text_size(px(11.5))
                    .font_weight(FontWeight::SEMIBOLD)
                    .text_color(muted)
                    .child(self.label),
            )
            .child(count(self.count, faint))
    }
}

/// A header's fold chevron, the mock's 14 px faint one.
fn chevron(id: String, folded: bool, faint: Hsla, on_toggle: OnToggle) -> Button {
    Button::new(SharedString::from(id))
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
fn count(count: usize, faint: Hsla) -> Div {
    div()
        .flex_none()
        .text_size(px(11.5))
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
