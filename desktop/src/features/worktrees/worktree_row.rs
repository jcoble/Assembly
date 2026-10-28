//! One worktree's row: the title line (chevron, branch, marks) and the body it grows to show (folder, head commit,
//! when it was made). Draws only; every word comes from `Worktree`. Replaces WorktreeRow.svelte and
//! WorktreeDetail.svelte's facts.

use std::f32::consts::FRAC_PI_2;

use gpui_kit::component::{ActiveTheme as _, Icon, IconName, Sizable as _, h_flex, v_flex};
use gpui_kit::*;

use super::worktree::{Tone, Worktree};

/// The title line, with the chevron turned as far as the row has grown.
pub fn title(worktree: &Worktree, grown: f32, cx: &App) -> Div {
    let theme = cx.theme();
    let marks = worktree.marks().into_iter().map(|mark| {
        let (fg, bg) = match mark.tone {
            Tone::Quiet => (theme.muted_foreground, theme.muted),
            Tone::Warn => (theme.warning, theme.warning.opacity(0.15)),
            Tone::Danger => (theme.danger, theme.danger.opacity(0.15)),
        };
        div()
            .flex_none()
            .px_1p5()
            .rounded(theme.radius)
            .text_xs()
            .text_color(fg)
            .bg(bg)
            .child(mark.text)
    });
    h_flex()
        .h(px(28.))
        .px_2()
        .gap_2()
        .text_sm()
        .child(
            Icon::new(IconName::ChevronRight)
                .xsmall()
                .rotate(radians(grown * FRAC_PI_2)),
        )
        .child(div().flex_1().min_w_0().truncate().child(worktree.name()))
        .child(h_flex().flex_none().gap_1().children(marks))
}

/// What an open row shows.
pub fn body(worktree: &Worktree, cx: &App) -> Div {
    let muted = cx.theme().muted_foreground;
    let fact = |label: &'static str, value: String| {
        h_flex()
            .gap_2()
            .child(div().flex_none().w(px(56.)).text_color(muted).child(label))
            .child(div().min_w_0().truncate().child(value))
    };
    v_flex()
        .px_2()
        .pb_2()
        .gap_1()
        .text_xs()
        .child(fact("Folder", worktree.path.clone()))
        .children(worktree.head_line().map(|head| fact("Head", head)))
        .children(worktree.committed().map(|at| fact("Made", at)))
}
