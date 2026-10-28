//! `ExpandRow` owns the one way a row or card reveals more: it grows in place (the design system's Motion, Jesse
//! 2026-10-27, look items 13 and 16: "a generic drop down that makes the row grow, and a custom component beneath it
//! that grows with it"). A click on the title grows the row to fit its body over 160 ms, with the rows below sliding
//! down, while the body fades in with it from 40 ms in; the title stays where it was. The open row is a lighter band,
//! 8 px corners unless the owner draws its own. A second click runs it in reverse, and the body is dropped once it has
//! shrunk. A row open at its first render is drawn open, without motion. A click during the motion turns the row back
//! from where it is, taking as long as the way back. A body whose height changes while the row is open grows or
//! shrinks to its new height the same way. The owner hears each click (`Opened`, `Closed`) and the row's height on
//! every frame it changes (`Height`, for a list that measures its rows), and can open or shut it itself
//! (`set_open`). With Reduce Motion on, rows switch open and shut at once. New: the old app used accordions.

use std::time::{Duration, Instant};

use gpui_kit::base::ElementExt as _;
use gpui_kit::component::{ActiveTheme as _, v_flex};
use gpui_kit::prelude::FluentBuilder as _;
use gpui_kit::*;

use crate::core::theme::grow::{GROW, ease, faded};

/// The open row's band corners (the design system's `radius-sm`).
const BAND_RADIUS: Pixels = px(8.);

/// Builds the title from how far the row has grown, 0 shut to 1 open (for a chevron that turns with it).
type Title = Box<dyn Fn(f32, &mut Window, &mut App) -> AnyElement>;
type Body = Box<dyn Fn(&mut Window, &mut App) -> AnyElement>;
/// Draws the band on the row's root, from how far it has grown.
type Band = Box<dyn Fn(Div, f32, &App) -> Div>;

#[derive(Clone, Debug, PartialEq)]
pub enum ExpandRowEvent {
    /// A click opened the row.
    Opened,
    /// A click shut the row.
    Closed,
    /// The row's drawn height changed: on every frame while it moves.
    Height(Pixels),
}

impl EventEmitter<ExpandRowEvent> for ExpandRow {}

pub struct ExpandRow {
    title: Title,
    build_body: Body,
    band: Band,
    open: bool,
    /// The body's full size, from its last layout; `None` until it's first drawn.
    body: Option<Size<Pixels>>,
    /// The row's height as last drawn, as the owner last heard it.
    height: Pixels,
    /// Set while the row opens or shuts; the body is clipped to the growing height until it ends.
    moving: Option<Motion>,
    /// Set while the body moves to a new height; it runs alongside an opening or shutting.
    resizing: Option<Resize>,
}

/// One opening or shutting, from how open the row was when it started.
struct Motion {
    from: f32,
    started: Instant,
}

/// A body moving from the height it was drawn at to its new one.
struct Resize {
    from: Pixels,
    started: Instant,
}

impl ExpandRow {
    pub fn new(
        open: bool,
        title: impl Fn(f32, &mut Window, &mut App) -> AnyElement + 'static,
        body: impl Fn(&mut Window, &mut App) -> AnyElement + 'static,
    ) -> Self {
        Self {
            title: Box::new(title),
            build_body: Box::new(body),
            band: Box::new(|row, grown, cx| {
                row.when(grown > 0., |row| {
                    row.bg(cx.theme().accent).rounded(BAND_RADIUS)
                })
            }),
            open,
            body: None,
            height: px(0.),
            moving: None,
            resizing: None,
        }
    }

    /// The owner draws the band instead, on the row's root, from how far it has grown.
    pub fn band(mut self, band: impl Fn(Div, f32, &App) -> Div + 'static) -> Self {
        self.band = Box::new(band);
        self
    }

    /// What the row reveals from now on. It needs no notify: the owner calls this from its own render, and the row
    /// draws after it.
    pub fn set_body(&mut self, body: impl Fn(&mut Window, &mut App) -> AnyElement + 'static) {
        self.build_body = Box::new(body);
    }

    /// Its title from now on, drawn from how far it has grown; called from the owner's render like `set_body`.
    pub fn set_title(
        &mut self,
        title: impl Fn(f32, &mut Window, &mut App) -> AnyElement + 'static,
    ) {
        self.title = Box::new(title);
    }

    pub fn is_open(&self) -> bool {
        self.open
    }

    /// The row's height as last drawn.
    #[cfg(test)]
    pub(super) fn drawn_height(&self) -> Pixels {
        self.height
    }

    /// How open the row is at `now`, from 0 (shut) to 1 (open), along the motion's time rather than its curve.
    pub(super) fn openness(&self, now: Instant) -> f32 {
        let to = self.target();
        match &self.moving {
            None => to,
            Some(motion) => {
                let needs = travel(motion.from, to);
                motion.from + (to - motion.from) * done(motion.started, needs, now)
            }
        }
    }

