//! `SentMessage` draws a prompt: its text, folded past ten lines until opened, the images attached to it (their
//! "Attached image:" lines taken off the text) as chips that open the lightbox, and who sent it when another
//! session did. Replaces SentText.svelte, SentImage.svelte and chat/sender.ts.

use crate::core::theme::text::{TextRole, TextRoleExt as _};
use gpui_kit::component::{ActiveTheme as _, h_flex, v_flex};
use gpui_kit::prelude::FluentBuilder as _;
use gpui_kit::*;

use crate::core::state::preferences::Preferences;

use super::session_action::{OnAction, SessionAction};

/// The bubble's corners: the design system's radius-xl.
const BUBBLE_RADIUS: Pixels = px(20.);
/// The bubble's widest, the approved mock's `.bubble`.
const BUBBLE_WIDTH: Pixels = px(470.);
/// A prompt folds past this many lines.
pub const FOLD_LINES: usize = 10;

#[derive(IntoElement)]
pub struct SentMessage {
    /// The item's key, which its fold toggle sends.
    pub key: String,
    pub text: String,
    /// The session that sent it, when another one did.
    pub sender: Option<String>,
    pub sender_machine: Option<String>,
    pub sender_label: Option<String>,
    /// The attached images its lines name, as (path, name); a click reads one and opens it full size.
    pub images: Vec<(String, String)>,
    pub open: bool,
    pub on_action: OnAction,
}

/// The text to show, and whether more is folded away: the first `FOLD_LINES` lines of a longer prompt, until opened.
pub fn shown(text: &str, open: bool) -> (&str, bool) {
    let long = text.lines().count() > FOLD_LINES;
    if open || !long {
        return (text, long);
    }
    let end = text
        .match_indices('\n')
        .nth(FOLD_LINES - 1)
        .map_or(text.len(), |(at, _)| at);
    (&text[..end], true)
}

impl RenderOnce for SentMessage {
    fn render(self, _window: &mut Window, cx: &mut App) -> impl IntoElement {
        let theme = cx.theme();
        let (chat_size, chat_height) =
            Preferences::read(cx, |appearance| appearance.ui_text(TextRole::Chat));
        let (text, long) = shown(&self.text, self.open);
        let toggle = long.then(|| {
            let (key, on_action) = (self.key.clone(), self.on_action.clone());
            div()
                .id(SharedString::from(format!("{}:more", self.key)))
                .text_role(TextRole::Meta, cx)
                .text_color(theme.link)
                .cursor_pointer()
                .on_click(move |_, window, cx| {
                    on_action(SessionAction::Toggle { key: key.clone() }, window, cx)
                })
                .child(if self.open { "Show less" } else { "Show more" })
        });
        let sender = self
            .sender_label
            .clone()
            .or_else(|| self.sender.as_ref().map(|_| "From another session".into()))
            .map(|label| {
                let node = div()
                    .debug_selector(|| "sent-message-sender".into())
                    .id(SharedString::from(format!("{}:sender", self.key)))
                    .text_role(TextRole::Meta, cx)
                    .text_color(theme.link)
                    .child(label);
                if let Some((machine, session_id)) = self
                    .sender_machine
                    .clone()
                    .filter(|machine| !machine.is_empty())
                    .zip(self.sender.clone().filter(|id| !id.is_empty()))
                {
                    let on_action = self.on_action.clone();
                    node.cursor_pointer().on_click(move |_, window, cx| {
                        on_action(
                            SessionAction::OpenSender {
                                key: crate::core::engine::SessionKey {
                                    machine: machine.clone(),
                                    id: session_id.clone(),
                                },
                            },
                            window,
                            cx,
                        )
                    })
                } else {
                    node
                }
            });
        let images = self.images.iter().enumerate().map(|(at, (path, name))| {
            let on_action = self.on_action.clone();
            let file = path.rsplit(['/', '\\']).next().unwrap_or(path).to_string();
            div()
                .id(SharedString::from(format!("{}:image:{at}", self.key)))
                .px_2()
                .py_1()
                .rounded(theme.radius)
                .border_1()
                .border_color(theme.border)
                .text_role(TextRole::Meta, cx)
                .cursor_pointer()
                .on_click(move |_, window, cx| {
                    let name = file.clone();
                    on_action(SessionAction::ShowImage { name }, window, cx)
                })
                .child(name.clone())
        });
        v_flex()
            .ml_auto()
            .max_w(BUBBLE_WIDTH)
            .gap_1()
            .children(sender)
            .child(
                v_flex()
                    .px(px(16.))
                    .py(px(10.))
                    .text_size(chat_size)
                    .line_height(chat_height)
                    .rounded(BUBBLE_RADIUS)
                    .bg(theme.secondary)
                    .gap_1()
                    .when(!text.is_empty(), |bubble| bubble.child(text.to_string()))
                    .children(toggle),
            )
            .when(!self.images.is_empty(), |column| {
                column.child(h_flex().gap_1().flex_wrap().children(images))
            })
    }
}

#[cfg(test)]
#[path = "sent_message_tests.rs"]
mod tests;
