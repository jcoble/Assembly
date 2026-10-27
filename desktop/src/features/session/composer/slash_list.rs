//! `SlashList` is the "/" list above the composer: the agent's commands matching the command word being typed, the
//! highlighted one lit, each with its description. A click picks one; the keys are the composer's (composer_config.rs),
//! since the draft keeps focus. Drawn on the popover ground, as the kit's menus are. Replaces the slash list in
//! Composer.svelte.

use std::rc::Rc;

use gpui_kit::component::{ActiveTheme as _, h_flex, v_flex};
use gpui_kit::prelude::FluentBuilder as _;
use gpui_kit::*;

/// The design system's `radius-md`, a popover's corners, and `radius-sm` for its rows.
const RADIUS: Pixels = px(12.);
const ROW_RADIUS: Pixels = px(8.);
/// The most it grows to before it scrolls.
const MAX_HEIGHT: Pixels = px(280.);

/// Picks the command at a row.
pub type OnPick = Rc<dyn Fn(usize, &mut Window, &mut App)>;

#[derive(IntoElement)]
pub struct SlashList {
    /// (name as typed, description) for each command shown.
    pub commands: Vec<(SharedString, SharedString)>,
    pub selected: usize,
    pub on_pick: OnPick,
}

impl RenderOnce for SlashList {
    fn render(self, _window: &mut Window, cx: &mut App) -> impl IntoElement {
        let theme = cx.theme();
        let rows = self
            .commands
            .into_iter()
            .enumerate()
            .map(|(at, (name, description))| {
                let on_pick = self.on_pick.clone();
                h_flex()
                    .id(("slash", at))
                    .gap_2p5()
                    .px_2p5()
                    .py_1p5()
                    .rounded(ROW_RADIUS)
                    .text_sm()
                    .cursor_pointer()
                    .when(at == self.selected, |row| row.bg(theme.secondary_hover))
                    .hover(|row| row.bg(theme.secondary_hover))
                    .on_click(move |_, window, cx| on_pick(at, window, cx))
                    .child(
                        div()
                            .flex_none()
                            .font_weight(FontWeight::SEMIBOLD)
                            .text_color(theme.foreground)
                            .child(name),
                    )
                    .child(
                        div()
                            .min_w_0()
                            .truncate()
                            .text_color(theme.muted_foreground)
                            .child(description),
                    )
            });
        v_flex()
            .id("composer:slash")
            .max_h(MAX_HEIGHT)
            .overflow_y_scroll()
            .p_1()
            .rounded(RADIUS)
            .bg(theme.popover)
            .border_1()
            .border_color(theme.border)
            .occlude()
            .children(rows)
    }
}
