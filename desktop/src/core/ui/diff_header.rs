//! The header above each file in a stacked diff, shared by Changes and a pull request: its icon, folder and name, its
//! counts and whatever actions the view gives it. Replaces MultiFileDiff.svelte's section header.
use super::file_icon::file_icon;
use gpui_kit::assets::IconName;
use gpui_kit::component::button::{Button, ButtonVariants as _};
use gpui_kit::component::diff::DiffFile;
use gpui_kit::component::{ActiveTheme as _, Sizable as _, h_flex};
use gpui_kit::prelude::FluentBuilder as _;
use gpui_kit::*;

/// A path's folder, with its slash, and its name.
pub fn path_parts(path: &str) -> (&str, &str) {
    match path.rfind('/') {
        Some(slash) => path.split_at(slash + 1),
        None => ("", path),
    }
}
/// The eye that folds a file away once it has been read.
pub fn viewed_button(
    path: &str,
    viewed: bool,
    on_toggle: impl Fn(&mut Window, &mut App) + 'static,
) -> Button {
    Button::new(SharedString::from(format!("viewed:{path}")))
        .ghost()
        .small()
        .icon(if viewed {
            IconName::EyeOff
        } else {
            IconName::Eye
        })
        .tooltip(if viewed {
            "Mark as not viewed"
        } else {
            "Mark as viewed"
        })
        .on_click(move |_, window, cx| on_toggle(window, cx))
}
/// `tag` names the part of the file these lines are, when a file shows two.
pub fn file_heading(
    file: &DiffFile,
    tag: Option<&'static str>,
    actions: impl IntoElement,
    cx: &App,
) -> Div {
    let theme = cx.theme();
    let (muted, added, removed) = (theme.muted_foreground, theme.green, theme.red);
    let path = file.path().to_string();
    let (folder, name) = path_parts(&path);
    let moved_from = file
        .original_path()
        .zip(file.modified_path())
        .and_then(|(old, new)| (old != new).then(|| old.to_string()));
    h_flex()
        .w_full()
        .min_w_0()
        .gap_2()
        .text_sm()
        .child(file_icon(format!("heading-icon:{path}"), &path, cx))
        .child(
            h_flex()
                .flex_1()
                .min_w_0()
                .child(
                    div()
                        .min_w_0()
                        .truncate()
                        .child(div().text_color(muted).child(match moved_from {
                            Some(from) => format!("{from} → {folder}"),
                            None => folder.to_string(),
                        })),
                )
                .child(div().flex_none().child(name.to_string()))
                .when_some(tag, |line, tag| line.child(part_tag(tag, cx))),
        )
        .when(!file.is_binary(), |line| {
            line.child(
                h_flex()
                    .flex_none()
                    .gap_1()
                    .text_xs()
                    .child(
                        div()
                            .text_color(added)
                            .child(format!("+{}", file.additions())),
                    )
                    .child(
                        div()
                            .text_color(removed)
                            .child(format!("-{}", file.deletions())),
                    ),
            )
        })
        .child(actions)
}
/// The label on the second part of a file. Its folder, name and counts are on the part above it.
pub fn part_heading(tag: &'static str, cx: &App) -> Div {
    h_flex().w_full().min_w_0().child(part_tag(tag, cx))
}
fn part_tag(tag: &'static str, cx: &App) -> Div {
    div()
        .flex_none()
        .ml_2()
        .text_xs()
        .text_color(cx.theme().muted_foreground)
        .child(tag)
}

#[cfg(test)]
#[path = "diff_header_tests.rs"]
mod tests;