    fn target(&self) -> f32 {
        if self.open { 1. } else { 0. }
    }

    /// Opens or shuts the row, with the motion when `animate`, starting from where it is now. The owner hears
    /// nothing: only a click tells it.
    pub fn set_open(&mut self, open: bool, animate: bool, cx: &mut Context<Self>) {
        if open == self.open {
            return;
        }
        let now = cx.background_executor().now();
        let from = self.openness(now);
        self.open = open;
        self.moving = (animate && !cx.reduce_motion()).then_some(Motion { from, started: now });
        cx.notify();
    }

    fn click(&mut self, cx: &mut Context<Self>) {
        self.set_open(!self.open, true, cx);
        cx.emit(if self.open {
            ExpandRowEvent::Opened
        } else {
            ExpandRowEvent::Closed
        });
    }

    /// The body's height as drawn at `now`, partway through a resize.
    fn resized_height(&self, now: Instant) -> Pixels {
        let full = self.body.map_or(px(0.), |body| body.height);
        match &self.resizing {
            None => full,
            Some(resize) => {
                let eased = ease(done(resize.started, GROW, now));
                resize.from + (full - resize.from) * eased
            }
        }
    }

    /// Takes the body's size from its layout. A body that was drawn before moves to a new height from where it is
    /// drawn now, unless its width changed: text wrapping again as a column is dragged follows the drag at once.
    fn measured(&mut self, size: Size<Pixels>, cx: &mut Context<Self>) {
        let Some(last) = self.body else {
            self.body = Some(size);
            cx.notify();
            return;
        };
        if size == last {
            return;
        }
        let now = cx.background_executor().now();
        let from = self.resized_height(now);
        self.body = Some(size);
        self.resizing = (size.width == last.width && !cx.reduce_motion())
            .then_some(Resize { from, started: now });
        cx.notify();
    }
}

/// How long the row takes to get from one openness to another: the whole grow time for the whole way.
fn travel(from: f32, to: f32) -> Duration {
    GROW.mul_f32((to - from).abs())
}

/// How far through a motion that needs `needs` it is at `now`, from 0 to 1.
fn done(started: Instant, needs: Duration, now: Instant) -> f32 {
    if needs.is_zero() {
        return 1.;
    }
    let took = now.saturating_duration_since(started).as_secs_f32();
    (took / needs.as_secs_f32()).min(1.)
}

impl Render for ExpandRow {
    fn render(&mut self, window: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        let now = cx.background_executor().now();
        // A motion whose time is up ends on the frame that draws its end, and Reduce Motion ends one at once.
        if cx.reduce_motion() {
            self.moving = None;
            self.resizing = None;
        }
        let to = self.target();
        self.moving
            .take_if(|motion| done(motion.started, travel(motion.from, to), now) >= 1.);
        self.resizing
            .take_if(|resize| done(resize.started, GROW, now) >= 1.);
        if self.moving.is_some() || self.resizing.is_some() {
            window.request_animation_frame();
        }
        let grown = ease(self.openness(now)).clamp(0., 1.);
        let shown = self.open || self.moving.is_some();
        let measure = cx.entity().downgrade();
        let body = shown.then(|| {
            div()
                .relative()
                .flex_none()
                .on_prepaint(move |bounds, _, cx| {
                    _ = measure.update(cx, |row, cx| row.measured(bounds.size, cx));
                })
                .child((self.build_body)(window, cx))
        });
        // Clipped once measured, still too, so a body that grew doesn't show at its full height for a frame before it
        // grows into it. The height is the body's, as it resizes, times how far the row has grown, so either can
        // change while the other moves without a jump.
        let clip = (self.moving.is_some() || self.resizing.is_some() || self.body.is_some())
            .then(|| (self.resized_height(now) * grown, faded(grown)));
        let heard = cx.entity().downgrade();
        let root = v_flex().on_prepaint(move |bounds, _, cx| {
            _ = heard.update(cx, |row, cx| {
                if row.height != bounds.size.height {
                    row.height = bounds.size.height;
                    cx.emit(ExpandRowEvent::Height(row.height));
                }
            });
        });
        (self.band)(root, grown, cx)
            .child(
                div()
                    .id("expand-row-title")
                    .cursor_pointer()
                    .on_click(cx.listener(|row, _, _, cx| row.click(cx)))
                    .child((self.title)(grown, window, cx)),
            )
            .when_some(body, |row, body| match clip {
                None => row.child(body),
                Some((height, opacity)) => row.child(
                    div()
                        .overflow_hidden()
                        .h(height)
                        .opacity(opacity)
                        .child(body),
                ),
            })
    }
}
