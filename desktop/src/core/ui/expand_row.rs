//! `ExpandRow` owns the one way a row or card reveals more: it grows in place (the design system's Motion, Jesse
//! 2026-10-27, look items 13 and 16). A click on the title grows the row to fit its body over 160 ms, with the rows
//! below sliding down, while the body fades in from 40 ms to 160 ms; the title stays where it was. The open row is a
//! lighter band with 8 px corners. A second click runs it in reverse, and the body is dropped once it has shrunk. A
//! row open at its first render is drawn open, without motion. A click during the motion turns the row back from
//! where it is, taking as long as the way back. With Reduce Motion on, the kit's animations jump to
//! the end, so rows switch open and shut at once. New: the old app used accordions.

use std::time::{Duration, Instant};

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
    /// Set while a click's motion plays; the body is clipped to the growing height until it ends.
    moving: Option<Motion>,
}

/// One click's motion, from how open the row was when it was clicked.
struct Motion {
    from: f32,
    started: Instant,
    /// Ends the motion once the row has got where it's going.
    _done: Task<()>,
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

    /// How open the row is at `now`, from 0 (shut) to 1 (open), along the motion's time rather than its curve.
    pub(super) fn openness(&self, now: Instant) -> f32 {
        let to = self.target();
        match &self.moving {
            None => to,
            Some(motion) => {
                let took = now.saturating_duration_since(motion.started).as_secs_f32();
                let needs = travel(motion.from, to).as_secs_f32();
                let done = if needs > 0. {
                    (took / needs).min(1.)
                } else {
                    1.
                };
                motion.from + (to - motion.from) * done
            }
        }
    }

    fn target(&self) -> f32 {
        if self.open { 1. } else { 0. }
    }

    /// Opens a shut row or shuts an open one, with the motion, starting from where the row is now.
    pub fn toggle(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        let now = cx.background_executor().now();
        let from = self.openness(now);
        self.open = !self.open;
        self.turns += 1;
        let needs = travel(from, self.target());
        let _done = cx.spawn_in(window, async move |row, cx| {
            cx.background_executor().timer(needs).await;
            _ = row.update(cx, |row, cx| {
                row.moving = None;
                cx.notify();
            });
        });
        self.moving = Some(Motion {
            from,
            started: now,
            _done,
        });
        cx.notify();
    }
}

/// How long the row takes to get from one openness to another: the whole grow time for the whole way.
fn travel(from: f32, to: f32) -> Duration {
    GROW.mul_f32((to - from).abs())
}

/// The body's opacity at an openness: 0 until 40 ms into the grow, then up to 1 by 160 ms.
pub(super) fn fade(openness: f32) -> f32 {
    let at = openness * GROW.as_secs_f32();
    ((at - FADE_START.as_secs_f32()) / (GROW - FADE_START).as_secs_f32()).clamp(0., 1.)
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
        let (to, full) = (self.target(), self.body_height);
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
            .when_some(body, |row, body| match &self.moving {
                None => row.child(body),
                Some(motion) => {
                    let from = motion.from;
                    row.child(
                        div()
                            .overflow_hidden()
                            .child(body)
                            .with_animation(
                                ("expand-row", self.turns),
                                // Linear in time: the curve shapes the height only, and the fade keeps its timing.
                                Animation::new(travel(from, to).max(Duration::from_millis(1))),
                                move |clip, t| {
                                    let open = from + (to - from) * t;
                                    clip.h(full * easing.sample(open).clamp(0., 1.))
                                        .opacity(fade(open))
                                },
                            )
                            .into_any_element(),
                    )
                }
            })
    }
}
