//! Engine-started sessions use canonical owner-qualified edges. Provider-native
//! cards remain on the existing persisted-row path and are not synthesized here.
use crate::core::engine::{EngineFeed, SessionKey};
use crate::core::state::preferences::Preferences;
use crate::core::theme::text::TextRole;
use gpui_kit::component::{ActiveTheme as _, h_flex, v_flex};
use gpui_kit::*;
use std::rc::Rc;

/// The owning pane handles the canonical source-qualified open request.
pub type OnOpen = Rc<dyn Fn(SessionKey, &mut Window, &mut App)>;
#[derive(IntoElement)]
pub struct StartedSessions {
    pub parent: SessionKey,
    pub feed: Entity<EngineFeed>,
    pub on_open: OnOpen,
}
impl RenderOnce for StartedSessions {
    fn render(self, _: &mut Window, cx: &mut App) -> impl IntoElement {
        let children = self.feed.read(cx).children_of(&self.parent);
        let theme = cx.theme();
        let (size, height) =
            Preferences::read(cx, |appearance| appearance.ui_text(TextRole::Second));
        let mut column = v_flex().gap_1();
        if children.is_empty() {
            return column;
        }
        column = column.child(
            div()
                .text_color(theme.muted_foreground)
                .text_size(size)
                .line_height(height)
                .child("Started sessions"),
        );
        for child in children {
            let key = child.session_key();
            let label = child
                .title
                .as_deref()
                .filter(|title| !title.is_empty())
                .unwrap_or(if child.folder.is_empty() {
                    &child.key.id
                } else {
                    &child.folder
                })
                .to_string();
            let on_open = self.on_open.clone();
            column = column.child(
                h_flex()
                    .id(SharedString::from(format!(
                        "started:{}:{}",
                        key.machine, key.id
                    )))
                    .min_w_0()
                    .gap_2()
                    .py_1()
                    .cursor_pointer()
                    .child(
                        div()
                            .flex_1()
                            .min_w_0()
                            .truncate()
                            .text_size(size)
                            .line_height(height)
                            .child(label),
                    )
                    .child(
                        div()
                            .flex_none()
                            .text_size(size)
                            .text_color(theme.muted_foreground)
                            .child(child.state),
                    )
                    .on_click(move |_, window, cx| on_open(key.clone(), window, cx)),
            );
        }
        column
    }
}
