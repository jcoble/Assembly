//! Asking an agent about a pull request: which session, and the words, which the person can change. Choosing opens that
//! session with the words in its composer; the person sends them there. With no session linked, the words can be copied.
use super::*;
use crate::core::engine::SessionKey;
use crate::core::intents::ask_session::AskSession;
use gpui_kit::component::WindowExt as _;
use gpui_kit::component::input::Textarea;

pub(super) struct AskSheet {
    sessions: Vec<(SessionKey, String)>,
    chosen: usize,
    input: Entity<TextareaState>,
}
pub(super) fn open(
    sessions: Vec<(SessionKey, String)>,
    prompt: String,
    window: &mut Window,
    cx: &mut App,
) {
    let input = cx.new(|cx| {
        TextareaState::new(window, cx)
            .default_value(prompt)
            .auto_grow(6, 16)
    });
    let sheet = cx.new(|_| AskSheet {
        sessions,
        chosen: 0,
        input,
    });
    window.open_dialog(cx, move |dialog, _, _| {
        dialog
            .title("Ask an agent")
            .w(px(640.))
            .footer(Empty)
            .child(sheet.clone())
    });
}
impl AskSheet {
    fn send(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        let Some((key, _)) = self.sessions.get(self.chosen).cloned() else {
            return;
        };
        let text = self.input.read(cx).value().trim().to_string();
        if text.is_empty() {
            return;
        }
        window.close_dialog(cx);
        window.dispatch_action(Box::new(AskSession { key, text }), cx);
    }
}
impl Render for AskSheet {
    fn render(&mut self, _: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        let theme = cx.theme();
        let (muted, border, radius) = (theme.muted_foreground, theme.border, theme.radius);
        let has_text = !self.input.read(cx).value().trim().is_empty();
        let none = self.sessions.is_empty();
        v_flex()
            .gap_3()
            .text_role(TextRole::Body, cx)
            .child(div().text_color(muted).child(if none {
                "No session is working on this pull request. Start one on its branch, or copy these words to use anywhere."
            } else {
                "The words open in the session's composer. Nothing is sent until you send it there."
            }))
            .when(!none, |sheet| {
                sheet.child(v_flex().gap_1().children(self.sessions.iter().enumerate().map(|(index, (_, title))| {
                    Button::new(SharedString::from(format!("ask-session:{index}")))
                        .ghost()
                        .small()
                        .w_full()
                        .icon(IconName::MessageSquare)
                        .label(title.clone())
                        .selected(index == self.chosen)
                        .on_click(cx.listener(move |sheet, _, _, cx| {
                            sheet.chosen = index;
                            cx.notify();
                        }))
                })))
            })
            .child(
                div()
                    .p_2()
                    .rounded(radius)
                    .border_1()
                    .border_color(border)
                    .child(Textarea::new(&self.input).appearance(false)),
            )
            .child(
                h_flex()
                    .gap_2()
                    .justify_end()
                    .child(Button::new("ask-cancel").ghost().small().label("Cancel").on_click(|_, window, cx| window.close_dialog(cx)))
                    .child(
                        Button::new("ask-copy")
                            .ghost()
                            .small()
                            .icon(IconName::Copy)
                            .label("Copy")
                            .disabled(!has_text)
                            .on_click(cx.listener(|sheet, _, _, cx| {
                                let text = sheet.input.read(cx).value().to_string();
                                cx.write_to_clipboard(ClipboardItem::new_string(text));
                            })),
                    )
                    .when(!none, |row| {
                        row.child(
                            Button::new("ask-open")
                                .primary()
                                .small()
                                .label("Open in session")
                                .disabled(!has_text)
                                .on_click(cx.listener(|sheet, _, window, cx| sheet.send(window, cx))),
                        )
                    }),
            )
    }
}
