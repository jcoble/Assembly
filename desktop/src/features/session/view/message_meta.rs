//! `MessageMeta` is the line under a prompt or a finished reply: its copy button and when it was written ("Thursday
//! 12:02 PM" this week, "Oct 17, 12:02 PM" before), or for a final reply its turn's line. It keeps its place but
//! shows only while the pointer is over its row. Replaces chat/MessageMeta.svelte and fold.ts's messageTime.

use gpui_kit::component::button::{Button, ButtonVariants as _};
use gpui_kit::component::{ActiveTheme as _, h_flex};
use gpui_kit::*;

use crate::features::session::model::turn_line::message_time;

#[derive(IntoElement)]
pub struct MessageMeta {
    /// The item's key, for the button's id.
    pub key: String,
    /// Its row's hover group: it shows while the pointer is over the row.
    pub group: SharedString,
    /// What the copy button copies: the prompt, or the reply as markdown.
    pub copy: SharedString,
    pub at: i64,
    /// A final reply's turn line, shown in place of the time.
    pub turn: Option<String>,
    pub now: i64,
}

impl RenderOnce for MessageMeta {
    fn render(self, _window: &mut Window, cx: &mut App) -> impl IntoElement {
        let copy = self.copy.clone();
        h_flex()
            .invisible()
            .group_hover(self.group, |style| style.visible())
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
            .child(self.turn.unwrap_or_else(|| message_time(self.at, self.now)))
    }
}
