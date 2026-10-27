//! `WindowRoot` owns the window's content view (the kit's `Root` wraps it, for dialogs and sheets), and draws
//! `NativeViews::placer` once over it, so native views are placed once a frame after everything has laid out.

use gpui_kit::*;

use super::native_views::views::NativeViews;

pub struct WindowRoot {
    content: AnyView,
}

impl WindowRoot {
    pub fn new(content: impl Into<AnyView>) -> Self {
        Self {
            content: content.into(),
        }
    }
}

impl Render for WindowRoot {
    fn render(&mut self, _window: &mut Window, _cx: &mut Context<Self>) -> impl IntoElement {
        div()
            .size_full()
            .child(self.content.clone())
            .child(NativeViews::placer())
    }
}
