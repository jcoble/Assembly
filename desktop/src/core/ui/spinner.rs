//! The spinner for running work, used as the kit's is: `Spinner::new().xsmall().color(color)`. It's the kit's mark, turned
//! in steps and ten times a second instead of on every display frame.
//!
//! An animation redraws the whole panel holding it each time it moves, so the kit's spinner, which moves every frame,
//! holds up a core for as long as it's on screen. Running work can stay on screen for hours. Ten ticks a second is what
//! the mark's eight spokes need to read as turning, and every spinner sits at the same place in its turn.

use std::time::Duration;

use gpui_kit::assets::IconName;
use gpui_kit::component::{Icon, Sizable, Size};
use gpui_kit::prelude::FluentBuilder as _;
use gpui_kit::*;

const TICKS_PER_SECOND: f32 = 10.;
/// The mark has this many spokes, so a turn of one looks like it moved on by one.
const SPOKES: f32 = 8.;
/// One full turn, a spoke at each tick.
const TURN: Duration = Duration::from_millis(800);

#[derive(IntoElement)]
pub struct Spinner {
    size: Size,
    color: Option<Hsla>,
}

impl Spinner {
    pub fn new() -> Self {
        Spinner { size: Size::Medium, color: None }
    }

    pub fn color(mut self, color: Hsla) -> Self {
        self.color = Some(color);
        self
    }
}

impl Sizable for Spinner {
    fn with_size(mut self, size: impl Into<Size>) -> Self {
        self.size = size.into();
        self
    }
}

/// How far round the mark sits, in whole spokes.
fn stepped(turned: f32) -> f32 {
    (turned * SPOKES).floor() / SPOKES
}

impl RenderOnce for Spinner {
    fn render(self, _window: &mut Window, _cx: &mut App) -> impl IntoElement {
        Icon::new(IconName::Loader)
            .with_size(self.size)
            .when_some(self.color, |icon, color| icon.text_color(color))
            .with_animation(
                "spinner",
                Animation::new(TURN).repeat_synced().with_max_fps(TICKS_PER_SECOND),
                |icon, turned| icon.transform(Transformation::rotate(percentage(stepped(turned)))),
            )
    }
}

#[cfg(test)]
#[path = "spinner_tests.rs"]
mod tests;
