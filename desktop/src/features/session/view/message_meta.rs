//! `MessageMeta` is the line under a prompt or a finished reply: its copy button and when it was written ("Thursday
//! 12:02 PM" this week, "Oct 17, 12:02 PM" before), or for a final reply its turn's line. It keeps its place but
//! shows only while the pointer is over its row. Replaces chat/MessageMeta.svelte and fold.ts's messageTime.

use gpui_kit::assets::IconName as Glyph;
use gpui_kit::component::button::Button;
use gpui_kit::component::{ActiveTheme as _, Sizable as _, h_flex};
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
    /// Under a reply its chips are the mock's 26 px with 18 px under them; under a prompt, 22 px and close under it,
    /// so the prompt keeps the mock's 18 px to what follows.
    pub reply: bool,
}

impl RenderOnce for MessageMeta {
    fn render(self, _window: &mut Window, cx: &mut App) -> impl IntoElement {
        let copy = self.copy.clone();
        let theme = cx.theme();
        // The mock's `.ghost` chips: 26 px high, 8 px corners, an edge, a shade lighter than the panel, 12 px text.
        let (chip_bg, chip_edge) = (theme.group_box.opacity(0.5), theme.border);
        let (high, top, under) = if self.reply {
            (px(26.), px(8.), px(18.))
        } else {
            (px(22.), px(4.), px(0.))
        };
        h_flex()
            .invisible()
            .group_hover(self.group, |style| style.visible())
            .gap(px(6.))
            .pt(top)
            .pb(under)
            .text_size(px(12.))
            .child(
                Button::new(SharedString::from(format!("{}:copy", self.key)))
                    .outline()
                    .small()
                    .icon(Glyph::Copy)
                    .label("Copy")
                    .h(high)
                    .px(px(10.))
                    .rounded(px(8.))
                    .bg(chip_bg)
                    .border_color(chip_edge)
                    .on_click(move |_, _, cx| {
                        cx.write_to_clipboard(ClipboardItem::new_string(copy.to_string()))
                    }),
            )
            .child(
                div()
                    .h(high)
                    .px(px(10.))
                    .flex()
                    .items_center()
                    .rounded(px(8.))
                    .border_1()
                    .border_color(chip_edge)
                    .bg(chip_bg)
                    .text_color(theme.foreground)
                    .child(self.turn.unwrap_or_else(|| message_time(self.at, self.now))),
            )
    }
}
