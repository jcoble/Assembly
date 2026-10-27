//! `ExpandRow` owns the one way a row or card reveals more: it grows in place (the design system's Motion, Jesse
//! 2026-10-27, look items 13 and 16). A click on the title grows the row to fit its body over 160 ms, with the rows
//! below sliding down, while the body fades in from 40 ms to 160 ms; the title stays where it was. The open row is a
//! lighter band with 8 px corners. A second click runs it in reverse, and the body is dropped once it has shrunk. A
//! row open at its first render is drawn open, without motion. With Reduce Motion on, the kit's animations jump to
//! the end, so rows switch open and shut at once. New: the old app used accordions.

use std::time::Duration;

use gpui_kit::base::ElementExt as _;
use gpui_kit::component::{ActiveTheme as _, v_flex};
use gpui_kit::prelude::FluentBuilder as _;
use gpui_kit::*;

/// The design system's grow time: the row's height takes this long.
pub const GROW: Duration = Duration::from_millis(160);
/// When, into the grow, the body starts fading in. It's fully in at `GROW`, 120 ms later.
const FADE_START: Duration = Duration::from_millis(40);
/// The open row's band corners (the design system's `radius-sm`).
const BAND_RADIUS: Pixels = px(8.);

type Build = Box<dyn Fn(&mut Window, &mut App) -> AnyElement>;

pub struct ExpandRow {
    title: Build,
    body: Build,
    open: bool,
    /// The body's full height, from its last layout.
    body_height: Pixels,
    /// Counts the clicks, so each one plays its motion from the start.
    turns: usize,
    /// Held while a click's motion plays; the body is clipped to the growing height until it ends.
    moving: Option<Task<()>>,
}

impl ExpandRow {
    pub fn new(
        open: bool,
        title: impl Fn(&mut Window, &mut App) -> AnyElement + 'static,
        body: impl Fn(&mut Window, &mut App) -> AnyElement + 'static,
    ) -> Self {
        Self {
            title: Box::new(title),
            body: Box::new(body),
            open,
            body_height: px(0.),
            turns: 0,
            moving: None,
        }
    }

    pub fn is_open(&self) -> bool {
        self.open
    }

    /// Opens a shut row or shuts an open one, with the motion.
    pub fn toggle(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        self.open = !self.open;
        self.turns += 1;
        self.moving = Some(cx.spawn_in(window, async move |row, cx| {
            cx.background_executor().timer(GROW).await;
            _ = row.update(cx, |row, cx| {
                row.moving = None;
                cx.notify();
            });
        }));
        cx.notify();
    }
}

impl Render for ExpandRow {
    fn render(&mut self, window: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        let shown = self.open || self.moving.is_some();
        let this = cx.entity().downgrade();
        let body = shown.then(|| {
            div()
                .relative()
                .flex_none()
                .on_prepaint(move |bounds, _, cx| {
                    _ = this.update(cx, |row, cx| {
                        if row.body_height != bounds.size.height {
                            row.body_height = bounds.size.height;
                            cx.notify();
                        }
                    });
                })
                .child((self.body)(window, cx))
        });
        let (opening, full) = (self.open, self.body_height);
        let easing = cx.theme().motion_tokens().easing_enter.clone();
        v_flex()
            .when(shown, |row| {
                row.bg(cx.theme().secondary).rounded(BAND_RADIUS)
            })
            .child(
                div()
                    .id("expand-row-title")
                    .cursor_pointer()
                    .on_click(cx.listener(|row, _, window, cx| row.toggle(window, cx)))
                    .child((self.title)(window, cx)),
            )
            .when_some(body, |row, body| match self.moving {
                None => row.child(body),
                Some(_) => row.child(
                    div()
                        .overflow_hidden()
                        .child(body)
                        .with_animation(
                            ("expand-row", self.turns),
                            Animation::new(GROW)
                                .with_easing(move |t| easing.sample(t).clamp(0., 1.)),
                            move |clip, t| {
                                // How far open the row is, so shrinking is growing run backwards.
                                let open = if opening { t } else { 1. - t };
                                let fade = (open * GROW.as_secs_f32() - FADE_START.as_secs_f32())
                                    / (GROW - FADE_START).as_secs_f32();
                                clip.h(full * open).opacity(fade.clamp(0., 1.))
                            },
                        )
                        .into_any_element(),
                ),
            })
    }
}
