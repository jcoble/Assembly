//! `ProjectHeader` heads one project in the rail: a small coloured badge with two letters of its name, the label the
//! backend gave it in small capitals, and a New session button that opens the new-session form with the project's
//! folder and its first session's machine filled in.

use gpui_kit::assets::IconName;
use gpui_kit::component::button::{Button, ButtonVariants as _};
use gpui_kit::component::{ActiveTheme as _, Sizable as _, h_flex};
use gpui_kit::*;

/// A project's header line: room for its + button.
pub const PROJECT_HEIGHT: Pixels = px(30.);

type OnNew = Box<dyn Fn(&ClickEvent, &mut Window, &mut App)>;

#[derive(IntoElement)]
pub struct ProjectHeader {
    label: String,
    repo_url: String,
    on_new: OnNew,
}

impl ProjectHeader {
    pub fn new(
        label: String,
        repo_url: String,
        on_new: impl Fn(&ClickEvent, &mut Window, &mut App) + 'static,
    ) -> Self {
        ProjectHeader {
            label,
            repo_url,
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
        h_flex()
            .w_full()
            .h(PROJECT_HEIGHT)
            .mt_1()
            .gap(px(6.))
            .pl(px(14.))
            .pr_1()
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
                    .text_xs()
                    .font_weight(FontWeight::SEMIBOLD)
                    .text_color(theme.muted_foreground)
                    .truncate()
                    .child(self.label.to_uppercase()),
            )
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
