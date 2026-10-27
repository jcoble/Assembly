//! `MessageMeta` is the line under a prompt or a finished reply: its copy button and when it was written ("Thursday
//! 12:02 PM" this week, "Oct 17, 12:02 PM" before). Replaces chat/MessageMeta.svelte and fold.ts's messageTime.

use gpui_kit::component::button::{Button, ButtonVariants as _};
use gpui_kit::component::{ActiveTheme as _, h_flex};
use gpui_kit::*;

use crate::features::session::model::turn_line::message_time;

#[derive(IntoElement)]
pub struct MessageMeta {
    /// The item's key, for the button's id.
    pub key: String,
    /// What the copy button copies: the prompt, or the reply as markdown.
    pub copy: SharedString,
    pub at: i64,
    pub now: i64,
}

impl RenderOnce for MessageMeta {
    fn render(self, _window: &mut Window, cx: &mut App) -> impl IntoElement {
        let copy = self.copy.clone();
        h_flex()
            .gap_2()
            .text_xs()
            .text_color(cx.theme().muted_foreground)
            .child(
                Button::new(SharedString::from(format!("{}:copy", self.key)))
                    .ghost()
                    .compact()
                    .label("Copy")
                    .on_click(move |_, _, cx| {
                        cx.write_to_clipboard(ClipboardItem::new_string(copy.to_string()))
                    }),
            )
            .child(message_time(self.at, self.now))
    }
}
