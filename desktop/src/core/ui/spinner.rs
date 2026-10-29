//! `RingSpinner` is the small spinner beside running work: a faint ring with an arc turning on it, in one colour. It
//! turns only while it's drawn: its animation asks for the next frame from a frame that painted it, so once it's
//! scrolled out of view or gone, the window stops repainting for it. There's no timer behind it.

use std::time::Duration;

use gpui_kit::assets::IconName;
use gpui_kit::component::Icon;
use gpui_kit::*;

/// The spinner's size. The arc (Lucide's loader-circle) is a 1 px stroke on a 9 px circle at this size, so the ring
/// under it is that circle: inset 1 px, 1 px wide.
const SIZE: Pixels = px(12.);

#[derive(IntoElement)]
pub struct RingSpinner {
    color: Hsla,
}

impl RingSpinner {
    pub fn new(color: Hsla) -> Self {
        RingSpinner { color }
    }
}

impl RenderOnce for RingSpinner {
    fn render(self, _window: &mut Window, _cx: &mut App) -> impl IntoElement {
        div()
            .flex_none()
            .relative()
            .size(SIZE)
            .child(
                div()
                    .absolute()
                    .top(px(1.))
                    .left(px(1.))
                    .size(SIZE - px(2.))
                    .rounded_full()
                    .border_1()
                    .border_color(self.color.opacity(0.25)),
            )
            .child(
                Icon::new(IconName::LoaderCircle)
                    .size(SIZE)
                    .text_color(self.color)
                    .with_animation(
                        "ring-spinner",
                        Animation::new(Duration::from_millis(800)).repeat(),
                        |arc, turned| arc.transform(Transformation::rotate(percentage(turned))),
                    ),
            )
    }
}
